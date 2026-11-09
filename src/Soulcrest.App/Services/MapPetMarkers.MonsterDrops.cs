using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>A source-confirmed crafting material; its immutable ID is the legend kind key.</summary>
public sealed record MapMonsterDropItem(string Id, string En, string? De, string? Icon, string Type)
{
    public string Category => MapPetMarkers.CategoryKey("Monster Drops", Type);
    public string In(string language) => language == "de" && !string.IsNullOrWhiteSpace(De) ? De : En;
}

/// <summary>Drop materials on one exact existing monster or boss marker, never an inferred position.</summary>
public sealed record MapMonsterDropSpawn(int MarkerIndex, string NpcId, double X, double Y, MonsterName Monster,
    IReadOnlyList<MapMonsterDropItem> Items, MapResource SourceSymbol);

/// <summary>Available distinguishes a confirmed empty contract from missing or invalid optional metadata.</summary>
public sealed record MapMonsterDropCatalog(bool Available, IReadOnlyList<MapMonsterDropItem> Items, IReadOnlyList<MapMonsterDropSpawn> Spawns)
{
    public static MapMonsterDropCatalog Unknown { get; } = new(false, [], []);
}

public static partial class MapPetMarkers
{
    private static readonly HashSet<string> MonsterDropTypes = new(StringComparer.Ordinal)
    {
        "Meat", "Fruit", "Leather", "Refining Stones", "Spiritstones", "Balaur Materials", "Other Materials",
    };

    // User request 2026-11-02: keep older packages readable, but only expose these material families.
    private static readonly HashSet<string> VisibleMonsterDropTypes = new(StringComparer.Ordinal)
    {
        "Meat", "Fruit", "Leather", "Spiritstones",
    };

    public static MapMonsterDropCatalog MonsterDropCatalogFor(string mapDataDirectory, string mapId) =>
        Markers(mapDataDirectory, mapId).MonsterDrops ?? MapMonsterDropCatalog.Unknown;

    public static IReadOnlyList<MapMonsterDropSpawn> MonsterDropsFor(string mapDataDirectory, string mapId) =>
        MonsterDropCatalogFor(mapDataDirectory, mapId).Spawns;

    internal static MapMonsterDropCatalog LoadMonsterDropCatalog(string path) => Read(path).MonsterDrops ?? MapMonsterDropCatalog.Unknown;

    internal static IReadOnlyList<MapMonsterDropSpawn> LoadMonsterDrops(string path) => LoadMonsterDropCatalog(path).Spawns;

    /// <summary>User request 2026-11-02: optional ID-based materials linked only to existing exact monster markers.</summary>
    private static MapMonsterDropCatalog ReadMonsterDrops(JsonElement root, IReadOnlyList<string> categories,
        IReadOnlyDictionary<int, MapResource> symbols)
    {
        if (!root.TryGetProperty("monsterDrops", out var drops) || drops.ValueKind != JsonValueKind.Object
            || !drops.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1
            || !drops.TryGetProperty("items", out var itemRows) || itemRows.ValueKind != JsonValueKind.Array
            || !drops.TryGetProperty("spawns", out var spawnRows) || spawnRows.ValueKind != JsonValueKind.Array)
            return MapMonsterDropCatalog.Unknown;

        var items = new Dictionary<string, MapMonsterDropItem>(StringComparer.Ordinal);
        foreach (var row in itemRows.EnumerateArray())
        {
            if (!RequiredDropId(row, "id", out var id) || !RequiredText(row, "en", out var en)
                || !RequiredText(row, "type", out var type) || !MonsterDropTypes.Contains(type)
                || !OptionalText(row, "de", out var de) || !OptionalText(row, "icon", out var icon)
                || !items.TryAdd(id, new(id, en, de, LocalMonsterDropIcon(icon), type)))
                return MapMonsterDropCatalog.Unknown;
        }

        var markers = root.GetProperty("markers");
        var spawns = new List<MapMonsterDropSpawn>();
        var seen = new HashSet<int>();
        foreach (var row in spawnRows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("marker", out var index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var markerIndex) || markerIndex < 0 || markerIndex >= markers.GetArrayLength()
                || !seen.Add(markerIndex) || !RequiredDropId(row, "npcId", out var npcId)
                || !row.TryGetProperty("items", out var refs) || refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() == 0
                || !symbols.TryGetValue(markerIndex, out var symbol))
                return MapMonsterDropCatalog.Unknown;
            var marker = markers[markerIndex];
            var categoryIndex = marker[0].GetInt32();
            if (categoryIndex < 0 || categoryIndex >= categories.Count
                || !(categories[categoryIndex].StartsWith("Monsters/", StringComparison.Ordinal) || categories[categoryIndex] == "Locations/Field Boss"))
                return MapMonsterDropCatalog.Unknown;
            // Where an explicit source identity accompanies the marker, both contracts must agree.
            if (marker.GetArrayLength() > 7 && marker[7].ValueKind == JsonValueKind.Object
                && marker[7].TryGetProperty("npcId", out var sourceNpc) && sourceNpc.ToString() != npcId)
                return MapMonsterDropCatalog.Unknown;
            var selected = new List<MapMonsterDropItem>();
            var selectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in refs.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.String || reference.GetString() is not { } id
                    || !items.TryGetValue(id, out var item) || !selectedIds.Add(id))
                    return MapMonsterDropCatalog.Unknown;
                selected.Add(item);
            }
            var en = marker.GetArrayLength() > 3 && marker[3].ValueKind == JsonValueKind.String ? marker[3].GetString() : null;
            var de = marker.GetArrayLength() > 4 && marker[4].ValueKind == JsonValueKind.String ? marker[4].GetString() : null;
            if (string.IsNullOrWhiteSpace(en) || !double.IsFinite(symbol.X) || !double.IsFinite(symbol.Y))
                return MapMonsterDropCatalog.Unknown;
            var visible = selected.Where(item => VisibleMonsterDropTypes.Contains(item.Type)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            if (visible.Length > 0)
                spawns.Add(new(markerIndex, npcId, symbol.X, symbol.Y, new(en, de), visible, symbol));
        }
        return new(true, items.Values.Where(item => VisibleMonsterDropTypes.Contains(item.Type))
            .OrderBy(i => i.Id, StringComparer.Ordinal).ToArray(), spawns);
    }

    private static bool RequiredText(JsonElement row, string name, out string value)
    {
        value = "";
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var field)
            || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) return false;
        value = field.GetString()!;
        return true;
    }

    private static bool RequiredDropId(JsonElement row, string name, out string value) =>
        RequiredText(row, name, out value) && value[0] is >= '1' and <= '9' && value.All(character => character is >= '0' and <= '9');

    private static string? LocalMonsterDropIcon(string? path) => path is not null
        && MonsterDropIconPath().IsMatch(path) && !path.Split('/').Contains("..", StringComparer.Ordinal) ? path : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^icons/[A-Za-z0-9_./-]+\.png$")]
    private static partial System.Text.RegularExpressions.Regex MonsterDropIconPath();

    private static bool OptionalText(JsonElement row, string name, out string? value)
    {
        value = null;
        if (!row.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString();
        return true;
    }
}
