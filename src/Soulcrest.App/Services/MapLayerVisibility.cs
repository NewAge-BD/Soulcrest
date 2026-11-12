namespace Soulcrest.App.Services;

/// <summary>User request 2026-10-12: each map surface has an independent legend.</summary>
public enum MapSurface { Interactive, Minimap, Worldmap }

/// <summary>Shown Group/Name categories and hidden Group/Name/Kind entries; replace arrays on edit.</summary>
public sealed record MapLayerSelection(string[] Categories, string[] HiddenKinds);

/// <summary>Native selections override the former shared pet/resource switches once a map has been edited.</summary>
public static class MapLayerVisibility
{
    /// <summary>Null means no saved selection: the interactive map uses its data defaults, native maps use legacy preferences.</summary>
    public static MapLayerSelection? Selection(AppSettings settings, string map, MapSurface surface) => surface switch
    {
        MapSurface.Interactive => settings.MapCategories.TryGetValue(map, out var shown)
            ? new(shown, settings.MapHiddenKinds.GetValueOrDefault(map) ?? []) : null,
        MapSurface.Minimap => settings.MapMinimapLayers.GetValueOrDefault(map),
        MapSurface.Worldmap => settings.MapWorldmapLayers.GetValueOrDefault(map),
        _ => null,
    };

    public static bool CategoryVisible(AppSettings settings, string map, MapSurface surface, string group, string name)
    {
        var key = MapPetMarkers.CategoryKey(group, name);
        if (Selection(settings, map, surface) is { } selection)
            return selection.Categories.Contains(key, StringComparer.Ordinal);
        if (surface == MapSurface.Interactive)
            return group == "Pets";
        if (group == "Pets")
            return settings.ShowPetsInGame;
        return settings.ShowResourcesInGame && MapPetMarkers.ShownInGame(group, name)
            && settings.MapCategories.TryGetValue(map, out var shown) && shown.Contains(key, StringComparer.Ordinal);
    }

    public static bool KindVisible(AppSettings settings, string map, MapSurface surface, string group, string name, string? kind)
    {
        if (!CategoryVisible(settings, map, surface, group, name)) return false;
        if (kind is null) return true;
        var key = MapPetMarkers.KindKey(MapPetMarkers.CategoryKey(group, name), kind);
        var hidden = Selection(settings, map, surface)?.HiddenKinds ?? settings.MapHiddenKinds.GetValueOrDefault(map) ?? [];
        return !hidden.Contains(key, StringComparer.Ordinal);
    }

    /// <summary>Edits the same category/kind keys as the legend, freezing legacy native views first.</summary>
    public static void SetKind(AppSettings settings, string map, MapSurface surface,
        IReadOnlyList<MapLegendLayer> layers, string category, string kind, bool visible)
    {
        MapLayerSelection Capture(MapSurface target)
        {
            if (Selection(settings, map, target) is { } saved) return saved;
            var shown = layers.Where(layer => target == MapSurface.Interactive ? layer.DefaultVisible
                : CategoryVisible(settings, map, target, layer.Category.Split('/')[0], layer.Category[(layer.Category.IndexOf('/') + 1)..]))
                .Select(layer => layer.Category).ToArray();
            return new(shown, settings.MapHiddenKinds.GetValueOrDefault(map) ?? []);
        }
        // Capture all three before changing any fallback dependency.
        var interactive = Capture(MapSurface.Interactive); var mini = Capture(MapSurface.Minimap); var world = Capture(MapSurface.Worldmap);
        settings.MapMinimapLayers = new(settings.MapMinimapLayers) { [map] = mini };
        settings.MapWorldmapLayers = new(settings.MapWorldmapLayers) { [map] = world };
        settings.MapCategories = new(settings.MapCategories) { [map] = interactive.Categories };
        settings.MapHiddenKinds = new(settings.MapHiddenKinds) { [map] = interactive.HiddenKinds };
        var current = surface switch { MapSurface.Minimap => mini, MapSurface.Worldmap => world, _ => interactive };
        var categories = current.Categories.ToHashSet(StringComparer.Ordinal); var hidden = current.HiddenKinds.ToHashSet(StringComparer.Ordinal);
        var key = MapPetMarkers.KindKey(category, kind);
        if (visible)
        {
            hidden.Remove(key);
            if (categories.Add(category))
                foreach (var other in layers.FirstOrDefault(layer => layer.Category == category)?.Kinds ?? [])
                    if (other != kind) hidden.Add(MapPetMarkers.KindKey(category, other));
        }
        else hidden.Add(key);
        var next = new MapLayerSelection(categories.Order(StringComparer.Ordinal).ToArray(), hidden.Order(StringComparer.Ordinal).ToArray());
        if (surface == MapSurface.Minimap) settings.MapMinimapLayers = new(settings.MapMinimapLayers) { [map] = next };
        else if (surface == MapSurface.Worldmap) settings.MapWorldmapLayers = new(settings.MapWorldmapLayers) { [map] = next };
        else { settings.MapCategories = new(settings.MapCategories) { [map] = next.Categories }; settings.MapHiddenKinds = new(settings.MapHiddenKinds) { [map] = next.HiddenKinds }; }
    }

    internal static Dictionary<string, MapLayerSelection> Normalize(Dictionary<string, MapLayerSelection>? selections) =>
        (selections ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => new MapLayerSelection(Clean(pair.Value.Categories), Clean(pair.Value.HiddenKinds)), StringComparer.Ordinal);

    private static string[] Clean(string[]? values) => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal).ToArray();
}
