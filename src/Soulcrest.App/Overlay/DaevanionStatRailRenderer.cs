using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Soulcrest.App.Overlay;

/// <summary>Fixed slate/gold rune controls, with alpha-clear spaces between the actual buttons.</summary>
public static class DaevanionStatRailRenderer
{
    public static Bitmap Render(DaevanionStatRailLayout layout, string? hoverId = null, string? pressedId = null)
    {
        if (!layout.HasSpace || layout.Bounds.Width > 4096 || layout.Bounds.Height > 16384)
            throw new ArgumentOutOfRangeException(nameof(layout));
        var image = new Bitmap(layout.Bounds.Width, layout.Bounds.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image); graphics.Clear(Color.Transparent);
            Paint(graphics, layout with { Bounds = new(Point.Empty, layout.Bounds.Size) }, hoverId, pressedId);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static void Paint(Graphics graphics, DaevanionStatRailLayout layout, string? hoverId = null, string? pressedId = null)
    {
        if (!layout.HasSpace) return;
        var saved = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SetClip(layout.Bounds, CombineMode.Intersect);
            graphics.TranslateTransform(layout.Bounds.X, layout.Bounds.Y);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var item in layout.Items) PaintItem(graphics, item, item.Id == hoverId, item.Id == pressedId);
        }
        finally { graphics.Restore(saved); }
    }

    internal static GraphicsPath Shape(RectangleF bounds)
    {
        var bevel = Math.Min(bounds.Height / 7, bounds.Width / 12);
        var path = new GraphicsPath();
        path.AddPolygon([
            new PointF(bounds.Left + bevel, bounds.Top), new PointF(bounds.Right - bevel, bounds.Top),
            new PointF(bounds.Right, bounds.Top + bevel), new PointF(bounds.Right, bounds.Bottom - bevel),
            new PointF(bounds.Right - bevel, bounds.Bottom), new PointF(bounds.Left + bevel, bounds.Bottom),
            new PointF(bounds.Left, bounds.Bottom - bevel), new PointF(bounds.Left, bounds.Top + bevel)]);
        return path;
    }

    private static void PaintItem(Graphics graphics, DaevanionStatRailItem item, bool hover, bool pressed)
    {
        var saved = graphics.Save();
        try
        {
            var scale = item.Bounds.Height / 28f;
            graphics.TranslateTransform(item.Bounds.X, item.Bounds.Y);
            graphics.ScaleTransform(scale, scale);
            var width = item.Bounds.Width / scale;
            hover &= item.Enabled; pressed &= item.Enabled;
            var top = pressed ? Color.FromArgb(52, 57, 55) : hover ? Color.FromArgb(75, 80, 76) : Color.FromArgb(52, 62, 68);
            var bottom = Color.FromArgb(20, 28, 33);
            var edge = item.Selected ? Color.FromArgb(238, 221, 170) : Color.FromArgb(143, 155, 162);
            var ink = item.Selected ? Color.FromArgb(255, 248, 219) : item.Enabled ? Color.FromArgb(226, 229, 222) : Color.FromArgb(130, 139, 141);
            using var shape = Shape(new(.5f, .5f, width - 1, 27));
            using var fill = new LinearGradientBrush(new RectangleF(0, 0, width, 28), top, bottom, 90);
            using var border = new Pen(edge, item.Selected ? 1.15f : .8f);
            graphics.FillPath(fill, shape); graphics.DrawPath(border, shape);
            using var inset = Shape(new(2.5f, 2.5f, width - 5, 23));
            using var insetPen = new Pen(Color.FromArgb(90, edge), .65f); graphics.DrawPath(insetPen, inset);
            var labelArea = new RectangleF(8, 4, width - 16, 20);
            if (item.Kind == DaevanionStatRailItemKind.Stat)
            {
                using var mark = new Pen(ink, 1.1f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawRectangle(mark, 10, 9, 9, 9);
                if (item.Selected) graphics.DrawLines(mark, [new(11, 13), new(14, 16), new(20, 9)]);
                labelArea = new(28, 4, width - 36, 20);
            }
            Text(graphics, item.Label, labelArea, ink, item.Kind is DaevanionStatRailItemKind.Previous or DaevanionStatRailItemKind.Next ? 19 : 12);
        }
        finally { graphics.Restore(saved); }
    }

    private static void Text(Graphics graphics, string text, RectangleF area, Color ink, float emSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var family = new FontFamily("Palatino Linotype");
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var path = new GraphicsPath(); path.AddString(text, family, (int)FontStyle.Bold, emSize, PointF.Empty, format);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var factor = Math.Min(1, Math.Min(area.Width / bounds.Width, area.Height / bounds.Height));
        using var move = new Matrix(factor, 0, 0, factor,
            area.Left + (area.Width - bounds.Width * factor) / 2 - bounds.Left * factor,
            area.Top + (area.Height - bounds.Height * factor) / 2 - bounds.Top * factor);
        path.Transform(move);
        using var shadow = new Pen(Color.FromArgb(240, 5, 8, 9), .9f) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(ink); graphics.DrawPath(shadow, path); graphics.FillPath(fill, path);
    }
}
