using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soulcrest.App.Services;

public enum LootTrackerReplayProvenance { Matched, Flow, GapEstimated, Missing }
public readonly record struct LootTrackerPreparedPoint(double X, double Y, DateTimeOffset? At,
    TimeSpan Elapsed, LootTrackerReplayProvenance Provenance);
public sealed record LootTrackerPreparedRoute(string MapId, string MapName, int OriginalOrder,
    ImmutableArray<LootTrackerPreparedPoint> Points);
public readonly record struct LootTrackerReplayTimeIndex(int RouteIndex, TimeSpan Start, TimeSpan End, TimeSpan MaximumEndThrough = default);
public sealed record LootTrackerReplayConnection(string MapId, string MapName,
    LootTrackerPreparedPoint From, LootTrackerPreparedPoint To)
{
    public int FromRouteOrder { get; init; }
    public int ToRouteOrder { get; init; }
}
public readonly record struct LootTrackerConnectionTimeIndex(int ConnectionIndex, TimeSpan Start, TimeSpan End, TimeSpan MaximumEndThrough);
public sealed record LootTrackerPreparedConnection(int ConnectionIndex, string MapId, TimeSpan End, LootTrackerCharts.RouteLine Line);
public sealed record LootTrackerPreparedPart(int RouteIndex, string MapId, string MapName,
    ImmutableArray<LootTrackerPreparedPoint> Points, LootTrackerCharts.RouteLine Line)
{
    public bool IsRouteStart { get; init; }
    public bool IsRouteEnd { get; init; }
    private LootTrackerRouteSegment? _route;
    [System.Text.Json.Serialization.JsonIgnore]
    public LootTrackerRouteSegment Route => _route ??= new(MapId, MapName,
        Points.Select(p => new LootTrackerRoutePoint(p.X, p.Y, p.At, p.Elapsed)).ToImmutableArray());
}

