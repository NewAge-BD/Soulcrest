using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapLayerVisibilityTests
{
    [Fact]
    public void ThreeSurfacesKeepIndependentCategoryAndKindSelections()
    {
        var settings = new AppSettings
        {
            MapCategories = new() { ["test"] = ["Pets/Cogni", "Resources/Gem"] },
            MapHiddenKinds = new() { ["test"] = ["Resources/Gem/Ruby"] },
            MapMinimapLayers = new() { ["test"] = new(["NPCs/Other", "Resources/Gem"], ["Resources/Gem/Diamond"]) },
            MapWorldmapLayers = new() { ["test"] = new(["Pets/Fera", "Locations/Kibelisk"], []) },
        };
        Assert.True(Visible(settings, MapSurface.Interactive, "Pets", "Cogni"));
        Assert.False(Visible(settings, MapSurface.Minimap, "Pets", "Cogni"));
        Assert.False(Visible(settings, MapSurface.Worldmap, "Pets", "Cogni"));
        Assert.True(Visible(settings, MapSurface.Worldmap, "Pets", "Fera"));
        Assert.True(Visible(settings, MapSurface.Minimap, "NPCs", "Other"));
        Assert.False(Visible(settings, MapSurface.Interactive, "NPCs", "Other"));
        Assert.False(Kind(settings, MapSurface.Interactive, "Ruby"));
        Assert.True(Kind(settings, MapSurface.Minimap, "Ruby"));
        Assert.True(Kind(settings, MapSurface.Interactive, "Diamond"));
        Assert.False(Kind(settings, MapSurface.Minimap, "Diamond"));
        Assert.False(Kind(settings, MapSurface.Worldmap, "Ruby"));
        settings.MapCategories = new() { ["test"] = [] };
        settings.MapHiddenKinds = new() { ["test"] = [] };
        Assert.True(Visible(settings, MapSurface.Minimap, "NPCs", "Other"));
        Assert.False(Kind(settings, MapSurface.Minimap, "Diamond"));
        Assert.True(Visible(settings, MapSurface.Worldmap, "Pets", "Fera"));
        Assert.Null(MapLayerVisibility.Selection(settings, "other-map", MapSurface.Minimap));
    }

    [Theory]
    [InlineData(MapSurface.Minimap)]
    [InlineData(MapSurface.Worldmap)]
    public void MissingNativeSelectionsRetainLegacyChoices(MapSurface surface)
    {
        var settings = new AppSettings
        {
            MapCategories = new() { ["test"] = ["Resources/Gem", "Collectibles/Hidden Cube", "NPCs/Other", "Monsters/Fera"] },
            MapHiddenKinds = new() { ["test"] = ["Resources/Gem/Ruby"] },
        };
        Assert.Null(MapLayerVisibility.Selection(settings, "test", surface));
        Assert.True(Visible(settings, surface, "Pets", "Cogni")); // the former pet switch did not use interactive genera
        Assert.True(Visible(settings, surface, "Pets", "Fera"));
        Assert.True(Visible(settings, surface, "Collectibles", "Hidden Cube"));
        Assert.True(Kind(settings, surface, "Diamond"));
        Assert.False(Kind(settings, surface, "Ruby"));
        Assert.False(Visible(settings, surface, "Resources", "Herb"));
        Assert.False(Visible(settings, surface, "NPCs", "Other"));
        Assert.False(Visible(settings, surface, "Monsters", "Fera"));
        Assert.False(Visible(settings, surface, "Locations", "Kibelisk"));
        settings.ShowPetsInGame = settings.ShowResourcesInGame = false;
        Assert.False(Visible(settings, surface, "Pets", "Cogni"));
        Assert.False(Kind(settings, surface, "Diamond"));
        Assert.False(Visible(settings, surface, "Collectibles", "Hidden Cube"));
    }

    [Theory]
    [InlineData(MapSurface.Minimap)]
    [InlineData(MapSurface.Worldmap)]
    public void ExplicitNativeSelectionsOverrideLegacyAndEmptyMeansHidden(MapSurface surface)
    {
        var settings = new AppSettings { ShowPetsInGame = false, ShowResourcesInGame = false };
        var selections = surface == MapSurface.Minimap ? settings.MapMinimapLayers : settings.MapWorldmapLayers;
        selections["test"] = new(["Pets/Cogni", "Resources/Gem", "NPCs/Other", "Locations/Kibelisk", "Monsters/Fera"], []);
        Assert.True(Visible(settings, surface, "Pets", "Cogni"));
        Assert.False(Visible(settings, surface, "Pets", "Fera"));
        Assert.True(Kind(settings, surface, "Ruby"));
        Assert.True(Visible(settings, surface, "NPCs", "Other"));
        Assert.True(Visible(settings, surface, "Locations", "Kibelisk"));
        Assert.True(Visible(settings, surface, "Monsters", "Fera"));
        settings.ShowPetsInGame = settings.ShowResourcesInGame = true;
        selections["test"] = new([], []);
        Assert.NotNull(MapLayerVisibility.Selection(settings, "test", surface));
        Assert.False(Visible(settings, surface, "Pets", "Cogni"));
        Assert.False(Visible(settings, surface, "NPCs", "Other"));
        Assert.False(Kind(settings, surface, "Ruby"));
    }

    [Theory]
    [InlineData(false, MapSurface.Minimap)]
    [InlineData(true, MapSurface.Worldmap)]
    public void NativePlacementSelectsTheCorrectSurface(bool worldMap, MapSurface expected)
    {
        var placement = new MapPlacement("test", new MapFix(1, 0, 0, 0, 1, 0, 50, 60, .3, false), 1,
            new System.Drawing.Rectangle(0, 0, 600, 400), new Point2d(40, 40), WorldMap: worldMap);
        Assert.Equal(expected, RouteOverlayForm.SurfaceOf(placement));
    }

    [Fact]
    public void NativeBaseSymbolsAndPetGeneraUseTheirSurfaceWhileMarkedPetsRemainVisible()
    {
        var settings = new AppSettings
        {
            MapMinimapLayers = new() { ["test"] = new(["Pets/Cogni", "NPCs/Other", "Resources/Gem"], ["Resources/Gem/Ruby"]) },
            MapWorldmapLayers = new() { ["test"] = new(["Pets/Fera", "Locations/Kibelisk"], []) },
        };
        var cogni = new PetSpawn("a", 10, 20, null, Category: "Pets/Cogni");
        var fera = new PetSpawn("b", 30, 40, null, Category: "Pets/Fera");
        Assert.True(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Minimap, cogni, false));
        Assert.False(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Minimap, fera, false));
        Assert.False(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Worldmap, cogni, false));
        Assert.True(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Worldmap, fera, false));
        Assert.True(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Minimap, fera, true));
        Assert.True(RouteOverlayForm.PetVisible(settings, "test", MapSurface.Worldmap, cogni, true));
        var npc = new MapResource("NPCs/Other", 10, 20, null);
        var location = new MapResource("Locations/Kibelisk", 30, 40, null);
        Assert.True(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Minimap, npc));
        Assert.False(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Worldmap, npc));
        Assert.False(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Minimap, location));
        Assert.True(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Worldmap, location));
        Assert.False(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Minimap, new("Resources/Gem", 0, 0, null, "Ruby")));
        Assert.True(RouteOverlayForm.SymbolVisible(settings, "test", MapSurface.Minimap, new("Resources/Gem", 0, 0, null, "Diamond")));
    }

    [Fact]
    public void OldSettingsRemainLegacyUntilExplicitSelectionAndNewChoicesSurviveRestart()
    {
        WithSettingsFile("""
            {"SettingsRevision":4,"ShowPetsInGame":false,"ShowResourcesInGame":true,
             "MapCategories":{"test":["Resources/Gem"]},"MapHiddenKinds":{"test":["Resources/Gem/Ruby"]}}
            """, () =>
        {
            var settings = new SettingsService();
            Assert.Empty(settings.Current.MapMinimapLayers);
            Assert.Empty(settings.Current.MapWorldmapLayers);
            Assert.False(Visible(settings.Current, MapSurface.Minimap, "Pets", "Cogni"));
            Assert.True(Kind(settings.Current, MapSurface.Worldmap, "Diamond"));
            settings.Update(s =>
            {
                s.MapMinimapLayers = new() { ["test"] = new(["Pets/Cogni", "NPCs/Other"], []) };
                s.MapWorldmapLayers = new() { ["test"] = new([], ["Resources/Gem/Diamond"]) };
            });
            var reloaded = new SettingsService().Current;
            Assert.True(Visible(reloaded, MapSurface.Minimap, "Pets", "Cogni"));
            Assert.True(Visible(reloaded, MapSurface.Minimap, "NPCs", "Other"));
            Assert.False(Kind(reloaded, MapSurface.Worldmap, "Diamond"));
            Assert.False(Visible(reloaded, MapSurface.Worldmap, "Pets", "Cogni"));
            Assert.False(reloaded.ShowPetsInGame);
            Assert.Equal(["Resources/Gem"], reloaded.MapCategories["test"]);
            Assert.Equal(["Resources/Gem/Ruby"], reloaded.MapHiddenKinds["test"]);
            Assert.Null(MapLayerVisibility.Selection(reloaded, "other-map", MapSurface.Worldmap));
        });
    }

    [Fact]
    public void MalformedNewSelectionsNormalizeWithoutConvertingMissingMapsToExplicitEmpty()
    {
        WithSettingsFile("""
            {"SettingsRevision":4,"MapMinimapLayers":null,"MapWorldmapLayers":{
              "missing":null,"empty":{"Categories":null,"HiddenKinds":null},
              "test":{"Categories":["Pets/Cogni","Pets/Cogni",null,""],"HiddenKinds":["Resources/Gem/Ruby","Resources/Gem/Ruby"]}}}
            """, () =>
        {
            var settings = new SettingsService();
            Assert.Empty(settings.Current.MapMinimapLayers);
            Assert.Null(MapLayerVisibility.Selection(settings.Current, "missing", MapSurface.Worldmap));
            Assert.Empty(MapLayerVisibility.Selection(settings.Current, "empty", MapSurface.Worldmap)!.Categories);
            Assert.Equal(["Pets/Cogni"], settings.Current.MapWorldmapLayers["test"].Categories);
            Assert.Equal(["Resources/Gem/Ruby"], settings.Current.MapWorldmapLayers["test"].HiddenKinds);
            settings.Update(_ => { });
            var reloaded = new SettingsService().Current;
            Assert.Null(MapLayerVisibility.Selection(reloaded, "missing", MapSurface.Worldmap));
            Assert.NotNull(MapLayerVisibility.Selection(reloaded, "empty", MapSurface.Worldmap));
        });
    }

    private static bool Visible(AppSettings settings, MapSurface surface, string group, string name) =>
        MapLayerVisibility.CategoryVisible(settings, "test", surface, group, name);

    private static bool Kind(AppSettings settings, MapSurface surface, string name) =>
        MapLayerVisibility.KindVisible(settings, "test", surface, "Resources", "Gem", name);

    private static void WithSettingsFile(string json, Action test)
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try { File.WriteAllText(path, json); test(); }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllBytes(path, backup);
            _ = new SettingsService();
        }
    }
}
