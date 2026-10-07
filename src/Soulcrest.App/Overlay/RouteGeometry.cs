using System.Drawing;

namespace Soulcrest.App.Overlay;

/// <summary>Geometry of a route line on the in-game map: clipping to the map area, arrow positions.</summary>
public static class RouteGeometry
{
    /// <summary>
    /// Part of the segment from → to inside the rectangle (Liang–Barsky). Null when it misses the
    /// rectangle. <c>ReachesEnd</c> tells whether the target itself is inside.
    /// </summary>
    public static (PointF Start, PointF End, bool ReachesEnd)? Clip(PointF from, PointF to, RectangleF area)
    {
        double t0 = 0, t1 = 1;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-9)
                return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
        if (!Edge(-dx, from.X - area.Left) || !Edge(dx, area.Right - from.X) || !Edge(-dy, from.Y - area.Top) || !Edge(dy, area.Bottom - from.Y))
            return null;
        var start = new PointF((float)(from.X + t0 * dx), (float)(from.Y + t0 * dy));
        var end = new PointF((float)(from.X + t1 * dx), (float)(from.Y + t1 * dy));
        return (start, end, t1 >= 1 - 1e-9);
    }

    /// <summary>
    /// Arrow positions along start → end, every <paramref name="spacing"/> pixels, the first half a
    /// spacing after the start; angle in degrees (direction of travel, 0 = right, 90 = down).
    /// </summary>
    public static IReadOnlyList<(PointF Point, float Angle)> Arrows(PointF start, PointF end, float spacing, int max = 40)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        var result = new List<(PointF, float)>();
        if (length < 1 || spacing <= 0)
            return result;
        var angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        for (var d = spacing / 2; d < length - spacing / 4 && result.Count < max; d += spacing)
            result.Add((new PointF(start.X + dx * d / length, start.Y + dy * d / length), angle));
        return result;
    }
}
