using System.Collections.Concurrent;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>A pet spawn of the map data package (marker of the "Pets" group).</summary>
/// <param name="Monster">The monster that drops the soul here (English, German), when it is named differently from the pet.</param>
public sealed record PetSpawn(string PetId, double X, double Y, string? Icon, MonsterName? Monster = null, string? Category = null);

/// <summary>A guard NPC on the map (world pixels).</summary>
public sealed record GuardPost(double X, double Y);

/// <summary>A monster name in English and German (German may be missing); Count = monsters of its spawn group.</summary>
public sealed record MonsterName(string En, string? De, int Count = 1)
{
    public string In(string language) => language == "de" && !string.IsNullOrWhiteSpace(De) ? De : En;
}

/// <summary>
/// A non-pet symbol of the map data package; Category is "Group/Name" as in the legend, Kind
/// the resource itself (Gem: "Ruby"), null for categories without a kind filter.
/// </summary>
public sealed record MapResource(string Category, double X, double Y, string? Icon, string? Kind = null, string? OutlineColor = null)
{
    public string? KindKey => Kind is null ? null : MapPetMarkers.KindKey(Category, Kind);
}

/// <summary>
/// Markers per map, read from the data package's data.js (the same markers the interactive map
/// shows). Used to draw pet symbols with their progress and the symbols chosen for each native
/// map surface.
/// </summary>
public static partial class MapPetMarkers
{
    private sealed record MapMarkers(IReadOnlyList<PetSpawn> Pets, IReadOnlyList<MapResource> Resources, IReadOnlyList<PetSpawn> SoulMonsters,
        IReadOnlyList<GuardPost> Guards, IReadOnlyDictionary<string, string>? CategoryIcons = null, IReadOnlyList<MapResource>? Symbols = null, IReadOnlyList<MapMonsterSpawn>? AllMonsters = null, IReadOnlyList<HiddenCubeGroup>? HiddenCubeGroups = null);

    private static readonly ConcurrentDictionary<string, MapMarkers> Cache = new();

    public static IReadOnlyList<PetSpawn> For(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Pets;

    /// <summary>Monsters dropping a pet's soul (Monsters group with a pet), shown for marked pets.</summary>
    public static IReadOnlyList<PetSpawn> SoulMonstersFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).SoulMonsters;

