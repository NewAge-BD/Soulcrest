using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CharacterWingPersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string WingSource = "own-wing-ownership-list";
    private static CharacterWingOwnership Wings(params (int Id, byte Level)[] rows) => new(rows.ToImmutableDictionary(row => row.Id, row => row.Level));

    [Fact]
    public void CompleteListStoresLevelsAndReloadsOnlyAsProfileHistory()
    {
        using var f = new Fixture();
        f.Login(101, 7, Wings((40101100, 0), (40100100, 8)));
        var saved = Assert.IsType<ProfileWingSnapshot>(f.State.SavedWings);
        Assert.Equal(f.Exploration.ActiveId, saved.ProfileId);
        Assert.Equal((byte)8, saved.Wings[40100100]);
        var reloaded = new ExplorationService(f.Path, []);
        Assert.Equal(saved.Wings.OrderBy(row => row.Key), reloaded.TryGetSavedWings()!.Wings.OrderBy(row => row.Key));
        using var offline = new Source { Running = false };
        using var skills = new OwnSkillStateService(offline, reloaded);
        using var state = new CharacterCombatStateService(offline, skills, reloaded);
        Assert.Equal(saved.ProfileId, state.SavedWings!.ProfileId);
        Assert.Equal(At, state.SavedWings.ObservedAt);
        Assert.False(state.Snapshot.IsFresh);
        Assert.Null(state.Snapshot.WingOwnership.Value);
        Assert.Null(state.Snapshot.EquippedWing.Value);
        Assert.Null(state.Snapshot.Graph);
    }

    [Theory]
    [InlineData(NetworkCombatSourceStatus.Unknown, true)]
    [InlineData(NetworkCombatSourceStatus.Partial, true)]
    [InlineData(NetworkCombatSourceStatus.Stale, true)]
    [InlineData(NetworkCombatSourceStatus.Conflict, true)]
    [InlineData(NetworkCombatSourceStatus.Complete, false)]
    public void InvalidOrIncompleteListCannotEraseHistory(NetworkCombatSourceStatus status, bool fresh)
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var before = File.ReadAllText(f.Path);
        f.Publish(new(Wings(), status, fresh, At.AddSeconds(1), 2, WingSource));
        Assert.Equal(before, File.ReadAllText(f.Path));
        Assert.True(f.State.SavedWings!.Wings.ContainsKey(40101100));
    }

    [Fact]
    public void EmptyCompleteListAndChangedInternalLevelReplaceHistory()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        f.Publish(new(Wings((40101100, 5)), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(1), 2, WingSource));
        Assert.Equal((byte)5, f.State.SavedWings!.Wings[40101100]);
        f.Publish(new(Wings(), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(2), 3, WingSource));
        Assert.Empty(new ExplorationService(f.Path, []).TryGetSavedWings()!.Wings);
    }

    [Fact]
    public void OldAndDuplicateFactsDoNotRewriteTheProfile()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var before = File.ReadAllText(f.Path);
        var changed = 0; f.Exploration.Changed += () => changed++;
        f.Publish(f.Source.CombatSnapshot.WingOwnership);
        f.Publish(new(Wings((40100100, 3)), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(-1), 2, WingSource));
        Assert.Equal(0, changed);
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Fact]
    public void SameTimestampOlderSequenceCannotOverwriteNewerAcceptedOwnership()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var own = f.Source.CombatSnapshot.Character!;
        var newer = new NetworkCombatDomain<CharacterWingOwnership>(Wings((40100100, 4)), NetworkCombatSourceStatus.Complete, true, At, 3, WingSource);
        Assert.True(f.Exploration.ApplyNetworkWingOwnership(own, f.Exploration.ActiveId, newer));
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own, f.Exploration.ActiveId, newer with { Sequence = 2, Value = Wings((40101100, 0)) }));
        Assert.True(f.State.SavedWings!.Wings.ContainsKey(40100100));
    }

    [Fact]
    public void ReentrantTitleCallbackCannotWriteThePreviousWingRevisionBack()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var reentered = false;
        f.Exploration.Changed += () =>
        {
            if (reentered) return;
            reentered = true;
            f.Publish(new(Wings((40100100, 4)), NetworkCombatSourceStatus.Complete, true, At, 3, WingSource));
        };
        f.Source.CombatSnapshot = f.Source.CombatSnapshot with
        {
            TitleOwnership = new(ImmutableHashSet.Create(8001), NetworkCombatSourceStatus.Complete, true, At, 2, "own-title-ownership-list"),
            WingOwnership = new(Wings((40101100, 0)), NetworkCombatSourceStatus.Complete, true, At, 2, WingSource)
        };
        f.Source.Raise();
        Assert.True(reentered);
        Assert.True(f.State.SavedWings!.Wings.ContainsKey(40100100));
        Assert.False(f.State.SavedWings.Wings.ContainsKey(40101100));
    }

    [Fact]
    public void CharacterAndServerBindingsStaySeparateAndLiveBindingWinsOverSelection()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var first = f.Exploration.ActiveId;
        f.Login(102, 8, Wings((30101100, 2)), 502);
        var second = f.Exploration.ActiveId;
        Assert.NotEqual(first, second);
        f.Exploration.Select(first);
        Assert.Equal(second, f.State.SavedWings!.ProfileId);
        Assert.True(f.State.SavedWings.Wings.ContainsKey(30101100));
        Assert.True(f.Exploration.TryGetSavedWings(first)!.Wings.ContainsKey(40101100));
        f.Source.Running = false; f.Source.Raise();
        Assert.Equal(first, f.State.SavedWings!.ProfileId);
        f.Exploration.Add("Synthetic unsampled character");
        Assert.Null(f.State.SavedWings);
    }

    [Fact]
    public void StaleForeignGenerationAndUnknownSourceCannotPersist()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var own = f.Source.CombatSnapshot.Character!;
        var profile = f.Exploration.ActiveId;
        var domain = new NetworkCombatDomain<CharacterWingOwnership>(Wings((30101100, 0)), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(1), 2, WingSource);
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own, "unrelated-profile", domain));
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own with { Generation = 6 }, profile, domain));
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own with { Identity = own.Identity with { EntityKey = 301 } }, profile, domain));
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own, profile, domain with { Source = "catalog" }));
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own, profile, domain with { Value = Wings((0, 0)) }));
        f.Source.CombatSnapshot = f.Source.CombatSnapshot with { Generation = 8, WingOwnership = domain };
        f.Source.Raise();
        Assert.True(f.State.SavedWings!.Wings.ContainsKey(40101100));
        f.Exploration.MarkNetworkStale();
        Assert.False(f.Exploration.ApplyNetworkWingOwnership(own, profile, domain));
    }

    [Fact]
    public void ConcurrentHistoricalReadCannotReturnPreviousLiveBinding()
    {
        using var f = new Fixture(); f.Login(101, 7, Wings((40101100, 0)));
        var reads = 0;
        f.Source.ReadCombat = () => { if (++reads == 3) f.Source.Running = false; };
        Assert.Null(f.State.SavedWings);
        f.Source.ReadCombat = null;
        Assert.NotNull(f.State.SavedWings);
    }

    [Fact]
    public void LegacyProfileRemainsUnknownAndCallbacksRunOutsideTheProfileLock()
    {
        using var f = new Fixture();
        Assert.Null(f.State.SavedWings);
        var callbacksUnlocked = true;
        f.Exploration.Changed += () => callbacksUnlocked &= Task.Run(() => f.Exploration.TryGetSavedWings()).Wait(TimeSpan.FromSeconds(5));
        f.Login(101, 7, Wings((40101100, 0)));
        Assert.True(callbacksUnlocked);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-wing-history-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "exploration.json");
        internal Source Source { get; } = new();
        internal ExplorationService Exploration { get; }
        private OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService State { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path, "{\"SchemaVersion\":2,\"Active\":\"11111111111111111111111111111111\",\"Characters\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Character 1\"}]}");
            Exploration = new(Path, []); Skills = new(Source, Exploration); State = new(Source, Skills, Exploration);
        }
        internal void Login(long character, long generation, CharacterWingOwnership owned, int server = 501)
        {
            var identity = new OwnCharacterIdentity(character, server, 300, "Synthetic character " + character) { ClassId = "Cleric" };
            Exploration.BindNetworkContext(new(identity, generation, 1, At));
            Source.CombatSnapshot = new() { Character = new(identity, generation) { ObservedAt = At }, Generation = generation, Sequence = 1,
                ClassId = "Cleric", WingOwnership = new(owned, NetworkCombatSourceStatus.Complete, true, At, 1, WingSource) };
            Source.Raise();
        }
        internal void Publish(NetworkCombatDomain<CharacterWingOwnership> domain)
        {
            Source.CombatSnapshot = Source.CombatSnapshot with { Sequence = Source.CombatSnapshot.Sequence + 1, WingOwnership = domain };
            Source.Raise();
        }
        public void Dispose() { State.Dispose(); Skills.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource, IDisposable
    {
        private NetworkCharacterCombatSnapshot _snapshot = NetworkCharacterCombatSnapshot.Empty;
        internal Action? ReadCombat { get; set; }
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get { ReadCombat?.Invoke(); return _snapshot; } set => _snapshot = value; }
        public NetworkSkillSnapshot SkillSnapshot => NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
        public void Dispose() { }
    }
}
