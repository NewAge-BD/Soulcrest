using System.Drawing;
using Soulcrest.App.OverlayEditorKit;

namespace Soulcrest.App.Services;

/// <summary>One temporary placement target. Native family ids and editor window ids have separate namespaces.</summary>
public sealed record OverlayPlacementItem(string Id, string Title, OverlayDescriptor? Native = null, OverlayInstance? Editor = null)
{
    public bool IsEditor => Editor is not null;
    public bool CanPlace => IsEditor || Native?.CanPlace == true;
    public double MinScale => IsEditor ? .5 : Native?.MinScale ?? 1;
    public double MaxScale => IsEditor ? 2 : Native?.MaxScale ?? 1;
}

/// <summary>Only the boundary between normalized editor placement and physical desktop pixels.</summary>
internal static class OverlayEditorPlacementGeometry
{
    internal static OverlayLayout Read(OverlaySettings settings, Size logicalSize, OverlayMonitor monitor)
    {
        var scale = double.IsFinite(settings.Scale) ? Math.Clamp(settings.Scale, .5, 2) : 1;
        var size = ScaledSize(logicalSize, scale);
        var travelX = Math.Max(0, monitor.WorkingArea.Width - size.Width);
        var travelY = Math.Max(0, monitor.WorkingArea.Height - size.Height);
        return new(monitor.WorkingArea.Left + (int)Math.Round(Fraction(settings.PositionX) * travelX),
            monitor.WorkingArea.Top + (int)Math.Round(Fraction(settings.PositionY) * travelY), scale);
    }

    internal static (double X, double Y) Fractions(OverlayLayout layout, Size logicalSize, OverlayMonitor monitor)
    {
        var size = ScaledSize(logicalSize, layout.Scale);
        var travelX = Math.Max(0, monitor.WorkingArea.Width - size.Width);
        var travelY = Math.Max(0, monitor.WorkingArea.Height - size.Height);
        return (travelX == 0 ? 0 : Fraction((layout.X - monitor.WorkingArea.Left) / (double)travelX),
            travelY == 0 ? 0 : Fraction((layout.Y - monitor.WorkingArea.Top) / (double)travelY));
    }

    internal static Size ScaledSize(Size logicalSize, double scale) => new(
        Math.Max(1, (int)Math.Ceiling(logicalSize.Width * scale)), Math.Max(1, (int)Math.Ceiling(logicalSize.Height * scale)));
    private static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
