using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetLevelUpEventTests
{
    private const int Number = 1135;
    private static readonly DateTimeOffset At = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.Find(Number)!;
    private static string Id => Pet.CatalogId!;
    private static PetProgressContext Context(long sequence, long generation = 1) => new(generation, sequence, At.AddSeconds(sequence));
    private static PetProgressSnapshot Snapshot(int level, int balance = 0) => new(
        new Dictionary<int, int> { [Number] = level }, new Dictionary<int, int> { [Number] = balance });
    private static bool Confirm(ProgressService progress, bool acquisition, int level, long sequence, long generation = 1) => acquisition
        ? progress.ApplyPetAcquisition(new(1001, Number, level), Context(sequence, generation))
        : progress.ApplyPetUpgrade(new(0, Number, level), Context(sequence, generation));

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void AThresholdPickupWaitsForExplicitConfirmationAndAcquisitionAndUpgradeDoNotDoubleSignal(int level, bool acquisition)
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        var cost = SoulThresholds.Default.Steps[level - 1];
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetSnapshot(Snapshot(level - 1, cost - 1), Context(1)));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(2)));
        Assert.Empty(events);
        Assert.Equal(level - 1, progress.ObservedLevel(Id));
        Assert.True(Confirm(progress, acquisition, level, 3));
        Assert.Equal(new PetLevelUp(Id, level, level == 3), Assert.Single(events));
        Assert.True(progress.ApplyPetSpent([new(Number, cost)], Context(4)));
        Assert.True(Confirm(progress, !acquisition, level, 5));
        Assert.True(Confirm(progress, acquisition, level, 6));
        Assert.False(Confirm(progress, acquisition, level, 6));
        Assert.Single(events);
        Assert.Equal(level, progress.ObservedLevel(Id));
        Assert.Equal(0, progress.AvailableSouls(Id));
        Assert.Equal(1, Assert.Single(progress.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(f.EventsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASpendingMessageBeforeLevelConfirmationDoesNotSignalOrConsumeTheLaterUpgrade(bool acquisition)
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1, 25), Context(1));
        Assert.True(progress.ApplyPetSpent([new(Number, 25)], Context(2)));
        Assert.Empty(events);
        Assert.True(Confirm(progress, acquisition, 2, 3));
        Assert.Equal(new PetLevelUp(Id, 2, false), Assert.Single(events));
        Assert.Empty(progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void TheFirstPositiveExplicitSuccessCanSignalWithoutAnOldObservedLevel(int level, bool acquisition)
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        Assert.Null(progress.ObservedLevel(Id));
        Assert.True(Confirm(progress, acquisition, level, 1));
        Assert.Equal(new PetLevelUp(Id, level, level == 3), Assert.Single(events));
        Assert.True(Confirm(progress, !acquisition, level, 2));
        Assert.Single(events);
        Assert.Empty(progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public void AnUnobservedLegacyEstimateDoesNotSuppressAConfirmedInitialUpgrade()
    {
        using var f = new Fixture(legacyTotal: 105);
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(1)));
        Assert.Null(progress.ObservedLevel(Id));
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        Assert.True(Confirm(progress, false, 1, 2));
        Assert.Equal(new PetLevelUp(Id, 1, false), Assert.Single(events));
    }

    [Fact]
    public void AZeroLevelAcquisitionIsNotAnUnlockAnimation()
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        Assert.True(Confirm(progress, true, 0, 1));
        Assert.Empty(events);
        Assert.True(Confirm(progress, false, 1, 2));
        Assert.Equal(new PetLevelUp(Id, 1, false), Assert.Single(events));
    }

    [Fact]
    public void BaselinesAndCorrectionsAreSilentAndPreviouslyConfirmedLevelsNeverReplay()
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        Assert.True(progress.ApplyPetSnapshot(Snapshot(3), Context(1)));
        Assert.True(Confirm(progress, false, 3, 2));
        Assert.True(progress.ApplyPetSnapshot(Snapshot(1), Context(3)));
        Assert.True(Confirm(progress, true, 2, 4));
        Assert.True(Confirm(progress, false, 3, 5));
        Assert.Empty(events);
        Assert.Empty(progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Theory]
    [InlineData("source-stale")]
    [InlineData("session-reset")]
    [InlineData("new-generation")]
    public void SourceChangesDoNotReplayAnEarlierLiveConfirmation(string change)
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1), Context(1));
        Assert.True(Confirm(progress, false, 2, 2));
        if (change == "source-stale") progress.MarkNetworkPetSourceStale();
        else if (change == "session-reset") progress.MarkPetNetworkStale();
        var generation = change == "source-stale" ? 1 : 2;
        if (generation == 2) Assert.True(progress.BindPetSession(Context(0, generation)));
        Assert.True(Confirm(progress, true, 2, 3, generation));
        Assert.Single(events);
        Assert.True(Confirm(progress, false, 3, 4, generation));
        Assert.Equal(new[] { new PetLevelUp(Id, 2, false), new PetLevelUp(Id, 3, true) }, events);
    }

    [Fact]
    public void ALoadedObservedLevelIsASilentFloorWithoutAnAdditionalPersistedEventLog()
    {
        using var f = new Fixture();
        var original = f.Load();
        original.BindPetSession(Context(0));
        original.ApplyPetSnapshot(Snapshot(2), Context(1));
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.BindPetSession(Context(0));
        Assert.True(Confirm(progress, true, 2, 1));
        Assert.Empty(events);
        Assert.True(Confirm(progress, false, 3, 2));
        Assert.Equal(new PetLevelUp(Id, 3, true), Assert.Single(events));
        Assert.Empty(progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public void ManualCorrectionsAndClearingThemNeverEmitOrReplayAnIgnoredUpgrade()
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        progress.SetSouls(Id, 105);
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1), Context(1));
        Assert.True(Confirm(progress, false, 2, 2));
        Assert.Empty(events);
        Assert.True(progress.EntrySnapshot(Id).ManualOverride);
        progress.ClearManualOverride(Id);
        Assert.Empty(events);
        Assert.True(Confirm(progress, true, 2, 3));
        Assert.Empty(events);
        Assert.True(Confirm(progress, false, 3, 4));
        Assert.Equal(new PetLevelUp(Id, 3, true), Assert.Single(events));
    }

    [Fact]
    public void InvalidFailedOldAndUnboundResultsCannotEmit()
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        Assert.False(Confirm(progress, false, 1, 1));
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1), Context(1));
        Assert.False(progress.ApplyPetUpgrade(new(1, Number, 2), Context(2)));
        Assert.False(progress.ApplyPetUpgrade(new(0, Number, 0), Context(2)));
        Assert.False(progress.ApplyPetUpgrade(new(0, Number, 4), Context(2)));
        Assert.False(progress.ApplyPetUpgrade(new(0, 0, 2), Context(2)));
        Assert.False(Confirm(progress, false, 2, 1));
        Assert.False(Confirm(progress, true, 2, 2, generation: 2));
        Assert.True(progress.ApplyPetAcquisition(new(1001, Number, 1), Context(2)));
        Assert.False(progress.ApplyPetAcquisition(new(1001, Number, 0), Context(3)));
        progress.MarkPetNetworkStale();
        Assert.False(Confirm(progress, false, 2, 4));
        Assert.Empty(events);
    }

    [Fact]
    public void OwnBoundAcquisitionKeepsItsEntityProofAndPublishesTheSameEventContract()
    {
        using var f = new Fixture();
        var progress = f.Load();
        var events = new List<PetLevelUp>();
        progress.PetLeveled += events.Add;
        NetworkProgressContext Legacy(long sequence) => new(new(101, 501, 1001, "Synthetic pet event"),
            1, sequence, At.AddSeconds(sequence));
        Assert.True(progress.BindNetworkContext(Legacy(0)));
        Assert.True(progress.ApplyNetworkSnapshot(Snapshot(0), Legacy(1)));
        Assert.False(progress.ApplyNetworkAcquisition(new(2002, Number, 1), Legacy(2)));
        Assert.Empty(events);
        Assert.True(progress.ApplyNetworkAcquisition(new(1001, Number, 1), Legacy(2)));
        Assert.Equal(new PetLevelUp(Id, 1, false), Assert.Single(events));
        Assert.True(progress.ApplyNetworkUpgrade(new(0, Number, 1), Legacy(3)));
        Assert.Single(events);
    }

    [Fact]
    public void SubscribersRunOutsideTheProgressLockBeforeChangedAndOneFailureDoesNotBlockOthers()
    {
        using var f = new Fixture();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1), Context(1));
        var order = new List<string>();
        var readCompleted = false;
        progress.PetLeveled += _ => throw new InvalidOperationException("Synthetic event subscriber");
        progress.PetLeveled += _ =>
        {
            readCompleted = Task.Run(() => progress.Level(Id)).Wait(TimeSpan.FromSeconds(1));
            order.Add("level-up");
        };
        progress.Changed += () => order.Add("changed");
        Assert.True(Confirm(progress, false, 2, 2));
        Assert.True(readCompleted);
        Assert.Equal(new[] { "level-up", "changed" }, order);
        Assert.Equal(2, progress.ObservedLevel(Id));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-level-events-" + Guid.NewGuid().ToString("N"));
        private readonly PetCatalog _catalog = new([new PetDefinition { Id = Id, En = Pet.En,
            Genus = Pet.Genus.ToLowerInvariant(), NumericId = Number.ToString(CultureInfo.InvariantCulture) }]);
        private string ProgressPath => Path.Combine(_folder, "progress.json");
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal Fixture(int? legacyTotal = null)
        {
            Directory.CreateDirectory(_folder);
            if (legacyTotal is { } total)
            {
                var profile = new ProgressProfile { SchemaVersion = 3 };
                profile.Pets[Id] = new() { Souls = total };
                File.WriteAllBytes(ProgressPath, JsonSerializer.SerializeToUtf8Bytes(profile));
            }
        }
        internal ProgressService Load() => new(ProgressPath, _catalog);
        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
