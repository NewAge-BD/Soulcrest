using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class RouteOverlayPreparationTests
{
    [Fact]
    public void UnchangedContentIsReusedWhileEveryPlacementStillChangesVisibilityAndDistance()
    {
        using var fixture = new Fixture();
        var cache = new PreparationCache<string, RouteOverlayForm.PreparedOverlay>();
        var builds = 0;
        RouteOverlayForm.PreparedOverlay Build() { builds++; return fixture.Prepare(); }
        var prepared = cache.Get("map/minimap/en", Build);
        var original = Placement();
        var moved = Placement(-180);
        Assert.Equal(0, fixture.PetIconReads);
        Assert.Equal(0, fixture.ResourceIconReads);
        Assert.Equal([30d, 60d], RouteOverlayForm.VisiblePets(original, prepared).Select(p => p.X));
        Assert.Equal(2, fixture.PetIconReads);
        Assert.Equal([200d], RouteOverlayForm.VisiblePets(moved, prepared).Select(p => p.X));
        Assert.Equal([35d], RouteOverlayForm.VisibleSoulMonsters(original, prepared).Select(p => p.X));
        Assert.Equal([205d], RouteOverlayForm.VisibleSoulMonsters(moved, prepared).Select(p => p.X));
        Assert.Equal([40d], RouteOverlayForm.VisibleSymbols(original, prepared).Select(p => p.X));
        Assert.Equal([210d], RouteOverlayForm.VisibleSymbols(moved, prepared).Select(p => p.X));
        Assert.Equal([25d], RouteOverlayForm.VisibleGuardZones(original, prepared)!.Guards.Select(p => p.X));
        Assert.Equal([195d], RouteOverlayForm.VisibleGuardZones(moved, prepared)!.Guards.Select(p => p.X));
        var first = RouteOverlayForm.TargetsWithDistance(prepared, new("map", 0, 20, DateTimeOffset.UnixEpoch));
        var next = RouteOverlayForm.TargetsWithDistance(prepared, new("map", 20, 20, DateTimeOffset.UnixEpoch));
        Assert.Equal("Source monster 6/25 · 60 m", first[0].Target.Name);
        Assert.Equal("Source monster 6/25 · 20 m", next[0].Target.Name);
        Assert.Equal("Source monster 6/25", prepared.Targets[0].Target.Name);
        for (var i = 0; i < 60; i++) Assert.Same(prepared, cache.Get("map/minimap/en", Build));
        for (var i = 0; i < 60; i++) RouteOverlayForm.VisiblePets(original, prepared);
        for (var i = 0; i < 60; i++) RouteOverlayForm.VisibleSymbols(original, prepared);
        Assert.Equal(1, builds);
        Assert.Equal(3, fixture.PetIconReads); // same pet's second spawn is retained for future panning
        Assert.Equal(2, fixture.ResourceIconReads);
    }

    [Fact]
    public void InvalidationDuringPreparationCannotRetainOldProgressAsTheNewGeneration()
    {
        var cache = new PreparationCache<string, string>();
        var builds = 0;
        string Build()
        {
            builds++;
            if (builds == 1) { cache.Invalidate(); return "old"; }
            return "current";
        }
        Assert.Equal("old", cache.Get("map", Build)); // in-flight frame belongs to the old dispatch epoch
        Assert.Equal("current", cache.Get("map", Build));
        Assert.Equal("current", cache.Get("map", Build));
        Assert.Equal(2, builds);
        cache.Invalidate();
        Assert.Equal("changed", cache.Get("map", () => "changed"));
        Assert.Equal("other surface", cache.Get("map/worldmap", () => "other surface"));
        Assert.Equal("other language", cache.Get("map/worldmap/de", () => "other language"));
        Assert.Equal("other data", cache.Get("other/map/worldmap/de", () => "other data"));
    }

    [Fact]
    public void ProgressSettingsTargetsAndLanguageRefreshCompleteCandidatesWithoutFreezingGeometry()
    {
        using var fixture = new Fixture();
        var cache = new PreparationCache<string, RouteOverlayForm.PreparedOverlay>();
        fixture.Progress.Changed += cache.Invalidate;
        fixture.Targets.Changed += cache.Invalidate;
        var before = cache.Get("en/minimap", () => fixture.Prepare());
        fixture.Progress.SetLevel("p", 3, 0);
        var max = cache.Get("en/minimap", () => fixture.Prepare());
        Assert.NotSame(before, max);
        Assert.All(max.Pets.Where(p => p.X is 30 or 200), p => { Assert.True(p.Symbol.Done); Assert.EndsWith("max", p.Symbol.Label); });
        Assert.Equal("Source monster max", max.Targets[0].Target.Name);
        fixture.Progress.SetLevel("q", 3, 0);
        fixture.Settings.HideLevel3 = true;
        cache.Invalidate();
        UiText.Language = "de";
        var german = cache.Get("de/minimap", () => fixture.Prepare("de"));
        Assert.Equal("Quellmonster max", german.Targets[0].Target.Name);
        Assert.StartsWith("St. ", german.Pets[0].Symbol.Label);
        Assert.Equal([30d, 200d], german.Pets.Select(p => p.X)); // marked max stays; unmarked max is filtered
        var world = cache.Get("de/worldmap", () => fixture.Prepare("de", MapSurface.Worldmap));
        Assert.Equal([30d, 200d], world.Pets.Select(p => p.X)); // mark bypasses the empty worldmap legend
        Assert.Empty(world.Resources);
        fixture.Targets.Clear();
        fixture.Settings.ShowRoutesInGame = false;
        cache.Invalidate(); // the form's Settings.Changed handler calls this after SettingsService.Update
        var hidden = cache.Get("de/worldmap", () => fixture.Prepare("de", MapSurface.Worldmap));
        Assert.Empty(hidden.Targets);
        Assert.Empty(hidden.Pets);
        Assert.Empty(hidden.Souls);
    }

    [Fact]
    public void PreparedFixtureMatchesExpectedOrderedSceneAndPixelsIncludingCompletedAndChainedTargets()
    {
        using var fixture = new Fixture(extraTargets: true);
        var prepared = fixture.Prepare();
        var placement = Placement();
        var onMap = RouteOverlayForm.TargetsWithDistance(prepared, new("map", 0, 20, DateTimeOffset.UnixEpoch));
        Assert.Equal([1, 2, 3, 4, -1, -2, -3], onMap.Select(t => t.Index));
        Assert.Equal(["Source monster 6/25 · 60 m", "Chain", "Completed", "", "Dungeon · 80 m", "Boss · 100 m", "Preview · 120 m"],
            onMap.Select(t => t.Target.Name));
        var resources = RouteOverlayForm.VisibleSymbols(placement, prepared);
        RouteOverlayForm.ApplyTargetSymbols(resources, prepared);
        Assert.Equal([80d, 70d, 90d], resources.Select(r => r.X));
        Assert.Equal(.28f, resources.Single(r => r.X == 70).Opacity);
        var pets = RouteOverlayForm.VisiblePets(placement, prepared);
        var souls = RouteOverlayForm.VisibleSoulMonsters(placement, prepared);
        var guards = RouteOverlayForm.VisibleGuardZones(placement, prepared);
        using var actual = RouteOverlayForm.Draw(placement, onMap, pets, resources, souls, guards, prepared.TargetIcons);
        var expectedTargets = fixture.InitialTargets.Skip(1).Select((t, i) => (Target: t, Index: i + 1)).ToList();
        expectedTargets[0] = (expectedTargets[0].Target with { Name = "Source monster 6/25 · 60 m" }, 1);
        expectedTargets.Add((fixture.Targets.Progression! with { Name = "Dungeon · 80 m" }, -1));
        expectedTargets.Add((fixture.Targets.BossRush! with { Name = "Boss · 100 m" }, -2));
        expectedTargets.Add((fixture.Targets.BossPreview! with { Name = "Preview · 120 m" }, -3));
        using var expected = RouteOverlayForm.Draw(placement, expectedTargets,
            [new(30, 20, null, fixture.Progress.PetMapLabel("p"), false), new(60, 20, null, fixture.Progress.PetMapLabel("q"), false)],
            [new(80, 20, null), new(70, 20, null, "#ffffff", .28f), new(90, 20, null)], [new(35, 20)],
            new([new(25, 20)], 10));
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    [Fact]
    public void DistanceLabelsAreReusedForFlowButEveryAcceptedPositionAndContentChangeIsObserved()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        var cache = new RouteOverlayForm.TargetDistanceCache();
        var position = new PlayerPosition("map", 0, 20, DateTimeOffset.UnixEpoch);
        var initial = cache.Get(prepared, position, "en");
        for (var i = 0; i < 60; i++) Assert.Same(initial, cache.Get(prepared, position with { }, "en"));
        var accepted = cache.Get(prepared, position with { X = 20, At = position.At.AddSeconds(.25) }, "en");
        Assert.NotSame(initial, accepted);
        Assert.Equal("Source monster 6/25 · 20 m", accepted[0].Target.Name);
        fixture.Progress.SetLevel("p", 2, 7);
        var progress = cache.Get(fixture.Prepare(), position, "en");
        Assert.NotSame(initial, progress);
        Assert.Equal("Source monster 7/75 · 60 m", progress[0].Target.Name);
        var distant = position with { X = -570 };
        Assert.EndsWith("1.2 km", cache.Get(prepared, distant, "en")[0].Target.Name);
        UiText.Language = "de";
        Assert.EndsWith("1,2 km", cache.Get(prepared, distant, "de")[0].Target.Name);
        var otherMap = cache.Get(prepared, position with { MapId = "other" }, "de");
        Assert.Equal("Source monster 6/25", otherMap[0].Target.Name);
    }

    [Fact]
    public void GroupLinesAndCubeColorRimsFollowBothLayerChoicesWithRoutesDisabled()
    {
        using var fixture = new Fixture();
        fixture.Targets.Clear();
        fixture.Settings.ShowRoutesInGame = false;
        fixture.Settings.MapMinimapLayers["map"] = new(["Collectibles/Hidden Cube"], []);
        var cubes = fixture.Prepare();
        Assert.Empty(cubes.Targets);
        Assert.Empty(cubes.HiddenCubeGroups);
        Assert.Equal(2, cubes.Resources.Count);
        Assert.All(cubes.Resources, candidate => Assert.Null(candidate.Symbol.OutlineColor));
        fixture.Settings.MapMinimapLayers["map"] = new(["Collectibles/Hidden Cube", "Collectibles/Cube Groups"], []);
        var groups = fixture.Prepare();
        Assert.Empty(groups.Targets);
        Assert.Single(groups.HiddenCubeGroups);
        Assert.All(groups.Resources, candidate => Assert.Equal("#5edec7", candidate.Symbol.OutlineColor));
        fixture.Settings.MapMinimapLayers["map"] = new(["Collectibles/Cube Groups"], []);
        var hidden = fixture.Prepare();
        Assert.Empty(hidden.HiddenCubeGroups);
        Assert.Empty(hidden.Resources);
    }

    [Fact]
    public void DeferredVisibleIconRetriesTransientFailureAndReusesSuccessfulSymbol()
    {
        var reads = 0;
        var candidate = new RouteOverlayForm.PreparedCandidate<RouteOverlayForm.ResourceSymbol>(10, 20, () =>
        {
            if (++reads == 1) throw new IOException("temporary icon read");
            return new(10, 20, null);
        });
        Assert.Throws<IOException>(() => candidate.Symbol);
        var symbol = candidate.Symbol;
        for (var i = 0; i < 60; i++) Assert.Same(symbol, candidate.Symbol);
        Assert.Equal(2, reads);
    }

    private static byte[] Pixels(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static MapPlacement Placement(double x = 0) => new("map", new MapFix(1, 0, x, 0, 1, 0, 100, 100, .3, false),
        1, new System.Drawing.Rectangle(0, 0, 100, 80), new Point2d(10, 20));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-overlay-preparation-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapdata = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _language = UiText.Language;
        private readonly Dictionary<string, byte[]?> _saved = new();
        internal ProgressService Progress { get; }
        internal MapTargetsService Targets { get; }
        internal AppSettings Settings { get; } = new()
        {
            MapMinimapLayers = new() { ["map"] = new(["Pets/Cogni", "Resources/Gem"], ["Resources/Gem/Ruby"]) },
            MapWorldmapLayers = new() { ["map"] = new([], []) },
            ExclusivePetsOnly = true,
            GuardZoneMaps = ["map"], GuardRadiusMeters = 20,
        };
        internal List<MapTarget> InitialTargets { get; }
        internal int PetIconReads { get; private set; }
        internal int ResourceIconReads { get; private set; }

        internal Fixture(bool extraTargets = false)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Directory.CreateDirectory(Path.Combine(_directory, "map"));
            File.WriteAllText(Path.Combine(_directory, "manifest.json"), """{"maps":[{"id":"map","label":"Map","metersPerPixel":2}]}""");
            File.WriteAllText(Path.Combine(_directory, "map", "data.js"), """
                window.SoulcrestMaps["map"] = {
                  "categories":[{"group":"Pets","name":"Fera"},{"group":"Pets","name":"Cogni"},
                    {"group":"Monsters","name":"Fera"},{"group":"Resources","name":"Gem"},{"group":"NPCs","name":"Other"},
                    {"group":"Collectibles","name":"Hidden Cube"},{"group":"Collectibles","name":"Cube Groups"}],
                  "icons":[],
                  "markers":[[0,30,20,"Pet",null,-1,"p",{"en":"Source monster","de":"Quellmonster"}],
                    [0,200,20,"Pet",null,-1,"p"],[1,60,20,"Other",null,-1,"q"],
                    [1,90,20,"Elsewhere",null,-1,"r"],[2,35,20,"Source monster",null,-1,"p"],
                    [2,205,20,"Source monster",null,-1,"p"],[3,40,20,"Diamond",null,-1,null],
                    [3,210,20,"Diamond",null,-1,null],[3,50,20,"Ruby",null,-1,null],
                    [4,25,20,"Guard",null,-1,null],[4,195,20,"Guard",null,-1,null],
                    [5,55,40,"Cube",null,-1,null,{"hiddenCubeGroup":"cube-a"}],[5,75,40,"Cube",null,-1,null,{"hiddenCubeGroup":"cube-a"}]],
                  "hiddenCubeGroups":[{"id":"cube-a","number":1,"color":"#5edec7","markers":[11,12],"connections":[[11,12]]}]
                };
                """);
            foreach (var path in new[] { AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile })
            {
                _saved[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                File.Delete(path);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _directory);
            UiText.Language = "en";
            Progress = new ProgressService(Path.Combine(_directory, "progress.json"), new PetCatalog([
                new() { Id = "p", En = "Pet", Genus = "fera", Spawns = new() { ["map"] = 2, ["other"] = 1 } },
                new() { Id = "q", En = "Other", Genus = "cogni", Spawns = new() { ["map"] = 1 } },
                new() { Id = "r", En = "Elsewhere", Genus = "cogni", Spawns = new() { ["map"] = 1, ["other"] = 1 } },
            ]));
            Progress.SetLevel("p", 1, 6);
            Progress.SetLevel("q", 1, 4);
            InitialTargets = [new("elsewhere", "other", 30, 20, "Elsewhere", "Locations", null),
                new("pet", "map", 30, 20, "Pet", "Pets · Fera", null, PetId: "p")];
            if (extraTargets) InitialTargets.AddRange([
                new("chain", "map", 80, 20, "Chain", "Locations", "missing.png", After: "pet"),
                new("completed", "map", 70, 20, "Completed", "Locations", null, Color: "#ffffff", Leveling: true, Completed: true),
                new("empty", "map", 90, 20, "", "Locations", null, Leveling: true),
            ]);
            File.WriteAllText(AppPaths.TargetsFile, JsonSerializer.Serialize(InitialTargets));
            Targets = new MapTargetsService();
            if (extraTargets)
            {
                Targets.SetProgression(new("next", "map", 40, 20, "Dungeon", "Sealed Dungeon", null));
                Targets.SetBossRush(new("boss", "map", 50, 20, "Boss", "Monsters", null));
                Targets.SetBossPreview(new("boss-next", "map", 60, 20, "Preview", "Monsters", null));
            }
        }

        internal RouteOverlayForm.PreparedOverlay Prepare(string language = "en", MapSurface surface = MapSurface.Minimap) =>
            RouteOverlayForm.PrepareOverlay("map", surface, language, Settings, Progress, Targets,
                (_, _) => { PetIconReads++; return null; }, (_, _) => { ResourceIconReads++; return null; });

        public void Dispose()
        {
            Targets.Dispose();
            foreach (var (path, bytes) in _saved)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapdata);
            UiText.Language = _language;
            Directory.Delete(_directory, true);
        }
    }
}
