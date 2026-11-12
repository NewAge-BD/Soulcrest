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
            var ink = hover ? Color.FromArgb(255, 249, 224) : contentVisible ? Color.FromArgb(252, 240, 207) : Color.FromArgb(187, 184, 165);
            using var outline = Shape(new(.5f, .5f, LogicalWidth - 1, LogicalHeight - 1));
            PaintNativeButton(graphics, new(0, 0, LogicalWidth, LogicalHeight), outline, contentVisible, true, hover, pressed);
            PaintNativeCheckbox(graphics, new(6, 3, 22, 22), contentVisible);
            var text = language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? contentVisible ? "Overlay an" : "Overlay aus"
                : contentVisible ? "Overlay on" : "Overlay off";
            Text(graphics, text, new(30, 4, 77, 20), ink);
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

    // User-selected native engraving design (2026-11-12). These are the original
    // button/checkbox images, shared with the stat rail and never mutated per state.
    internal static void PaintNativeButton(Graphics graphics, RectangleF bounds, GraphicsPath outline,
        bool selected, bool enabled, bool hover, bool pressed)
    {
        var saved = graphics.Save();
        try
        {
            graphics.SetClip(outline, CombineMode.Intersect);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            DaevanionNativeArtwork.Button.Paint(graphics, bounds);
            var wash = !enabled ? Color.FromArgb(95, 8, 13, 17)
                : pressed ? Color.FromArgb(110, 7, 12, 16)
                : hover ? Color.FromArgb(42, 250, 237, 204)
                : selected ? Color.FromArgb(15, 235, 216, 165) : Color.Transparent;
            if (wash.A > 0)
            {
                using var brush = new SolidBrush(wash);
                graphics.FillPath(brush, outline);
            }
            if (enabled && (selected || hover || pressed))
            {
                using var border = new Pen(Color.FromArgb(hover ? 190 : 110, 221, 203, 154), .8f);
                graphics.DrawPath(border, outline);
            }
        }
        finally { graphics.Restore(saved); }
    }

    internal static void PaintNativeCheckbox(Graphics graphics, RectangleF bounds, bool selected)
    {
        var saved = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            (selected ? DaevanionNativeArtwork.CheckboxOn : DaevanionNativeArtwork.CheckboxOff).Paint(graphics, bounds);
        }
        finally { graphics.Restore(saved); }
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
