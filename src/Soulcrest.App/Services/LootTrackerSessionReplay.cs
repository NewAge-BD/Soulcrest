using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>A pure chronological prefix. The camera may retain the last observation after the player dot expires.</summary>
public sealed record LootTrackerSessionReplay(LootTrackerSession Session, TimeSpan Elapsed,
    string? MapId, LootTrackerCombatPosition? PlayerPosition)
{
    public LootTrackerCombatPosition? LastPosition { get; init; }
    public TimeSpan? LastPositionElapsed { get; init; }
    public LootTrackerReplayProvenance Provenance { get; init; } = LootTrackerReplayProvenance.Missing;
    public IReadOnlyList<LootTrackerPreparedPart> PreparedParts { get; init; } = [];
    public IReadOnlyList<LootTrackerCharts.RouteLine> EstimatedConnections { get; init; } = [];
    public bool IsPrepared { get; init; }
    public bool IsFresh => PlayerPosition is not null && Provenance is LootTrackerReplayProvenance.Matched or LootTrackerReplayProvenance.Flow;

    public static LootTrackerSessionReplay Build(LootTrackerSession session, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(session);
        var end = session.Elapsed > TimeSpan.Zero ? session.Elapsed : TimeSpan.Zero;
        var head = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > end ? end : elapsed;
        var atEnd = head == end;
        var routes = ImmutableArray.CreateBuilder<LootTrackerRouteSegment>();
        var pointReferences = new Dictionary<(int Segment, int Point), (int Segment, int Point)>();
        (TimeSpan Elapsed, LootTrackerCombatPosition Position)? last = null;
        var source = session.RouteSegments.IsDefault ? [] : session.RouteSegments;
        for (var segmentIndex = 0; segmentIndex < source.Length; segmentIndex++)
        {
            var segment = source[segmentIndex];
            if (segment.Points.IsDefaultOrEmpty) continue;
            var points = ImmutableArray.CreateBuilder<LootTrackerRoutePoint>();
            TimeSpan? previous = null;
            for (var pointIndex = 0; pointIndex < segment.Points.Length; pointIndex++)
            {
                var point = segment.Points[pointIndex];
                var time = Time(point.Elapsed, point.At);
                if (!Valid(segment.MapId, point.X, point.Y) || time is { } known && (known > head || previous is { } before && known < before)
                    || time is null && (!atEnd || point.Elapsed is not null || point.At is not null))
                { Flush(); previous = null; continue; }
                pointReferences[(segmentIndex, pointIndex)] = (routes.Count, points.Count);
                points.Add(point);
                if (time is { } moment)
                {
                    Observe(moment, new(segment.MapId, segment.MapName, point.X, point.Y, point.At ?? session.StartedAt));
                    previous = moment;
                }
            }
            Flush();
            void Flush()
            {
                if (points.Count == 0) return;
                routes.Add(segment with { Points = points.ToImmutable() }); points.Clear();
            }
        }

        var timeline = session.TimelineEvents.IsDefault ? [] : session.TimelineEvents;
        foreach (var row in timeline)
            if (row.Kind == LootTrackerTimelineEventKind.Drop && row.Elapsed >= TimeSpan.Zero && row.Elapsed <= head
                && row.Position is { } placement && Valid(placement.MapId, placement.X, placement.Y)
                && placement.ObservedAt != default && placement.ObservedAt <= row.At
                && row.At - placement.ObservedAt <= TimeSpan.FromSeconds(2))
                // A first-drop snapshot may precede session start. Preserve that real observation time;
                // the drop fact itself gates when the placement becomes available to the replay.
                Observe(row.Elapsed - (row.At - placement.ObservedAt), placement);
        var visitsById = timeline.Where(row => row.GatherVisit is not null)
            .GroupBy(row => row.GatherVisit!.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Elapsed, StringComparer.Ordinal);
        var visits = (session.GatherVisits.IsDefault ? [] : session.GatherVisits)
            .Where(visit => VisitTime(visit) is { } time && time <= head).ToImmutableArray();
        foreach (var visit in visits)
            if (Valid(visit.MapId, visit.OriginalX, visit.OriginalY) && VisitTime(visit) is { } moment)
                Observe(moment, new(visit.MapId, visit.MapName, visit.OriginalX, visit.OriginalY, visit.At));
        var combat = (session.CombatEvents.IsDefault ? [] : session.CombatEvents)
            .Where(row => row.Elapsed >= TimeSpan.Zero && row.Elapsed <= head).ToImmutableArray();
        foreach (var row in combat)
            if (row.Position is { } position && Valid(position.MapId, position.X, position.Y)
                && position.ObservedAt != default && position.ObservedAt <= row.At)
            {
                var observedElapsed = row.Elapsed - (row.At - position.ObservedAt);
                if (observedElapsed >= TimeSpan.Zero) Observe(observedElapsed, position);
            }
        var resources = ImmutableArray.CreateBuilder<LootTrackerResourceVisit>();
        foreach (var visit in session.ResourceNodes.IsDefault ? [] : session.ResourceNodes)
        {
            if (!pointReferences.TryGetValue((visit.SegmentIndex, visit.PointIndex), out var reference)
                || Time(null, visit.At) is { } time && time > head) continue;
            resources.Add(visit with { SegmentIndex = reference.Segment, PointIndex = reference.Point });
        }
        var filtered = session with
        {
            RouteSegments = routes.ToImmutable(), CombatEvents = combat, GatherVisits = visits,
            FarmRoutePoints = LootTrackerPresentation.UsesFarmRoute(session)
                ? atEnd && session.GatherVisits.IsDefaultOrEmpty ? session.FarmRoutePoints : LifeskillFarmRouteBuilder.Build(visits)
                : session.FarmRoutePoints,
            ResourceNodes = resources.ToImmutable(), TimelineEvents = timeline.Where(row => row.Elapsed >= TimeSpan.Zero && row.Elapsed <= head).ToImmutableArray()
        };
        var positionNow = last is { } latest && head - latest.Elapsed <= TimeSpan.FromSeconds(2) ? latest.Position : null;
        var mapId = last?.Position.MapId ?? filtered.RouteSegments.LastOrDefault()?.MapId
            ?? filtered.FarmRoutePoints.LastOrDefault()?.MapId;
        return new(filtered, head, mapId, positionNow)
        { LastPosition = last?.Position, LastPositionElapsed = last?.Elapsed, Provenance = positionNow is null ? LootTrackerReplayProvenance.Missing : LootTrackerReplayProvenance.Matched };

        TimeSpan? Time(TimeSpan? recorded, DateTimeOffset? at)
        {
            var value = recorded ?? (at is { } observed && observed != default && session.StartedAt != default
                ? observed - session.StartedAt : (TimeSpan?)null);
            return value is { } known && known >= TimeSpan.Zero && known <= end ? known : null;
        }
        TimeSpan? VisitTime(LootTrackerGatherVisit visit) => visitsById.TryGetValue(visit.Id, out var time)
            ? time >= TimeSpan.Zero && time <= end ? time : null : Time(null, visit.At);
        void Observe(TimeSpan moment, LootTrackerCombatPosition position)
        {
            if (last is null || moment >= last.Value.Elapsed) last = (moment, position);
        }
    }

    /// <summary>Projects indexed source positions and bounded display parts without copying the high-frequency route.</summary>
    public static LootTrackerSessionReplay Build(LootTrackerSession session, TimeSpan elapsed, LootTrackerPreparedReplay prepared)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Version != LootTrackerPreparedReplay.CurrentVersion || prepared.SessionId != session.Id || prepared.Kind != session.Kind)
            throw new ArgumentException("Prepared replay belongs to another recording.", nameof(prepared));
        var markerPrefix = Build(session with { RouteSegments = [], ResourceNodes = [] }, elapsed);
        var head = markerPrefix.Elapsed;
        LootTrackerPreparedPoint? latest = null; LootTrackerPreparedRoute? latestRoute = null; var latestRouteIndex = -1; var routeIndex = -1;
        var low = 0; var high = prepared.TimeIndex.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (prepared.TimeIndex[middle].Start <= head) { routeIndex = middle; low = middle + 1; } else high = middle - 1;
        }
        // Parts may overlap in old recordings; the latest actual point wins, not an invented link.
        for (var index = routeIndex; index >= 0; index--)
        {
            if (latest is { } selected && prepared.TimeIndex[index].MaximumEndThrough < selected.Elapsed) break;
            var route = prepared.Routes[prepared.TimeIndex[index].RouteIndex];
            var pointIndex = LastAt(route.Points, head);
            if (pointIndex >= 0 && route.Points[pointIndex].Provenance != LootTrackerReplayProvenance.Missing
                && (latest is null || route.Points[pointIndex].Elapsed >= latest.Value.Elapsed))
            { latest = route.Points[pointIndex]; latestRoute = route; latestRouteIndex = prepared.TimeIndex[index].RouteIndex; }
        }
        LootTrackerCombatPosition? camera = latest is { } known ? Position(latestRoute!, known, session.StartedAt) : null;
        var observedAt = latest?.Elapsed;
        var provenance = latest is { } observed && head - observed.Elapsed <= TimeSpan.FromSeconds(2)
            ? observed.Provenance : LootTrackerReplayProvenance.Missing;
        if (markerPrefix.LastPositionElapsed is { } markerTime && (observedAt is null || markerTime > observedAt))
        { camera = markerPrefix.LastPosition; observedAt = markerTime; provenance = markerPrefix.Provenance; }
        var player = provenance == LootTrackerReplayProvenance.Missing ? null : camera;
        LootTrackerReplayConnection? activeConnection = null;
        low = 0; high = prepared.ConnectionTimeIndex.Length - 1; var activeIndex = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (prepared.ConnectionTimeIndex[middle].Start < head) { activeIndex = middle; low = middle + 1; } else high = middle - 1;
        }
        for (var index = activeIndex; index >= 0; index--)
        {
            if (prepared.ConnectionTimeIndex[index].MaximumEndThrough <= head) break;
            var candidate = prepared.Connections[prepared.ConnectionTimeIndex[index].ConnectionIndex];
            if (observedAt is { } confirmed && confirmed > candidate.From.Elapsed) break;
            if (head >= candidate.To.Elapsed) continue;
            activeConnection = candidate; break;
        }
        LootTrackerCharts.RouteLine? partialConnection = null;
        if (activeConnection is { } connection)
        {
            var fraction = (head - connection.From.Elapsed).Ticks / (double)(connection.To.Elapsed - connection.From.Elapsed).Ticks;
            var end = new LootTrackerPreparedPoint(connection.From.X + (connection.To.X - connection.From.X) * fraction,
                connection.From.Y + (connection.To.Y - connection.From.Y) * fraction, connection.From.At, head,
                LootTrackerReplayProvenance.GapEstimated);
            camera = player = new(connection.MapId, connection.MapName, end.X, end.Y, connection.From.At ?? session.StartedAt);
            provenance = LootTrackerReplayProvenance.GapEstimated;
            partialConnection = LootTrackerPreparedReplay.Line([connection.From, end]);
        }
        var estimated = prepared.DisplayConnections.Where(part => part.End <= head && part.MapId == camera?.MapId)
            .Select(part => part.Line).ToList();
        if (partialConnection is not null) estimated.Add(partialConnection);
        var parts = new List<LootTrackerPreparedPart>();
        for (var index = 0; index < prepared.DisplayParts.Length; index++)
        {
            var part = prepared.DisplayParts[index];
            if (part.Points[0].Elapsed > head) continue;
            if (part.Points[^1].Elapsed <= head) { parts.Add(part); continue; }
            var count = LastAt(part.Points, head) + 1;
            if (count == 0) continue;
            var points = part.Points.Take(count).ToImmutableArray();
            if (latest is { } endpoint && latestRoute == prepared.Routes[part.RouteIndex]
                && endpoint.Elapsed > points[^1].Elapsed && endpoint.Elapsed <= head) points = points.Add(endpoint);
            parts.Add(new(part.RouteIndex, part.MapId, part.MapName, points, LootTrackerPreparedReplay.Line(points))
                { IsRouteStart = part.IsRouteStart, IsRouteEnd = latestRouteIndex == part.RouteIndex });
        }
        // Global LOD can omit a short historical part. Keep the current actual part precise without joining other parts.
        if (latest is { } current && latestRoute is not null)
        {
            var previousPart = parts.LastOrDefault(part => part.RouteIndex == latestRouteIndex);
            if (previousPart is null || previousPart.Points[^1] != current)
            {
                var first = previousPart?.Points[^1] ?? latestRoute.Points[0];
                ImmutableArray<LootTrackerPreparedPoint> points = first == current ? [current] : [first, current];
                parts.Add(new(latestRouteIndex, latestRoute.MapId, latestRoute.MapName, points, LootTrackerPreparedReplay.Line(points))
                    { IsRouteStart = previousPart is null && first == latestRoute.Points[0], IsRouteEnd = true });
            }
        }
        var routes = parts.Select(part => part.Route).ToImmutableArray();
        var resources = ImmutableArray.CreateBuilder<LootTrackerResourceVisit>();
        foreach (var visit in session.ResourceNodes.IsDefault ? [] : session.ResourceNodes)
        {
            if (visit.SegmentIndex < 0 || visit.SegmentIndex >= session.RouteSegments.Length) continue;
            var segment = session.RouteSegments[visit.SegmentIndex];
            if (visit.PointIndex < 0 || visit.PointIndex >= segment.Points.Length) continue;
            var point = segment.Points[visit.PointIndex];
            var time = point.Elapsed ?? (point.At is { } at ? at - session.StartedAt : (TimeSpan?)null);
            if (time is null && head != session.Elapsed || time > head || time < TimeSpan.Zero) continue;
            var visitTime = visit.At is { } visitAt && visitAt != default && session.StartedAt != default
                ? visitAt - session.StartedAt : (TimeSpan?)null;
            if (visitTime is { } knownTime && knownTime >= TimeSpan.Zero && knownTime <= session.Elapsed && knownTime > head) continue;
            resources.Add(visit with { SegmentIndex = routes.Length, PointIndex = 0 });
            routes = routes.Add(new(segment.MapId, segment.MapName, [point]));
        }
        return new(markerPrefix.Session with { RouteSegments = routes, ResourceNodes = resources.ToImmutable() }, head,
            camera?.MapId ?? parts.LastOrDefault()?.MapId ?? markerPrefix.MapId, player)
        {
            LastPosition = camera, LastPositionElapsed = observedAt, Provenance = provenance, IsPrepared = true,
            PreparedParts = parts, EstimatedConnections = estimated
        };
    }
    private static LootTrackerCombatPosition Position(LootTrackerPreparedRoute route, LootTrackerPreparedPoint point, DateTimeOffset startedAt) =>
        new(route.MapId, route.MapName, point.X, point.Y, point.At ?? startedAt);
    private static int LastAt(ImmutableArray<LootTrackerPreparedPoint> points, TimeSpan head)
    {
        var low = 0; var high = points.Length - 1; var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].Elapsed <= head) { found = middle; low = middle + 1; } else high = middle - 1;
        }
        return found;
    }

    private static bool Valid(string? mapId, double x, double y) => !string.IsNullOrWhiteSpace(mapId)
        && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0;
}