/// <summary>Versioned whole-session preparation. Display geometry is bounded; original positions retain their precision.</summary>
public sealed record LootTrackerPreparedReplay
{
    public const int CurrentVersion = 2;
    public const int DisplayPointBudgetPerMap = 4096;
    public const int DisplayConnectionBudgetPerMap = 2048;
    public int Version { get; init; } = CurrentVersion;
    public string SessionId { get; init; } = "";
    public LootTrackerSessionKind Kind { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public string ContentFingerprint { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public ImmutableArray<LootTrackerPreparedRoute> Routes { get; init; } = [];
    public ImmutableArray<LootTrackerReplayTimeIndex> TimeIndex { get; init; } = [];
    public ImmutableArray<LootTrackerPreparedPart> DisplayParts { get; init; } = [];
    public ImmutableArray<LootTrackerReplayConnection> Connections { get; init; } = [];
    public ImmutableArray<LootTrackerConnectionTimeIndex> ConnectionTimeIndex { get; init; } = [];
    public ImmutableArray<LootTrackerPreparedConnection> DisplayConnections { get; init; } = [];
    public bool HighResolution { get; init; }
    public bool CoverageLimited { get; init; }
    public static string Fingerprint(LootTrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var positions = Positions(session);
        return Fingerprint(session, positions.SourceFingerprint);
    }
    public static LootTrackerPreparedReplay Prepare(LootTrackerSession session, CancellationToken cancellationToken = default,
        Action<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status == LootTrackerSessionStatus.Live) throw new InvalidOperationException("Replay requires a terminal session.");
        cancellationToken.ThrowIfCancellationRequested();
        return Prepare(session, Positions(session), cancellationToken, progress);
    }
    private static ReplayPositionReadResult Positions(LootTrackerSession session) => session.UnifiedVersion > 0
        && Guid.TryParseExact(session.Id, "N", out _) ? ReplayPositionStore.Read(session.Id, session.Elapsed)
        : new([], [], "", false, false);

    internal static string Fingerprint(LootTrackerSession session, string positionFingerprint)
    {
        // Names, price proofs, language, theme and view filters are not recording identity.
        var value = new
        {
            session.Id, session.Kind, session.Status, session.UnifiedVersion, session.TimelineVersion,
            session.StartedAt, session.EndedAt, session.Elapsed, session.Mode, session.FarmDay,
            session.LastLootAt, session.LastPresenceAt, session.CoverageLimited, session.FarmRouteVersion,
            session.KinaBound, session.KinaUnbound, session.MonsterCount, session.ItemCount, session.HarvestCount,
            session.FailedHarvestCount, session.SoulCount, session.AbyssPoints, session.RankPointsGained, session.RankPointsLost,
            Minutes = session.MinuteIncome.IsDefault ? [] : session.MinuteIncome,
            PositionSource = positionFingerprint,
            Loot = (session.Loot.IsDefault ? [] : session.Loot).Select(row => new { row.Id, row.Kind, row.Quantity, row.MobId }),
            Mobs = (session.Mobs.IsDefault ? [] : session.Mobs).Select(row => new { row.Id, row.Count }),
            Routes = (session.RouteSegments.IsDefault ? [] : session.RouteSegments).Select(r => new { r.MapId, Points = r.Points.IsDefault ? [] : r.Points }),
            Combat = (session.CombatEvents.IsDefault ? [] : session.CombatEvents).Select(e => new
                { e.Id, e.Kind, e.At, e.Elapsed, e.Origin, e.OpponentClassId, e.OpponentAbyssRankGrade,
                    Position = e.Position is null ? null : new { e.Position.MapId, e.Position.X, e.Position.Y, e.Position.ObservedAt } }),
            Visits = (session.GatherVisits.IsDefault ? [] : session.GatherVisits).Select(v => new
                { v.Id, v.Section, v.MapId, v.OriginalX, v.OriginalY, v.X, v.Y, v.At, v.Succeeded, v.GatherableId, v.CatalogNodeId }),
            Events = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents).Select(e => new
                { e.Id, e.Kind, e.At, e.Elapsed, e.Amount, e.Origin, e.Metric, e.Available, e.FromStageId, e.ToStageId,
                    e.GatherableId, GatherVisitId = e.GatherVisit?.Id,
                    Loot = (e.Loot.IsDefault ? [] : e.Loot).Select(l => new { l.Id, l.Kind, l.Quantity }) }),
            Resources = (session.ResourceNodes.IsDefault ? [] : session.ResourceNodes).Select(v => new
                { v.NodeId, v.MapId, v.X, v.Y, v.SegmentIndex, v.PointIndex, v.At }),
            Farm = (session.FarmRoutePoints.IsDefault ? [] : session.FarmRoutePoints).Select(v => new
                { v.Id, v.Section, v.MapId, v.X, v.Y, v.VisitCount, v.SuccessCount, v.FailureCount })
        };
        var placements = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents)
            .Where(row => row.Position is not null).Select(row => new
            { row.Id, row.Position!.MapId, row.Position.X, row.Position.Y, row.Position.ObservedAt }).ToArray();
        // Preserve existing cache identities for recordings without the new optional drop placements.
        var bytes = placements.Length == 0 ? JsonSerializer.SerializeToUtf8Bytes(value)
            : JsonSerializer.SerializeToUtf8Bytes(new { Source = value, Placements = placements });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static LootTrackerPreparedReplay Prepare(LootTrackerSession session, ReplayPositionReadResult positions,
        CancellationToken token = default, Action<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status == LootTrackerSessionStatus.Live) throw new InvalidOperationException("Replay requires a terminal session.");
        token.ThrowIfCancellationRequested(); progress?.Invoke(5);
        var routes = new List<LootTrackerPreparedRoute>();
        if (positions.Exists && !positions.Samples.IsDefaultOrEmpty)
        {
            var points = ImmutableArray.CreateBuilder<LootTrackerPreparedPoint>();
            long? segment = null; string? map = null; string name = ""; var order = 0; var gapIndex = 0;
            var gaps = (positions.Gaps.IsDefault ? [] : positions.Gaps).OrderBy(gap => gap.Elapsed).ToArray();
            foreach (var sample in positions.Samples)
            {
                token.ThrowIfCancellationRequested();
                if (!Valid(sample.MapId, sample.X, sample.Y) || sample.Elapsed < TimeSpan.Zero || sample.Elapsed > session.Elapsed)
                { Flush(); order++; continue; }
                var crossedGap = false;
                while (gapIndex < gaps.Length && gaps[gapIndex].Elapsed <= sample.Elapsed) { gapIndex++; crossedGap = true; }
                if (crossedGap || segment != sample.SegmentId || map != sample.MapId) Flush();
                segment = sample.SegmentId; map = sample.MapId; name = sample.MapName;
                if (points.Count > 0 && sample.Elapsed < points[^1].Elapsed) { Flush(); order++; }
                points.Add(new(sample.X, sample.Y, sample.OriginalAt, sample.Elapsed,
                    sample.Provenance == ReplayPositionProvenance.Flow ? LootTrackerReplayProvenance.Flow : LootTrackerReplayProvenance.Matched));
            }
            Flush();
            void Flush()
            {
                if (points.Count > 0) routes.Add(new(map!, name, order++, points.ToImmutable()));
                points.Clear();
            }
        }
        else
        {
            var order = 0;
            foreach (var segment in session.RouteSegments.IsDefault ? [] : session.RouteSegments)
            {
                token.ThrowIfCancellationRequested();
                var points = ImmutableArray.CreateBuilder<LootTrackerPreparedPoint>();
                foreach (var point in segment.Points.IsDefault ? [] : segment.Points)
                {
                    var time = point.Elapsed ?? (point.At is { } at && at != default ? at - session.StartedAt : (TimeSpan?)null);
                    if (!Valid(segment.MapId, point.X, point.Y) || time < TimeSpan.Zero || time > session.Elapsed)
                    { Flush(); order++; continue; }
                    if (points.Count > 0 && time is { } current && current < points[^1].Elapsed) { Flush(); order++; }
                    points.Add(new(point.X, point.Y, point.At, time ?? session.Elapsed,
                        time is null ? LootTrackerReplayProvenance.Missing : LootTrackerReplayProvenance.Matched));
                }
                Flush(); order++;
                void Flush()
                {
                    if (points.Count > 0) routes.Add(new(segment.MapId, segment.MapName, order, points.ToImmutable()));
                    points.Clear();
                }
            }
        }
        progress?.Invoke(35); token.ThrowIfCancellationRequested();
        var ordered = routes.OrderBy(route => route.Points[0].Elapsed).ThenBy(route => route.OriginalOrder).ToImmutableArray();
        var temporalOrder = ordered.Select((route, index) => (route.OriginalOrder, index)).ToDictionary(row => row.OriginalOrder, row => row.index);
        var mapChanges = new MapChangeIndex(session);
        var deaths = Deaths(session);
        var connections = new List<LootTrackerReplayConnection>();
        for (var index = 1; index < routes.Count; index++)
        {
            var previous = routes[index - 1]; var next = routes[index];
            var from = previous.Points[^1]; var to = next.Points[0];
            if (next.OriginalOrder != previous.OriginalOrder + 1 || temporalOrder[next.OriginalOrder] != temporalOrder[previous.OriginalOrder] + 1
                || previous.MapId != next.MapId || from.Elapsed >= to.Elapsed
                || from.Provenance == LootTrackerReplayProvenance.Missing || to.Provenance == LootTrackerReplayProvenance.Missing) continue;
            if (!HasDeath(deaths, from.Elapsed, to.Elapsed) && !mapChanges.Contains(previous.MapId, from.Elapsed, to.Elapsed))
                connections.Add(new(previous.MapId, previous.MapName, from, to)
                    { FromRouteOrder = previous.OriginalOrder, ToRouteOrder = next.OriginalOrder });
        }
        var selections = ordered.GroupBy(route => route.MapId).ToDictionary(group => group.Key,
            group => SelectIndices(group.Sum(route => (long)route.Points.Length), DisplayPointBudgetPerMap), StringComparer.Ordinal);
        var offsets = selections.Keys.ToDictionary(map => map, _ => 0L, StringComparer.Ordinal);
        var cursors = selections.Keys.ToDictionary(map => map, _ => 0, StringComparer.Ordinal);
        var parts = ImmutableArray.CreateBuilder<LootTrackerPreparedPart>();
        for (var index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var route = ordered[index]; var selection = selections[route.MapId]; var cursor = cursors[route.MapId];
            var offset = offsets[route.MapId]; var limit = offset + route.Points.Length;
            var displayed = new List<LootTrackerPreparedPoint>();
            while (cursor < selection.Length && selection[cursor] < limit)
                displayed.Add(route.Points[(int)(selection[cursor++] - offset)]);
            cursors[route.MapId] = cursor; offsets[route.MapId] = limit;
            for (var start = 0; start < displayed.Count; start += 127)
            {
                var slice = displayed.Skip(start).Take(128).ToImmutableArray();
                parts.Add(new(index, route.MapId, route.MapName, slice, Line(slice))
                    { IsRouteStart = start == 0 && slice[0] == route.Points[0],
                        IsRouteEnd = start + slice.Length == displayed.Count && slice[^1] == route.Points[^1] });
                if (start + 128 >= displayed.Count) break;
            }
        }
        var orderedConnections = connections.OrderBy(connection => connection.From.Elapsed).ThenBy(connection => connection.To.Elapsed).ToImmutableArray();
        var connectionParts = ImmutableArray.CreateBuilder<LootTrackerPreparedConnection>();
        foreach (var group in orderedConnections.Select((connection, index) => (connection, index)).GroupBy(row => row.connection.MapId))
        {
            var source = group.ToArray();
            foreach (var selected in SelectIndices(source.Length, DisplayConnectionBudgetPerMap))
            {
                token.ThrowIfCancellationRequested();
                var (connection, index) = source[(int)selected];
                connectionParts.Add(new(index, connection.MapId, connection.To.Elapsed, Line([connection.From, connection.To])));
            }
        }
        var maximumConnectionEnd = TimeSpan.Zero;
        var connectionIndex = orderedConnections.Select((connection, index) =>
        {
            if (connection.To.Elapsed > maximumConnectionEnd) maximumConnectionEnd = connection.To.Elapsed;
            return new LootTrackerConnectionTimeIndex(index, connection.From.Elapsed, connection.To.Elapsed, maximumConnectionEnd);
        }).ToImmutableArray();
        progress?.Invoke(90); token.ThrowIfCancellationRequested();
        var maximumEnd = TimeSpan.Zero;
        var timeIndex = ordered.Select((route, index) =>
        {
            if (route.Points[^1].Elapsed > maximumEnd) maximumEnd = route.Points[^1].Elapsed;
            return new LootTrackerReplayTimeIndex(index, route.Points[0].Elapsed, route.Points[^1].Elapsed, maximumEnd);
        }).ToImmutableArray();
        var result = new LootTrackerPreparedReplay
        {
            SessionId = session.Id, Kind = session.Kind, SourceFingerprint = Fingerprint(session, positions.SourceFingerprint),
            CreatedAt = DateTimeOffset.UtcNow, Routes = ordered,
            TimeIndex = timeIndex,
            DisplayParts = parts.ToImmutable(), Connections = orderedConnections,
            ConnectionTimeIndex = connectionIndex, DisplayConnections = connectionParts.ToImmutable(),
            HighResolution = positions.Exists && !positions.Samples.IsDefaultOrEmpty,
            CoverageLimited = session.CoverageLimited || positions.Truncated || !positions.Gaps.IsDefaultOrEmpty
        };
        progress?.Invoke(100);
        return result with { ContentFingerprint = result.PayloadFingerprint() };
    }

    internal static LootTrackerCharts.RouteLine Line(ImmutableArray<LootTrackerPreparedPoint> points) => new(
        string.Join(" ", points.Select(p => Number(p.X) + "," + Number(p.Y))), Number(points[0].X), Number(points[0].Y),
        Number(points[^1].X), Number(points[^1].Y));
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    internal static bool Valid(string? map, double x, double y) => !string.IsNullOrWhiteSpace(map)
        && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0;

    private static long[] SelectIndices(long count, int budget)
    {
        if (count == 0) return [];
        var selected = (int)Math.Min(count, budget);
        return Enumerable.Range(0, selected).Select(index => selected == 1 ? 0L : index * (count - 1) / (selected - 1)).ToArray();
    }
    private static ImmutableArray<TimeSpan> Deaths(LootTrackerSession session) =>
        (session.CombatEvents.IsDefault ? [] : session.CombatEvents).Where(row => row.Kind == LootTrackerCombatKind.Death
            && (session.Kind == LootTrackerSessionKind.Abyss || row.Origin == LootTrackerActivityOrigin.Abyss))
            .Select(row => row.Elapsed).OrderBy(time => time).ToImmutableArray();
    private static bool HasDeath(ImmutableArray<TimeSpan> deaths, TimeSpan from, TimeSpan to)
    {
        var low = 0; var high = deaths.Length;
        while (low < high) { var middle = low + (high - low) / 2; if (deaths[middle] < from) low = middle + 1; else high = middle; }
        return low < deaths.Length && deaths[low] <= to;
    }
    private sealed class MapChangeIndex
    {
        private readonly (TimeSpan At, string Map)[] _observations;
        private readonly (TimeSpan From, TimeSpan To, string Map)[] _unknown;
        private readonly HashSet<string> _maps;
        internal MapChangeIndex(LootTrackerSession session)
        {
            var observations = new List<(TimeSpan At, string Map)>();
            var unknown = new List<(TimeSpan From, TimeSpan To, string Map)>();
            TimeSpan? Time(LootTrackerRoutePoint point) => point.Elapsed
                ?? (point.At is { } at && at != default ? at - session.StartedAt : null);
            var routes = session.RouteSegments.IsDefault ? [] : session.RouteSegments;
            var first = new TimeSpan?[routes.Length]; var last = new TimeSpan?[routes.Length];
            for (var index = 0; index < routes.Length; index++)
                foreach (var point in routes[index].Points.IsDefault ? [] : routes[index].Points)
                    if (Time(point) is { } at)
                    { first[index] ??= at; last[index] = at; observations.Add((at, routes[index].MapId)); }
            var next = session.Elapsed; var after = new TimeSpan[routes.Length];
            for (var index = routes.Length - 1; index >= 0; index--) { after[index] = next; next = first[index] ?? next; }
            var previous = TimeSpan.Zero;
            for (var index = 0; index < routes.Length; index++)
            {
                if (first[index] is null) unknown.Add((previous, after[index], routes[index].MapId));
                previous = last[index] ?? previous;
            }
            foreach (var row in session.CombatEvents.IsDefault ? [] : session.CombatEvents)
                if (row.Position is { } position && position.ObservedAt != default && position.ObservedAt <= row.At)
                    observations.Add((row.Elapsed - (row.At - position.ObservedAt), position.MapId));
            foreach (var row in session.TimelineEvents.IsDefault ? [] : session.TimelineEvents)
                if (row.Kind == LootTrackerTimelineEventKind.Drop && row.Position is { } position
                    && Valid(position.MapId, position.X, position.Y) && position.ObservedAt != default
                    && position.ObservedAt <= row.At && row.At - position.ObservedAt <= TimeSpan.FromSeconds(2))
                    observations.Add((row.Elapsed - (row.At - position.ObservedAt), position.MapId));
            var visits = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents).Where(row => row.GatherVisit is not null)
                .GroupBy(row => row.GatherVisit!.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Elapsed, StringComparer.Ordinal);
            foreach (var visit in session.GatherVisits.IsDefault ? [] : session.GatherVisits)
                if (visits.TryGetValue(visit.Id, out var moment)) observations.Add((moment, visit.MapId));
                else if (visit.At != default) observations.Add((visit.At - session.StartedAt, visit.MapId));
            _observations = observations.OrderBy(row => row.At).ToArray(); _unknown = unknown.ToArray();
            _maps = observations.Select(row => row.Map).Concat(unknown.Select(row => row.Map)).ToHashSet(StringComparer.Ordinal);
        }
        internal bool Contains(string map, TimeSpan from, TimeSpan to)
        {
            if (_maps.Count == 0 || _maps.Count == 1 && _maps.Contains(map)) return false;
            var low = 0; var high = _observations.Length;
            while (low < high) { var middle = low + (high - low) / 2; if (_observations[middle].At < from) low = middle + 1; else high = middle; }
            for (var index = low; index < _observations.Length && _observations[index].At <= to; index++)
                if (_observations[index].Map != map) return true;
            return _unknown.Any(row => row.Map != map && from <= row.To && to >= row.From);
        }
    }

    private string PayloadFingerprint() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Routes, TimeIndex, DisplayParts, Connections, ConnectionTimeIndex, DisplayConnections, HighResolution, CoverageLimited })));

    public bool Matches(LootTrackerSession session, string fingerprint)
    {
        if (Version != CurrentVersion || SessionId != session.Id || Kind != session.Kind || SourceFingerprint != fingerprint
            || Routes.IsDefault || TimeIndex.IsDefault || DisplayParts.IsDefault || Connections.IsDefault
            || ConnectionTimeIndex.IsDefault || DisplayConnections.IsDefault || ConnectionTimeIndex.Length != Connections.Length
            || DisplayConnections.Any(part => part is null)
            || TimeIndex.Length != Routes.Length
            || DisplayParts.Any(part => part is null || part.RouteIndex < 0 || part.RouteIndex >= Routes.Length)) return false;
        var partsByRoute = DisplayParts.ToLookup(part => part.RouteIndex);
        var routeOrders = new Dictionary<int, LootTrackerPreparedRoute>();
        var routeIndices = new Dictionary<int, int>();
        for (var index = 0; index < Routes.Length; index++)
        {
            var route = Routes[index];
            if (route is null || route.Points.IsDefaultOrEmpty || route.OriginalOrder < 0
                || string.IsNullOrWhiteSpace(route.MapId) || route.MapId.Length > 256 || route.MapName is null || route.MapName.Length > 1024) return false;
            if (!routeOrders.TryAdd(route.OriginalOrder, route)) return false;
            routeIndices.Add(route.OriginalOrder, index);
            TimeSpan? previous = null;
            foreach (var point in route.Points)
            {
                if (!Valid(route.MapId, point.X, point.Y) || point.Elapsed < TimeSpan.Zero || point.Elapsed > session.Elapsed
                    || point.Provenance is not (LootTrackerReplayProvenance.Matched or LootTrackerReplayProvenance.Flow or LootTrackerReplayProvenance.Missing)
                    || previous is { } before && point.Elapsed < before) return false;
                previous = point.Elapsed;
            }
            var time = TimeIndex[index];
            var maximumEnd = index == 0 || time.End > TimeIndex[index - 1].MaximumEndThrough
                ? time.End : TimeIndex[index - 1].MaximumEndThrough;
            if (time.RouteIndex != index || time.Start != route.Points[0].Elapsed || time.End != route.Points[^1].Elapsed
                || time.MaximumEndThrough != maximumEnd || index > 0 && time.Start < TimeIndex[index - 1].Start) return false;
            if (!partsByRoute.Contains(index)) continue;
            var raw = route.Points.ToHashSet();
            foreach (var part in partsByRoute[index])
                if (part.Points.IsDefaultOrEmpty || part.Points.Length > 128 || part.MapId != route.MapId
                    || part.MapName != route.MapName || part.Line != Line(part.Points) || part.Points.Any(point => !raw.Contains(point))
                    || part.IsRouteStart && part.Points[0] != route.Points[0] || part.IsRouteEnd && part.Points[^1] != route.Points[^1]
                    || part.Points.Zip(part.Points.Skip(1)).Any(pair => pair.First.Elapsed > pair.Second.Elapsed)) return false;
        }
        if (DisplayParts.Any(part => part is null || part.RouteIndex < 0 || part.RouteIndex >= Routes.Length)) return false;
        if (DisplayParts.GroupBy(part => part.MapId).Any(group => group.Sum(part => part.Points.Length) > DisplayPointBudgetPerMap + DisplayPointBudgetPerMap / 127)
            || DisplayConnections.GroupBy(part => part.MapId).Any(group => group.Count() > DisplayConnectionBudgetPerMap)) return false;
        var mapChanges = new MapChangeIndex(session); var deaths = Deaths(session);
        for (var index = 0; index < Connections.Length; index++)
        {
            var connection = Connections[index];
            if (connection is null || connection.From.Elapsed < TimeSpan.Zero || connection.To.Elapsed > session.Elapsed
                || connection.From.Elapsed >= connection.To.Elapsed || !Valid(connection.MapId, connection.From.X, connection.From.Y)
                || !Valid(connection.MapId, connection.To.X, connection.To.Y)
                || connection.From.Provenance == LootTrackerReplayProvenance.Missing || connection.To.Provenance == LootTrackerReplayProvenance.Missing
                || !routeOrders.TryGetValue(connection.FromRouteOrder, out var from) || !routeOrders.TryGetValue(connection.ToRouteOrder, out var to)
                || from.MapId != connection.MapId || to.MapId != connection.MapId || from.Points[^1] != connection.From || to.Points[0] != connection.To
                || connection.ToRouteOrder != connection.FromRouteOrder + 1
                || routeIndices[connection.ToRouteOrder] != routeIndices[connection.FromRouteOrder] + 1 || mapChanges.Contains(connection.MapId, connection.From.Elapsed, connection.To.Elapsed)
                || HasDeath(deaths, connection.From.Elapsed, connection.To.Elapsed)) return false;
            var time = ConnectionTimeIndex[index];
            var maximum = index == 0 || connection.To.Elapsed > ConnectionTimeIndex[index - 1].MaximumEndThrough
                ? connection.To.Elapsed : ConnectionTimeIndex[index - 1].MaximumEndThrough;
            if (time.ConnectionIndex != index || time.Start != connection.From.Elapsed || time.End != connection.To.Elapsed
                || time.MaximumEndThrough != maximum || index > 0 && time.Start < ConnectionTimeIndex[index - 1].Start) return false;
        }
        foreach (var part in DisplayConnections)
        {
            if (part is null || part.ConnectionIndex < 0 || part.ConnectionIndex >= Connections.Length) return false;
            var source = Connections[part.ConnectionIndex];
            if (part.MapId != source.MapId || part.End != source.To.Elapsed || part.Line != Line([source.From, source.To])) return false;
        }
        try { return ContentFingerprint == PayloadFingerprint(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }
}
