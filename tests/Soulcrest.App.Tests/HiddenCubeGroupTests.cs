using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class HiddenCubeGroupTests
{
    [Fact]
    public void ExplicitGroupsKeepExistingMarkersAndTheirCompleteUndirectedConnections()
    {
        using var fixture = new Fixture();
        var groups = MapPetMarkers.LoadHiddenCubeGroups(fixture.Path);
        Assert.Equal(["a", "b"], groups.Select(g => g.Id));
        Assert.Equal([0, 1, 2], groups[0].Markers);
        Assert.Equal([(0, 1), (1, 2)], groups[0].Connections.Select(c => (c.FromMarker, c.ToMarker)));
        Assert.Equal(new HiddenCubeConnection(0, 1, 20, 30, 130, 30), groups[0].Connections[0]);
        var symbols = MapPetMarkers.LoadSymbols(fixture.Path);
        Assert.Equal(6, symbols.Count); // grouping adds metadata and edges, never additional map markers
        Assert.Equal(["#5edec7", "#5edec7", "#5edec7", "#f4ae68", "#f4ae68", null], symbols.Select(s => s.OutlineColor));
        Assert.All(groups, g => Assert.All(g.Connections, c =>
        {
            Assert.Contains(c.FromMarker, g.Markers);
            Assert.Contains(c.ToMarker, g.Markers);
        }));
    }

    [Theory]
    [InlineData("cross-group")]
    [InlineData("disconnected")]
    [InlineData("wrong-category")]
    [InlineData("wrong-assignment")]
    [InlineData("invalid-index")]
    [InlineData("invalid-color")]
    [InlineData("invalid-type")]
    [InlineData("blank-id")]
    public void InvalidGroupCannotCreateGuessedOrCrossGroupLines(string problem)
    {
        using var fixture = new Fixture();
        var root = fixture.Data;
        var group = root["hiddenCubeGroups"]![0]!;
        switch (problem)
        {
            case "cross-group": group["connections"] = JsonNode.Parse("[[0,1],[1,3]]"); break;
            case "disconnected": group["connections"] = JsonNode.Parse("[[0,1]]"); break;
            case "wrong-category": root["markers"]![2]![0] = 1; break;
            case "wrong-assignment": root["markers"]![2]![7]!["hiddenCubeGroup"] = "b"; break;
            case "invalid-index": group["markers"] = JsonNode.Parse("[0,1,99]"); break;
            case "invalid-color": group["color"] = "red"; break;
            case "invalid-type": group["number"] = "1"; break;
            case "blank-id": group["id"] = " "; break;
        }
        fixture.Save();
        Assert.Equal("b", Assert.Single(MapPetMarkers.LoadHiddenCubeGroups(fixture.Path)).Id);
    }

    [Fact]
    public void AmbiguousMembershipAndDuplicateIdsRejectBothGroups()
    {
        using var fixture = new Fixture();
        fixture.Data["hiddenCubeGroups"]!.AsArray().Add(fixture.Data["hiddenCubeGroups"]![0]!.DeepClone());
        fixture.Save();
        Assert.Equal("b", Assert.Single(MapPetMarkers.LoadHiddenCubeGroups(fixture.Path)).Id);
        fixture.Data["hiddenCubeGroups"]![2]!["id"] = "overlap";
        foreach (var marker in fixture.Data["markers"]!.AsArray().Take(3)) marker![7] = null;
        fixture.Save();
        Assert.Equal("b", Assert.Single(MapPetMarkers.LoadHiddenCubeGroups(fixture.Path)).Id);
    }

    [Theory]
    [InlineData("malformed-overlap")]
    [InlineData("duplicate-number")]
    [InlineData("duplicate-position")]
    [InlineData("different-category-index")]
    public void RawConflictsCannotBecomeUnambiguousByDiscardingAnInvalidDefinition(string problem)
    {
        using var fixture = new Fixture();
        var root = fixture.Data;
        switch (problem)
        {
            case "malformed-overlap":
                root["hiddenCubeGroups"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"invalid\",\"number\":3,\"color\":\"#f4ae68\",\"markers\":[0],\"connections\":[[0,99]]}"));
                Assert.Equal("b", Assert.Single(SaveAndRead()).Id);
                break;
            case "duplicate-number":
                root["hiddenCubeGroups"]![1]!["number"] = 1;
                Assert.Empty(SaveAndRead());
                break;
            case "duplicate-position":
                root["markers"]![3]![1] = 20;
                root["markers"]![3]![2] = 30;
                Assert.Empty(SaveAndRead());
                break;
            case "different-category-index":
                root["categories"]!.AsArray().Add(root["categories"]![0]!.DeepClone());
                root["markers"]![1]![0] = 2;
                Assert.Equal("b", Assert.Single(SaveAndRead()).Id);
                break;
        }
        IReadOnlyList<HiddenCubeGroup> SaveAndRead() { fixture.Save(); return MapPetMarkers.LoadHiddenCubeGroups(fixture.Path); }
    }

    [Fact]
    public void OlderPackagesWithoutExplicitGroupsRemainCompatible()
    {
        using var fixture = new Fixture();
        fixture.Data.Remove("hiddenCubeGroups");
        fixture.Save();
        Assert.Empty(MapPetMarkers.LoadHiddenCubeGroups(fixture.Path));
        Assert.Equal(6, MapPetMarkers.LoadSymbols(fixture.Path).Count);
    }

    [Fact]
    public void CubeLayerControlsGroupsSeparatelyForEverySurfaceWithoutAnyRouteTarget()
    {
        using var fixture = new Fixture();
        var settings = new AppSettings
        {
            ShowRoutesInGame = false,
            ShowResourcesInGame = false,
            MapCategories = new() { ["test"] = ["Collectibles/Hidden Cube", "Collectibles/Cube Groups"] },
            MapMinimapLayers = new() { ["test"] = new([], []) },
            MapWorldmapLayers = new() { ["test"] = new(["Collectibles/Hidden Cube", "Collectibles/Cube Groups"], []) },
        };
        Assert.Equal(2, RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Interactive, fixture.Directory).Count);
        Assert.Empty(RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Minimap, fixture.Directory));
        Assert.Equal(2, RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Worldmap, fixture.Directory).Count);
        settings.MapMinimapLayers["test"] = new(["Collectibles/Hidden Cube", "Collectibles/Cube Groups"], []);
        settings.MapWorldmapLayers["test"] = new([], []);
        settings.MapCategories["test"] = [];
        Assert.Equal(2, RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Minimap, fixture.Directory).Count);
        Assert.Empty(RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Worldmap, fixture.Directory));
        Assert.Empty(RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", MapSurface.Interactive, fixture.Directory));
    }

    [Theory]
    [InlineData(MapSurface.Interactive, true, true, 2)]
    [InlineData(MapSurface.Interactive, true, false, 0)]
    [InlineData(MapSurface.Interactive, false, true, 0)]
    [InlineData(MapSurface.Interactive, false, false, 0)]
    [InlineData(MapSurface.Minimap, true, true, 2)]
    [InlineData(MapSurface.Minimap, true, false, 0)]
    [InlineData(MapSurface.Minimap, false, true, 0)]
    [InlineData(MapSurface.Minimap, false, false, 0)]
    [InlineData(MapSurface.Worldmap, true, true, 2)]
    [InlineData(MapSurface.Worldmap, true, false, 0)]
    [InlineData(MapSurface.Worldmap, false, true, 0)]
    [InlineData(MapSurface.Worldmap, false, false, 0)]
    public void BothCubeAndCubeGroupsLayersMustBeEnabledOnTheSameSurface(MapSurface surface, bool cubes, bool groups, int expected)
    {
        using var fixture = new Fixture();
        var categories = new List<string>();
        if (cubes) categories.Add("Collectibles/Hidden Cube");
        if (groups) categories.Add("Collectibles/Cube Groups");
        var settings = new AppSettings
        {
            ShowRoutesInGame = false,
            MapCategories = new() { ["test"] = surface == MapSurface.Interactive ? categories.ToArray() : [] },
            MapMinimapLayers = new() { ["test"] = new(surface == MapSurface.Minimap ? categories.ToArray() : [], []) },
            MapWorldmapLayers = new() { ["test"] = new(surface == MapSurface.Worldmap ? categories.ToArray() : [], []) },
        };
        Assert.Equal(expected, RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", surface, fixture.Directory).Count);
        foreach (var other in Enum.GetValues<MapSurface>().Where(s => s != surface))
            Assert.Empty(RouteOverlayForm.PrepareHiddenCubeGroups(settings, "test", other, fixture.Directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBitmapShowsSeparatedUndirectedGroupsWithoutTargetsAndHidesThemWithLayer(bool worldMap)
    {
        using var fixture = new Fixture();
        var groups = MapPetMarkers.LoadHiddenCubeGroups(fixture.Path);
        var placement = Placement(worldMap);
        using var image = RouteOverlayForm.Draw(placement, [], hiddenCubeGroups: groups);
        Assert.True(image.GetPixel(80, 30).A > 0);
        Assert.True(image.GetPixel(130, 70).A > 0);
        Assert.True(image.GetPixel(280, 65).A > 0);
        Assert.Equal(0, image.GetPixel(180, 30).A); // no edge between the two confirmed groups
        Assert.Equal(0, image.GetPixel(80, 36).A); // thin line, no directional chevron
        Assert.NotEqual(image.GetPixel(80, 30).ToArgb(), image.GetPixel(280, 65).ToArgb());
        using var hidden = RouteOverlayForm.Draw(placement, [], hiddenCubeGroups: []);
        Assert.Equal(0, hidden.GetPixel(80, 30).A);
        if (Environment.GetEnvironmentVariable("SOULCREST_CUBE_PREVIEW") is { Length: > 0 } preview)
        {
            using var shown = RouteOverlayForm.Draw(placement, [], resources: MapPetMarkers.LoadSymbols(fixture.Path)
                .Where(s => s.Category == "Collectibles/Hidden Cube")
                .Select(s => new RouteOverlayForm.ResourceSymbol(s.X, s.Y, null, OutlineColor: s.OutlineColor)).ToArray(), hiddenCubeGroups: groups);
            using var background = new Bitmap(shown.Width, shown.Height);
            using var graphics = Graphics.FromImage(background);
            graphics.Clear(Color.FromArgb(29, 39, 47));
            graphics.DrawImageUnscaled(shown, 0, 0);
            background.Save(preview + (worldMap ? "-worldmap.png" : "-minimap.png"), ImageFormat.Png);
        }
    }

    [Fact]
    public void SegmentCrossingViewportDrawsEvenWhenBothEndpointsAreOutsideAndStaysClipped()
    {
        var group = new HiddenCubeGroup("across", 1, "#5edec7", [0, 1], [new(0, 1, -50, 50, 500, 50)]);
        using var image = RouteOverlayForm.Draw(Placement(false), [], hiddenCubeGroups: [group]);
        Assert.True(image.GetPixel(190, 50).A > 0);
        Assert.Equal(0, image.GetPixel(1, 50).A);
        Assert.Equal(0, image.GetPixel(378, 50).A);
        var outside = group with { Connections = [new(0, 1, -50, -10, 500, -10)] };
        using var hidden = RouteOverlayForm.Draw(Placement(false), [], hiddenCubeGroups: [outside]);
        Assert.Equal(0, hidden.GetPixel(190, 3).A);
    }

    [Fact]
    public void LocalAltgardPackageGroupsEveryCubeAndProducesOfflineNativePreview()
    {
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "imports", "generated", "mapdata"));
        var path = System.IO.Path.Combine(directory, "altgard", "data.js");
        if (!File.Exists(path) || !File.ReadAllText(path).Contains("hiddenCubeGroups", StringComparison.Ordinal)) return;
        var groups = MapPetMarkers.LoadHiddenCubeGroups(path);
        // Own grouping by the nearest named place (2026-10-16): every cube once, one tree per group.
        Assert.NotEmpty(groups);
        Assert.All(groups, group => Assert.StartsWith("altgard-cubes-", group.Id, StringComparison.Ordinal));
        Assert.Equal(groups.Count, groups.Select(group => group.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, groups.Count), groups.Select(group => group.Number));
        Assert.Equal(1371, groups.Sum(g => g.Markers.Count));
        Assert.Equal(1371 - groups.Count, groups.Sum(g => g.Connections.Count));
        Assert.Equal(1371, groups.SelectMany(g => g.Markers).Distinct().Count());
        if (Environment.GetEnvironmentVariable("SOULCREST_CUBE_PACKAGE_PREVIEW") is not { Length: > 0 } preview) return;
        const int size = 1024;
        var placement = new MapPlacement("altgard", new MapFix(.125, 0, 0, 0, .125, 0, 100, 100, .3, false), 1,
            new Rectangle(0, 0, size, size), default, WorldMap: true);
        using var image = RouteOverlayForm.Draw(placement, [], hiddenCubeGroups: groups);
        using var background = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(background);
        graphics.Clear(Color.FromArgb(29, 39, 47));
        var tilePath = System.IO.Path.Combine(directory, "altgard", "tiles", "0_0_0.webp");
        if (File.Exists(tilePath))
        {
            using var tile = OpenCvSharp.Cv2.ImRead(tilePath);
            using var stream = new MemoryStream(tile.ToBytes(".png"));
            using var terrain = Image.FromStream(stream);
            graphics.DrawImage(terrain, 0, 0, size, size);
        }
        graphics.DrawImageUnscaled(image, 0, 0);
        background.Save(preview, ImageFormat.Png);
        foreach (var (name, scale, width, height) in new[] { ("minimap", 1.25, 380, 280), ("worldmap", .125, size, size) })
        {
            var fix = new MapFix(scale, 0, width / 2.0 - 4096 * scale, 0, scale, height / 2.0 - 4096 * scale, 100, 100, .3, false);
            var viewport = new MapPlacement("altgard", fix, 1, new Rectangle(0, 0, width, height), default, WorldMap: name == "worldmap");
            for (var i = 0; i < 5; i++) using (RouteOverlayForm.Draw(viewport, [], hiddenCubeGroups: groups)) { }
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) using (RouteOverlayForm.Draw(viewport, [], hiddenCubeGroups: groups)) { }
            var withGroups = elapsed.Elapsed.TotalMilliseconds / 20;
            elapsed.Restart();
            for (var i = 0; i < 20; i++) using (RouteOverlayForm.Draw(viewport, [])) { }
            File.AppendAllText(preview + ".timings.txt", $"{name}: groups {withGroups:F2} ms/frame, empty {elapsed.Elapsed.TotalMilliseconds / 20:F2} ms/frame{Environment.NewLine}");
        }
    }

    private static MapPlacement Placement(bool worldMap) => new("test",
        new MapFix(1, 0, 0, 0, 1, 0, 100, 100, .3, false), 1, new Rectangle(170, 90, 380, 160),
        new OpenCvSharp.Point2d(60, 80), WorldMap: worldMap);

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-cube-groups-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "test", "data.js");
        internal JsonObject Data { get; } = JsonNode.Parse("""
            {"categories":[{"group":"Collectibles","name":"Hidden Cube"},{"group":"Resources","name":"Gem"}],"icons":[],
             "markers":[[0,20,30,"Cube",null,-1,null,{"hiddenCubeGroup":"a"}],[0,130,30,"Cube",null,-1,null,{"hiddenCubeGroup":"a"}],
                        [0,130,100,"Cube",null,-1,null,{"hiddenCubeGroup":"a"}],[0,230,30,"Cube",null,-1,null,{"hiddenCubeGroup":"b"}],
                        [0,330,100,"Cube",null,-1,null,{"hiddenCubeGroup":"b"}],[1,180,120,"Gem",null,-1,null]],
             "hiddenCubeGroups":[{"id":"a","number":1,"color":"#5edec7","markers":[0,1,2],"connections":[[0,1],[1,2]]},
                                 {"id":"b","number":2,"color":"#f4ae68","markers":[3,4],"connections":[[3,4]]}]}
            """)!.AsObject();
        internal Fixture() { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); Save(); }
        internal void Save() => File.WriteAllText(Path, "window.SoulcrestMaps[\"test\"] = " + Data.ToJsonString() + ";");
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
