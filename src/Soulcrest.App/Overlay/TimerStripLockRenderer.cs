using System.Drawing.Drawing2D;

namespace Soulcrest.App.Overlay;

/// <summary>A currency-strip lock glyph. Bounds are physical pixels; bitmap DPI never adds another scale.</summary>
internal static class TimerStripLockRenderer
{
    private const float LogicalSize = 18;

    internal static void Paint(Graphics graphics, Rectangle bounds, bool locked, bool hover = false, bool pressed = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var size = Math.Min(bounds.Width, bounds.Height);
            graphics.TranslateTransform(bounds.Left + (bounds.Width - size) / 2f,
                bounds.Top + (bounds.Height - size) / 2f);
            graphics.ScaleTransform(size / LogicalSize, size / LogicalSize);
            var upper = pressed ? Color.FromArgb(227, 210, 163)
                : hover ? Color.FromArgb(255, 251, 230) : Color.FromArgb(248, 237, 202);
            var lower = pressed ? Color.FromArgb(174, 144, 89)
                : hover ? Color.FromArgb(237, 214, 156) : Color.FromArgb(204, 177, 112);
            var rim = Color.FromArgb(250, 20, 24, 29);
            using var shackle = new GraphicsPath();
            if (locked)
            {
                shackle.AddLine(6, 8.5f, 6, 6.3f);
                shackle.AddArc(6, 1.9f, 6, 8.8f, 180, 180);
                shackle.AddLine(12, 6.3f, 12, 8.5f);
            }
            else
            {
                shackle.AddLine(8, 8.5f, 8, 5.8f);
                shackle.AddArc(8, 1.9f, 6, 7.8f, 180, 180);
                shackle.AddLine(14, 5.8f, 14, 6.5f);
            }
            using var body = Body();
            using (var shadow = new Pen(Color.FromArgb(150, 5, 8, 12), 3.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                graphics.TranslateTransform(0, .6f);
                graphics.DrawPath(shadow, shackle);
                graphics.DrawPath(shadow, body);
                graphics.TranslateTransform(0, -.6f);
            }
            using (var outline = new Pen(rim, 3.1f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                graphics.DrawPath(outline, shackle);
            using (var ivory = new Pen(upper, 1.5f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                graphics.DrawPath(ivory, shackle);
            using (var metal = new LinearGradientBrush(new RectangleF(4, 8, 10, 8.5f), upper, lower, 90))
                graphics.FillPath(metal, body);
            using (var outline = new Pen(rim, 1.25f) { LineJoin = LineJoin.Round })
                graphics.DrawPath(outline, body);
            using (var highlight = new Pen(Color.FromArgb(185, 255, 249, 222), .65f))
                graphics.DrawLine(highlight, 5.7f, 9.25f, 12.3f, 9.25f);
            using (var keyhole = new SolidBrush(rim))
            {
                graphics.FillEllipse(keyhole, 8.25f, 10.7f, 1.5f, 1.5f);
                graphics.FillRectangle(keyhole, 8.6f, 11.7f, .8f, 2);
            }
        }
        finally { graphics.Restore(state); }
    }

    private static GraphicsPath Body()
    {
        var path = new GraphicsPath();
        path.AddArc(4.25f, 8.25f, 2.5f, 2.5f, 180, 90);
        path.AddArc(11.25f, 8.25f, 2.5f, 2.5f, 270, 90);
        path.AddArc(11.25f, 13.5f, 2.5f, 2.5f, 0, 90);
        path.AddArc(4.25f, 13.5f, 2.5f, 2.5f, 90, 90);
        path.CloseFigure();
        return path;
    }
}
