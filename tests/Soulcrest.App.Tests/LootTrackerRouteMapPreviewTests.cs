using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerRouteMapPreviewTests
{
    private static LootTrackerRouteMapPreview.MapDefinition Map(string id = "sample-map", int tileSize = 512,
        int maxZoom = 6) => new(id, "Sample map", id + "/tiles/{z}_{x}_{y}.webp", 4096, 4, tileSize, 1, maxZoom);

    private static LootTrackerSession Session(params LootTrackerRoutePoint[] points) => new()
    {
        RouteSegments = [new("sample-map", "Sample map", [.. points])],
    };

    [Fact]
    public void NativeMapCoordinatesAlignWithOriginal512PixelTiles()
    {
        var preview = LootTrackerRouteMapPreview.Build(Session(new(1024, 1024), new(1280, 1152)), "sample-map", Map(), _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status);
        var line = Assert.Single(preview.Lines);
        Assert.Equal("1024,1024 1280,1152", line.Points);
        Assert.Equal("1024", line.StartX);
        Assert.Equal("1152", line.EndY); // positive Y remains downwards, as in MapTracking/CRS.Simple.
        var tile = Assert.Single(preview.Tiles, tile => tile.RelativePath == "sample-map/tiles/5_4_4.webp");
        Assert.Equal(1024, tile.X);
        Assert.Equal(1024, tile.Y);
        Assert.Equal(256, tile.Side); // 512 * 2^(4-5), rather than 512 logical map pixels.
        Contains(preview.View, 1024, 1024);
        Contains(preview.View, 1280, 1152);
        Assert.Equal(16d / 9, preview.View.Width / preview.View.Height, 10);
    }

    [Fact]
    public void Legacy256TilesUseTheirOwnNativeZoomWithoutChangingRouteCoordinates()
    {
        var session = Session(new(1024, 1024), new(1280, 1152));
        var current = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true);
        var legacy = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(tileSize: 256, maxZoom: 4), _ => true);
        Assert.Equal(current.View, legacy.View);
        Assert.Equal(Assert.Single(current.Lines), Assert.Single(legacy.Lines));
        var tile = Assert.Single(legacy.Tiles, tile => tile.RelativePath == "sample-map/tiles/4_4_4.webp");
        Assert.Equal(1024, tile.X);
        Assert.Equal(1024, tile.Y);
        Assert.Equal(256, tile.Side);
    }

    [Fact]
    public void MissingHighResolutionFallsBackOnlyToCompleteExistingLowerNativeLevel()
    {
        var preview = LootTrackerRouteMapPreview.Build(Session(new(1024, 1024), new(1280, 1152)), "sample-map", Map(),
            path => path.StartsWith("sample-map/tiles/4_", StringComparison.Ordinal));
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status);
        Assert.NotEmpty(preview.Tiles);
        Assert.All(preview.Tiles, tile =>
        {
            Assert.StartsWith("sample-map/tiles/4_", tile.RelativePath);
            Assert.Equal(512, tile.Side);
        });
    }

    [Fact]
    public void MissingTilesKeepUnchangedRouteAndNeverBorrowAnotherMapBackground()
    {
        var session = Session(new(600, 900), new(700, 1000));
        var missing = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => false);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.MissingTiles, missing.Status);
        Assert.Empty(missing.Tiles);
        Assert.Equal("600,900 700,1000", Assert.Single(missing.Lines).Points);
        var wrong = LootTrackerRouteMapPreview.Build(session, "sample-map", Map("other-map"), _ => throw new InvalidOperationException());
        Assert.Equal(LootTrackerRouteMapPreview.Availability.MissingMap, wrong.Status);
        Assert.Empty(wrong.Tiles);
        Assert.Empty(wrong.Lines);
    }

    [Fact]
    public void UnknownMapAndAbsentRouteHaveExplicitAvailability()
    {
        var unknown = LootTrackerRouteMapPreview.Build(Session(new LootTrackerRoutePoint(10, 20)), "unknown", null, _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.MissingMap, unknown.Status);
        var empty = LootTrackerRouteMapPreview.Build(new(), "sample-map", Map(), _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.NoRoute, empty.Status);
        Assert.Empty(empty.Tiles);
        var invalid = LootTrackerRouteMapPreview.Build(Session(new LootTrackerRoutePoint(9000, 9000)), "sample-map", Map(), _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.NoRoute, invalid.Status);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(4097d)]
    public void InvalidOrOutOfMapPointsSplitTheLineWithoutJoiningAcrossTheGap(double invalid)
    {
        var session = Session(new(100, 100), new(200, 200), new(invalid, 300), new(400, 400), new(500, 500));
        var preview = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true);
        Assert.Equal(new[] { "100,100 200,200", "400,400 500,500" }, preview.Lines.Select(line => line.Points));
    }

    [Fact]
    public void MapSwitchAndRepeatedSegmentsNeverBecomeCrossMapConnections()
    {
        var session = new LootTrackerSession
        {
            RouteSegments =
            [
                new("sample-map", "Sample map", [new(100, 100), new(200, 200)]),
                new("other-map", "Other map", [new(3000, 3000), new(4000, 4000)]),
                new("sample-map", "Sample map", [new(400, 400), new(500, 500)]),
            ],
        };
        var preview = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal("100,100 200,200", preview.Lines[0].Points);
        Assert.Equal("400,400 500,500", preview.Lines[1].Points);
        Assert.True(preview.View.Width < 1000);
        Assert.All(preview.Tiles, tile => Assert.StartsWith("sample-map/", tile.RelativePath));
    }

    [Fact]
    public void ResourceNodesUseCatalogCoordinatesAndGlobalOrderInTheSameViewport()
    {
        var session = new LootTrackerSession
        {
            RouteSegments =
            [
                new("sample-map", "Sample map", [new(1000, 1000), new(1100, 1100)]),
                new("other-map", "Other map", [new(2000, 2000)]),
                new("sample-map", "Sample map", [new(1200, 1200), new(5000, 5000)]),
            ],
            ResourceNodes =
            [
                new("first", "Sample mineral", "sample-map", "Sample map", 900, 950, 0, 0),
                new("other", "Other mineral", "other-map", "Other map", 2000, 2000, 1, 0),
                new("third", "Sample plant", "sample-map", "Sample map", 1400, 1450, 2, 0),
                new("invalid", "Out of map", "sample-map", "Sample map", 1300, 1300, 2, 1),
            ],
        };
        var preview = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true);
        Assert.Equal(new[] { 1, 3 }, preview.Nodes.Select(node => node.Order));
        Assert.Equal("900", preview.Nodes[0].X);
        Assert.Equal("950", preview.Nodes[0].Y);
        Contains(preview.View, 900, 950);
        Contains(preview.View, 1400, 1450);
    }

    [Theory]
    [InlineData(0d, 0d, 4096d, 4096d)]
    [InlineData(0d, 4096d, 0d, 4096d)]
    [InlineData(4096d, 0d, 4096d, 0d)]
    [InlineData(2048d, 2048d, 2048d, 2048d)]
    public void ViewportIncludesBorderAndSinglePointRoutesWithoutDistortingItsAspect(double x1, double y1, double x2, double y2)
    {
        var preview = LootTrackerRouteMapPreview.Build(Session(new(x1, y1), new(x2, y2)), "sample-map", Map(), _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status);
        Contains(preview.View, x1, y1);
        Contains(preview.View, x2, y2);
        Assert.Equal(16d / 9, preview.View.Width / preview.View.Height, 10);
        Assert.All(preview.Tiles, tile => { Assert.True(tile.X >= 0); Assert.True(tile.Y >= 0); });
        Assert.InRange(preview.Tiles.Count, 1, 256);
    }

    [Fact]
    public void ManifestReadsDynamicMapIdsAndBothOriginalAndLegacyGeometry()
    {
        var maps = LootTrackerRouteMapPreview.ParseManifest(JsonSerializer.Serialize(new { maps = new object[]
        {
            new { id = "altgard", label = "Altgard", tiles = "altgard/tiles512/{z}_{x}_{y}.webp", size = 8192, refZoom = 5, tileSize = 512, minNativeZoom = 1, maxNativeZoom = 5 },
            new { id = "verteron", label = "Verteron", tiles = "verteron/tiles/{z}/{x}/{y}.png", size = 4096, refZoom = 4, maxNativeZoom = 4 },
            new { id = "abyss-reshanta-a", label = "Lower Reshanta", tiles = "abyss-reshanta-a/tiles512/{z}_{x}_{y}.webp", size = 4096, refZoom = 4, tileSize = 512, minNativeZoom = 1, maxNativeZoom = 4 },
        } }));
        Assert.Equal(3, maps.Count);
        Assert.Equal(8192, maps["altgard"].Size);
        Assert.Equal(5, maps["altgard"].RefZoom);
        Assert.Equal(256, maps["verteron"].TileSize);
        Assert.Equal(0, maps["verteron"].MinNativeZoom);
        Assert.Equal(512, maps["abyss-reshanta-a"].TileSize);
    }

    [Theory]
    [InlineData("https://remote.invalid/{z}_{x}_{y}.webp")]
    [InlineData("../outside/{z}_{x}_{y}.webp")]
    [InlineData("/root/{z}_{x}_{y}.webp")]
    [InlineData("map/{z}_{x}_{y}.svg")]
    [InlineData("map/{z}_{x}_{y}_{q}.webp")]
    [InlineData("map/{z}_{x}.webp")]
    [InlineData("map/{z}_{x}_{y}.webp?remote=1")]
    [InlineData("map\\{z}_{x}_{y}.webp")]
    public void UnsafeOrUnknownTileTemplatesAreRejectedBeforeAnyFileProbe(string template)
    {
        var map = Map() with { Tiles = template };
        var preview = LootTrackerRouteMapPreview.Build(Session(new LootTrackerRoutePoint(100, 100)), "sample-map", map,
            _ => throw new InvalidOperationException("Rejected template must never reach file access."));
        Assert.Equal(LootTrackerRouteMapPreview.Availability.MissingMap, preview.Status);
        Assert.Empty(preview.Tiles);
    }

    [Fact]
    public void ConflictingDuplicateMapIdsAreNotGuessed()
    {
        var row = new { id = "sample-map", label = "Sample map", tiles = "sample-map/{z}_{x}_{y}.webp", size = 4096, refZoom = 4, maxNativeZoom = 4 };
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest(JsonSerializer.Serialize(new { maps = new[] { row, row } })));
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest("{invalid"));
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest("{\"maps\":{}}"));
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest("[]"));
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest("null"));
    }

    [Fact]
    public void ExplicitInvalidOptionalGeometryNeverSilentlyUsesLegacyDefaults()
    {
        var row = new { id = "sample-map", tiles = "sample-map/{z}_{x}_{y}.webp", size = 4096, refZoom = 4, maxNativeZoom = 4, tileSize = "invalid" };
        Assert.Empty(LootTrackerRouteMapPreview.ParseManifest(JsonSerializer.Serialize(new { maps = new[] { row } })));
        Assert.Empty(LootTrackerRouteMapPreview.Load(@"\\unavailable.invalid\mapdata"));
        Assert.False(LootTrackerRouteMapPreview.TileExists(@"\\unavailable.invalid\mapdata", "map/1_0_0.webp"));
    }

    [Fact]
    public void FileProbesStayWithinTheSelectedLocalMapdataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-route-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sample-map"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "sample-map", "1_0_0.webp"), "isolated placeholder; no actual image decoding");
            Assert.True(LootTrackerRouteMapPreview.TileExists(directory, "sample-map/1_0_0.webp"));
            Assert.False(LootTrackerRouteMapPreview.TileExists(directory, "../outside.webp"));
            Assert.False(LootTrackerRouteMapPreview.TileExists(directory, "https://remote.invalid/file.webp"));
            Assert.False(LootTrackerRouteMapPreview.TileExists(directory, "sample-map/missing.webp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ViewportAndRouteNumbersAreCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var preview = LootTrackerRouteMapPreview.Build(Session(new(1024.5, 1024.25), new(1280.75, 1152.5)), "sample-map", Map(), _ => true);
            Assert.Equal("1024.5,1024.25 1280.75,1152.5", Assert.Single(preview.Lines).Points);
            Assert.DoesNotContain(",", preview.View.ViewBox);
            Assert.Equal(4, preview.View.ViewBox.Split(' ').Length);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ReplayUsesAFixedViewportFollowingTheLastObservedPositionAndOnlyThePrefixWalk()
    {
        var at = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        var session = new LootTrackerSession { StartedAt = at, Elapsed = TimeSpan.FromSeconds(20),
            RouteSegments = [new("sample-map", "Sample map", [new(1000, 1000, at, TimeSpan.Zero),
                new(2000, 1500, at.AddSeconds(10), TimeSpan.FromSeconds(10)),
                new(3000, 2000, at.AddSeconds(20), TimeSpan.FromSeconds(20))])] };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(10));
        var preview = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true, replay);
        Assert.Equal(4096d / 16, preview.View.Width);
        Assert.Equal(2000, preview.View.X + preview.View.Width / 2);
        Assert.Equal(1500, preview.View.Y + preview.View.Height / 2);
        Assert.Equal("1000,1000 2000,1500", Assert.Single(preview.Lines).Points);
        var stale = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true,
            LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(13)));
        Assert.Equal(preview.View, stale.View);
    }

    [Fact]
    public void ReplayLifeskillDrawsTheRealWalkRatherThanTheOptimizedFarmRoute()
    {
        var at = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        var visit = new LootTrackerGatherVisit("visit", 0, "sample-map", "Sample map", 1000, 1000,
            1010, 1000, at, true, "Example resource");
        var session = new LootTrackerSession { Kind = LootTrackerSessionKind.Lifeskill,
            StartedAt = at, Elapsed = TimeSpan.FromSeconds(5), FarmRouteVersion = 1, GatherVisits = [visit],
            FarmRoutePoints = LifeskillFarmRouteBuilder.Build([visit]),
            RouteSegments = [new("sample-map", "Sample map", [new(1000, 1000, at, TimeSpan.Zero),
                new(1200, 1300, at.AddSeconds(5), TimeSpan.FromSeconds(5))])] };
        var replay = LootTrackerSessionReplay.Build(session, session.Elapsed);
        var preview = LootTrackerRouteMapPreview.Build(session, "sample-map", Map(), _ => true, replay);
        Assert.Equal("1000,1000 1200,1300", Assert.Single(preview.Lines).Points);
        Assert.Equal("1010", Assert.Single(preview.Nodes).X);
        Assert.Empty(LootTrackerRouteSaveService.Preview(session)); // A single farm stop cannot become an exported walk.
    }

    private static void Contains(LootTrackerRouteMapPreview.Viewport viewport, double x, double y)
    {
        Assert.InRange(x, viewport.X - 0.000001, viewport.X + viewport.Width + 0.000001);
        Assert.InRange(y, viewport.Y - 0.000001, viewport.Y + viewport.Height + 0.000001);
    }
}
