using System.Drawing.Drawing2D;

namespace Soulcrest.App.Overlay;

/// <summary>Immutable presentation tokens. All themes share the same stored overlay layout.</summary>
internal sealed class OverlayThemeDefinition
{
    // User request 2026-10-20: keep both approved Aion2 frames and offer three distinct additional themes.
    internal const string DefaultId = "aion2-stylized";
    internal static OverlayThemeDefinition Aion2 { get; } = new(DefaultId, "Aion2 - stilisierte", FrameStyle.Stylized,
        Color.FromArgb(204, 23, 27, 31), Color.FromArgb(154, 145, 135, 111),
        Color.FromArgb(244, 239, 226), Color.FromArgb(178, 181, 177),
        Color.FromArgb(237, 201, 111), Color.FromArgb(100, 226, 193), "Georgia");
    internal static OverlayThemeDefinition Aion2Neutral { get; } = new("aion2-neutral", "Aion2 - neutral", FrameStyle.Neutral,
        Aion2.Surface, Aion2.Border, Aion2.Primary, Aion2.Secondary, Aion2.Gold, Aion2.Mint, "Georgia");
    internal static OverlayThemeDefinition Obsidian { get; } = new("obsidian", "Obsidian", FrameStyle.Obsidian,
        Color.FromArgb(235, 11, 14, 19), Color.FromArgb(175, 113, 126, 144),
        Color.FromArgb(246, 249, 253), Color.FromArgb(184, 196, 212),
        Color.FromArgb(217, 229, 245), Color.FromArgb(128, 226, 184), "Segoe UI");
    internal static OverlayThemeDefinition Aurora { get; } = new("aurora", "Nordlicht", FrameStyle.Aurora,
        Color.FromArgb(218, 10, 29, 43), Color.FromArgb(165, 67, 166, 194),
        Color.FromArgb(228, 247, 251), Color.FromArgb(153, 191, 205),
        Color.FromArgb(130, 227, 241), Color.FromArgb(108, 237, 206), "Bahnschrift");
    internal static OverlayThemeDefinition Runic { get; } = new("runic", "Runen", FrameStyle.Runic,
        Color.FromArgb(224, 39, 28, 22), Color.FromArgb(180, 163, 120, 71),
        Color.FromArgb(249, 236, 210), Color.FromArgb(202, 180, 151),
        Color.FromArgb(239, 187, 100), Color.FromArgb(156, 221, 166), "Palatino Linotype");
    internal static OverlayThemeDefinition Legacy { get; } = new("soulcrest-legacy", "Soulcrest legacy", FrameStyle.Legacy,
        Color.FromArgb(17, 24, 32), Color.FromArgb(44, 59, 73),
        Color.FromArgb(235, 242, 248), Color.FromArgb(154, 173, 189),
        Color.FromArgb(237, 201, 111), Color.FromArgb(100, 226, 193), "Segoe UI");
    internal static IReadOnlyList<OverlayThemeDefinition> All { get; } = Array.AsReadOnly(
        new[] { Aion2, Aion2Neutral, Obsidian, Aurora, Runic, Legacy });

    private enum FrameStyle { Legacy, Stylized, Neutral, Obsidian, Aurora, Runic }
    private readonly FrameStyle _frame;
    private readonly string _headingFamily;

    private OverlayThemeDefinition(string id, string label, FrameStyle frame, Color surface, Color border,
        Color primary, Color secondary, Color gold, Color mint, string headingFamily)
    {
        Id = id; Label = label; _frame = frame; _headingFamily = headingFamily;
        Surface = surface; Border = border; Primary = primary; Secondary = secondary; Gold = gold; Mint = mint;
    }

