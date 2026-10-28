using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerSessionReplayTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static LootTrackerRoutePoint Point(double x, int second) => new(x, 1000, At.AddSeconds(second), TimeSpan.FromSeconds(second));
    private static LootTrackerSession Session(params LootTrackerRouteSegment[] routes) => new()
    { StartedAt = At, Elapsed = TimeSpan.FromSeconds(20), RouteSegments = [.. routes] };

    [Fact]
    public void ScrubbingFiltersFuturePointsAndExpiresTheDotWithoutLosingTheCameraPosition()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), Point(200, 5), Point(300, 10)]));
        var middle = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(6));
        Assert.Equal([100d, 200d], Assert.Single(middle.Session.RouteSegments).Points.Select(row => row.X).ToArray());
        Assert.Equal(200, middle.PlayerPosition!.X);
        Assert.True(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(7)).IsFresh);
        var stale = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(8));
        Assert.False(stale.IsFresh); Assert.Null(stale.PlayerPosition); Assert.Equal(200, stale.LastPosition!.X);
        var back = LootTrackerSessionReplay.Build(session, TimeSpan.Zero);
        Assert.Single(Assert.Single(back.Session.RouteSegments).Points); Assert.Equal(100, back.PlayerPosition!.X);
        Assert.Equal(3, Assert.Single(session.RouteSegments).Points.Length);
    }

    [Fact]
    public void RepeatedMapChangesFollowChronologyWithoutConnectingSeparateSegments()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), Point(200, 2)]),
            new LootTrackerRouteSegment("b", "B", [Point(500, 5)]), new LootTrackerRouteSegment("a", "A", [Point(800, 10)]));
        Assert.Equal("a", LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(2)).MapId);
        Assert.Equal("b", LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(6)).MapId);
        var returned = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(10));
        Assert.Equal("a", returned.MapId); Assert.Equal(800, returned.PlayerPosition!.X);
        Assert.Equal(3, returned.Session.RouteSegments.Length);
    }

    [Fact]
    public void InvalidOrUntimedSamplesDoNotBridgeEarlierPartsOfATimedReplay()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), new(double.NaN, 10, At.AddSeconds(1), TimeSpan.FromSeconds(1)),
            Point(300, 2), new(400, 1000), Point(500, 4)]));
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(4));
        Assert.Equal(3, replay.Session.RouteSegments.Length);
        Assert.All(replay.Session.RouteSegments, segment => Assert.Single(segment.Points));
        Assert.Equal(500, replay.PlayerPosition!.X);
    }

    [Fact]
    public void LegacyUntimedRoutesAppearOnlyAtTheEndAndNeverClaimASynchronousPlayerPosition()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [new(100, 100), new(200, 200)]));
        Assert.Empty(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(19)).Session.RouteSegments);
        var end = LootTrackerSessionReplay.Build(session, session.Elapsed);
        Assert.Equal(session.RouteSegments[0].Points.ToArray(), Assert.Single(end.Session.RouteSegments).Points.ToArray()); Assert.Equal("a", end.MapId);
        Assert.Null(end.PlayerPosition); Assert.Null(end.LastPosition);
    }

    [Fact]
    public void LegacyFarmStopsWithoutVisitsAppearOnlyAtTheEndWithoutInventingAWalkOrPlayerPosition()
    {
        var session = Session() with
        {
            Kind = LootTrackerSessionKind.Lifeskill, FarmRouteVersion = 1,
            FarmRoutePoints = [new("stop-a", 0, "a", "A", 100, 100, "Resource A", 2, 1, 1),
                new("stop-b", 0, "a", "A", 200, 200, "Resource B", 1, 1, 0)]
        };
        var earlier = LootTrackerSessionReplay.Build(session, session.Elapsed - TimeSpan.FromSeconds(1));
        Assert.Empty(earlier.Session.FarmRoutePoints); Assert.Empty(earlier.Session.RouteSegments);
        Assert.Null(earlier.MapId); Assert.Null(earlier.PlayerPosition);
        var end = LootTrackerSessionReplay.Build(session, session.Elapsed);
        Assert.Equal(session.FarmRoutePoints.ToArray(), end.Session.FarmRoutePoints.ToArray());
        Assert.Equal("a", end.MapId); Assert.Empty(end.Session.RouteSegments);
        Assert.Null(end.PlayerPosition); Assert.Null(end.LastPosition); Assert.False(end.IsFresh);
    }

    [Fact]
    public void OriginalLegacyObservationTimesCanFilterRoutesWithoutInventingMissingTimes()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [new(100, 100, At), new(200, 200, At.AddSeconds(10))]));
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5));
        Assert.Single(Assert.Single(replay.Session.RouteSegments).Points); Assert.False(replay.IsFresh);
    }

    [Fact]
    public void FutureCombatAndGroupedVisitCountsRemainHiddenEvenWhenWallClockMovesBackwards()
    {
        var first = new LootTrackerGatherVisit("visit-1", 0, "a", "A", 100, 100, 110, 110,
            At.AddSeconds(5), true, "Resource", CatalogNodeId: "node", GatherableId: 1);
        var future = first with { Id = "visit-2", At = At.AddHours(-1) };
        var session = Session() with
        {
            Kind = LootTrackerSessionKind.Abyss, TimelineVersion = 1,
            GatherVisits = [first, future],
            TimelineEvents = [new("one", LootTrackerTimelineEventKind.HarvestSuccess, first.At, TimeSpan.FromSeconds(5)) { GatherVisit = first },
                new("two", LootTrackerTimelineEventKind.HarvestSuccess, future.At, TimeSpan.FromSeconds(10)) { GatherVisit = future }],
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddSeconds(2), TimeSpan.FromSeconds(2)),
                new("death", LootTrackerCombatKind.Death, At.AddSeconds(12), TimeSpan.FromSeconds(12))]
        };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(6));
        Assert.Single(replay.Session.CombatEvents); Assert.Single(replay.Session.GatherVisits);
        var marker = Assert.Single(LootTrackerSessionMarkers.All(replay.Session)); Assert.Single(marker.Times);
        Assert.Equal(100, replay.PlayerPosition!.X); // Original player position, not the snapped node.
        Assert.Equal(2, session.GatherVisits.Length);
    }

    [Fact]
    public void CombatDoesNotRefreshAnOlderVisualPositionToTheCombatTimestamp()
    {
        var session = Session() with { Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [new("death", LootTrackerCombatKind.Death, At.AddSeconds(5), TimeSpan.FromSeconds(5),
                new("a", "A", 100, 100, At.AddSeconds(3)))] };
        Assert.True(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5)).IsFresh);
        var stale = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(6));
        Assert.False(stale.IsFresh); Assert.Equal(100, stale.LastPosition!.X); Assert.Equal("a", stale.MapId);
    }

    [Fact]
    public void LifeskillFarmStopsAreRebuiltFromThePrefixWithoutReplacingTheObservedWalk()
    {
        var first = new LootTrackerGatherVisit("first", 0, "a", "A", 100, 100, 110, 110, At, true, "Resource");
        var future = first with { Id = "future", X = 1000, OriginalX = 990, At = At.AddSeconds(10) };
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), Point(300, 5), Point(990, 10)])) with
        { Kind = LootTrackerSessionKind.Lifeskill, FarmRouteVersion = 1, GatherVisits = [first, future],
            FarmRoutePoints = LifeskillFarmRouteBuilder.Build([first, future]) };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5));
        Assert.Single(replay.Session.FarmRoutePoints); Assert.Single(replay.Session.GatherVisits);
        Assert.Equal([100d, 300d], Assert.Single(replay.Session.RouteSegments).Points.Select(row => row.X).ToArray());
        Assert.Equal(2, session.FarmRoutePoints.Length);
    }

    [Fact]
    public void LegacyResourceReferencesAreRemappedAfterFutureSegmentsAreRemoved()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 10)]), new LootTrackerRouteSegment("b", "B", [Point(200, 2)])) with
        { ResourceNodes = [new("node", "Resource", "b", "B", 210, 1010, 1, 0, At.AddSeconds(2))] };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5));
        Assert.Equal(0, Assert.Single(replay.Session.ResourceNodes).SegmentIndex);
        Assert.Single(LootTrackerPresentation.GetValidatedResourceVisits(replay.Session));
    }
}
