using System.Collections.Immutable;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>An exact offline placement. GatherableId is a resource template, never an item or live object identifier.</summary>
public sealed record ResourceNodeDefinition(string Id, string MapId, int GatherableId, string TypeId,
    string NameEn, string? NameDe, double X, double Y, double NativeX, double NativeY);

/// <summary>Read-only additive map sidecar; no name/item joins, nearest-node selection or network access.</summary>
public sealed class ResourceNodeCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private readonly ImmutableDictionary<string, ResourceNodeDefinition> _byId;
    private readonly ImmutableDictionary<string, ImmutableArray<ResourceNodeDefinition>> _byMap;
    public static ResourceNodeCatalog Empty { get; } = new([]);
    public ImmutableArray<ResourceNodeDefinition> Nodes { get; }
    public bool HasData => !Nodes.IsDefaultOrEmpty;

    public ResourceNodeCatalog() : this(Load(AppPaths.FindMapData()).Nodes) { }

    public ResourceNodeCatalog(IEnumerable<ResourceNodeDefinition> nodes)
    {
        Nodes = nodes.ToImmutableArray();
        if (Nodes.Any(node => node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.MapId)
            || node.GatherableId <= 0 || string.IsNullOrWhiteSpace(node.TypeId) || !node.TypeId.StartsWith("resource-", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(node.NameEn) || !double.IsFinite(node.X) || !double.IsFinite(node.Y)
            || node.X < 0 || node.Y < 0 || !double.IsFinite(node.NativeX) || !double.IsFinite(node.NativeY))
            || Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != Nodes.Length)
            throw new InvalidDataException("Invalid resource-node catalogue.");
        _byId = Nodes.ToImmutableDictionary(node => node.Id, StringComparer.Ordinal);
        _byMap = Nodes.GroupBy(node => node.MapId, StringComparer.Ordinal).ToImmutableDictionary(
            group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
    }

    public ResourceNodeDefinition? Find(string nodeId) => _byId.GetValueOrDefault(nodeId);
    public IReadOnlyList<ResourceNodeDefinition> ForMap(string mapId) =>
        _byMap.TryGetValue(mapId, out var nodes) ? nodes : [];

    public static ResourceNodeCatalog Load(string? mapDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapDataDirectory)) return Empty;
        var path = Path.Combine(mapDataDirectory, "resource-nodes.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return Empty;
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException) { return Empty; }
    }

    public static ResourceNodeCatalog FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
        if (file?.SchemaVersion != 1 || file.Nodes is null) throw new InvalidDataException("Unknown resource-node catalogue schema.");
        return new(file.Nodes);
    }

    private sealed record CatalogFile(int SchemaVersion, ResourceNodeDefinition[]? Nodes);
}
