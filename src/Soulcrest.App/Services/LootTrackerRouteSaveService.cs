namespace Soulcrest.App.Services;

/// <summary>One continuous recorded path. The route format cannot represent gaps between parts.</summary>
public sealed record LootTrackerRoutePart(string MapId, string MapName, IReadOnlyList<RouteStop> Stops);

/// <summary>
/// Saves observed paths or growing gathering-point routes as ordinary inactive map routes.
/// Farm routes use the same frozen positions and ordering as the preview, not the travelled path.
/// </summary>
public sealed class LootTrackerRouteSaveService(MapTargetsService targets)
{
    /// <summary>Each source segment and invalid coordinate is a hard boundary, even on the same map.</summary>
    public static IReadOnlyList<LootTrackerRoutePart> Preview(LootTrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (LootTrackerPresentation.UsesFarmRoute(session)) return PreviewFarmRoute(session);
        if (session.Kind == LootTrackerSessionKind.Lifeskill && !LootTrackerPresentation.UsesFarmRoute(session)) return [];
        var segments = LootTrackerPresentation.DisplayedRouteSegments(session);
        if (segments.IsDefaultOrEmpty) return [];
        var result = new List<LootTrackerRoutePart>();
        var nodeNames = LootTrackerPresentation.GetValidatedResourceVisits(session)
            .GroupBy(visit => (visit.SegmentIndex, visit.PointIndex))
            .ToDictionary(group => group.Key, group => string.Join(" · ", group
                .Select(visit => visit.Name?.Trim() ?? "").Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)));
        for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            if (string.IsNullOrWhiteSpace(segment.MapId) || segment.Points.IsDefaultOrEmpty) continue;
            var points = new List<RouteStop>();
            for (var pointIndex = 0; pointIndex < segment.Points.Length; pointIndex++)
            {
                var point = segment.Points[pointIndex];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0)
                {
                    Flush();
                    continue;
                }
                var name = nodeNames.GetValueOrDefault((segmentIndex, pointIndex)) ?? "";
                // Standing samples add no path. Confirmed node names annotate the original point;
                // returning to an earlier position or visiting another named node remains in order.
                if (points.LastOrDefault() is { } previous && previous.X == point.X && previous.Y == point.Y)
                {
                    if (name.Length == 0 || name == previous.Name) continue;
                    if (previous.Name.Length == 0) { points[^1] = previous with { Name = name }; continue; }
                }
                points.Add(new(segment.MapId, point.X, point.Y, name, "Waypoint", null));
            }
            Flush();

            void Flush()
            {
                if (points.Count >= 2 && points.Any(point => point.X != points[0].X || point.Y != points[0].Y))
                    result.Add(new(segment.MapId, string.IsNullOrWhiteSpace(segment.MapName) ? segment.MapId : segment.MapName,
                        Array.AsReadOnly(points.ToArray())));
                points.Clear();
            }
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<LootTrackerRoutePart> PreviewFarmRoute(LootTrackerSession session) =>
        LootTrackerPresentation.FarmRouteSections(session)
            .Where(section => section.Length >= 2 && section.Any(point => point.X != section[0].X || point.Y != section[0].Y))
            .Select(section => new LootTrackerRoutePart(section[0].MapId,
                string.IsNullOrWhiteSpace(section[0].MapName) ? section[0].MapId : section[0].MapName,
                Array.AsReadOnly(section.Select(point => new RouteStop(point.MapId, point.X, point.Y,
                    UiText.T(point.Name), "Waypoint", null)).ToArray())))
            .ToArray();

    /// <summary>Each continuous part becomes an independent route; the saved routes remain inactive.</summary>
    public IReadOnlyList<SavedRoute> Save(LootTrackerSession session, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Der Routenname fehlt.");
        if (session.Kind == LootTrackerSessionKind.Lifeskill && session.Status == LootTrackerSessionStatus.Live)
            throw new InvalidDataException("Beende zuerst die Session.");
        var parts = Preview(session);
        if (parts.Count == 0) throw new InvalidDataException("Die Session enthält keinen speicherbaren Routenabschnitt.");
        var title = name.Trim();
        var routes = parts.Select((part, index) => new SavedRoute(Guid.NewGuid().ToString("N"),
            parts.Count == 1 ? title : $"{title} · {index + 1}/{parts.Count} · {part.MapName}", part.Stops)).ToArray();
        return targets.AddSavedRoutes(routes);
    }
}
