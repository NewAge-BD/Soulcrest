using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task TwentySecondsWithoutLootPausesAtTheLastGainAndResumesTheSameProductiveClock()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 2, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Drop(7, 3, 2); await f.Ready();
        var first = Assert.Single(f.Service.Snapshot.Sessions);
        f.Clock.Advance(TimeSpan.FromSeconds(19)); await f.Ready();
        Assert.False(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(29), Assert.Single(f.Service.Snapshot.Sessions).Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var paused = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.True(paused.IsPaused); Assert.Equal(LootTrackerSessionStatus.Live, paused.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), paused.Elapsed);
        Assert.Equal(paused.Elapsed, f.Service.OverlayState.Session!.Elapsed);
        Assert.Equal("Loot-Session pausiert.", f.Service.Status);
        var interrupted = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.False(interrupted.IsPaused); Assert.Equal(LootTrackerSessionStatus.Interrupted, interrupted.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), interrupted.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(20)); f.Source.Drop(7, 2, 3); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, resumed.Id); Assert.False(resumed.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(10), resumed.Elapsed); Assert.Equal(7, resumed.ItemCount);
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Drop(7, 1, 4); await f.Ready();
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(f.Service.Snapshot.Sessions).Elapsed);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15) },
            Assert.Single(f.Service.Snapshot.Sessions).TimelineEvents.Where(row => row.Kind == LootTrackerTimelineEventKind.Drop).Select(row => row.Elapsed));
    }

    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    public async Task ANewReceiptAtTheTwoMinuteBoundaryStartsANewSessionOnlyWhenTheDeadlineHasElapsed(int idle, bool completed)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.LootTrackerIdleTimeoutMinutes = 60; // Legacy configuration cannot change the new fixed policy.
        f.Source.Drop(7, 1, 1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(idle), fireTimers: false);
        f.Source.Drop(7, 1, 2); await f.Ready();
        var live = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.Equal(!completed, live.Id == id); Assert.False(live.IsPaused); Assert.Equal(TimeSpan.Zero, live.Elapsed);
        Assert.Equal(completed ? 1 : 2, live.ItemCount);
        if (completed) Assert.Equal(LootTrackerSessionStatus.Completed, f.Service.Snapshot.Sessions.Single(row => row.Id == id).Status);
    }

    [Fact]
    public async Task FailedGatheringAndRankLossDuringAPauseRemainFactsWithoutWakingOrExtendingIt()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 2, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Drop(7, 3, 2); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(20)); f.Source.Failed(1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Rank(-5, 1); await f.Ready();
        var paused = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.True(paused.IsPaused); Assert.Equal(TimeSpan.FromSeconds(10), paused.Elapsed);
        Assert.Equal(1, paused.FailedHarvestCount); Assert.Equal(5, paused.RankPointsLost);
        var failure = Assert.Single(paused.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestFailure);
        var loss = Assert.Single(paused.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.RankPoints);
        Assert.Equal(TimeSpan.FromSeconds(10), failure.Elapsed); Assert.Equal(TimeSpan.FromSeconds(10), loss.Elapsed);
        Assert.True(failure.At > paused.StartedAt.AddSeconds(10)); Assert.Equal(paused.StartedAt.AddSeconds(60), loss.At);
        f.Clock.Advance(TimeSpan.FromSeconds(70)); await f.Ready();
        var ended = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, ended.Status); Assert.False(ended.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(10), ended.Elapsed); Assert.Equal(loss.At, ended.EndedAt);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(ended.TimelineEvents.Select(row => (row.Id, row.Kind, row.At, row.Elapsed, row.Origin, row.Amount)),
            restored.TimelineEvents.Select(row => (row.Id, row.Kind, row.At, row.Elapsed, row.Origin, row.Amount)));
        Assert.Equal(ended.TimelineEvents.SelectMany(row => row.Loot).Select(row => (row.Id, row.Quantity)),
            restored.TimelineEvents.SelectMany(row => row.Loot).Select(row => (row.Id, row.Quantity)));
        Assert.Equal(1, restored.FailedHarvestCount); Assert.Equal(5, restored.RankPointsLost);
    }

    [Theory]
    [InlineData("kina")]
    [InlineData("ap")]
    [InlineData("rank")]
    [InlineData("soul")]
    [InlineData("harvest")]
    public async Task EveryPositiveLootChannelCanResumeTheSameSession(string channel)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(30)); await f.Ready();
        switch (channel)
        {
            case "kina": f.Source.Kina(10, 1); break;
            case "ap": f.Source.Points(10, 1); break;
            case "rank": f.Source.Rank(10, 1); break;
            case "soul": f.Source.Soul(2); break;
            case "harvest": f.Source.Harvest(1, new NetworkGatheringItem(8, 1)); break;
        }
        await f.Ready(); var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.False(session.IsPaused); Assert.Equal(TimeSpan.Zero, session.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(119)); await f.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(f.Service.Snapshot.Sessions).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(f.Service.Snapshot.Sessions).Status);
    }

    [Fact]
    public async Task RepeatedPausesRemoveOnlyIdleSpansAndUseTheMonotonicDeadline()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        for (var sequence = 2; sequence <= 4; sequence++)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Drop(7, 1, sequence * 2); await f.Ready();
            f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
            Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Drop(7, 1, sequence * 2 + 1); await f.Ready();
        }
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(TimeSpan.FromSeconds(15), session.Elapsed); Assert.Equal(7, session.ItemCount);
        f.Clock.AdjustWallClock(TimeSpan.FromHours(-2));
        f.Clock.Advance(TimeSpan.FromSeconds(120)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status); Assert.Equal(TimeSpan.FromSeconds(15), completed.Elapsed);
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("same-map-entry")]
    [InlineData("map")]
    public async Task TeleportAndMapBoundariesCompleteOutsideSessionsImmediately(string boundary)
    {
        await using var f = new Fixture(); await f.Ready(); f.Source.Drop(7, 1, 1); await f.Ready();
        if (boundary == "transition") f.WorldStatus = WorldContextStatus.Transitioning;
        if (boundary == "same-map-entry") f.Source.Switch(1);
        if (boundary == "map") f.MapDataId = 2;
        f.Source.Notify(); await f.Ready();
        var previous = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, previous.Status); Assert.Null(f.Service.OverlayState.Session);
        f.WorldStatus = WorldContextStatus.Confirmed; f.Source.Notify(); await f.Ready();
        f.Source.Drop(7, 1, 2); await f.Ready();
        Assert.NotEqual(previous.Id, Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live).Id);
    }

    [Fact]
    public async Task DuplicateReceiptsCannotResumeOrPreventTheDeadlineWithoutATimerCallback()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(20), fireTimers: false); f.Source.Drop(7, 1, 1); await f.Ready();
        Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        f.Clock.Advance(TimeSpan.FromSeconds(100), fireTimers: false); f.Source.Drop(7, 1, 1); await f.Ready();
        var ended = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, ended.Status); Assert.Equal(1, ended.ItemCount);
        Assert.Single(f.Starts); Assert.Single(f.Accepted);
    }

    [Fact]
    public async Task LowFrequencyRoutesFreezeTheIdleTailAndMarkTheResumedSegmentWithoutAFabricatedBridge()
    {
        await using var f = new Fixture(); await f.Ready();
        var x = 10d;
        f.Position = () => new(new("sample-map", x, 20, f.Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(10)); x = 20; f.Source.Drop(7, 1, 2); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(5)); x = 30; await f.Ready();
        Assert.Equal(3, Assert.Single(f.Service.Snapshot.Sessions).RouteSegments.Sum(row => row.Points.Length));
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Ready();
        var paused = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.True(paused.IsPaused); Assert.Equal(2, paused.RouteSegments.Sum(row => row.Points.Length));
        f.Clock.Advance(TimeSpan.FromSeconds(10)); x = 90; f.Source.Drop(7, 1, 3); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(3, resumed.RouteSegments.Length);
        Assert.All(resumed.RouteSegments.Take(2), row => Assert.False(row.StartsAfterPause));
        Assert.True(resumed.RouteSegments[^1].StartsAfterPause);
        Assert.Equal(TimeSpan.FromSeconds(10), resumed.RouteSegments[^1].Points[0].Elapsed);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.True(restored.RouteSegments[^1].StartsAfterPause);
        Assert.Equal(new[] { 10d, 20d, 90d }, restored.RouteSegments.SelectMany(row => row.Points).Select(row => row.X));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPauseOrResumeWritesPreserveOnlyCommittedLootAndReplay(bool failOnResume)
    {
        await using var f = new Fixture(replayPositions: true); await f.Ready();
        var facts = 0; f.Service.LootFactCommitted += _ => facts++;
        f.Source.Drop(7, 2, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.PositionRecorder!.Observe([new("sample-map", 1, 2, f.Clock.GetUtcNow(), f.Clock.GetTimestamp(),
            1, 1, 1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, ReplayPositionProvenance.Matched, 1)]);
        f.Clock.Advance(TimeSpan.FromSeconds(4)); f.Source.Drop(7, 3, 2); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.FailAppend = !failOnResume;
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        if (failOnResume)
        {
            Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
            f.FailAppend = true; f.Clock.Advance(TimeSpan.FromSeconds(10));
            f.Source.Drop(7, 100, 3); await f.Ready();
        }
        var interrupted = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.True(f.Service.StatusIsError); Assert.Equal(LootTrackerSessionStatus.Interrupted, interrupted.Status);
        Assert.False(interrupted.IsPaused); Assert.Equal(5, interrupted.ItemCount);
        Assert.Equal(TimeSpan.FromSeconds(5), interrupted.Elapsed); Assert.Equal(2, f.Accepted.Count); Assert.Equal(2, facts);
        Assert.Equal(2, interrupted.TimelineEvents.Count(row => row.Kind == LootTrackerTimelineEventKind.Drop));
        Assert.Null(f.Service.OverlayState.Session);
        await ReplayPositionStore.FlushAsync(f.SessionRoot, id);
        var replay = ReplayPositionStore.Read(f.SessionRoot, id, interrupted.Elapsed);
        Assert.False(replay.Truncated); Assert.Equal(1, Assert.Single(replay.Samples).FrameId);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(5, restored.ItemCount); Assert.Equal(TimeSpan.FromSeconds(5), restored.Elapsed);
    }

    [Fact]
    public async Task AReentrantResumeReceiptAppliesTheIdleOffsetExactlyOnce()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Drop(7, 2, 2); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        var reentrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.DropAccepted += drop =>
        {
            if (drop.Quantity == 3) f.Source.Drop(7, 4, 4);
            if (drop.Quantity == 4) reentrant.TrySetResult();
        };
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Drop(7, 3, 3);
        await reentrant.Task.WaitAsync(TimeSpan.FromSeconds(5)); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, resumed.Id); Assert.False(resumed.IsPaused); Assert.Equal(10, resumed.ItemCount);
        Assert.Equal(TimeSpan.FromSeconds(5), resumed.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Drop(7, 5, 5); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(15, current.ItemCount); Assert.Equal(TimeSpan.FromSeconds(10), current.Elapsed);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) },
            current.TimelineEvents.Where(row => row.Kind == LootTrackerTimelineEventKind.Drop).Select(row => row.Elapsed));
        Assert.Single(f.Starts); Assert.Equal(5, f.Accepted.Count);
    }

    [Fact]
    public async Task ADelayedOldOwnObservationCannotCloseTheNewlyStartedOutsideSession()
    {
        await using var f = new Fixture(); await f.Ready();
        f.BeforeBind = () =>
        {
            // The outer Capture has frozen the old own; these newer commands enter first.
            f.Source.Switch(1); f.Source.Drop(7, 1, 1);
        };
        f.Source.Notify(); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Live, current.Status); Assert.Equal(1, current.ItemCount);
        Assert.Single(f.Starts); Assert.Single(f.Accepted);
    }

    [Fact]
    public async Task AbyssResidenceNeverPausesOrExpiresAndRetainsNormalEntryContinuity()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(200)); await f.Ready();
        var live = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, live.Id); Assert.False(live.IsPaused); Assert.Equal(TimeSpan.FromSeconds(200), live.Elapsed);
        f.WorldStatus = WorldContextStatus.Transitioning; f.Source.LoseOwn(); await f.Ready();
        f.Source.Switch(1); f.WorldStatus = WorldContextStatus.Confirmed; f.Source.Notify(); await f.Ready();
        Assert.Equal(id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.False(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
    }
}
