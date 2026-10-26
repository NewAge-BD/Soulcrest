using System.Globalization;

namespace Soulcrest.App.Services;

/// <summary>Finite SVG projections only; missing observations never become an interpolated route or income.</summary>
public static class LootTrackerCharts
{
    public sealed record Line(string Points, string Area, string LastX, string LastY);
    public sealed record RouteLine(string Points, string StartX, string StartY, string EndX, string EndY);
    public sealed record ResourceNode(string NodeId, string Name, int Order, string X, string Y);
    private sealed record Projection(double CenterX, double CenterY, double Scale);

    public static IReadOnlyList<Line> Income(LootTrackerSession session) => Income(session, Total);

    /// <summary>A separately labelled observed component; missing bound rewards never become zero.</summary>
    public static IReadOnlyList<Line> UnboundIncome(LootTrackerSession session) =>
        Income(session, bin => bin.KinaUnbound is >= 0 ? bin.KinaUnbound : null);

    private static IReadOnlyList<Line> Income(LootTrackerSession session, Func<LootTrackerIncomeBin, long?> amountFor)
    {
        if (session.MinuteIncome.IsDefaultOrEmpty) return [];
        var bins = session.MinuteIncome.OrderBy(bin => bin.Minute).ToArray();
        var known = bins.Where(bin => bin.Minute >= 0 && amountFor(bin) is not null).ToArray();
        if (known.Length == 0) return [];
        var maxMinute = Math.Max(1, bins.Max(bin => bin.Minute));
        var maxValue = Math.Max(1, known.Max(bin => amountFor(bin)!.Value));
        var result = new List<Line>();
        var points = new List<(double X, double Y)>();
        int? previous = null;
        foreach (var bin in bins)
        {
            var amount = amountFor(bin);
            if (bin.Minute < 0 || amount is null || (previous is { } last && bin.Minute != last + 1)) Flush();
            if (bin.Minute < 0 || amount is null) { previous = null; continue; }
            points.Add((12 + bin.Minute / (double)maxMinute * 416, 135 - amount.Value / (double)maxValue * 110));
            previous = bin.Minute;
        }
        Flush();
        return result;

        void Flush()
        {
            if (points.Count == 0) return;
            var line = string.Join(" ", points.Select(p => $"{N(p.X)},{N(p.Y)}"));
            result.Add(new(line, $"{N(points[0].X)},135 {line} {N(points[^1].X)},135", N(points[^1].X), N(points[^1].Y)));
            points.Clear();
        }
    }

    public static IReadOnlyList<RouteLine> Route(LootTrackerSession session, string? mapId)
    {
        var route = LootTrackerPresentation.DisplayedRouteSegments(session);
        if (route.IsDefaultOrEmpty || mapId is null) return [];
        var segments = route.Where(segment => segment.MapId == mapId && !segment.Points.IsDefaultOrEmpty).ToArray();
        var projection = Project(session, mapId);
        if (projection is null) return [];
        var result = new List<RouteLine>();
        foreach (var segment in segments)
        {
            var current = new List<(double X, double Y)>();
            foreach (var point in segment.Points)
            {
                if (!Finite(point)) { Flush(); continue; }
                var x = 220 + (point.X - projection.CenterX) * projection.Scale;
                // Native map pixels use positive Y downwards; Leaflet conversion belongs at its own boundary.
                var y = 80 + (point.Y - projection.CenterY) * projection.Scale;
                if (!double.IsFinite(x) || !double.IsFinite(y)) { Flush(); continue; }
                current.Add((x, y));
            }
            Flush();

            void Flush()
            {
                if (current.Count == 0) return;
                if (current.Count == 1 && LootTrackerPresentation.UsesFarmRoute(session)) { current.Clear(); return; }
                result.Add(new(string.Join(" ", current.Select(p => $"{N(p.X)},{N(p.Y)}")),
                    N(current[0].X), N(current[0].Y), N(current[^1].X), N(current[^1].Y)));
                current.Clear();
            }
        }
        return result;
    }

    public static IReadOnlyList<ResourceNode> ResourceNodes(LootTrackerSession session, string? mapId)
    {
        var projection = Project(session, mapId);
        if (projection is null) return [];
        if (LootTrackerPresentation.UsesFarmRoute(session))
            return FarmNodes(session, mapId).Select(row => new ResourceNode(row.Point.Id, row.Point.Name, row.Order,
                N(220 + (row.Point.X - projection.CenterX) * projection.Scale),
                N(80 + (row.Point.Y - projection.CenterY) * projection.Scale))).ToArray();
        return LootTrackerPresentation.GetValidatedResourceVisits(session).Select((visit, index) => (visit, index))
            .Where(row => row.visit.MapId == mapId)
            .Select(row => new ResourceNode(row.visit.NodeId, row.visit.Name, row.index + 1,
                N(220 + (row.visit.X - projection.CenterX) * projection.Scale),
                N(80 + (row.visit.Y - projection.CenterY) * projection.Scale))).ToArray();
    }

