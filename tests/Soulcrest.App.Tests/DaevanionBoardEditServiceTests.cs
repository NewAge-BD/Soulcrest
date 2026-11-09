using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionBoardEditServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncludedPartialBoardIsKnownAndMissingBoardsAreNotEmptyObservations()
    {
        using var f = new Fixture(); f.Publish();
        var snapshot = f.Edit.Snapshot; var board = f.Row();
        Assert.True(snapshot.IsFresh); Assert.False(snapshot.IsActive);
        Assert.Equal("profile-a", snapshot.ProfileId); Assert.Equal("Cleric", snapshot.ClassId);
        Assert.Equal(6, snapshot.Boards.Count); Assert.True(board.IsKnown); Assert.True(board.CanEdit);
        Assert.Equal(new[] { f.Start }, board.ActualNodeIds.Order());
        Assert.All(snapshot.Boards.Where(row => row.Board.Id != f.Board.Id), row =>
        { Assert.False(row.IsKnown); Assert.False(row.CanEdit); Assert.Empty(row.ActualNodeIds); });
        Assert.False(f.Edit.ToggleNode(snapshot.Boards.Last().Board.Id, f.Inactive));
    }

    [Fact]
    public void ClicksToggleDesiredMembershipAgainstActualAndNeverMutateTheStartOrSource()
    {
        using var f = new Fixture(); f.Publish(f.First);
        var original = f.Source.CombatSnapshot.DaevanionBoards.Value!;
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.First));
        Assert.True(f.Edit.Snapshot.IsActive); Assert.Contains(f.First, f.Row().RemovedNodeIds);
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.First));
        Assert.Empty(f.Row().RemovedNodeIds); Assert.Contains(f.First, f.Row().DesiredNodeIds);
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.Inactive));
        Assert.Contains(f.Inactive, f.Row().AddedNodeIds);
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.Inactive)); Assert.Empty(f.Row().AddedNodeIds);
        var revision = f.Edit.Snapshot.Revision;
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.Start));
        Assert.False(f.Edit.ToggleNode(f.Board.Id, int.MaxValue)); Assert.Equal(revision, f.Edit.Snapshot.Revision);
        Assert.Same(original, f.Source.CombatSnapshot.DaevanionBoards.Value);
        Assert.Equal(new[] { f.First }, original.Boards[f.Board.Id].Order());
    }

    [Fact]
    public void AnActiveEmptyDraftOverridesASavedRouteUntilExplicitlyEnded()
    {
        using var f = new Fixture(); f.Publish();
        Assert.Null(f.Edit.ForOverlay(f.Own, f.Board));
        Assert.True(f.Edit.BeginFromCurrent());
        var empty = f.Edit.ForOverlay(f.Own, f.Board)!;
        Assert.True(empty.CanPresent); Assert.Empty(empty.AddedNodeIds); Assert.Empty(empty.RemovedNodeIds);
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.First));
        Assert.True(f.Edit.BeginFromCurrent()); Assert.Contains(f.First, f.Row().AddedNodeIds);
        Assert.True(f.Edit.ResetToCurrent()); Assert.True(f.Edit.Snapshot.IsActive); Assert.Empty(f.Row().AddedNodeIds);
        Assert.True(f.Edit.EndEdit()); Assert.False(f.Edit.Snapshot.IsActive); Assert.Null(f.Edit.ForOverlay(f.Own, f.Board));
    }

    [Fact]
    public void LiveFulfilmentConsumesBooleanIntentionsAndLaterChangesDoNotResurrectThem()
    {
        using var f = new Fixture(); f.Publish(f.First);
        f.Edit.ToggleNode(f.Board.Id, f.First); f.Edit.ToggleNode(f.Board.Id, f.Inactive);
        f.Publish(f.Inactive);
        Assert.True(f.Edit.Snapshot.IsActive); Assert.Empty(f.Row().AddedNodeIds); Assert.Empty(f.Row().RemovedNodeIds);
        f.Publish(f.First);
        Assert.Contains(f.First, f.Row().DesiredNodeIds); Assert.DoesNotContain(f.Inactive, f.Row().DesiredNodeIds);
        Assert.Empty(f.Row().AddedNodeIds); Assert.Empty(f.Row().RemovedNodeIds);
    }

    [Fact]
    public void UnrelatedScoreOrSkillProjectionAndRepeatedIdenticalBoardsDoNotPublishAnotherRevision()
    {
        using var f = new Fixture(); f.Publish();
        var before = f.Edit.Snapshot; var events = 0; f.Edit.Changed += () => Interlocked.Increment(ref events);
        f.Change(source => source with { CurrentCombatScore = 90000 });
        Assert.Same(before, f.Edit.Snapshot);
        f.Change(source => source with { DaevanionBoards = source.DaevanionBoards with { Sequence = source.Sequence } });
        Assert.Same(before, f.Edit.Snapshot); Assert.Equal(0, Volatile.Read(ref events));
        f.Publish(); Assert.True(f.Edit.Snapshot.ObservedAt > before.ObservedAt);
        Assert.True(f.Edit.Snapshot.Revision > before.Revision);
    }

    [Fact]
    public void ADeferredCallbackBeforeCharacterRefreshCannotInvalidateAnUnchangedOwnBoardOnACpUpdate()
    {
        using var f = new Fixture(); f.Publish();
        var before = f.Edit.Snapshot;
        var reached = false; var freshness = new List<bool>();
        f.Edit.Changed += () => { lock (freshness) freshness.Add(f.Edit.Snapshot.IsFresh); };
        f.BeforeCharacterRefresh = () =>
        {
            reached = true;
            // Network already has the newer CP-only object, but CharacterCombatStateService
            // has not received that event yet. Its cached derived snapshot is genuinely Empty.
            Assert.False(f.Character.Snapshot.IsFresh);
            Assert.Equal("profile-a", f.Character.ResolveCurrentOwnProfileId(f.Source.CombatSnapshot));
            // This is the same editor callback used by delayed graph/skill notifications.
            f.Source.RaiseOnly(f.Edit);
            Assert.Same(before, f.Edit.Snapshot);
        };
        f.Change(source => source with { CurrentCombatScore = 90000 });
        Assert.True(reached); Assert.Same(before, f.Edit.Snapshot);
        lock (freshness) Assert.DoesNotContain(false, freshness);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("not-fresh")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    [InlineData("wrong-source")]
    [InlineData("no-time")]
    [InlineData("no-sequence")]
    public void AnInvalidBoardSourceCannotEditOrExposeOverlayDiffs(string variant)
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First);
        f.Change(source => source with { DaevanionBoards = variant switch
        {
            "stale" => source.DaevanionBoards with { Status = NetworkCombatSourceStatus.Stale },
            "not-fresh" => source.DaevanionBoards with { IsFresh = false },
            "conflict" => source.DaevanionBoards with { Status = NetworkCombatSourceStatus.Conflict },
            "unknown" => source.DaevanionBoards with { Status = NetworkCombatSourceStatus.Unknown },
            "wrong-source" => source.DaevanionBoards with { Source = "unqualified-board-source" },
            "no-time" => source.DaevanionBoards with { ObservedAt = null },
            "no-sequence" => source.DaevanionBoards with { Sequence = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        } });
        Assert.False(f.Edit.Snapshot.IsFresh); Assert.False(f.Row().CanEdit);
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.Inactive));
        var overlay = f.Edit.ForOverlay(f.Own, f.Board)!;
        Assert.NotNull(overlay); Assert.False(overlay.CanPresent); Assert.Empty(overlay.AddedNodeIds);
        f.Publish(); Assert.Contains(f.First, f.Row().AddedNodeIds);
    }

    [Fact]
    public void ANewSubsetCannotMixAnOlderMissingBoardIntoTheCurrentSource()
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First);
        var second = DaevanionCatalog.Default.GetBoard("Cleric", "zikel")!;
        f.Change(source => source with { DaevanionBoards = source.DaevanionBoards with
        { Value = new(ImmutableDictionary<int, ImmutableHashSet<int>>.Empty.Add(second.Id, ImmutableHashSet<int>.Empty)) } });
        Assert.False(f.Row().IsKnown); Assert.False(f.Row().CanEdit);
        Assert.False(f.Edit.ForOverlay(f.Own, f.Board)!.CanPresent);
        Assert.True(f.Edit.Snapshot.Boards.Single(row => row.Board.Id == second.Id).IsKnown);
        f.Publish(); Assert.Contains(f.First, f.Row().AddedNodeIds);
    }

    [Fact]
    public void SameOwnerRejoinRetainsRamIntentButNeedsFreshBoardConfirmationAndTheNewOwnContext()
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First); var oldOwn = f.Own;
        f.Change(source => source with { Character = null, Generation = 8,
            DaevanionBoards = source.DaevanionBoards with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } });
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.Inactive));
        Assert.False(f.Edit.ForOverlay(oldOwn, f.Board)!.CanPresent);
        f.Own = new(oldOwn.Identity with { EntityKey = 999 }, 8) { ObservedAt = At.AddMinutes(1) };
        f.Publish();
        Assert.Contains(f.First, f.Row().AddedNodeIds); Assert.Equal(8, f.Edit.Snapshot.Generation);
        Assert.True(f.Edit.ForOverlay(f.Own, f.Board)!.CanPresent);
        Assert.False(f.Edit.ForOverlay(oldOwn, f.Board)!.CanPresent);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("server")]
    [InlineData("profile")]
    [InlineData("class")]
    public void AForeignBindingNeverBorrowsThePreviousLocalIntent(string variant)
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First);
        var previous = f.Own;
        f.Own = variant switch
        {
            "character" => new(previous.Identity with { CharacterId = 102 }, 8),
            "server" => new(previous.Identity with { ServerId = 502 }, 8),
            "class" => new(previous.Identity with { ClassId = "Sorcerer" }, 8),
            _ => previous
        };
        if (variant == "profile") f.Profile = "profile-b";
        f.Publish();
        Assert.False(f.Edit.Snapshot.IsActive); Assert.All(f.Edit.Snapshot.Boards, row => Assert.Empty(row.AddedNodeIds));
        Assert.Null(f.Edit.ForOverlay(f.Own, f.Board));
    }

    [Fact]
    public void NativeIdentityAndSourceGenerationAreRecheckedEvenWithoutAChangedEvent()
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First); var original = f.Own;
        f.Source.CombatSnapshot = f.Source.CombatSnapshot with
        { Character = new(original.Identity with { CharacterId = 5000 }, 8), Generation = 8 };
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.Inactive));
        Assert.False(f.Edit.ForOverlay(original, f.Board)!.CanPresent);
        Assert.False(f.Edit.Snapshot.IsFresh);
    }

    [Fact]
    public void ARejectedClickCannotReappearAsAnIntentionAfterTheSourceReturns()
    {
        using var f = new Fixture(); f.Publish();
        var interrupted = false;
        f.Edit.Changed += () =>
        {
            if (interrupted) return;
            interrupted = true;
            // Change authority after the tentative local mutation, before its final fence.
            f.Source.CombatSnapshot = f.Source.CombatSnapshot with { Sequence = 900 };
        };
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.First));
        f.Publish();
        Assert.False(f.Edit.Snapshot.IsActive); Assert.Empty(f.Row().AddedNodeIds);
        Assert.True(f.Edit.BeginFromCurrent()); Assert.Empty(f.Row().AddedNodeIds);
    }

    [Fact]
    public void LosingProfileResolutionDuringTheClickCallbackRejectsAndRollsBackTheNewIntention()
    {
        using var f = new Fixture(); f.Publish();
        var originalNetwork = f.Source.CombatSnapshot;
        var interrupted = false;
        f.Edit.Changed += () =>
        {
            if (interrupted) return;
            interrupted = true; f.Bound = false;
        };
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.First));
        Assert.Same(originalNetwork, f.Source.CombatSnapshot);
        f.Bound = true;
        Assert.False(f.Edit.Snapshot.IsActive); Assert.Empty(f.Row().AddedNodeIds);
        Assert.True(f.Edit.BeginFromCurrent()); Assert.Empty(f.Row().AddedNodeIds);
    }

    [Fact]
    public async Task LosingProfileResolutionInAnOverlayPublicationCallbackCannotReturnAnOldPresentableResult()
    {
        using var f = new Fixture(); f.Publish(); Assert.True(f.Edit.BeginFromCurrent());
        using var publishedCharacter = new ManualResetEventSlim();
        using var continueSource = new ManualResetEventSlim();
        var intercepting = 0;
        // Pause the producer after CharacterCombatStateService publishes its new coherent
        // snapshot, before the edit service receives that callback. ForOverlay then performs
        // the real pending publication itself; no reflection or fake edit snapshot is needed.
        f.BeforeEditRefresh = () =>
        {
            if (Volatile.Read(ref intercepting) == 0
                || f.Source.CombatSnapshot.DaevanionBoards.Value?.Boards[f.Board.Id].Contains(f.First) != true) return;
            publishedCharacter.Set(); continueSource.Wait(TimeSpan.FromSeconds(10));
        };
        var producer = Task.Run(() =>
        {
            Volatile.Write(ref intercepting, 1);
            f.Publish(f.First);
        });
        try
        {
            Assert.True(publishedCharacter.Wait(TimeSpan.FromSeconds(10)));
            var once = false; var callbackWasFresh = false;
            f.Edit.Changed += () =>
            {
                if (once) return;
                once = true; callbackWasFresh = f.Edit.Snapshot.IsFresh; f.Bound = false;
            };
            var overlay = f.Edit.ForOverlay(f.Own, f.Board);
            Assert.True(once); Assert.True(callbackWasFresh);
            Assert.NotNull(overlay); Assert.False(overlay.CanPresent);
            Assert.Empty(overlay.AddedNodeIds); Assert.Empty(overlay.RemovedNodeIds);
        }
        finally { Volatile.Write(ref intercepting, 0); continueSource.Set(); await producer; }
    }

    [Fact]
    public void ASourceChangeDuringTheCoherentReadDoesNotPublishAnEmptyFrameBetweenFreshBoards()
    {
        using var f = new Fixture(); f.Publish();
        var freshness = new List<bool>();
        f.Edit.Changed += () => { lock (freshness) freshness.Add(f.Edit.Snapshot.IsFresh); };
        f.Source.OnRead = () => f.Publish(f.First);
        var result = f.Edit.Snapshot;
        Assert.True(result.IsFresh); Assert.Contains(f.First, f.Row().ActualNodeIds);
        lock (freshness) Assert.DoesNotContain(false, freshness);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void MissingOrInsufficientLevelPreventsEditingWithoutInventingABudget(int? level)
    {
        using var f = new Fixture(); f.Level = level; f.Publish();
        Assert.True(f.Row().IsKnown); Assert.False(f.Row().IsUnlocked); Assert.False(f.Row().CanEdit);
        Assert.False(f.Edit.BeginFromCurrent()); Assert.False(f.Edit.ToggleNode(f.Board.Id, f.First));
    }

    [Fact]
    public void UnknownOrForeignNodeIdsDoNotBecomeACompleteActivationBaseline()
    {
        using var f = new Fixture(); f.Publish(int.MaxValue);
        Assert.False(f.Row().IsKnown); Assert.Empty(f.Row().ActualNodeIds); Assert.False(f.Row().CanEdit);
        Assert.False(f.Edit.ToggleNode(f.Board.Id, f.First));
    }

    [Fact]
    public void ALocalSelectionDoesNotRequireSolverConnectivityOrAnObservedPointBudget()
    {
        using var f = new Fixture(); f.Publish();
        var far = f.Board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start)
            .OrderByDescending(node => Math.Abs(node.Row - f.Board.Nodes.Single(n => n.Id == f.Start).Row)
                + Math.Abs(node.Column - f.Board.Nodes.Single(n => n.Id == f.Start).Column)).First();
        Assert.True(f.Edit.ToggleNode(f.Board.Id, far.Id)); Assert.Contains(far.Id, f.Row().AddedNodeIds);
        Assert.Null(f.Character.Snapshot.PointPools.First().Total);
    }

    [Fact]
    public void SnapshotsAreImmutableAndNotificationsRunOutsideThePublicationGate()
    {
        using var f = new Fixture(); f.Publish(); var before = f.Edit.Snapshot;
        var completed = false;
        f.Edit.Changed += () =>
        {
            // A second thread must be able to read the snapshot while the subscriber runs.
            completed = Task.Run(() => f.Edit.Snapshot.IsActive).Wait(TimeSpan.FromSeconds(2));
        };
        Assert.True(f.Edit.ToggleNode(f.Board.Id, f.First)); Assert.True(completed);
        Assert.Empty(before.Boards.Single(row => row.Board.Id == f.Board.Id).AddedNodeIds);
        if (f.Row().ActualNodeIds is ISet<int> set)
            Assert.Throws<NotSupportedException>(() => set.Add(f.Inactive));
        Assert.Throws<NotSupportedException>(() => ((IList<DaevanionBoardEditBoard>)before.Boards).Clear());
    }

    [Fact]
    public void StoppedOrUnboundSourcesCannotBorrowTheSelectedProfileAndDisposeDropsRamOnly()
    {
        using var f = new Fixture(); f.Publish(); f.Edit.ToggleNode(f.Board.Id, f.First);
        f.Source.Running = false; f.Source.Raise(); Assert.False(f.Edit.Snapshot.IsFresh);
        Assert.False(f.Edit.ForOverlay(f.Own, f.Board)!.CanPresent);
        f.Source.Running = true; f.Bound = false; f.Source.Raise();
        Assert.Null(f.Edit.Snapshot.ProfileId); Assert.False(f.Edit.ToggleNode(f.Board.Id, f.Inactive));
        f.Edit.Dispose(); Assert.Null(f.Edit.ForOverlay(f.Own, f.Board)); Assert.False(f.Edit.ToggleNode(f.Board.Id, f.First));
    }

    private sealed class Fixture : IDisposable
    {
        internal Source Source { get; } = new();
        internal string Profile { get; set; } = "profile-a";
        internal bool Bound { get; set; } = true;
        internal int? Level { get; set; } = 45;
        internal OwnCharacterContext Own { get; set; } = new(new(101, 501, 300, "Synthetic") { ClassId = "Cleric" }, 7) { ObservedAt = At };
        internal DaevanionBoard Board => DaevanionCatalog.Default.GetBoard(Own.Identity.ClassId!, "nezekan")!;
        internal int Start => Board.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start).Id;
        internal int First => Board.Nodes.First(node => node.Kind != DaevanionNodeKind.Start).Id;
        internal int Inactive => Board.Nodes.Last(node => node.Kind != DaevanionNodeKind.Start).Id;
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService Character { get; }
        internal DaevanionBoardEditService Edit { get; }
        internal Action? BeforeCharacterRefresh { get; set; }
        internal Action? BeforeEditRefresh { get; set; }
        private long _sequence;
        internal Fixture()
        {
            LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext _) => Bound ? new(Profile, "Synthetic") : null;
            Source.Changed += () => BeforeCharacterRefresh?.Invoke();
            Skills = new(Source, Resolve); Character = new(Source, Skills, Resolve);
            Character.Changed += () => BeforeEditRefresh?.Invoke();
            Edit = new(Character, Source);
        }
        internal void Publish(params int[] ids)
        {
            var sequence = ++_sequence;
            Source.CombatSnapshot = new()
            {
                Character = Own, ClassId = Own.Identity.ClassId, CharacterLevel = Level, Generation = Own.Generation, Sequence = sequence,
                DaevanionBoards = new(new(ImmutableDictionary<int, ImmutableHashSet<int>>.Empty.Add(Board.Id, ids.ToImmutableHashSet())),
                    NetworkCombatSourceStatus.Partial, true, At.AddSeconds(sequence), sequence, "own-daevanion-board-lists")
            };
            Source.Raise();
        }
        internal void Change(Func<NetworkCharacterCombatSnapshot, NetworkCharacterCombatSnapshot> change)
        {
            Source.CombatSnapshot = change(Source.CombatSnapshot) with { Sequence = ++_sequence }; Source.Raise();
        }
        internal DaevanionBoardEditBoard Row() => Edit.Snapshot.Boards.Single(row => row.Board.Id == Board.Id);
        public void Dispose() { Edit.Dispose(); Character.Dispose(); Skills.Dispose(); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; } = true;
        private NetworkCharacterCombatSnapshot _snapshot = NetworkCharacterCombatSnapshot.Empty;
        private Action? _onRead;
        internal Action? OnRead { set => Interlocked.Exchange(ref _onRead, value); }
        public NetworkCharacterCombatSnapshot CombatSnapshot
        {
            get { var value = _snapshot; Interlocked.Exchange(ref _onRead, null)?.Invoke(); return value; }
            set => _snapshot = value;
        }
        public NetworkSkillSnapshot SkillSnapshot { get; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
        internal void RaiseOnly(object target)
        {
            foreach (var callback in Changed?.GetInvocationList() ?? [])
                if (ReferenceEquals(callback.Target, target)) ((Action)callback)();
        }
    }
}
