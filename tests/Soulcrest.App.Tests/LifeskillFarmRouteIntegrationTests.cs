using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LifeskillFarmRouteIntegrationTests
{
    [Fact]
    public void GrowingFarmRouteUsesTheSameOrderedNodesInPreviewAndExportIncludingFailures()
    {
        LootTrackerGatherVisit[] visits = [Visit("one", 1000), Visit("three", 1400),
            Visit("two", 1200, succeeded: false), Visit("three-again", 1400)];
        var session = FarmSession(visits) with
        {
            // A previously supplied travelled path must never take precedence over farm points.
            RouteSegments = [new("sample-map", "Sample map", [new(10, 10), new(20, 20)])],
        };
        Assert.Equal([1000d, 1200d, 1400d], session.FarmRoutePoints.Select(point => point.X));
        Assert.Equal([1, 1, 2], session.FarmRoutePoints.Select(point => point.VisitCount));
        Assert.Equal(1, session.FarmRoutePoints[1].FailureCount);
        var map = new LootTrackerRouteMapPreview.MapDefinition("sample-map", "Sample map",
            "sample-map/{z}_{x}_{y}.webp", 8192, 5, 512, 0, 5);
        var preview = LootTrackerRouteMapPreview.Build(session, map.Id, map, _ => true);
        var line = Assert.Single(preview.Lines);
        Assert.Equal("1000,1000 1200,1000 1400,1000", line.Points);
        Assert.Equal(3, preview.Nodes.Count);
        var exported = Assert.Single(LootTrackerRouteSaveService.Preview(session));
        Assert.Equal(session.FarmRoutePoints.Select(point => (point.X, point.Y)),
            exported.Stops.Select(stop => (stop.X, stop.Y)));
        Assert.Equal(["Sample resource", "Sample resource", "Sample resource"], exported.Stops.Select(stop => stop.Name));
    }

    [Fact]
    public void ExportRetainsDifferentNodesAtTheSameCoordinatesWithTheirIndividualNames()
    {
        var session = FarmSession([
            Visit("a", 1000) with { GatherableId = 101, CatalogNodeId = "node-a", Name = "First node" },
            Visit("b", 1000) with { GatherableId = 102, CatalogNodeId = "node-b", Name = "Second node" },
            Visit("c", 1400) with { GatherableId = 103, CatalogNodeId = "node-c", Name = "Third node" },
        ]);
        var exported = Assert.Single(LootTrackerRouteSaveService.Preview(session));
        Assert.Equal(3, session.FarmRoutePoints.Length);
        Assert.Equal(3, exported.Stops.Count);
        Assert.Equal(["First node", "Second node", "Third node"], exported.Stops.Select(stop => stop.Name));
        Assert.Equal(session.FarmRoutePoints.Select(point => (point.X, point.Y)),
            exported.Stops.Select(stop => (stop.X, stop.Y)));
        Assert.Equal("1000,1000 1000,1000 1400,1000", Assert.Single(LootTrackerCharts.MapRoute(session, "sample-map", 8192)).Points);
    }

    [Fact]
    public void LegacyLifeskillPathRemainsVisibleButCannotBeExportedAsANodeRoute()
    {
        var legacy = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments = [new("sample-map", "Sample map", [new(100, 100), new(200, 200)])],
        };
        Assert.False(LootTrackerPresentation.UsesFarmRoute(legacy));
        Assert.Single(LootTrackerCharts.MapRoute(legacy, "sample-map", 8192));
        Assert.Empty(LootTrackerRouteSaveService.Preview(legacy));
        Assert.Empty(LootTrackerRouteSaveService.Preview(legacy with { FarmRouteVersion = 1 }));
    }

    [Fact]
    public void SeparateSectionsStaySeparateInRenderingAndExportWithoutClosingTheLoop()
    {
        var session = FarmSession([Visit("a", 1000), Visit("b", 1200),
            Visit("c", 3000) with { Section = 1 }, Visit("d", 3200) with { Section = 1 }]);
        var parts = LootTrackerRouteSaveService.Preview(session);
        var lines = LootTrackerCharts.MapRoute(session, "sample-map", 8192);
        Assert.Equal(2, parts.Count);
        Assert.Equal(2, lines.Count);
        Assert.All(parts, part => Assert.Equal(2, part.Stops.Count));
        Assert.Equal("1000,1000 1200,1000", lines[0].Points);
        Assert.Equal("3000,1000 3200,1000", lines[1].Points);
    }

    [Fact]
    public void OneFailurePointIsVisibleOnTheOfflineMapButCannotBeExportedAsAPath()
    {
        var session = FarmSession([Visit("failed", 1000, succeeded: false)]);
        var map = new LootTrackerRouteMapPreview.MapDefinition("sample-map", "Sample map",
            "sample-map/{z}_{x}_{y}.webp", 8192, 5, 512, 0, 5);
        var preview = LootTrackerRouteMapPreview.Build(session, map.Id, map, _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status);
        Assert.Single(preview.Nodes);
        Assert.Empty(preview.Lines);
        Assert.Equal(1, Assert.Single(session.FarmRoutePoints).FailureCount);
        Assert.Empty(LootTrackerRouteSaveService.Preview(session));
    }

    private static LootTrackerSession FarmSession(IEnumerable<LootTrackerGatherVisit> visits)
    {
        var captured = visits.ToArray();
        return new()
        {
            Kind = LootTrackerSessionKind.Lifeskill, Status = LootTrackerSessionStatus.Completed,
            FarmRouteVersion = 1, GatherVisits = [.. captured], FarmRoutePoints = LifeskillFarmRouteBuilder.Build(captured),
        };
    }

    private static LootTrackerGatherVisit Visit(string id, double x, bool succeeded = true) =>
        new(id, 0, "sample-map", "Sample map", x, 1000, x, 1000,
            DateTimeOffset.Parse("2026-10-25T12:00:00Z"), succeeded, "Sample resource");
}
