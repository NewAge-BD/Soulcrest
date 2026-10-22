using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Soulcrest.App.Overlay;

/// <summary>Fixed game-style content toggle, shared by the live mouse surface and offline previews.</summary>
public static class DaevanionToggleRenderer
{
    public const int LogicalWidth = 112;
    public const int LogicalHeight = 28;

    public static Bitmap Render(Size size, bool contentVisible, string language, bool hover = false, bool pressed = false)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 4096 || size.Height > 4096)
            throw new ArgumentOutOfRangeException(nameof(size));
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            Paint(graphics, new Rectangle(Point.Empty, size), contentVisible, language, hover, pressed);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static void Paint(Graphics graphics, Rectangle bounds, bool contentVisible, string language,
        bool hover = false, bool pressed = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var saved = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / (float)LogicalWidth, bounds.Height / (float)LogicalHeight);
            var top = pressed ? Color.FromArgb(52, 57, 55) : hover ? Color.FromArgb(75, 80, 76) : Color.FromArgb(52, 62, 68);
            var bottom = pressed ? Color.FromArgb(20, 25, 27) : Color.FromArgb(20, 28, 33);
            var edge = contentVisible ? Color.FromArgb(211, 190, 137) : Color.FromArgb(143, 155, 162);
            var ink = hover ? Color.FromArgb(255, 249, 224) : contentVisible ? Color.FromArgb(248, 237, 202) : Color.FromArgb(215, 222, 223);
            using var outline = Shape(new(.5f, .5f, LogicalWidth - 1, LogicalHeight - 1));
            using var fill = new LinearGradientBrush(new Rectangle(0, 0, LogicalWidth, LogicalHeight), top, bottom, 90);
            using var border = new Pen(edge, .85f);
            graphics.FillPath(fill, outline); graphics.DrawPath(border, outline);
            using var inset = Shape(new(2.5f, 2.5f, LogicalWidth - 5, LogicalHeight - 5));
            using var insetPen = new Pen(Color.FromArgb(100, edge), .65f);
            graphics.DrawPath(insetPen, inset);
            using var icon = new Pen(ink, 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var eye = new GraphicsPath();
            eye.AddBezier(10, 14, 15, 7, 22, 7, 28, 14);
            eye.AddBezier(28, 14, 22, 21, 15, 21, 10, 14);
            graphics.DrawPath(icon, eye);
            graphics.DrawEllipse(icon, 16.5f, 11.5f, 5, 5);
            if (!contentVisible) graphics.DrawLine(icon, 10, 21, 28, 7);
            var text = language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? contentVisible ? "Overlay an" : "Overlay aus"
                : contentVisible ? "Overlay on" : "Overlay off";
            Text(graphics, text, new(34, 4, 71, 20), ink);
        }
        finally { graphics.Restore(saved); }
    }

    internal static GraphicsPath Shape(RectangleF bounds)
    {
        var bevel = Math.Min(bounds.Width / LogicalWidth, bounds.Height / LogicalHeight) * 4;
        var path = new GraphicsPath();
        path.AddPolygon([
            new PointF(bounds.Left + bevel, bounds.Top), new PointF(bounds.Right - bevel, bounds.Top),
            new PointF(bounds.Right, bounds.Top + bevel), new PointF(bounds.Right, bounds.Bottom - bevel),
            new PointF(bounds.Right - bevel, bounds.Bottom), new PointF(bounds.Left + bevel, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom - bevel), new PointF(bounds.Left, bounds.Top + bevel)]);
        return path;
    }

    private static void Text(Graphics graphics, string text, RectangleF area, Color ink)
    {
        using var family = new FontFamily("Palatino Linotype");
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 12, PointF.Empty, format);
        var bounds = path.GetBounds();
        var factor = Math.Min(1, Math.Min(area.Width / bounds.Width, area.Height / bounds.Height));
        using var move = new Matrix(factor, 0, 0, factor,
            area.Left + (area.Width - bounds.Width * factor) / 2 - bounds.Left * factor,
            area.Top + (area.Height - bounds.Height * factor) / 2 - bounds.Top * factor);
        path.Transform(move);
        using var shadow = new Pen(Color.FromArgb(240, 5, 8, 9), .9f) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(ink);
        graphics.DrawPath(shadow, path); graphics.FillPath(fill, path);
    }
}
