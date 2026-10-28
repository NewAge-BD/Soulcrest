using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task AChangedTimeoutAndAMonotonicDeadlineEndOnlyAtTheLastConfirmedActivity()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Harvest(1, new NetworkGatheringItem(7, 1)); await f.Ready();
        var started = Assert.Single(f.Service.Snapshot.Sessions).StartedAt;
        f.Settings.Current.LootTrackerIdleTimeoutMinutes = 1;
        f.Clock.AdjustWallClock(TimeSpan.FromHours(-2));
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(started, completed.EndedAt); Assert.Equal(TimeSpan.Zero, completed.Elapsed);
        Assert.Equal(1, completed.HarvestCount); Assert.Null(f.Service.OverlayState.Session);
        Assert.Null(LootTrackerPresentation.PerHour(completed.HarvestCount, completed.Elapsed));
    }

    [Fact]
    public async Task AReceiptChecksTheSharedDeadlineWithoutDependingOnATimerCallback()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready(); var original = Assert.Single(f.Service.Snapshot.Sessions);
        f.Clock.Advance(TimeSpan.FromMinutes(2), fireTimers: false);
        f.Source.Failed(1); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions, row => row.Id == original.Id);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(original.StartedAt, completed.EndedAt); Assert.Equal(TimeSpan.Zero, completed.Elapsed);
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(original.Id, current.Id); Assert.Equal(1, current.FailedHarvestCount);
        Assert.Equal(0, current.ItemCount); Assert.Equal(2, f.Starts.Count); Assert.Single(f.Accepted);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unverified")]
    [InlineData("stopped")]
    public async Task AnUnavailableGatheringChannelCannotExtendAnExistingPveSession(string unavailable)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(100), fireTimers: false);
        switch (unavailable)
        {
            case "disabled": f.Settings.Current.LifeskillTrackingEnabled = false; break;
            case "unverified": f.Source.GatheringVerified = false; break;
            case "stopped": f.Source.Running = false; break;
        }
        f.Source.Harvest(1, new NetworkGatheringItem(8, 100)); f.Source.Failed(2); await f.Ready();
        f.Source.Running = true; f.Source.GatheringVerified = true;
        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(1, completed.ItemCount); Assert.Single(completed.Loot); Assert.Single(f.Accepted);
        Assert.DoesNotContain(completed.TimelineEvents, row => row.Kind is LootTrackerTimelineEventKind.HarvestSuccess
            or LootTrackerTimelineEventKind.HarvestFailure);
    }

    [Fact]
    public async Task TwoOwnCompletionsAtTheSameUnplacedNodeAreDistinctVisitsWithoutInventedRouteEvidence()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Harvest(2, new NetworkGatheringItem(7, 3)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, session.HarvestCount); Assert.Equal(5, session.ItemCount);
        Assert.Equal(5, Assert.Single(session.Loot).Quantity); Assert.Equal(2, f.Accepted.Count);
        var attempts = session.TimelineEvents.Where(row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).ToArray();
        Assert.Equal(2, attempts.Length); Assert.NotEqual(attempts[0].Id, attempts[1].Id);
        Assert.Equal(TimeSpan.FromSeconds(5), attempts[1].Elapsed);
        Assert.All(attempts, row => Assert.Null(row.GatherVisit));
        Assert.Empty(session.GatherVisits); Assert.Empty(session.RouteSegments);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(2, recovered.HarvestCount); Assert.Equal(5, recovered.ItemCount);
        var restored = recovered.TimelineEvents.Where(row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).ToArray();
        Assert.Equal(attempts.Select(row => (row.Id, row.Kind, row.At, row.Elapsed, row.Origin, row.GatherableId)),
            restored.Select(row => (row.Id, row.Kind, row.At, row.Elapsed, row.Origin, row.GatherableId)));
        Assert.Equal(attempts.SelectMany(row => row.Loot).Select(row => (row.Id, row.Kind, row.Name, row.Quantity)),
            restored.SelectMany(row => row.Loot).Select(row => (row.Id, row.Kind, row.Name, row.Quantity)));
        Assert.All(restored, row => Assert.Null(row.GatherVisit));
    }

    [Fact]
    public async Task AnOutsideCharacterSwitchFreezesThePreviousProfileAndStartsOnlyFromNewConfirmedActivity()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Failed(1); await f.Ready(); var first = Assert.Single(f.Service.Snapshot.Sessions);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Switch(2); await f.Ready();
        var old = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, old.Status); Assert.Equal(first.Character, old.Character);
        Assert.Equal(1, old.FailedHarvestCount); Assert.Equal(0, old.ItemCount);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 3)); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(first.Id, current.Id); Assert.NotEqual(first.Character!.ProfileId, current.Character!.ProfileId);
        Assert.Equal(3, current.ItemCount); Assert.Equal(1, current.HarvestCount);
        Assert.Equal(0, Assert.Single(f.Service.Snapshot.Sessions, row => row.Id == first.Id).ItemCount);
    }

    [Fact]
    public async Task DailyAbsoluteFactsRemainIndependentWhenOutsideRecordingIsPausedAndMayDecrease()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.MonsterLootTrackingEnabled = f.Settings.Current.LifeskillTrackingEnabled = false;
        f.Source.Daily(900_000); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Starts);
        Assert.True(f.Service.OverlayState.DailyConfirmed); Assert.Equal(900_000, f.Service.CachedDailyMonsterKina!.Total);
        f.Source.Daily(120); await f.Ready();
        Assert.Equal(120, f.Service.DailyMonsterKinaSnapshot.Total);
        var cached = new DailyMonsterKinaStore(Path.Combine(f.Root, "daily.json"))
            .Read(f.Service.CachedDailyMonsterKina.ProfileId);
        Assert.Equal(120, cached!.Total); Assert.Empty(f.Accepted);
        Assert.Empty(f.Service.Snapshot.Sessions);
    }

    [Fact]
    public async Task DeletingTheActiveOutsideSessionKeepsItsEchoGuardAndAbsoluteDailyCache()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(42); f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        await f.Service.DeleteSessionAsync(id); await f.Ready();
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Single(f.Accepted);
        Assert.Equal(42, f.Service.CachedDailyMonsterKina!.Total);
        f.Source.Failed(2); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.NotEqual(id, current.Id); Assert.Equal(1, current.FailedHarvestCount); Assert.Equal(0, current.ItemCount);
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, id)));
        Assert.Equal(current.Id, Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).Id);
    }

    [Fact]
    public async Task ShutdownDrainsAcceptedFactsAndRecoveryNeverReannouncesTheirDropsOrLiveTime()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); f.Source.Failed(2);
        await f.Service.DisposeAsync();
        Assert.Single(f.Starts); Assert.Single(f.Accepted);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, restored.Status);
        Assert.Equal(2, restored.ItemCount); Assert.Equal(1, restored.HarvestCount); Assert.Equal(1, restored.FailedHarvestCount);
        var notices = 0; var starts = 0;
        await using var replay = new UnifiedLootTrackerService(f.Settings, f.Source, f.Source, f.Source, f.Source,
            _ => new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character"), f.World,
            new(f.SessionRoot), f.Clock, dailyStore: new(Path.Combine(f.Root, "daily.json")));
        replay.DropAccepted += _ => notices++; replay.SessionStarted += _ => starts++;
        await replay.DrainAsync();
        Assert.Equal(restored.Id, Assert.Single(replay.Snapshot.Sessions).Id);
        Assert.Null(replay.OverlayState.Session); Assert.Equal(0, notices); Assert.Equal(0, starts);
    }

    [Fact]
    public async Task AbyssAbsoluteStageFactsRemainSeparateFromQualifiedSignedIncomeAndGaps()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Absolute(15_000); f.Source.Notify(); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        var first = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.PointsStageReached);
        Assert.Equal("rank-19", first.FromStageId); Assert.Equal("rank-17", first.ToStageId);
        Assert.Equal(0, session.RankPointsGained); Assert.Equal(0, session.AbyssPoints);
        Assert.Null(session.LastLootAt);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.ClearAbsolute(); await f.Ready();
        f.Source.Absolute(100_000); await f.Ready();
        Assert.Single(Assert.Single(f.Service.Snapshot.Sessions).TimelineEvents,
            row => row.Kind == LootTrackerTimelineEventKind.PointsStageReached);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Absolute(200_000); await f.Ready();
        f.Source.Rank(-20, 1); f.Source.Rank(-20, 1); f.Source.Rank(5, 2); f.Source.Points(12, 1); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, current.TimelineEvents.Count(row => row.Kind == LootTrackerTimelineEventKind.PointsStageReached));
        Assert.Equal(20, current.RankPointsLost); Assert.Equal(5, current.RankPointsGained); Assert.Equal(12, current.AbyssPoints);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(current.TimelineEvents.ToArray(), restored.TimelineEvents.ToArray()); Assert.Equal(current.RankPointsLost, restored.RankPointsLost);
    }
}
