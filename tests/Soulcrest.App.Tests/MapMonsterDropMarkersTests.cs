using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapMonsterDropMarkersTests
{
    [Fact]
    public void MaterialsUseExactMarkerIdentityAndStableIdsWithoutChangingExistingSymbols()
    {
        using var fixture = new Fixture();
        var catalog = MapPetMarkers.MonsterDropCatalogFor(fixture.Directory, "map");
        Assert.True(catalog.Available);
        Assert.Equal(["101", "102", "103", "104", "105"], catalog.Items.Select(item => item.Id));
        var spawn = catalog.Spawns[0];
        Assert.Equal(0, spawn.MarkerIndex);
        Assert.Equal("1001", spawn.NpcId);
        Assert.Equal("Test beast", spawn.Monster.In("en"));
        Assert.Equal("Testbestie", spawn.Monster.In("de"));
        Assert.Equal("Monster Drops/Meat", spawn.Items[0].Category);
        Assert.Equal("Test meat", spawn.Items[0].In("de")); // absent translation keeps the source name
        Assert.Equal("Monster Drops/Meat/101", MapPetMarkers.KindKey(spawn.Items[0].Category, spawn.Items[0].Id));
        Assert.Equal(["101", "102", "103", "104", "105"], spawn.Items.Select(item => item.Id));
        var symbols = MapPetMarkers.SymbolsFor(fixture.Directory, "map");
        Assert.Equal(5, symbols.Count);
        Assert.Same(symbols[0], spawn.SourceSymbol);
        Assert.NotSame(symbols[1], spawn.SourceSymbol); // same point, different source remains independently visible
        Assert.Single(MapPetMarkers.For(fixture.Directory, "map"));
    }

    [Theory]
    [InlineData("Refining Stones")]
    [InlineData("Balaur Materials")]
    [InlineData("Other Materials")]
    public void RemovedMaterialFamiliesFromOlderPackagesAreExcludedWithoutLosingOtherDrops(string type)
    {
        using var fixture = new Fixture(removedType: type);
        var catalog = MapPetMarkers.MonsterDropCatalogFor(fixture.Directory, "map");
        Assert.True(catalog.Available);
        Assert.DoesNotContain(catalog.Items, item => item.Type == type);
        Assert.Equal(["101", "102", "103", "104"], catalog.Items.Select(item => item.Id));
        Assert.Equal(3, catalog.Spawns.Count); // the source with only the removed material disappears
        Assert.All(catalog.Spawns, spawn => Assert.DoesNotContain(spawn.Items, item => item.Type == type));
        fixture.Settings.MapMinimapLayers["map"] = new(["Monster Drops/" + type], []);
        Assert.DoesNotContain(fixture.Prepare().Resources, candidate => candidate.Symbol.DropIcons is not null);
    }

    [Fact]
    public void MaterialSpritesLeaveTransparentAreasAndOverflowBackgroundUnpainted()
    {
        using var icon = new Bitmap(20, 20, PixelFormat.Format32bppPArgb);
        icon.SetPixel(10, 10, Color.Red);
        foreach (var theme in OverlayThemeDefinition.All)
        {
            var group = new RouteOverlayForm.MonsterDropIcons([icon], 2, "transparent-material-icons-" + Guid.NewGuid().ToString("N"));
            var sprite = RouteOverlayForm.MonsterDropSprite(group, theme);
            Assert.Equal(0, sprite.GetPixel(12, 5).A); // empty area within the icon's former circular backplate
            Assert.Equal(0, sprite.GetPixel(27, 3).A); // former overflow backplate
            Assert.Equal(Color.Red.ToArgb(), sprite.GetPixel(12, 12).ToArgb());
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-version")]
    [InlineData("text-version")]
    [InlineData("unknown-item")]
    [InlineData("wrong-category")]
    [InlineData("wrong-npc")]
    [InlineData("duplicate-marker")]
    [InlineData("duplicate-item")]
    [InlineData("unknown-type")]
    [InlineData("nonnumeric-item")]
    [InlineData("nonnumeric-npc")]
    [InlineData("malformed-icon")]
    [InlineData("duplicate-reference")]
    public void MissingOrInvalidOptionalContractIsUnknownAndDoesNotBreakOrdinaryMap(string change)
    {
        using var fixture = new Fixture(change);
        var catalog = MapPetMarkers.MonsterDropCatalogFor(fixture.Directory, "map");
        Assert.False(catalog.Available);
        Assert.Empty(catalog.Items);
        Assert.Empty(catalog.Spawns);
        Assert.Equal(5, MapPetMarkers.SymbolsFor(fixture.Directory, "map").Count);
        Assert.Single(MapPetMarkers.For(fixture.Directory, "map"));
    }

    [Fact]
    public void ExplicitEmptyContractIsAvailableAndDefaultSelectionDoesNotEnableDrops()
    {
        using var empty = new Fixture("empty");
        Assert.True(MapPetMarkers.MonsterDropCatalogFor(empty.Directory, "map").Available);
        Assert.Empty(MapPetMarkers.MonsterDropsFor(empty.Directory, "map"));
        using var populated = new Fixture();
        populated.Settings.MapMinimapLayers.Clear();
        populated.Settings.MapWorldmapLayers.Clear();
        Assert.DoesNotContain(populated.Prepare().Resources, candidate => candidate.Symbol.DropIcons is not null);
    }

    [Theory]
    [InlineData("https://example.invalid/icon.png")]
    [InlineData("icons/../outside.png")]
    [InlineData("//example.invalid/icon.png")]
    public void UnsafeOptionalIconPathsBecomeLocalFallbacksWithoutDiscardingConfirmedRelations(string icon)
    {
        using var fixture = new Fixture(iconOverride: icon);
        var catalog = MapPetMarkers.MonsterDropCatalogFor(fixture.Directory, "map");
        Assert.True(catalog.Available);
        Assert.Null(catalog.Items.Single(item => item.Id == "105").Icon);
        Assert.Equal(4, catalog.Spawns.Count);
    }

    [Fact]
    public void EachSurfaceFiltersItemsIndependentlyAndOnlyReplacesTheExactSourceSymbol()
    {
        using var fixture = new Fixture();
        var minimap = fixture.Prepare();
        Assert.Equal(4, minimap.Resources.Count(candidate => candidate.MarginX > 12));
        Assert.Equal(1, minimap.Resources.Count(candidate => candidate.X == 40 && candidate.Symbol.DropIcons is null));
        Assert.Equal(1, minimap.Resources.Count(candidate => candidate.X == 40 && candidate.Symbol.DropIcons is not null));
        Assert.Single(minimap.Pets);
        Assert.DoesNotContain(fixture.Prepare(MapSurface.Worldmap).Resources, candidate => candidate.Symbol.DropIcons is not null);

        fixture.Settings.MapMinimapLayers["map"] = new(["Monsters/Fera", "Monster Drops/Meat"], ["Monster Drops/Meat/101", "Monster Drops/Meat/105"]);
        var selected = fixture.Prepare();
        var only = selected.Resources.Single(candidate => candidate.X == 40 && candidate.Symbol.DropIcons is not null).Symbol.DropIcons!;
        Assert.Single(only.Icons);
        Assert.Same(fixture.Icons["icons/item104.png"], only.Icons[0]);
        Assert.Equal(0, only.Additional);
        Assert.Equal(12, selected.Resources.Single(candidate => candidate.X == 40 && candidate.Symbol.DropIcons is not null).MarginX);

        fixture.Settings.MapMinimapLayers["map"] = new(["Monsters/Fera"], []);
        var hidden = fixture.Prepare();
        Assert.Equal(2, hidden.Resources.Count(candidate => candidate.X == 40));
        Assert.DoesNotContain(hidden.Resources, candidate => candidate.Symbol.DropIcons is not null);
    }

    [Fact]
    public void WideRowsStayVisibleAtViewportEdgeAndShareIconsWithoutOffscreenReads()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        Assert.Equal(0, fixture.ItemReads);
        var placement = new MapPlacement("map", new MapFix(1, 0, 0, 0, 1, 0, 100, 100, .3, false),
            1, new Rectangle(0, 0, 100, 80), new Point2d(10, 20));
        var visible = RouteOverlayForm.VisibleSymbols(placement, prepared);
        Assert.Contains(visible, symbol => symbol.X == 130 && symbol.DropIcons is not null); // centre outside, icon row intersects
        Assert.DoesNotContain(visible, symbol => symbol.X is 150 or 200);
        var groups = visible.Select(symbol => symbol.DropIcons).OfType<RouteOverlayForm.MonsterDropIcons>().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Same(groups[0], groups[1]);
        Assert.Equal(3, fixture.ItemReads);
        Assert.Equal(2, groups[0].Additional);
        Assert.Equal([fixture.Icons["icons/item101.png"], fixture.Icons["icons/item102.png"], fixture.Icons["icons/item103.png"]], groups[0].Icons);
        for (var frame = 0; frame < 60; frame++) RouteOverlayForm.VisibleSymbols(placement, prepared);
        Assert.Equal(3, fixture.ItemReads);
    }

    [Fact]
    public void CompositeSpriteIsReusedAndContainsThreeIconsAndOverflowBadge()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        var group = prepared.Resources.Single(candidate => candidate.X == 40 && candidate.MarginX > 12).Symbol.DropIcons!;
        var sprite = RouteOverlayForm.MonsterDropSprite(group, OverlayThemeDefinition.Legacy);
        Assert.Equal(94, sprite.Width);
        Assert.Equal(24, sprite.Height);
        Assert.Same(sprite, RouteOverlayForm.MonsterDropSprite(group, OverlayThemeDefinition.Legacy));
        Assert.Equal(Color.Red.ToArgb(), sprite.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), sprite.GetPixel(34, 12).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), sprite.GetPixel(56, 12).ToArgb());
        Assert.Contains(Enumerable.Range(70, 22).SelectMany(x => Enumerable.Range(3, 18).Select(y => sprite.GetPixel(x, y))),
            pixel => pixel.R > 180 && pixel.G > 180 && pixel.B > 180); // readable +N text in the extra slot
    }

    [Fact]
    public void TransientDropIconReadCanRetryWithoutPoisoningTheSharedPreparedGroup()
    {
        using var fixture = new Fixture { IconFailures = 1 };
        var prepared = fixture.Prepare();
        var candidate = prepared.Resources.Single(candidate => candidate.X == 40 && candidate.MarginX > 12);
        Assert.Throws<IOException>(() => candidate.Symbol);
        var symbol = candidate.Symbol;
        Assert.NotNull(symbol.DropIcons);
        Assert.Same(symbol, candidate.Symbol);
        Assert.Equal(4, fixture.ItemReads); // failed first read, followed by three successful cached icons
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-map-drops-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly Dictionary<string, byte[]?> _saved = new();
        internal Dictionary<string, Bitmap> Icons { get; } = new(StringComparer.Ordinal);
        internal int ItemReads { get; private set; }
        internal int IconFailures { get; set; }
        internal AppSettings Settings { get; } = new()
        {
            MapMinimapLayers = new() { ["map"] = new(["Monsters/Fera", "Locations/Field Boss", "Pets/Fera", "Monster Drops/Meat", "Monster Drops/Fruit", "Monster Drops/Leather"], []) },
            MapWorldmapLayers = new() { ["map"] = new([], []) },
        };
        private ProgressService Progress { get; }
        private MapTargetsService Targets { get; }

        internal Fixture(string? change = null, string? iconOverride = null, string? removedType = null)
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "map"));
            System.IO.Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(Path.Combine(Directory, "manifest.json"), """{"maps":[{"id":"map","label":"Map","metersPerPixel":1}]}""");
            var root = JsonNode.Parse("""
                {"categories":[{"group":"Monsters","name":"Fera"},{"group":"Pets","name":"Fera"},{"group":"Locations","name":"Field Boss"}],"icons":[],
                 "markers":[[0,40,20,"Test beast","Testbestie",-1,null,{"npcId":"1001"}],
                            [0,40,20,"Different beast",null,-1,null,{"npcId":"1002"}],
                            [1,40,20,"Test pet",null,-1,"pet"],
                            [2,200,20,"Test boss",null,-1,null,{"npcId":"1003"}],
                            [0,130,20,"Edge beast",null,-1,null,{"npcId":"1004"}],
                            [0,150,20,"Far beast",null,-1,null,{"npcId":"1005"}]],
                 "monsterDrops":{"schemaVersion":1,"items":[
                    {"id":"105","en":"Other meat","de":null,"type":"Meat","icon":"icons/item105.png"},
                    {"id":"103","en":"Test leather","de":"Testleder","type":"Leather","icon":"icons/item103.png"},
                    {"id":"101","en":"Test meat","de":null,"type":"Meat","icon":"icons/item101.png"},
                    {"id":"104","en":"Rare meat","de":null,"type":"Meat","icon":"icons/item104.png"},
                    {"id":"102","en":"Test fruit","de":"Testfrucht","type":"Fruit","icon":"icons/item102.png"}],
                    "spawns":[{"marker":0,"npcId":"1001","items":["105","103","101","102","104"]},
                              {"marker":3,"npcId":"1003","items":["105","103","101","102","104"]},
                              {"marker":4,"npcId":"1004","items":["105","103","101","102","104"]},
                              {"marker":5,"npcId":"1005","items":["105","103","101","102","104"]}]}}
                """)!.AsObject();
            var drops = root["monsterDrops"]!;
            switch (change)
            {
                case "missing": root.Remove("monsterDrops"); break;
                case "wrong-version": drops["schemaVersion"] = 2; break;
                case "text-version": drops["schemaVersion"] = "1"; break;
                case "unknown-item": drops["spawns"]![0]!["items"]![0] = "unknown"; break;
                case "wrong-category": drops["spawns"]![0]!["marker"] = 2; break;
                case "wrong-npc": drops["spawns"]![0]!["npcId"] = "9999"; break;
                case "duplicate-marker": drops["spawns"]!.AsArray().Add(drops["spawns"]![0]!.DeepClone()); break;
                case "duplicate-item": drops["items"]!.AsArray().Add(drops["items"]![0]!.DeepClone()); break;
                case "unknown-type": drops["items"]![0]!["type"] = "Unproven"; break;
                case "nonnumeric-item": drops["items"]![0]!["id"] = "item-name"; break;
                case "nonnumeric-npc": drops["spawns"]![0]!["npcId"] = "monster-name"; break;
                case "malformed-icon": drops["items"]![0]!["icon"] = 42; break;
                case "duplicate-reference": drops["spawns"]![0]!["items"]!.AsArray().Add("105"); break;
                case "empty": drops["items"] = new JsonArray(); drops["spawns"] = new JsonArray(); break;
            }
            if (removedType is not null)
            {
                drops["items"]![0]!["type"] = removedType;
                drops["spawns"]![3]!["items"] = new JsonArray("105");
            }
            if (iconOverride is not null) drops["items"]![0]!["icon"] = iconOverride;
            File.WriteAllText(Path.Combine(Directory, "map", "data.js"), "window.SoulcrestMaps[\"map\"] = " + root.ToJsonString() + ";");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", Directory);
            foreach (var file in new[] { AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile })
            {
                _saved[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
                File.Delete(file);
            }
            Progress = new ProgressService(Path.Combine(Directory, "progress.json"), new PetCatalog([new() { Id = "pet", En = "Test pet", Genus = "fera", Spawns = new() { ["map"] = 1 } }]));
            Targets = new MapTargetsService();
            foreach (var (id, color) in new[] { ("101", Color.Red), ("102", Color.Green), ("103", Color.Blue), ("104", Color.Yellow), ("105", Color.Purple) })
            {
                var bitmap = new Bitmap(20, 20, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(color);
                Icons.Add("icons/item" + id + ".png", bitmap);
            }
        }

        internal RouteOverlayForm.PreparedOverlay Prepare(MapSurface surface = MapSurface.Minimap) => RouteOverlayForm.PrepareOverlay("map", surface, "en",
            Settings, Progress, Targets, (_, _) => null, (path, _) =>
            {
                if (path is null || !Icons.TryGetValue(path, out var icon)) return null;
                ItemReads++;
                if (IconFailures > 0) { IconFailures--; throw new IOException("temporary local icon read"); }
                return icon;
            });

        public void Dispose()
        {
            Targets.Dispose();
            foreach (var (path, bytes) in _saved)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            foreach (var icon in Icons.Values) icon.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