    /// <summary>Untransformed map pixels for an offline-map SVG. Invalid points split, rather than shorten, a segment.</summary>
    public static IReadOnlyList<RouteLine> MapRoute(LootTrackerSession session, string mapId, double mapSize)
    {
        var route = LootTrackerPresentation.DisplayedRouteSegments(session);
        if (route.IsDefaultOrEmpty || !double.IsFinite(mapSize) || mapSize <= 0) return [];
        var result = new List<RouteLine>();
        foreach (var segment in route.Where(segment => segment.MapId == mapId && !segment.Points.IsDefaultOrEmpty))
        {
            var current = new List<LootTrackerRoutePoint>();
            foreach (var point in segment.Points)
            {
                if (!InMap(point.X, point.Y, mapSize)) { Flush(); continue; }
                current.Add(point);
            }
            Flush();

            void Flush()
            {
                if (current.Count == 0) return;
                if (current.Count == 1 && LootTrackerPresentation.UsesFarmRoute(session)) { current.Clear(); return; }
                result.Add(new(string.Join(" ", current.Select(point => $"{MapNumber(point.X)},{MapNumber(point.Y)}")),
                    MapNumber(current[0].X), MapNumber(current[0].Y), MapNumber(current[^1].X), MapNumber(current[^1].Y)));
                current.Clear();
            }
        }
        return result;
    }

    public static IReadOnlyList<ResourceNode> MapResourceNodes(LootTrackerSession session, string mapId, double mapSize)
    {
        if (LootTrackerPresentation.UsesFarmRoute(session))
            return FarmNodes(session, mapId).Where(row => InMap(row.Point.X, row.Point.Y, mapSize))
                .Select(row => new ResourceNode(row.Point.Id, row.Point.Name, row.Order,
                    MapNumber(row.Point.X), MapNumber(row.Point.Y))).ToArray();
        return LootTrackerPresentation.GetValidatedResourceVisits(session).Select((visit, index) => (visit, index))
            .Where(row => row.visit.MapId == mapId && InMap(row.visit.X, row.visit.Y, mapSize)
                && InMap(session.RouteSegments[row.visit.SegmentIndex].Points[row.visit.PointIndex].X,
                    session.RouteSegments[row.visit.SegmentIndex].Points[row.visit.PointIndex].Y, mapSize))
            .Select(row => new ResourceNode(row.visit.NodeId, row.visit.Name, row.index + 1,
                MapNumber(row.visit.X), MapNumber(row.visit.Y))).ToArray();
    }

    internal static bool InMap(double x, double y, double size) => double.IsFinite(size) && size > 0
        && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0 && x <= size && y <= size;
    internal static string MapNumber(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static Projection? Project(LootTrackerSession session, string? mapId)
    {
        var route = LootTrackerPresentation.DisplayedRouteSegments(session);
        if (mapId is null || route.IsDefaultOrEmpty) return null;
        var points = route.Where(segment => segment.MapId == mapId && !segment.Points.IsDefaultOrEmpty)
            .SelectMany(segment => segment.Points).Where(Finite).ToList();
        if (points.Count == 0) return null;
        if (!LootTrackerPresentation.UsesFarmRoute(session))
            points.AddRange(LootTrackerPresentation.GetValidatedResourceVisits(session).Where(visit => visit.MapId == mapId)
                .Select(visit => new LootTrackerRoutePoint(visit.X, visit.Y)));
        var minX = points.Min(point => point.X); var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y); var maxY = points.Max(point => point.Y);
        var width = maxX - minX; var height = maxY - minY;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return null;
        var scale = width == 0 && height == 0 ? 1 : Math.Min(width > 0 ? 380 / width : double.PositiveInfinity,
            height > 0 ? 130 / height : double.PositiveInfinity);
        if (!double.IsFinite(scale)) return null;
        return new(minX / 2 + maxX / 2, minY / 2 + maxY / 2, scale);
    }

    private static bool Finite(LootTrackerRoutePoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.Y >= 0;
    private static IEnumerable<(LootTrackerFarmRoutePoint Point, int Order)> FarmNodes(LootTrackerSession session, string? mapId) =>
        session.FarmRoutePoints.IsDefaultOrEmpty ? [] : session.FarmRoutePoints.Select((point, index) => (Point: point, Order: index + 1))
            .Where(row => row.Point.MapId == mapId && row.Point.Section >= 0
                && double.IsFinite(row.Point.X) && double.IsFinite(row.Point.Y) && row.Point.X >= 0 && row.Point.Y >= 0);
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static long? Total(LootTrackerIncomeBin bin) => bin.KinaBound is >= 0 && bin.KinaUnbound is >= 0
        && bin.KinaBound.Value <= long.MaxValue - bin.KinaUnbound.Value ? bin.KinaBound.Value + bin.KinaUnbound.Value : null;
}
