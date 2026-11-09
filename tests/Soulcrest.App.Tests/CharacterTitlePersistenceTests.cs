using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CharacterTitlePersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private const string TitleSource = "own-title-ownership-list";

    [Fact]
    public void CompleteOwnListPersistsDistinctNativeIdsAndReloadsOnlyAsHistory()
    {
        using var fixture = new Fixture();
        fixture.Login(101, 7, [8001, 8002]);
        var first = Assert.IsType<ProfileTitleSnapshot>(fixture.State.SavedTitles);
        Assert.Equal(fixture.Exploration.ActiveId, first.ProfileId);
        Assert.True(first.OwnedTitleIds.SetEquals([8001, 8002]));
        Assert.Equal(At, first.ObservedAt); Assert.Equal(TitleSource, first.Source);
        var reloaded = new ExplorationService(fixture.Path, []);
        var saved = Assert.IsType<ProfileTitleSnapshot>(reloaded.TryGetSavedTitles());
        Assert.Equal(first.ProfileId, saved.ProfileId);
        Assert.Equal(first.ObservedAt, saved.ObservedAt); Assert.Equal(first.Source, saved.Source);
        Assert.True(first.OwnedTitleIds.SetEquals(saved.OwnedTitleIds));
        using var source = new Source { Running = false };
        using var skills = new OwnSkillStateService(source, reloaded);
        using var state = new CharacterCombatStateService(source, skills, reloaded);
        Assert.Equal(saved.ProfileId, state.SavedTitles!.ProfileId);
        Assert.True(saved.OwnedTitleIds.SetEquals(state.SavedTitles!.OwnedTitleIds));
        Assert.False(state.Snapshot.IsFresh);
        Assert.Null(state.Snapshot.TitleOwnership.Value);
        Assert.Null(state.Snapshot.Graph);
    }

    [Theory]
    [InlineData(NetworkCombatSourceStatus.Unknown, true)]
    [InlineData(NetworkCombatSourceStatus.Partial, true)]
    [InlineData(NetworkCombatSourceStatus.Stale, true)]
    [InlineData(NetworkCombatSourceStatus.Conflict, true)]
    [InlineData(NetworkCombatSourceStatus.Complete, false)]
    public void IncompleteOrInvalidatedSourcesNeverEraseSavedOwnership(NetworkCombatSourceStatus status, bool fresh)
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001, 8002]);
        var before = File.ReadAllText(fixture.Path);
        fixture.Publish(fixture.Source.CombatSnapshot.TitleOwnership with
        {
            Value = ImmutableHashSet<int>.Empty, Status = status, IsFresh = fresh, ObservedAt = At.AddSeconds(1)
        });
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        Assert.True(fixture.State.SavedTitles!.OwnedTitleIds.SetEquals([8001, 8002]));
    }

    [Fact]
    public void ConfirmedEmptyListReplacesThePreviousCompleteListAndSurvivesReload()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001, 8002]);
        var previous = fixture.State.SavedTitles!;
        fixture.Publish(new(ImmutableHashSet<int>.Empty, NetworkCombatSourceStatus.Complete, true,
            At.AddSeconds(1), 2, TitleSource));
        Assert.Empty(fixture.State.SavedTitles!.OwnedTitleIds);
        Assert.Equal(2, previous.OwnedTitleIds.Count);
        var reloaded = new ExplorationService(fixture.Path, []);
        Assert.Empty(reloaded.TryGetSavedTitles()!.OwnedTitleIds);
    }

    [Fact]
    public void OlderOrRepeatedTitleFactsDoNotRewriteTheProfileDuringOtherStatUpdates()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001]);
        var unchanged = File.ReadAllText(fixture.Path);
        var marker = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.Path, marker);
        var changes = 0; fixture.Exploration.Changed += () => changes++;
        fixture.Publish(fixture.Source.CombatSnapshot.TitleOwnership);
        fixture.Publish(new(ImmutableHashSet.Create(9999), NetworkCombatSourceStatus.Complete, true,
            At.AddSeconds(-1), 0, TitleSource));
        Assert.Equal(0, changes);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(fixture.Path));
        Assert.Equal(unchanged, File.ReadAllText(fixture.Path));
        Assert.True(fixture.State.SavedTitles!.OwnedTitleIds.SetEquals([8001]));
    }

    [Fact]
    public void CharacterProfilesKeepSeparateListsAndBoundOwnWinsOverManualSelection()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001]);
        var first = fixture.Exploration.ActiveId;
        fixture.Login(102, 8, [9001, 9002]);
        var second = fixture.Exploration.ActiveId;
        Assert.NotEqual(first, second);
        fixture.Exploration.Select(first);
        Assert.Equal(second, fixture.State.SavedTitles!.ProfileId);
        Assert.True(fixture.State.SavedTitles!.OwnedTitleIds.SetEquals([9001, 9002]));
        Assert.True(fixture.Exploration.TryGetSavedTitles(first)!.OwnedTitleIds.SetEquals([8001]));
        fixture.Source.Running = false; fixture.Source.Raise();
        Assert.Equal(first, fixture.State.SavedTitles!.ProfileId);
        var changes = 0; fixture.State.Changed += () => changes++;
        fixture.Exploration.Select(second);
        Assert.True(changes > 0);
        Assert.Equal(second, fixture.State.SavedTitles!.ProfileId);
        Assert.False(fixture.State.Snapshot.IsFresh);
        fixture.Exploration.Add("Synthetic unsampled character");
        Assert.Null(fixture.State.SavedTitles);
        Assert.Null(fixture.Exploration.TryGetSavedTitles());
        Assert.True(fixture.Exploration.TryGetSavedTitles(first)!.OwnedTitleIds.SetEquals([8001]));
    }

    [Fact]
    public void LostBindingForeignProfilesAndUnqualifiedSourcesCannotReceiveACompleteList()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001]);
        var own = fixture.Source.CombatSnapshot.Character!;
        var profileId = fixture.Exploration.ActiveId;
        var domain = new NetworkCombatDomain<ImmutableHashSet<int>>(ImmutableHashSet.Create(9001),
            NetworkCombatSourceStatus.Complete, true, At.AddSeconds(1), 2, TitleSource);
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own, "unrelated-local-profile", domain));
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own with { Generation = 6 }, profileId, domain));
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own with { Identity = own.Identity with { EntityKey = 301 } }, profileId, domain));
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own, profileId, domain with { Source = "catalog" }));
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own, profileId, domain with { Value = ImmutableHashSet.Create(0) }));
        fixture.Exploration.MarkNetworkStale();
        Assert.False(fixture.Exploration.ApplyNetworkTitleOwnership(own, profileId, domain));
        fixture.Publish(domain);
        Assert.False(fixture.State.Snapshot.IsFresh);
        Assert.True(fixture.Exploration.TryGetSavedTitles(profileId)!.OwnedTitleIds.SetEquals([8001]));
    }

    [Fact]
    public void BindingAndSourceChangesDuringHistoricalReadsReturnNoPreviousOwnHistory()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001]);
        var reads = 0;
        fixture.Source.ReadCombat = () =>
        {
            if (++reads == 3) fixture.Source.Running = false;
        };
        Assert.Null(fixture.State.SavedTitles);
        fixture.Source.ReadCombat = null;
        Assert.NotNull(fixture.State.SavedTitles); // Selected historical profile is still available offline.
    }

    [Fact]
    public void MismatchedCombatGenerationCannotPersistAnotherOwnershipList()
    {
        using var fixture = new Fixture(); fixture.Login(101, 7, [8001]);
        fixture.Source.CombatSnapshot = fixture.Source.CombatSnapshot with
        {
            Generation = 8, Sequence = 2,
            TitleOwnership = new(ImmutableHashSet.Create(9001), NetworkCombatSourceStatus.Complete,
                true, At.AddSeconds(1), 2, TitleSource)
        };
        fixture.Source.Raise();
        Assert.True(fixture.State.SavedTitles!.OwnedTitleIds.SetEquals([8001]));
    }

    [Fact]
    public void LegacyProfilesWithoutTheOptionalHistoryStayUnknownAndCallbacksRunAfterTheProfileLock()
    {
        using var fixture = new Fixture(legacy: true);
        Assert.Null(fixture.State.SavedTitles);
        var callbacksReadWithoutLock = true;
        fixture.Exploration.Changed += () =>
        {
            var read = Task.Run(() => fixture.Exploration.TryGetSavedTitles());
            callbacksReadWithoutLock &= read.Wait(TimeSpan.FromSeconds(5));
        };
        fixture.Login(101, 7, [8001]);
        Assert.True(callbacksReadWithoutLock);
        Assert.NotNull(fixture.State.SavedTitles);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-title-history-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "exploration.json");
        internal Source Source { get; } = new();
        internal ExplorationService Exploration { get; }
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService State { get; }
        internal Fixture(bool legacy = false)
        {
            Directory.CreateDirectory(_directory);
            if (legacy) File.WriteAllText(Path, "{\"SchemaVersion\":2,\"Active\":\"11111111111111111111111111111111\",\"Characters\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Character 1\"}]}");
            Exploration = new(Path, []); Skills = new(Source, Exploration); State = new(Source, Skills, Exploration);
        }
        internal void Login(long character, long generation, int[] owned)
        {
            var identity = new OwnCharacterIdentity(character, 501, 300, "Synthetic character " + character) { ClassId = "Cleric" };
            Exploration.BindNetworkContext(new(identity, generation, 1, At));
            var own = new OwnCharacterContext(identity, generation) { ObservedAt = At };
            Source.CombatSnapshot = new() { Character = own, Generation = generation, Sequence = 1, ClassId = "Cleric",
                TitleOwnership = new(owned.ToImmutableHashSet(), NetworkCombatSourceStatus.Complete, true, At, 1, TitleSource) };
            Source.Raise();
        }
        internal void Publish(NetworkCombatDomain<ImmutableHashSet<int>> ownership)
        {
            Source.CombatSnapshot = Source.CombatSnapshot with { Sequence = Source.CombatSnapshot.Sequence + 1, TitleOwnership = ownership };
            Source.Raise();
        }
        public void Dispose() { State.Dispose(); Skills.Dispose(); Source.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource, IDisposable
    {
        private NetworkCharacterCombatSnapshot _snapshot = NetworkCharacterCombatSnapshot.Empty;
        internal Action? ReadCombat { get; set; }
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get { ReadCombat?.Invoke(); return _snapshot; } set => _snapshot = value; }
        public NetworkSkillSnapshot SkillSnapshot { get; set; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
        public void Dispose() { }
    }
}
