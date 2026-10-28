using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>
/// One temporary map inspection of the already recorded route. It creates no navigation targets
/// or saved routes. Segment boundaries and invalid-position gaps are never joined.
/// </summary>
public sealed record LootTrackerMapFocus(string SessionId, string Name, string MapId,
    ImmutableArray<LootTrackerRouteSegment> Segments)
{
    public IReadOnlyList<LootTrackerSessionMarker> Markers { get; init; } = [];

    public static LootTrackerMapFocus? Create(LootTrackerSession session, string? mapId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var source = LootTrackerPresentation.DisplayedRouteSegments(session);
        mapId ??= source.FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment.MapId)
            && !segment.Points.IsDefaultOrEmpty && segment.Points.Any(Valid))?.MapId
            ?? LootTrackerSessionMarkers.All(session).FirstOrDefault()?.MapId;
        if (string.IsNullOrWhiteSpace(mapId)) return null;

        var segments = ImmutableArray.CreateBuilder<LootTrackerRouteSegment>();
        foreach (var segment in source)
        {
            if (!string.Equals(segment.MapId, mapId, StringComparison.Ordinal) || segment.Points.IsDefaultOrEmpty) continue;
            var points = ImmutableArray.CreateBuilder<LootTrackerRoutePoint>();
            foreach (var point in segment.Points)
            {
                if (Valid(point)) points.Add(point);
                else Flush();
            }
            Flush();

            void Flush()
            {
                if (points.Count > 0) segments.Add(segment with { Points = points.ToImmutable() });
                points.Clear();
            }
        }
        var markers = LootTrackerSessionMarkers.All(session).Where(row => row.MapId == mapId).ToArray();
        return segments.Count == 0 && markers.Length == 0 ? null : new(session.Id,
            LootTrackerPresentation.SessionName(session) ?? "", mapId, segments.ToImmutable()) { Markers = markers };
    }

    private static bool Valid(LootTrackerRoutePoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.Y >= 0;
}
