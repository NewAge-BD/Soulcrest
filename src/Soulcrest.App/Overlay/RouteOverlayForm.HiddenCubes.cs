using System.Drawing;
using System.Drawing.Drawing2D;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

public sealed partial class RouteOverlayForm
{
    /// <summary>
    /// User request 2026-10-16: persistent flight orientation within confirmed groups, independent
    /// of routes and selection. No arrows or ordering: opening a cube does not predict the next point.
    /// </summary>
    private static void DrawHiddenCubeGroups(Graphics graphics, MapPlacement placement, PointF origin, IReadOnlyList<HiddenCubeGroup> groups, OverlayThemeDefinition theme)
    {
        if (groups.Count == 0 || placement.Region.Width <= 6 || placement.Region.Height <= 6) return;
        var area = new RectangleF(3, 3, placement.Region.Width - 6, placement.Region.Height - 6);
        var state = graphics.Save();
        graphics.SetClip(area, CombineMode.Intersect);
        try
        {
            foreach (var group in groups)
            {
                using var outline = new Pen(theme.IsLegacy ? Color.FromArgb(180, 8, 12, 20) : Color.FromArgb(180, theme.Surface), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var line = new Pen(Color.FromArgb(205, ColorTranslator.FromHtml(group.Color)), 1.8f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var path = new GraphicsPath();
                foreach (var connection in group.Connections)
                {
                    var from = Offset(placement.WorldToScreen(connection.FromX, connection.FromY), origin);
                    var to = Offset(placement.WorldToScreen(connection.ToX, connection.ToY), origin);
                    if (!float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(to.X) || !float.IsFinite(to.Y)
                        || RouteGeometry.Clip(from, to, area) is not { } segment) continue;
                    // Cull the segment, not its endpoints: it can cross a minimap with both ends outside.
                    path.StartFigure(); // disconnected figures never add an implicit joining edge
                    path.AddLine(segment.Start, segment.End);
                }
                if (path.PointCount > 0)
                {
                    graphics.DrawPath(outline, path);
                    graphics.DrawPath(line, path);
                }
            }
        }
        finally { graphics.Restore(state); }
    }
}
