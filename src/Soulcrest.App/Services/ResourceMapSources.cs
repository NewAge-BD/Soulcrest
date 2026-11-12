using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace Soulcrest.App.Services;

public sealed record ResourceMapLocation(int ItemId, string MapId, string Category, string Kind, double X, double Y);
public sealed record MapLegendLayer(string Category, ImmutableArray<string> Kinds, bool DefaultVisible);

/// <summary>Exact offline item/template/placement joins. Names never establish a relation.</summary>
public sealed class ResourceMapSources
{
    private readonly ImmutableArray<ResourceMapLocation> _locations;
    private readonly ImmutableDictionary<int, ImmutableArray<ResourceMapLocation>> _byItem;
    private readonly ImmutableDictionary<string, ImmutableArray<MapLegendLayer>> _layers;
    public ImmutableArray<string> UnavailableMaps { get; }
    public ResourceMapSources(ProgressService progress) : this(progress.MapDataDirectory, progress.Maps) { }

    internal ResourceMapSources(string? directory, IReadOnlyList<MapInfo> maps)
    {
        var locations = new List<ResourceMapLocation>();
        var unavailable = ImmutableArray.CreateBuilder<string>();
        var layers = ImmutableDictionary.CreateBuilder<string, ImmutableArray<MapLegendLayer>>(StringComparer.Ordinal);
        var nodes = ResourceNodeCatalog.Load(directory);
        var relations = ReadRelations();
        if (directory is not null)
            foreach (var map in maps)
            {
                try
                {
                var mapPath = Path.Combine(directory, map.Id, "data.js");
                if (!File.Exists(mapPath) || new FileInfo(mapPath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Unavailable optional resource map.");
                var resources = MapPetMarkers.ResourcesFor(directory, map.Id);
                var byPoint = resources.Where(r => r.Category.StartsWith("Resources/", StringComparison.Ordinal))
                    .GroupBy(r => (r.X, r.Y)).ToDictionary(g => g.Key, g => g.ToArray());
                foreach (var relation in relations.Where(r => r.MapId == map.Id))
                    foreach (var node in nodes.ForMap(map.Id).Where(n => n.GatherableId == relation.GatherableId))
                        if (byPoint.TryGetValue((node.X, node.Y), out var matching) && matching.Length == 1
                            && matching[0].Kind is { Length: > 0 } kind)
                            locations.Add(new(relation.ItemId, map.Id, matching[0].Category, kind, node.X, node.Y));
                var drops = MapPetMarkers.AllMonsterDropCatalogFor(directory, map.Id);
                foreach (var spawn in drops.Spawns)
                    foreach (var item in spawn.Items)
                        if (int.TryParse(item.Id, out var itemId))
                            locations.Add(new(itemId, map.Id, item.Category, item.Id, spawn.X, spawn.Y));
                layers[map.Id] = ReadLayers(directory, map.Id, drops);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException)
                {
                    locations.RemoveAll(location => location.MapId == map.Id);
                    layers[map.Id] = [];
                    unavailable.Add(map.Id);
                }
            }
        _locations = locations.Distinct().ToImmutableArray();
        _byItem = _locations.GroupBy(l => l.ItemId).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
        _layers = layers.ToImmutable();
        UnavailableMaps = unavailable.ToImmutable();
    }

    internal ResourceMapSources(IEnumerable<ResourceMapLocation> locations, IReadOnlyDictionary<string, ImmutableArray<MapLegendLayer>> layers)
    { _locations = locations.ToImmutableArray(); _byItem = _locations.GroupBy(l => l.ItemId).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray()); _layers = layers.ToImmutableDictionary(StringComparer.Ordinal); UnavailableMaps = []; }

    public ImmutableArray<ResourceMapLocation> ForItem(int itemId) => _byItem.GetValueOrDefault(itemId, []);
    public bool HasItem(int itemId) => _byItem.ContainsKey(itemId);
    public ImmutableArray<MapLegendLayer> LayersFor(string mapId) => _layers.GetValueOrDefault(mapId, []);
    public int ItemCount => _byItem.Count;

    private static ImmutableArray<Relation> ReadRelations()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Soulcrest.ResourceItemSources.json");
        if (stream is null) return [];
        var file = JsonSerializer.Deserialize<RelationFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return file?.SchemaVersion == 1 && file.Entries is { } rows
            ? rows.Where(r => r.ItemId > 0 && r.GatherableId > 0 && !string.IsNullOrWhiteSpace(r.MapId)).Distinct().ToImmutableArray() : [];
    }

    private static ImmutableArray<MapLegendLayer> ReadLayers(string directory, string map, MapMonsterDropCatalog drops)
    {
        var path = Path.Combine(directory, map, "data.js");
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path); var start = text.IndexOf("] = {", StringComparison.Ordinal); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        using var json = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
        var categories = json.RootElement.GetProperty("categories").EnumerateArray().ToArray();
        var markers = json.RootElement.GetProperty("markers").EnumerateArray().ToArray();
        var result = new List<MapLegendLayer>();
        for (var i = 0; i < categories.Length; i++)
        {
            var row = categories[i]; var group = row.GetProperty("group").GetString()!; var name = row.GetProperty("name").GetString()!;
            var hidden = row.TryGetProperty("hidden", out var h) && h.GetBoolean();
            var kinds = group == "Resources" ? markers.Where(m => m[0].GetInt32() == i && m.GetArrayLength() > 3 && m[3].ValueKind == JsonValueKind.String)
                .Select(m => m[3].GetString()!).Distinct(StringComparer.Ordinal).ToImmutableArray() : [];
            result.Add(new(MapPetMarkers.CategoryKey(group, name), kinds,
                !hidden && (group is "Pets" or "Locations" || group == "Regions" && name == "Hunting Ground")));
        }
        foreach (var group in drops.Items.GroupBy(i => i.Category))
            result.Add(new(group.Key, group.Select(i => i.Id).ToImmutableArray(), false));
        return result.ToImmutableArray();
    }
    private sealed record Relation(int ItemId, int GatherableId, string MapId);
    private sealed record RelationFile(int SchemaVersion, Relation[] Entries);
}
