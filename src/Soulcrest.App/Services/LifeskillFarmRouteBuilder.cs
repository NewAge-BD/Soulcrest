using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>
/// Pure replay of frozen gathering visits into a growing route. No catalogue is consulted here:
/// historical route decisions survive catalogue changes and app restarts unchanged.
/// </summary>
public static class LifeskillFarmRouteBuilder
{
    public const double PositionRadiusAt8192 = 16;

    public static ImmutableArray<LootTrackerFarmRoutePoint> Build(IEnumerable<LootTrackerGatherVisit> visits)
    {
        ArgumentNullException.ThrowIfNull(visits);
        var groups = new List<RouteSection>();
        var bySection = new Dictionary<(int Section, string Map), RouteSection>();
        var seenVisits = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        foreach (var visit in visits)
        {
            if (!Valid(visit) || !seenVisits.Add(visit.Id)) continue;
            var key = (visit.Section, visit.MapId);
            if (!bySection.TryGetValue(key, out var group))
            { group = new(); bySection.Add(key, group); groups.Add(group); }
            Add(group.Points, visit, order++);
        }
        return groups.SelectMany(group => group.Points.Select(point => point.Value)).ToImmutableArray();
    }

    private static bool Valid(LootTrackerGatherVisit? visit) => visit is not null
        && !string.IsNullOrWhiteSpace(visit.Id) && !string.IsNullOrWhiteSpace(visit.MapId)
        && visit.Section >= 0 && visit.MapSize > 0 && visit.GatherableId is null or > 0
        && FinitePosition(visit.X, visit.Y) && FinitePosition(visit.OriginalX, visit.OriginalY)
        && (visit.CatalogNodeId is null || !string.IsNullOrWhiteSpace(visit.CatalogNodeId))
        && (visit.CatalogNodeId is null || visit.GatherableId is > 0);

    private static bool FinitePosition(double x, double y) => double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0;

    private static void Add(List<Stop> stops, LootTrackerGatherVisit visit, int order)
    {
        var known = visit.CatalogNodeId is { } nodeId
            ? stops.FirstOrDefault(candidate => candidate.Value.CatalogNodeId == nodeId) : null;
        // A stable node ID cannot change resource type within one recorded route.
        if (known is not null && !Compatible(known.Value.GatherableId, visit.GatherableId)) return;
        var nearby = Nearest(stops.Where(candidate => visit.CatalogNodeId is null || candidate.Value.CatalogNodeId is null), visit);
        var stop = known ?? nearby;
        if (known is not null && nearby is not null && !ReferenceEquals(known, nearby))
        {
            // Retain the earlier stop and its place in the route, even when a fallback is older
            // than an already snapped stop. Promotion must not duplicate or reorder the start.
            stop = known.CreatedOrder < nearby.CreatedOrder ? known : nearby;
            var removed = ReferenceEquals(stop, known) ? nearby : known;
            stop.Value = stop.Value with
            {
                VisitCount = checked(stop.Value.VisitCount + removed.Value.VisitCount),
                SuccessCount = checked(stop.Value.SuccessCount + removed.Value.SuccessCount),
                FailureCount = checked(stop.Value.FailureCount + removed.Value.FailureCount)
            };
            stops.Remove(removed);
        }
        if (stop is null)
        {
            stop = new(new(visit.Id, visit.Section, visit.MapId, visit.MapName, visit.X, visit.Y, visit.Name,
                0, 0, 0, visit.CatalogNodeId, visit.GatherableId), order);
            Insert(stops, stop);
        }
        else if (visit.CatalogNodeId is not null && stop.Value.CatalogNodeId is null)
        {
            stop.Value = stop.Value with { CatalogNodeId = visit.CatalogNodeId, GatherableId = visit.GatherableId,
                X = visit.X, Y = visit.Y, Name = visit.Name };
        }
        if (stop.Value.GatherableId is null && visit.GatherableId is not null)
            stop.Value = stop.Value with { GatherableId = visit.GatherableId };
        stop.Value = stop.Value with { VisitCount = checked(stop.Value.VisitCount + 1),
            SuccessCount = checked(stop.Value.SuccessCount + (visit.Succeeded ? 1 : 0)),
            FailureCount = checked(stop.Value.FailureCount + (visit.Succeeded ? 0 : 1)) };
    }

    private static Stop? Nearest(IEnumerable<Stop> candidates, LootTrackerGatherVisit visit)
    {
        var radius = PositionRadiusAt8192 * visit.MapSize / 8192d;
        Stop? closest = null;
        var distance = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            if (!Compatible(candidate.Value.GatherableId, visit.GatherableId)) continue;
            var next = Distance(candidate.Value.X, candidate.Value.Y, visit.X, visit.Y);
            // A 15-metre snap can be more than 16 map pixels away on a calibrated map. An
            // earlier fallback at this attempt's original position is still the same stop.
            if (visit.CatalogNodeId is not null && candidate.Value.CatalogNodeId is null)
                next = Math.Min(next, Distance(candidate.Value.X, candidate.Value.Y, visit.OriginalX, visit.OriginalY));
            if (next <= radius && (next < distance || next == distance
                && (closest is null || candidate.CreatedOrder < closest.CreatedOrder)))
            { closest = candidate; distance = next; }
        }
        return closest;
    }

    private static bool Compatible(int? first, int? second) => first is null || second is null || first == second;

    private static void Insert(List<Stop> stops, Stop added)
    {
        if (stops.Count == 0) { stops.Add(added); return; }
        var index = stops.Count;
        var least = double.PositiveInfinity;
        // Try gaps before the end so an exact tie chooses the earliest insertion position.
        for (var next = 1; next <= stops.Count; next++)
        {
            var previous = stops[next - 1].Value;
            var extra = Distance(previous.X, previous.Y, added.Value.X, added.Value.Y);
            if (next < stops.Count)
            {
                var following = stops[next].Value;
                extra += Distance(added.Value.X, added.Value.Y, following.X, following.Y)
                    - Distance(previous.X, previous.Y, following.X, following.Y);
            }
            if (extra < least) { least = extra; index = next; }
        }
        stops.Insert(index, added);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    { var dx = x1 - x2; var dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy); }

    private sealed class RouteSection { public List<Stop> Points { get; } = []; }
    private sealed class Stop(LootTrackerFarmRoutePoint value, int createdOrder)
    {
        public LootTrackerFarmRoutePoint Value { get; set; } = value;
        public int CreatedOrder { get; } = createdOrder;
    }
}
