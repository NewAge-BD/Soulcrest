using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ResourceMapServiceTests
{
    private static readonly ImmutableArray<MapLegendLayer> Layers =
        [new("Resources/Ore", ["Ore A", "Ore B"], false), new("Pets/Fera", [], true), new("Locations/Village", [], true)];
    private static ResourceMapSources Sources() => new(
        [new(7, "first", "Resources/Ore", "Ore A", 10, 20), new(7, "second", "Resources/Ore", "Ore A", 30, 40)],
        new Dictionary<string, ImmutableArray<MapLegendLayer>> { ["first"] = Layers, ["second"] = Layers });

    [Fact]
    public void OneConfirmationSetsEverySourceMapAndKeepsUnrelatedKindsAndSurfacesIndependent()
    {
        var settings = new SettingsService(); var ui = new UiState();
        settings.Current.MapCategories = new() { ["first"] = ["Pets/Fera", "Resources/Ore"] };
        settings.Current.MapHiddenKinds = new() { ["first"] = ["Resources/Ore/Ore B"] };
        using var service = new ResourceMapService(settings, Sources(), ui, () => "second");
        service.Open(7, "Synthetic ore"); service.Confirm(new(true, false, true), false);
        foreach (var map in new[] { "first", "second" })
        {
            Assert.True(MapLayerVisibility.KindVisible(settings.Current, map, MapSurface.Interactive, "Resources", "Ore", "Ore A"));
            Assert.True(MapLayerVisibility.KindVisible(settings.Current, map, MapSurface.Worldmap, "Resources", "Ore", "Ore A"));
            Assert.False(MapLayerVisibility.KindVisible(settings.Current, map, MapSurface.Minimap, "Resources", "Ore", "Ore A"));
            Assert.False(MapLayerVisibility.KindVisible(settings.Current, map, MapSurface.Interactive, "Resources", "Ore", "Ore B"));
            Assert.True(MapLayerVisibility.CategoryVisible(settings.Current, map, MapSurface.Interactive, "Pets", "Fera"));
        }
        Assert.Equal("map", ui.Tab);
        var focus = Assert.IsType<ResourceMapFocus>(ui.TakePendingResourceFocus());
        Assert.Equal("second", focus.MapId); Assert.Equal(2, focus.SourceMaps.Length); Assert.Single(focus.Locations);
        Assert.Null(ui.TakePendingResourceFocus());
    }

    [Fact]
    public void NativeOnlySelectionLeavesCurrentTabAndDisabledOverlayPreferencesAlone()
    {
        var settings = new SettingsService(); settings.Current.MapOverlayEnabled = false;
        var ui = new UiState(); ui.Navigate("crafting");
        using var service = new ResourceMapService(settings, Sources(), ui, () => null);
        service.Open(7, "Synthetic ore"); service.Confirm(new(false, true, false), false);
        Assert.Equal("crafting", ui.Tab); Assert.Null(ui.TakePendingResourceFocus()); Assert.False(settings.Current.MapOverlayEnabled);
    }

    [Fact]
    public void DialogReadsLegendChangesAndRepresentsMixedSourceMaps()
    {
        var settings = new SettingsService(); var ui = new UiState();
        settings.Current.MapWorldmapLayers = new() { ["first"] = new(["Resources/Ore"], []), ["second"] = new([], []) };
        using var service = new ResourceMapService(settings, Sources(), ui, () => null);
        service.Open(7, "Synthetic ore");
        var dialog = Assert.IsType<ResourceMapDialogRequest>(service.Pending);
        Assert.Contains(MapSurface.Worldmap, dialog.Mixed); Assert.True(dialog.Selection.Worldmap);
        service.Cancel(); Assert.Null(service.Pending);
        Assert.False(settings.Current.ResourceMapDontShowAgain);
        Assert.False(MapLayerVisibility.KindVisible(settings.Current, "second", MapSurface.Worldmap, "Resources", "Ore", "Ore A"));
    }

    [Fact]
    public void GlobalDefaultCanBeSavedReusedAndEditedWithoutApplyingAResource()
    {
        var settings = new SettingsService(); var ui = new UiState(); ui.Navigate("crafting");
        using var service = new ResourceMapService(settings, Sources(), ui, () => null);
        service.Open(7, "Synthetic ore"); service.Confirm(new(true, false, false), true);
        Assert.True(settings.Current.ResourceMapDontShowAgain);
        service.Open(7, "Synthetic ore"); Assert.Null(service.Pending);
        service.EditDefaults(); Assert.True(service.Pending!.EditingDefaults);
        var before = settings.Current.MapCategories;
        service.Confirm(new(false, true, false), false);
        Assert.False(settings.Current.ResourceMapDontShowAgain); Assert.Same(before, settings.Current.MapCategories);
        service.Open(7, "Synthetic ore"); Assert.NotNull(service.Pending);
        service.Confirm(new(false, false, false), true); Assert.NotNull(service.Pending);
    }

    [RequiresFact(TestRequirement.MapData)]
    public void EmbeddedGatherRelationsUseExactExistingPlacementsIncludingOrichalcum()
    {
        var directory = AppPaths.FindMapData();
        Assert.NotNull(directory);
        var sources = new ResourceMapSources(directory, [new("altgard", "Altgard", "Synthetic")]);
        var ore = sources.ForItem(610620013);
        Assert.NotEmpty(ore); Assert.All(ore, point => Assert.Equal("Resources/Ore", point.Category));
        Assert.Empty(sources.ForItem(110730019));
    }
}