    internal string Id { get; }
    internal string Label { get; }
    internal bool IsLegacy => _frame == FrameStyle.Legacy;
    internal Color Surface { get; }
    internal Color Border { get; }
    internal Color Primary { get; }
    internal Color Secondary { get; }
    internal Color Gold { get; }
    internal Color Mint { get; }
    internal int Spacing => 8;
    internal static bool IsKnown(string? id) => id == "aion2" || All.Any(theme => theme.Id == id);
    internal static string NormalizeId(string? id) => All.FirstOrDefault(theme => theme.Id == id)?.Id ?? DefaultId;
    internal static OverlayThemeDefinition Resolve(string? id) => All.FirstOrDefault(theme => theme.Id == id) ?? Aion2;

    // User request 2026-10-20: stronger strokes keep transparent panels readable at small scales.
    internal Font Heading(float size) => new(_headingFamily, size,
        IsLegacy ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
    internal Font Body(float size, FontStyle style = FontStyle.Regular) => new(
        !IsLegacy && !style.HasFlag(FontStyle.Bold) ? "Segoe UI Semibold" : "Segoe UI", size, style, GraphicsUnit.Pixel);
    internal Font Countdown(float size) => new(TimerStripTypeface.Family, size, TimerStripTypeface.Style, GraphicsUnit.Pixel);

    internal void Panel(Graphics graphics, Rectangle bounds, double surfaceOpacity = 1)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        surfaceOpacity = double.IsFinite(surfaceOpacity) ? Math.Clamp(surfaceOpacity, 0, 1) : 1;
        var surface = Color.FromArgb((int)Math.Round(Surface.A * surfaceOpacity), Surface);
        if (IsLegacy) { LegacyPanel(graphics, bounds, surface); return; }
        // Small map labels and lock surfaces must not let ornaments paint outside their measured geometry.
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            if (Math.Min(bounds.Width, bounds.Height) < 9)
            {
                using var fill = new SolidBrush(surface);
                using var edge = new Pen(Border);
                graphics.FillRectangle(fill, bounds);
                graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
                return;
            }
            switch (_frame)
            {
                case FrameStyle.Neutral: NeutralPanel(graphics, bounds, surface); break;
                case FrameStyle.Stylized: StylizedPanel(graphics, bounds, surface); break;
                case FrameStyle.Obsidian: ObsidianPanel(graphics, bounds, surface); break;
                case FrameStyle.Aurora: AuroraPanel(graphics, bounds, surface); break;
                case FrameStyle.Runic: RunicPanel(graphics, bounds, surface); break;
            }
        }
        finally { graphics.Restore(state); }
    }

    private void NeutralPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        using var fill = new SolidBrush(surface);
        using var edge = new Pen(Color.FromArgb(110, 118, 124, 128));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f,
            Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
    }

    private void StylizedPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        var x = bounds.Left + .5f;
        var y = bounds.Top + .5f;
        var right = bounds.Right - .5f;
        var bottom = bounds.Bottom - .5f;
        var cut = Math.Min(7f, Math.Min(bounds.Width, bounds.Height) / 5f);
        PointF[] outline = [new(x + cut, y), new(right - cut, y), new(right, y + cut),
            new(right, bottom - cut), new(right - cut, bottom), new(x + cut, bottom),
            new(x, bottom - cut), new(x, y + cut)];
        using var fill = new SolidBrush(surface);
        using var edge = new Pen(Color.FromArgb(165, 167, 157, 133));
        graphics.FillPolygon(fill, outline);
        graphics.DrawPolygon(edge, outline);
        using var accent = new Pen(Color.FromArgb(215, 205, 188, 141));
        using var inner = new Pen(Color.FromArgb(80, 186, 178, 157));
        var shoulder = Math.Min(19f, Math.Min(bounds.Width, bounds.Height) / 3f);
        foreach (var (cx, cy, dx, dy) in Corners(x, y, right, bottom))
        {
            graphics.DrawLines(accent, new PointF[] { new(cx + shoulder * dx, cy), new(cx + cut * dx, cy),
                new(cx, cy + cut * dy), new(cx, cy + shoulder * dy) });
            graphics.DrawLines(inner, new PointF[] { new(cx + (shoulder - 3) * dx, cy + 3 * dy),
                new(cx + (cut + 2) * dx, cy + 3 * dy), new(cx + 3 * dx, cy + (cut + 2) * dy),
                new(cx + 3 * dx, cy + (shoulder - 3) * dy) });
        }
    }

    private void ObsidianPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        using var fill = new SolidBrush(surface);
        using var edge = new Pen(Border);
        using var top = new Pen(Color.FromArgb(215, Primary), 2);
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        var inset = Math.Min(10, bounds.Width / 4);
        graphics.DrawLine(top, bounds.Left + inset, bounds.Top + 1, bounds.Right - inset, bounds.Top + 1);
    }

    private void AuroraPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        var rect = new RectangleF(bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        using var path = RoundedFrame(rect, Math.Min(6, Math.Min(bounds.Width, bounds.Height) / 4f));
        using var fill = new LinearGradientBrush(bounds, Color.FromArgb(surface.A, 17, 44, 61), surface, LinearGradientMode.Vertical);
        using var edge = new Pen(Border);
        using var rail = new Pen(Color.FromArgb(220, Mint), 2);
        graphics.FillPath(fill, path);
        graphics.DrawPath(edge, path);
        var inset = Math.Min(12, bounds.Height / 4);
        graphics.DrawLine(rail, bounds.Left + 2, bounds.Top + inset, bounds.Left + 2, bounds.Bottom - inset);
        graphics.DrawLine(rail, bounds.Right - 2, bounds.Top + inset, bounds.Right - 2, bounds.Bottom - inset);
    }

    private void RunicPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        using var fill = new LinearGradientBrush(bounds, Color.FromArgb(surface.A, 48, 34, 25), surface, LinearGradientMode.Vertical);
        using var edge = new Pen(Border);
        using var inner = new Pen(Color.FromArgb(55, Gold));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        if (bounds.Width > 8 && bounds.Height > 8)
            graphics.DrawRectangle(inner, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.Width - 7, bounds.Height - 7);
        using var ornament = new Pen(Color.FromArgb(190, Gold));
        var length = Math.Min(18f, Math.Min(bounds.Width, bounds.Height) / 3f);
        foreach (var (x, y, dx, dy) in Corners(bounds.Left + 1, bounds.Top + 1, bounds.Right - 2, bounds.Bottom - 2))
        {
            graphics.DrawLine(ornament, x + length * dx, y + 1 * dy, x + 5 * dx, y + 1 * dy);
            graphics.DrawLine(ornament, x + 1 * dx, y + 5 * dy, x + 1 * dx, y + length * dy);
            Diamond(graphics, new PointF(x + 3 * dx, y + 3 * dy), 2, Gold);
        }
    }

    // Preserve the pre-existing Legacy drawing path and palette.
    private void LegacyPanel(Graphics graphics, Rectangle bounds, Color surface)
    {
        using var fill = new SolidBrush(surface);
        using var edge = new Pen(Border);
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        using var accent = new Pen(Color.FromArgb(205, Gold), 1);
        foreach (var (x, y, dx, dy) in new[] { (bounds.Left + 1, bounds.Top + 1, 1, 1),
            (bounds.Right - 2, bounds.Top + 1, -1, 1), (bounds.Left + 1, bounds.Bottom - 2, 1, -1),
            (bounds.Right - 2, bounds.Bottom - 2, -1, -1) })
        {
            graphics.DrawLine(accent, x, y, x + 6 * dx, y);
            graphics.DrawLine(accent, x, y, x, y + 6 * dy);
        }
    }

    private static (float X, float Y, float Dx, float Dy)[] Corners(float x, float y, float right, float bottom) =>
        [(x, y, 1, 1), (right, y, -1, 1), (x, bottom, 1, -1), (right, bottom, -1, -1)];

    private static GraphicsPath RoundedFrame(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(.1f, radius * 2);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal void Diamond(Graphics graphics, PointF center, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, new PointF[] { new(center.X, center.Y - radius), new(center.X + radius, center.Y),
            new(center.X, center.Y + radius), new(center.X - radius, center.Y) });
    }
}
