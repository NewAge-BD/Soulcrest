using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LifeskillFarmRouteBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static LootTrackerGatherVisit Visit(string id, double x, double y = 100, bool succeeded = true,
        string? node = null, int? resource = 123, int section = 0, string map = "map", int size = 8192) =>
        new(id, section, map, "Test map", x, y, x, y, Now, succeeded, "Test resource", size, resource, node);

    [Fact]
    public void LaterRotationInsertsMiddleNodeWithoutReplacingOldStopsOrRepeatingTheLastNode()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("first", 100), Visit("last", 300),
            Visit("middle", 200), Visit("last-again", 300)]);
        Assert.Equal(["first", "middle", "last"], route.Select(point => point.Id));
        Assert.Equal([100d, 200d, 300d], route.Select(point => point.X));
        Assert.Equal([1, 1, 2], route.Select(point => point.VisitCount));
    }

    [Fact]
    public void FirstFailureCreatesAStopAndFailuresRemainInVisitAndOutcomeTotals()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("failure", 100, succeeded: false),
            Visit("success", 100), Visit("failure-again", 100, succeeded: false)]);
        var point = Assert.Single(route);
        Assert.Equal("failure", point.Id);
        Assert.Equal(3, point.VisitCount);
        Assert.Equal(1, point.SuccessCount);
        Assert.Equal(2, point.FailureCount);
    }

    [Theory]
    [InlineData(8192, 16, 1)]
    [InlineData(8192, 16.001, 2)]
    [InlineData(4096, 8, 1)]
    [InlineData(4096, 8.001, 2)]
    public void PositionToleranceIncludesBoundaryAndScalesWithMapSize(int size, double distance, int expected)
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("first", 100, size: size),
            Visit("next", 100 + distance, size: size)]);
        Assert.Equal(expected, route.Length);
        Assert.Equal(100, route[0].X);
    }

    [Fact]
    public void MatchingNearestFallbackWinsAndAnExactTieUsesTheOlderStop()
    {
        var nearest = LifeskillFarmRouteBuilder.Build([Visit("first", 100), Visit("second", 130), Visit("near-second", 118)]);
        Assert.Equal(1, nearest.Single(point => point.Id == "first").VisitCount);
        Assert.Equal(2, nearest.Single(point => point.Id == "second").VisitCount);
        var tie = LifeskillFarmRouteBuilder.Build([Visit("first", 100), Visit("second", 130), Visit("tie", 115)]);
        Assert.Equal(2, tie.Single(point => point.Id == "first").VisitCount);
        Assert.Equal(1, tie.Single(point => point.Id == "second").VisitCount);
    }

    [Fact]
    public void RepeatedFallbackVisitsDoNotDriftTheAnchorToAbsorbAnotherStop()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("first", 100), Visit("near", 115), Visit("beyond-anchor", 130)]);
        Assert.Equal(2, route.Length);
        Assert.Equal(100, route[0].X);
        Assert.Equal(2, route[0].VisitCount);
        Assert.Equal(130, route[1].X);
    }

    [Fact]
    public void StableNodeIdentityMergesVisitsRegardlessOfDistanceButDifferentKnownNodesNeverMerge()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("first", 100, node: "node-a"),
            Visit("same-id", 150, node: "node-a"), Visit("other-id", 101, node: "node-b")]);
        Assert.Equal(2, route.Length);
        Assert.Equal(100, route[0].X);
        Assert.Equal(2, route[0].VisitCount);
        Assert.Equal("node-b", route[1].CatalogNodeId);
    }

    [Fact]
    public void KnownDifferentResourceTypesNeverMergeEvenAtTheSamePosition()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("ore", 100, resource: 123), Visit("herb", 100, resource: 456)]);
        Assert.Equal(2, route.Length);
        Assert.Equal([123, 456], route.Select(point => point.GatherableId!.Value));
    }

    [Fact]
    public void LaterCatalogueResolutionPromotesExistingFallbackInPlace()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("first", 100, succeeded: false), Visit("end", 300),
            Visit("snapped", 110, node: "node-a") with { OriginalX = 105, Name = "Catalogue resource" }]);
        Assert.Equal(2, route.Length);
        Assert.Equal("first", route[0].Id);
        Assert.Equal("node-a", route[0].CatalogNodeId);
        Assert.Equal(110, route[0].X);
        Assert.Equal("Catalogue resource", route[0].Name);
        Assert.Equal(2, route[0].VisitCount);
        Assert.Equal(1, route[0].FailureCount);
        Assert.Equal("end", route[1].Id);
    }

    [Fact]
    public void PromotionUsesOriginalPositionWhenCalibratedSnapIsBeyondPixelMergeTolerance()
    {
        // 30 pixels at 0.5 m/pixel is an allowed 15-metre node snap. The earlier fallback is
        // at exactly the new attempt's original position and must not remain a second stop.
        var route = LifeskillFarmRouteBuilder.Build([Visit("fallback", 100, succeeded: false),
            Visit("snapped", 130, node: "node-a") with { OriginalX = 100 }]);
        var point = Assert.Single(route);
        Assert.Equal("fallback", point.Id);
        Assert.Equal("node-a", point.CatalogNodeId);
        Assert.Equal(130, point.X);
        Assert.Equal(2, point.VisitCount);
        Assert.Equal(1, point.FailureCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PromotionMergesExistingNodeAndCompatibleFallbackWhilePreservingTheEarlierStop(bool fallbackIsOlder)
    {
        var fallback = Visit("fallback", 100, succeeded: false);
        var node = Visit("catalogue", 125, node: "node-a");
        var visits = fallbackIsOlder ? new[] { fallback, node } : new[] { node, fallback };
        var route = LifeskillFarmRouteBuilder.Build(visits.Append(Visit("new-snap", 110, node: "node-a")));
        var point = Assert.Single(route);
        Assert.Equal(fallbackIsOlder ? "fallback" : "catalogue", point.Id);
        Assert.Equal("node-a", point.CatalogNodeId);
        Assert.Equal(3, point.VisitCount);
        Assert.Equal(2, point.SuccessCount);
        Assert.Equal(1, point.FailureCount);
    }

    [Fact]
    public void FallbackCanRevisitAnAlreadyKnownNodeButCannotBridgeDifferentKnownNodeIds()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("a", 100, node: "a"), Visit("b", 110, node: "b"),
            Visit("fallback", 101), Visit("a-again", 100, node: "a")]);
        Assert.Equal(2, route.Length);
        Assert.Equal(3, route.Single(point => point.CatalogNodeId == "a").VisitCount);
        Assert.Equal(1, route.Single(point => point.CatalogNodeId == "b").VisitCount);
    }

    [Fact]
    public void NewPointNeverPrependsAndEqualInsertionCostsChooseEarliestGap()
    {
        var beforeStart = LifeskillFarmRouteBuilder.Build([Visit("start", 200), Visit("end", 300), Visit("left", 100)]);
        Assert.Equal("start", beforeStart[0].Id);
        var tie = LifeskillFarmRouteBuilder.Build([Visit("start", 100, resource: 123),
            Visit("same-coordinate", 100, resource: 456), Visit("tie", 200, resource: 789)]);
        // Gap cost is 200, append cost 100: append is cheaper. A true tie is a point at the
        // existing end with a distinct resource, where gap and append both add zero distance.
        Assert.Equal(["start", "same-coordinate", "tie"], tie.Select(point => point.Id));
        var exactTie = LifeskillFarmRouteBuilder.Build([Visit("start", 100), Visit("end", 300),
            Visit("another-resource", 300, resource: 456)]);
        Assert.Equal(["start", "another-resource", "end"], exactTie.Select(point => point.Id));
    }

    [Fact]
    public void MapAndSectionBoundariesStaySeparateEvenForSameNodeIdsAndCoordinates()
    {
        var route = LifeskillFarmRouteBuilder.Build([Visit("one", 100, node: "same"),
            Visit("two", 100, node: "same", section: 1), Visit("three", 100, node: "same", section: 1, map: "another")]);
        Assert.Equal(3, route.Length);
        Assert.Equal([0, 1, 1], route.Select(point => point.Section));
        Assert.Equal(["map", "map", "another"], route.Select(point => point.MapId));
    }

    [Fact]
    public void ReplayOfFrozenVisitsDoesNotConsultChangedCatalogueOrDuplicateJournalIds()
    {
        var visits = new[] { Visit("first", 100), Visit("last", 300), Visit("middle", 200, succeeded: false),
            Visit("snapped", 105, node: "frozen-node") };
        var serialized = JsonSerializer.Serialize(visits);
        var recovered = JsonSerializer.Deserialize<LootTrackerGatherVisit[]>(serialized)!;
        Assert.Equal(LifeskillFarmRouteBuilder.Build(visits).ToArray(), LifeskillFarmRouteBuilder.Build(recovered.Concat(recovered)).ToArray());
        Assert.Equal(4, LifeskillFarmRouteBuilder.Build(recovered).Sum(point => point.VisitCount));
    }

    [Fact]
    public void MalformedVisitsDoNotPoisonValidHistory()
    {
        var valid = Visit("valid", 100);
        LootTrackerGatherVisit[] visits = [valid with { Id = "bad-x", X = double.NaN },
            valid with { Id = "bad-original", OriginalY = double.PositiveInfinity }, valid with { Id = "bad-size", MapSize = 0 },
            valid with { Id = "bad-section", Section = -1 }, valid with { Id = "bad-map", MapId = "" },
            valid with { Id = "bad-resource", GatherableId = 0 }, valid with { Id = "bad-node", CatalogNodeId = "" },
            valid with { Id = "node-without-type", CatalogNodeId = "node", GatherableId = null }, null!, valid, valid];
        Assert.Equal("valid", Assert.Single(LifeskillFarmRouteBuilder.Build(visits)).Id);
        Assert.Equal(1, Assert.Single(LifeskillFarmRouteBuilder.Build(visits)).VisitCount);
    }
}
