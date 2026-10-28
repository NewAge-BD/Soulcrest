namespace Soulcrest.App.Services;

/// <summary>
/// Freezes a gathering position and its optional nearby resource placement. Proximity is used only
/// after a confirmed own attempt, on the same map and with the exact resource template ID
/// (user request 2026-10-25). It never infers an attempt or resource type from a name.
/// </summary>
public sealed class LifeskillGatherPointResolver
{
    public const double NodeRadiusMeters = 15;
    private readonly ResourceNodeCatalog _catalog;
    private readonly Func<string, double?> _metersPerPixel;
    private readonly Func<string, int?> _mapSize;

    public LifeskillGatherPointResolver(ResourceNodeCatalog catalog, Func<string, double?> metersPerPixel,
        Func<string, int?> mapSize)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(metersPerPixel);
        ArgumentNullException.ThrowIfNull(mapSize);
        _catalog = catalog; _metersPerPixel = metersPerPixel; _mapSize = mapSize;
    }

    internal int? MapSize(string mapId) => _mapSize(mapId);

    /// <summary>The caller qualifies position freshness and the own character generation.</summary>
    public LootTrackerGatherVisit Resolve(string id, int section, PlayerPosition position, int? gatherableId,
        DateTimeOffset at, bool succeeded, string mapName, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(position.MapId);
        if (section < 0) throw new ArgumentOutOfRangeException(nameof(section));
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0)
            throw new ArgumentException("A gathering route requires a finite nonnegative position.", nameof(position));

        var size = _mapSize(position.MapId);
        var scale = _metersPerPixel(position.MapId);
        var resource = gatherableId is > 0 ? gatherableId : null;
        ResourceNodeDefinition? closest = null;
        var closestDistance = double.PositiveInfinity;
        var tied = false;
        if (resource is { } template && size is > 0 && scale is > 0 && double.IsFinite(scale.Value))
        {
            foreach (var node in _catalog.ForMap(position.MapId))
            {
                if (node.GatherableId != template || node.X > size.Value || node.Y > size.Value) continue;
                var dx = node.X - position.X;
                var dy = node.Y - position.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy) * scale.Value;
                if (!double.IsFinite(distance) || distance > NodeRadiusMeters) continue;
                if (distance < closestDistance)
                { closest = node; closestDistance = distance; tied = false; }
                else if (distance == closestDistance) tied = true;
            }
        }
        if (tied) closest = null;
        var name = closest is null ? fallbackName
            : UiText.Language == "de" && !string.IsNullOrWhiteSpace(closest.NameDe) ? closest.NameDe : closest.NameEn;
        return new(id, section, position.MapId, mapName, position.X, position.Y,
            closest?.X ?? position.X, closest?.Y ?? position.Y, at, succeeded, name,
            size is > 0 ? size.Value : 8192, resource, closest?.Id);
    }
}
