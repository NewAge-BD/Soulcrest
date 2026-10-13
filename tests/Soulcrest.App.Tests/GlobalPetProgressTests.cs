using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GlobalPetProgressTests
{
    private const int Number = 1135;
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.Find(Number)!;
    private static string Id => Pet.CatalogId!;
    private static PetProgressContext Context(long sequence, long generation = 1) => new(generation, sequence, At.AddSeconds(sequence));
    private static PetProgressSnapshot Snapshot(int level, int balance) => new(
        new Dictionary<int, int> { [Number] = level }, new Dictionary<int, int> { [Number] = balance });

    [Fact]
    public void StartingMidgameCountsOnlyNewOrderedGainsWithoutAnOwnOrBaseline()
    {
        using var f = new Folder();
        var progress = f.Load();
        Assert.False(progress.ApplyPetGain([new(Number, 50)], Context(1)));
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetGain([new(Number, 2)], Context(1)));
        Assert.False(progress.ApplyPetGain([new(Number, 2)], Context(1)));
        Assert.Equal(2, progress.Souls(Id));
        Assert.Null(progress.ActiveServerId);
        Assert.Null(progress.ObservedLevel(Id));
        Assert.Null(progress.AvailableSouls(Id));
        Assert.Null(progress.NetworkBaselineAt);
        Assert.Equal(ProgressFreshness.Unknown, progress.EntrySnapshot(Id).Freshness);
        Assert.Equal("?", progress.ReadMerchantCounter(Id).Counter);
        Assert.Equal(2, Assert.Single(progress.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(f.Events));
        Assert.Equal(2, f.Load().Souls(Id));
    }

    [Fact]
    public void RebindingTheSameGenerationDoesNotResetSequenceOrConfirmedBaseline()
    {
        using var f = new Folder();
        var progress = f.Load();
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetSnapshot(Snapshot(1, 3), Context(1)));
        var changes = 0;
        progress.Changed += () => changes++;
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.BindPetSession(Context(2)));
        Assert.False(progress.ApplyPetGain([new(Number, 20)], Context(1)));
        Assert.Equal(0, changes);
        Assert.Equal(ProgressFreshness.Fresh, progress.EntrySnapshot(Id).Freshness);
        Assert.True(progress.ApplyPetGain([new(Number, 2)], Context(2)));
        Assert.Equal(10, progress.Souls(Id));
        Assert.Equal(5, progress.AvailableSouls(Id));
        Assert.True(progress.ReadMerchantCounter(Id).IsFresh);
    }

    [Fact]
    public void ARealSourceResetRetainsCountsAndRecentPickupsButFencesTheOldGeneration()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1, 3), Context(1));
        progress.ApplyPetGain([new(Number, 2)], Context(2));
        progress.MarkPetNetworkStale();
        Assert.Equal(10, progress.Souls(Id));
        Assert.Equal(5, progress.AvailableSouls(Id));
        Assert.Equal(ProgressFreshness.Stale, progress.EntrySnapshot(Id).Freshness);
        Assert.False(progress.ApplyPetGain([new(Number, 20)], Context(3)));
        Assert.False(progress.BindPetSession(Context(99)));
        Assert.True(progress.BindPetSession(Context(0, 2)));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(1, 2)));
        Assert.False(progress.ApplyPetSnapshot(Snapshot(3, 0), Context(99)));
        Assert.Equal(11, progress.Souls(Id));
        Assert.Equal(1, progress.ObservedLevel(Id));
        Assert.Equal(2, progress.Recent().Count);
        Assert.Equal(2, File.ReadAllLines(f.Events).Length);
        Assert.False(progress.ReadMerchantCounter(Id).IsFresh);
    }

    [Fact]
    public void ANewGenerationAloneInvalidatesFreshnessWithoutReplacingTheGlobalCollection()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(2, 7), Context(1));
        progress.ToggleFocus(Id);
        Assert.True(progress.BindPetSession(Context(0, 2)));
        Assert.Equal(37, progress.Souls(Id));
        Assert.Equal(7, progress.AvailableSouls(Id));
        Assert.True(progress.IsFocus(Id));
        Assert.Equal(ProgressFreshness.Stale, progress.EntrySnapshot(Id).Freshness);
        Assert.False(progress.BindPetSession(Context(99)));
        Assert.False(progress.ApplyPetGain([new(Number, 1)], Context(99)));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(1, 2)));
        Assert.Equal(38, progress.Souls(Id));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeSourceCoordinatesCannotBind(long generation, long sequence)
    {
        using var f = new Folder();
        Assert.False(f.Load().BindPetSession(new(generation, sequence, At)));
        Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public void AcquisitionUpgradeAndSpendingKeepLevelBalanceAndEarnedTotalSeparate()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        Assert.True(progress.ApplyPetSnapshot(Snapshot(0, 5), Context(1)));
        // Entity validation for acquisition belongs to the source dispatcher, before this global API.
        Assert.True(progress.ApplyPetAcquisition(new(1001, Number, 1), Context(2)));
        Assert.True(progress.ApplyPetSpent([new(Number, 5)], Context(3)));
        Assert.True(progress.ApplyPetGain([new(Number, 25)], Context(4)));
        Assert.True(progress.ApplyPetUpgrade(new(0, Number, 2), Context(5)));
        Assert.True(progress.ApplyPetSpent([new(Number, 25)], Context(6)));
        Assert.Equal(2, progress.ObservedLevel(Id));
        Assert.Equal(0, progress.AvailableSouls(Id));
        Assert.Equal(30, progress.Souls(Id));
        Assert.Equal(25, Assert.Single(progress.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(f.Events));
        Assert.False(progress.ApplyPetUpgrade(new(1, Number, 3), Context(7)));
        Assert.False(progress.ApplyPetAcquisition(new(1001, Number, 1), Context(7)));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(7)));
    }

    [Fact]
    public void ALevelOnlyConfirmationDoesNotInventKnownBalanceOrFreshness()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        Assert.True(progress.ApplyPetGain([new(Number, 5)], Context(1)));
        Assert.True(progress.ApplyPetUpgrade(new(0, Number, 1), Context(2)));
        Assert.Equal(1, progress.ObservedLevel(Id));
        Assert.Null(progress.AvailableSouls(Id));
        Assert.Equal("?", progress.ReadMerchantCounter(Id).Counter);
        Assert.False(progress.ReadMerchantCounter(Id).IsFresh);
        Assert.Equal(ProgressFreshness.Unknown, progress.EntrySnapshot(Id).Freshness);
    }

    [Fact]
    public void MerchantCountersReadTheGlobalCollectionWithoutAServerContext()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1, 3), Context(1));
        Assert.Equal(("3/25", true, false), progress.ReadMerchantCounter(Id));
        progress.MarkPetNetworkStale();
        Assert.Equal(("3/25", false, false), progress.ReadMerchantCounter(Id));
    }

    [Fact]
    public void ConfirmedMaxLevelNeedsNoBalanceButDoesNotClaimFreshBaseline()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        Assert.True(progress.ApplyPetUpgrade(new(0, Number, 3), Context(1)));
        Assert.Null(progress.AvailableSouls(Id));
        Assert.Equal(("max", false, true), progress.ReadMerchantCounter(Id));
        Assert.Empty(progress.Recent());
        Assert.False(File.Exists(f.Events));
    }

    [Fact]
    public void MalformedBatchesCannotPartiallyWriteOrConsumeTheSequence()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1, 3), Context(1));
        var bytes = File.ReadAllBytes(f.Path);
        Assert.False(progress.ApplyPetGain([new(Number, 2), new(Number + 1, -1)], Context(2)));
        Assert.False(progress.ApplyPetSpent([new(0, 2)], Context(2)));
        Assert.False(progress.ApplyPetSnapshot(Snapshot(4, 1), Context(2)));
        Assert.False(progress.ApplyPetUpgrade(new(0, Number, 4), Context(2)));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Equal(8, progress.Souls(Id));
        Assert.True(progress.ApplyPetGain([new(Number, 2)], Context(2)));
        Assert.Equal(10, progress.Souls(Id));
    }

    [Fact]
    public void LegacyIdentityWrappersKeepTheirValidationWithoutSelectingServerCollections()
    {
        using var f = new Folder();
        var progress = f.Load();
        NetworkProgressContext Legacy(long sequence, long character = 101, int server = 501, string name = "Synthetic global") =>
            new(new(character, server, 1001, name), 1, sequence, At.AddSeconds(sequence));
        Assert.False(progress.BindNetworkContext(Legacy(0, character: 0)));
        Assert.False(progress.BindNetworkContext(Legacy(0, server: 0)));
        Assert.False(progress.BindNetworkContext(Legacy(0, name: "")));
        Assert.True(progress.BindNetworkContext(Legacy(0)));
        Assert.True(progress.ApplyNetworkSnapshot(Snapshot(1, 3), Legacy(1)));
        Assert.True(progress.BindNetworkContext(Legacy(2, character: 202, server: 502)));
        Assert.Equal(8, progress.Souls(Id));
        Assert.False(progress.ApplyNetworkGain([new(Number, 2)], Legacy(3)));
        Assert.False(progress.ApplyNetworkAcquisition(new(9999, Number, 2), Legacy(3, character: 202, server: 502)));
        Assert.True(progress.ApplyNetworkGain([new(Number, 2)], Legacy(3, character: 202, server: 502)));
        Assert.Null(progress.ActiveServerId);
        Assert.Equal(10, progress.Souls(Id));
        Assert.Equal(ProgressFreshness.Fresh, progress.EntrySnapshot(Id).Freshness);
    }

    [Fact]
    public void ReloadedSavedCountsAcceptNewMidgameGainsWithoutPromotingTheirLevelSeed()
    {
        using var f = new Folder();
        JsonFile.Save(f.Path, new ProgressProfile { SchemaVersion = 3,
            Pets = new() { [Id] = new() { Souls = 8, Focus = true } } });
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetGain([new(Number, 30)], Context(1));
        Assert.Equal(38, progress.Souls(Id));
        Assert.Equal(1, progress.Level(Id));
        Assert.Equal(33, progress.SoulsInLevel(Id));
        Assert.Null(progress.ObservedLevel(Id));
        Assert.Null(progress.AvailableSouls(Id));
        Assert.True(progress.IsFocus(Id));
        var reloaded = f.Load();
        Assert.Equal(38, reloaded.Souls(Id));
        Assert.Equal(1, reloaded.Level(Id));
        Assert.Null(reloaded.ObservedLevel(Id));
        Assert.Empty(Directory.GetFiles(f.Root, "*.bak"));
    }

    [Fact]
    public void AManualCorrectionRemainsExplicitWhileGlobalNetworkCountsContinue()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.SetSouls(Id, 40);
        progress.BindPetSession(Context(0));
        progress.ApplyPetSnapshot(Snapshot(1, 3), Context(1));
        progress.ApplyPetGain([new(Number, 2)], Context(2));
        Assert.Equal(42, progress.Souls(Id));
        Assert.True(progress.EntrySnapshot(Id).ManualOverride);
        Assert.Equal("12/75", progress.ReadMerchantCounter(Id).Counter);
        progress.ClearManualOverride(Id);
        Assert.Equal(10, progress.Souls(Id));
        Assert.Equal(5, progress.AvailableSouls(Id));
    }

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-global-pet-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "progress.json");
        internal string Events => System.IO.Path.Combine(Root, "events.jsonl");
        private readonly PetCatalog _catalog = new([new PetDefinition { Id = Id, En = Pet.En, Genus = Pet.Genus,
            NumericId = Number.ToString(CultureInfo.InvariantCulture) }]);
        internal Folder() => Directory.CreateDirectory(Root);
        internal ProgressService Load() => new(Path, _catalog);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
