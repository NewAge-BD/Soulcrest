namespace Soulcrest.App.Services;

/// <summary>Read-only inputs from the existing tracker; never starts a capture or changes tracking.</summary>
public sealed record ExplorationTrackingSnapshot(bool Running, bool Found, bool Paused, bool WorldMapOpen,
    bool InInstance, bool EscMenuOpen, PlayerPosition? Position, DetectedMapContext? DetectedMap);

public sealed record ExplorationMapSelection(string MapId, string Source, bool WorldMap = false);
public sealed record ExplorationCategorySummary(string Kind, int Completed, int Total);
public sealed record ExplorationOverviewRow(ExplorationPlace Place, bool Done,
    IReadOnlyList<ExplorationPlace> Missing, double? Distance)
{
    public bool Locked => !Done && Missing.Count > 0;
}
public sealed record ExplorationOverviewSnapshot(string MapId, int Completed, int Total,
    IReadOnlyList<ExplorationCategorySummary> Categories, IReadOnlyList<ExplorationOverviewRow> Rows,
    ExplorationOverviewRow? Next);

/// <summary>
/// User request 2026-10-11: follow the detected game map, allow a persistent manual pin, show an
/// overview/checklist and suggest the nearest available open place. Filters never change totals.
/// </summary>
public static class ExplorationOverview
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);
    private static readonly string[] Kinds = ["dungeon", "stronghold", "kibelisk"];

    public static ExplorationTrackingSnapshot Capture(MapTrackingService tracking) => new(
        tracking.Running, tracking.Found, tracking.Paused, tracking.WorldMapOpen, tracking.InInstance,
        tracking.EscMenuOpen, tracking.Position, tracking.DetectedMap);

    public static ExplorationMapSelection SelectMap(IReadOnlyList<MapInfo> maps, string? previousMap,
        string? fallbackMap, string? pinnedMap, bool scanning, string? scanMap,
        ExplorationTrackingSnapshot tracking, DateTimeOffset now)
    {
        bool Known(string? id) => id is not null && maps.Any(m => m.Id == id);
        if (scanning && Known(scanMap)) return new(scanMap!, "scan");
        if (Known(pinnedMap)) return new(pinnedMap!, "pinned");
        if (CurrentMap(tracking, now) is { } detected && Known(detected.MapId))
            return new(detected.MapId, "detected", detected.WorldMap);
        return new(Known(previousMap) ? previousMap! : Known(fallbackMap) ? fallbackMap! : maps.FirstOrDefault()?.Id ?? "", "previous");
    }

    private static DetectedMapContext? CurrentMap(ExplorationTrackingSnapshot tracking, DateTimeOffset now)
    {
        if (!tracking.Running || !tracking.Found || tracking.Paused || tracking.InInstance || tracking.EscMenuOpen
            || tracking.DetectedMap is not { } map || !Fresh(map.At, now) || map.WorldMap != tracking.WorldMapOpen)
            return null;
        return map;
    }

    /// <summary>World-map browsing is a map selection, never a new player location.</summary>
    public static PlayerPosition? FreshPosition(ExplorationTrackingSnapshot tracking, DateTimeOffset now)
    {
        if (CurrentMap(tracking, now) is not { WorldMap: false } detected
            || tracking.Position is not { } position || position.MapId != detected.MapId
            || !Fresh(position.At, now) || !Finite(position.X, position.Y))
            return null;
        return position;
    }

    private static bool Fresh(DateTimeOffset at, DateTimeOffset now) => at <= now && now - at <= Freshness;
    private static bool Finite(double x, double y) => double.IsFinite(x) && double.IsFinite(y);

    public static ExplorationOverviewSnapshot Build(IReadOnlyList<ExplorationPlace> places, IReadOnlySet<string> done,
        string map, string language, PlayerPosition? position = null, string search = "", string kind = "all", string status = "all")
    {
        var mapPlaces = places.Where(p => p.Map == map && Kinds.Contains(p.Kind)).DistinctBy(p => p.Id).ToArray();
        var categories = Kinds.Select(k => new ExplorationCategorySummary(k,
            mapPlaces.Count(p => p.Kind == k && done.Contains(p.Id)), mapPlaces.Count(p => p.Kind == k))).ToArray();
        var origin = position is not null && position.MapId == map && Finite(position.X, position.Y) ? position : null;
        var rows = mapPlaces.Select(p => new ExplorationOverviewRow(p, done.Contains(p.Id),
            ExplorationService.Prerequisites(p, places).Where(m => !done.Contains(m.Id)).ToArray(),
            origin is not null && Finite(p.X, p.Y) ? Distance(origin, p) : null)).ToArray();
        var categoryRows = rows.Where(r => kind == "all" || r.Place.Kind == kind);
        var next = categoryRows.Where(r => !r.Done && !r.Locked && r.Distance is not null)
            .OrderBy(r => r.Distance).ThenBy(r => r.Place.Name(language), StringComparer.CurrentCulture)
            .ThenBy(r => r.Place.Id, StringComparer.Ordinal).FirstOrDefault();
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visible = categoryRows.Where(r => status switch
            {
                "open" => !r.Done,
                "done" => r.Done,
                "locked" => r.Locked,
                _ => true,
            })
            .Where(r => terms.All(term => r.Place.En.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Place.De?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(r => r.Done).ThenBy(r => r.Locked).ThenBy(r => r.Distance ?? double.MaxValue)
            .ThenBy(r => r.Place.Name(language), StringComparer.CurrentCulture).ThenBy(r => r.Place.Id, StringComparer.Ordinal).ToArray();
        return new(map, mapPlaces.Count(p => done.Contains(p.Id)), mapPlaces.Length, categories, visible, next);
    }

    private static double? Distance(PlayerPosition origin, ExplorationPlace place)
    {
        var dx = origin.X - place.X;
        var dy = origin.Y - place.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        return double.IsFinite(distance) ? distance : null;
    }
}
