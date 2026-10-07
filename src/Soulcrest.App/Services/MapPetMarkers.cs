using System.Collections.Concurrent;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>A pet spawn of the map data package (marker of the "Pets" group).</summary>
public sealed record PetSpawn(string PetId, double X, double Y, string? Icon);

/// <summary>
/// A resource or hidden cube of the map data package; Category is "Group/Name" as in the legend, Kind
/// the resource itself (Gem: "Ruby"), null for hidden cubes.
/// </summary>
public sealed record MapResource(string Category, double X, double Y, string? Icon, string? Kind = null)
{
    public string? KindKey => Kind is null ? null : MapPetMarkers.KindKey(Category, Kind);
}

/// <summary>
/// Markers per map, read from the data package's data.js (the same markers the interactive map
/// shows). Used to draw pet symbols with their progress and the resources chosen in the legend on the
/// in-game map.
/// </summary>
public static class MapPetMarkers
{
    private sealed record MapMarkers(IReadOnlyList<PetSpawn> Pets, IReadOnlyList<MapResource> Resources, IReadOnlyList<PetSpawn> SoulMonsters);

    private static readonly ConcurrentDictionary<string, MapMarkers> Cache = new();

    public static IReadOnlyList<PetSpawn> For(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Pets;

    /// <summary>Monsters dropping a pet's soul (Monsters group with a pet), shown for marked pets.</summary>
    public static IReadOnlyList<PetSpawn> SoulMonstersFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).SoulMonsters;

    /// <summary>All resources and hidden cubes of a map (the categories the in-game map may show).</summary>
    public static IReadOnlyList<MapResource> ResourcesFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Resources;

    /// <summary>Categories that can be shown on the in-game map: every resource and the hidden cubes.</summary>
    public static bool ShownInGame(string group, string name) => group == "Resources" || (group == "Collectibles" && name == "Hidden Cube");

    public static string CategoryKey(string group, string name) => group + "/" + name;

    public static string KindKey(string categoryKey, string kind) => categoryKey + "/" + kind;

    private static MapMarkers Markers(string mapDataDirectory, string mapId) =>
        Cache.GetOrAdd(mapId, id => Read(Path.Combine(mapDataDirectory, id, "data.js")));

    internal static IReadOnlyList<PetSpawn> Load(string path) => Read(path).Pets;

    internal static IReadOnlyList<MapResource> LoadResources(string path) => Read(path).Resources;

    internal static IReadOnlyList<PetSpawn> LoadSoulMonsters(string path) => Read(path).SoulMonsters;

    /// <summary>data.js is "window.SoulcrestMaps["id"] = { ... };": the object after the first '=' is JSON.</summary>
    private static MapMarkers Read(string path)
    {
        if (!File.Exists(path))
            return new([], [], []);
        var text = File.ReadAllText(path);
        var start = text.IndexOf("] = {", StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            return new([], [], []);
        using var document = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
        var root = document.RootElement;
        var categories = root.GetProperty("categories").EnumerateArray()
            .Select(c => (Group: c.GetProperty("group").GetString() ?? "", Name: c.GetProperty("name").GetString() ?? "",
                Icon: c.TryGetProperty("icon", out var i) ? i.GetInt32() : -1))
            .ToList();
        var icons = root.GetProperty("icons").EnumerateArray().Select(i => i.GetString()).ToList();
        var spawns = new List<PetSpawn>();
        var resources = new List<MapResource>();
        var monsters = new List<PetSpawn>();
        // Marker: [category, x, y, name, nameDe, iconIndex, petId]
        foreach (var marker in root.GetProperty("markers").EnumerateArray())
        {
            var category = categories[marker[0].GetInt32()];
            if (category.Group == "Monsters")
            {
                if (marker.GetArrayLength() > 6 && marker[6].ValueKind == JsonValueKind.String)
                    monsters.Add(new PetSpawn(marker[6].GetString()!, marker[1].GetDouble(), marker[2].GetDouble(), null));
                continue;
            }
            var pet = category.Group == "Pets";
            if (!pet && !ShownInGame(category.Group, category.Name))
                continue;
            if (pet && (marker.GetArrayLength() < 7 || marker[6].ValueKind != JsonValueKind.String))
                continue;
            var iconIndex = marker.GetArrayLength() > 5 && marker[5].ValueKind == JsonValueKind.Number ? marker[5].GetInt32() : -1;
            if (iconIndex < 0)
                iconIndex = category.Icon;
            var icon = iconIndex >= 0 && iconIndex < icons.Count ? icons[iconIndex] : null;
            if (pet)
                spawns.Add(new PetSpawn(marker[6].GetString()!, marker[1].GetDouble(), marker[2].GetDouble(), icon));
            else
            {
                var kind = category.Group == "Resources" && marker.GetArrayLength() > 3 && marker[3].ValueKind == JsonValueKind.String ? marker[3].GetString() : null;
                resources.Add(new MapResource(CategoryKey(category.Group, category.Name), marker[1].GetDouble(), marker[2].GetDouble(), icon, kind));
            }
        }
        return new(spawns, resources, monsters);
    }
}