    /// <summary>All named monsters, whether or not a soul relation is known.</summary>
    public static IReadOnlyList<MapMonsterSpawn> MonstersFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).AllMonsters ?? [];

    /// <summary>All resources and hidden cubes of a map (the categories the in-game map may show).</summary>
    public static IReadOnlyList<MapResource> ResourcesFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Resources;

    /// <summary>Every non-pet legend marker, including NPCs, locations, collectibles and monsters.</summary>
    public static IReadOnlyList<MapResource> SymbolsFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Symbols ?? [];

    /// <summary>Explicit cube groups and orientation connections from the local map package.</summary>
    public static IReadOnlyList<HiddenCubeGroup> HiddenCubeGroupsFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).HiddenCubeGroups ?? [];

    internal static IReadOnlyList<HiddenCubeGroup> LoadHiddenCubeGroups(string path) => Read(path).HiddenCubeGroups ?? [];

    /// <summary>Guards of a map (NPC "Guard", mostly at the Kibelisks: Altgard 304, Verteron 277 in the 2026-10 data).</summary>
    public static IReadOnlyList<GuardPost> GuardsFor(string mapDataDirectory, string mapId) => Markers(mapDataDirectory, mapId).Guards;

    /// <summary>The legend icon of a category by its name ("Sealed Dungeon"), for targets that carry no icon of their own.</summary>
    public static string? CategoryIcon(string mapDataDirectory, string mapId, string category) =>
        Markers(mapDataDirectory, mapId).CategoryIcons?.GetValueOrDefault(category);

    internal static IReadOnlyList<GuardPost> LoadGuards(string path) => Read(path).Guards;

    /// <summary>
    /// The data names the guards only "Guard" (German "Wachmann"); the titles &lt;Dawn Legion&gt; and &lt;Vigilante
    /// Group&gt; the game shows above them are not in it.
    /// </summary>
    public const string GuardName = "Guard";

    /// <summary>The resource categories the legacy in-game overlay included before surface-specific legends.</summary>
    public static bool ShownInGame(string group, string name) => group == "Resources" || (group == "Collectibles" && name is "Hidden Cube" or "Cube Groups");

    public static string CategoryKey(string group, string name) => group + "/" + name;

    public static string KindKey(string categoryKey, string kind) => categoryKey + "/" + kind;

    private static MapMarkers Markers(string mapDataDirectory, string mapId) =>
        Cache.GetOrAdd(Path.GetFullPath(Path.Combine(mapDataDirectory, mapId, "data.js")), Read);

    internal static IReadOnlyList<PetSpawn> Load(string path) => Read(path).Pets;

    internal static IReadOnlyList<MapResource> LoadResources(string path) => Read(path).Resources;

    internal static IReadOnlyList<PetSpawn> LoadSoulMonsters(string path) => Read(path).SoulMonsters;

    internal static IReadOnlyList<MapResource> LoadSymbols(string path) => Read(path).Symbols ?? [];

    /// <summary>
    /// The monster at a pet symbol: pets are often named differently from the monster that drops their soul
    /// (pet "Drana Mutant Brute", monster "Drana Mutant"; user request 2026-10-07). Null when the symbol
    /// has no source monster or is not found.
    /// </summary>
    public static MonsterName? MonsterAt(string mapDataDirectory, string mapId, string petId, double x, double y) =>
        For(mapDataDirectory, mapId).Concat(SoulMonstersFor(mapDataDirectory, mapId))
            .FirstOrDefault(s => s.PetId == petId && s.Monster is not null && Math.Abs(s.X - x) < 0.5 && Math.Abs(s.Y - y) < 0.5)?.Monster;

    /// <summary>
    /// The monsters that drop a pet's soul on a map, the one with the most spawns first (user request
    /// 2026-10-07: the loot tracker names the monster to hunt, "Soft Breeze Spirit" for "Lesser Wind Spirit").
    /// </summary>
    public static IReadOnlyList<MonsterName> MonstersOf(string mapDataDirectory, string mapId, string petId) =>
        For(mapDataDirectory, mapId).Where(s => s.PetId == petId && s.Monster is not null).Select(s => s.Monster!)
            .GroupBy(m => m.En, StringComparer.Ordinal)
            .Select(g => g.First() with { Count = g.Sum(m => m.Count) })
            .OrderByDescending(m => m.Count).ThenBy(m => m.En, StringComparer.Ordinal)
            .ToList();

    /// <summary>Marker field 7 {"en": "Drana Mutant (28×)", "de": …}: the source monster, without the group size.</summary>
    internal static MonsterName? SourceMonster(JsonElement marker)
    {
        if (marker.GetArrayLength() < 8 || marker[7].ValueKind != JsonValueKind.Object
            || !marker[7].TryGetProperty("en", out var en) || en.GetString() is not { Length: > 0 } english)
            return null;
        var german = marker[7].TryGetProperty("de", out var de) ? de.GetString() : null;
        var size = GroupSize().Match(english);
        return new MonsterName(WithoutCount(english), german is null ? null : WithoutCount(german), size.Success ? int.Parse(size.Groups[1].Value) : 1);
    }

    private static string WithoutCount(string name) => GroupSize().Replace(name, "");

    [System.Text.RegularExpressions.GeneratedRegex(@" \((\d+)×\)$")]
    private static partial System.Text.RegularExpressions.Regex GroupSize();

    /// <summary>data.js is "window.SoulcrestMaps["id"] = { ... };": the object after the first '=' is JSON.</summary>
    private static MapMarkers Read(string path)
    {
        if (!File.Exists(path))
            return new([], [], [], []);
        var text = File.ReadAllText(path);
        var start = text.IndexOf("] = {", StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            return new([], [], [], []);
        using var document = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
        var root = document.RootElement;
        var categories = root.GetProperty("categories").EnumerateArray()
            .Select(c => (Group: c.GetProperty("group").GetString() ?? "", Name: c.GetProperty("name").GetString() ?? "",
                Icon: c.TryGetProperty("icon", out var i) ? i.GetInt32() : -1))
            .ToList();
        var icons = root.GetProperty("icons").EnumerateArray().Select(i => i.GetString()).ToList();
        var cubeGroups = ReadHiddenCubeGroups(root, categories.Select(c => CategoryKey(c.Group, c.Name)).ToArray());
        var cubeColors = cubeGroups.SelectMany(g => g.Markers.Select(marker => (marker, g.Color)))
            .ToDictionary(item => item.marker, item => item.Color);
        var spawns = new List<PetSpawn>();
        var resources = new List<MapResource>();
        var symbols = new List<MapResource>();
        var monsters = new List<PetSpawn>();
        var guards = new List<GuardPost>();
        var allMonsters = new List<MapMonsterSpawn>();
        // Marker: [category, x, y, name, nameDe, iconIndex, petId]
        foreach (var (marker, markerIndex) in root.GetProperty("markers").EnumerateArray().Select((marker, index) => (marker, index)))
        {
            var category = categories[marker[0].GetInt32()];
            if (category.Group.StartsWith("NPC", StringComparison.Ordinal) && marker.GetArrayLength() > 3
                && marker[3].ValueKind == JsonValueKind.String && marker[3].GetString() == GuardName)
            {
                guards.Add(new GuardPost(marker[1].GetDouble(), marker[2].GetDouble()));
            }
            if (category.Group == "Monsters")
            {
                if (marker.GetArrayLength() > 6 && marker[6].ValueKind == JsonValueKind.String)
                {
                    var en = marker[3].ValueKind == JsonValueKind.String ? marker[3].GetString() : null;
                    var de = marker[4].ValueKind == JsonValueKind.String ? marker[4].GetString() : null;
                    monsters.Add(new PetSpawn(marker[6].GetString()!, marker[1].GetDouble(), marker[2].GetDouble(), null,
                        string.IsNullOrWhiteSpace(en) ? null : new MonsterName(en, de), CategoryKey(category.Group, category.Name)));
                }
            }
            var pet = category.Group == "Pets";
            if (pet && (marker.GetArrayLength() < 7 || marker[6].ValueKind != JsonValueKind.String))
                continue;
            var iconIndex = marker.GetArrayLength() > 5 && marker[5].ValueKind == JsonValueKind.Number ? marker[5].GetInt32() : -1;
            if (iconIndex < 0)
                iconIndex = category.Icon;
            var icon = iconIndex >= 0 && iconIndex < icons.Count ? icons[iconIndex] : null;
            if (category.Group == "Monsters" && marker[3].ValueKind == JsonValueKind.String && marker[3].GetString() is { Length: > 0 } name)
                allMonsters.Add(new MapMonsterSpawn(name, marker[4].ValueKind == JsonValueKind.String ? marker[4].GetString() : null,
                    marker.GetArrayLength() > 5 && marker[5].ValueKind == JsonValueKind.Number && marker[5].GetInt32() >= 0 ? icon : null,
                    marker.GetArrayLength() > 6 && marker[6].ValueKind == JsonValueKind.String ? marker[6].GetString() : null));
            if (pet)
                spawns.Add(new PetSpawn(marker[6].GetString()!, marker[1].GetDouble(), marker[2].GetDouble(), icon, SourceMonster(marker), CategoryKey(category.Group, category.Name)));
            else
            {
                var kind = category.Group == "Resources" && marker.GetArrayLength() > 3 && marker[3].ValueKind == JsonValueKind.String ? marker[3].GetString() : null;
                var symbol = new MapResource(CategoryKey(category.Group, category.Name), marker[1].GetDouble(), marker[2].GetDouble(), icon, kind, cubeColors.GetValueOrDefault(markerIndex));
                symbols.Add(symbol);
                if (ShownInGame(category.Group, category.Name)) resources.Add(symbol);
            }
        }
        var categoryIcons = categories.Where(c => c.Icon >= 0 && c.Icon < icons.Count && icons[c.Icon] is not null)
            .GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => icons[g.First().Icon]!, StringComparer.Ordinal);
        return new(spawns, resources, monsters, guards, categoryIcons, symbols, allMonsters, cubeGroups);
    }
}
