using System.Drawing.Drawing2D;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

internal sealed record TimerStripCell(AlertDisplayRow Row, Rectangle Bounds, Rectangle IconBounds, Rectangle TextBounds,
    Rectangle? LabelBounds = null);
internal sealed record TimerStripLayout(Size Size, IReadOnlyList<TimerStripCell> Cells);

/// <summary>Physical-pixel currency-style strip; the caller applies the user scale exactly once.</summary>
internal static class TimerStripRenderer
{
    internal const int Height = 32, IconSize = 28, TextWidth = 90, Padding = 8;
    private const int CellWidth = IconSize + 4 + TextWidth, InlineLabelWidth = 198, InlineLabelGap = 6;
    private static readonly TimerIconCache Icons = new();
    private static readonly Color Ink = Color.FromArgb(237, 237, 230);

    internal static TimerStripLayout Layout(IReadOnlyList<AlertDisplayRow> rows, int gap = 18, int maximumWidth = int.MaxValue)
    {
        gap = Math.Clamp(gap, 4, 48);
        var ordered = rows.OrderBy(row => TimerOverlayPresentation.Order(row.Key)).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
        var available = (int)Math.Clamp((long)maximumWidth - Padding * 2, CellWidth, int.MaxValue);
        List<TimerStripCell> cells = [];
        var x = Padding; var y = 0; var right = 0;
        foreach (var row in ordered)
        {
            var labelWidth = string.IsNullOrWhiteSpace(row.InlineLabel) ? 0
                : Math.Min(InlineLabelWidth, Math.Max(0, available - CellWidth - InlineLabelGap));
            if (labelWidth < 3) labelWidth = 0;
            var extra = labelWidth > 0 ? labelWidth + InlineLabelGap : 0;
            var width = CellWidth + extra;
            if (x > Padding && (long)x + width + Padding > maximumWidth)
            {
                x = Padding;
                y += Height;
            }
            cells.Add(new(row, new(x, y, width, Height), new(x, y + 2, IconSize, IconSize),
                new(x + IconSize + 4 + extra, y, TextWidth, Height),
                labelWidth > 0 ? new Rectangle(x + IconSize + 4, y, labelWidth, Height) : null));
            right = Math.Max(right, x + width);
            x += width + gap;
        }
        return new(new(ordered.Length == 0 ? 1 : right + Padding, y + Height), cells);
    }

    internal static void Paint(Graphics graphics, TimerStripLayout layout, DateTimeOffset now,
        bool seconds = true, bool locked = true, Func<string, Image?>? icon = null)
    {
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(Point.Empty, layout.Size), CombineMode.Intersect);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // User request 2026-10-18: icons and text only; the former surface is removed.
        foreach (var cell in layout.Cells)
        {
            var id = IconId(cell.Row);
            var image = icon?.Invoke(id) ?? Icons.Load(id);
            if (image is not null) DrawIcon(graphics, image, cell.IconBounds, id.StartsWith("map:", StringComparison.Ordinal));
            var ink = cell.Row.Provisional ? Color.FromArgb(171, 180, 184)
                : cell.Row.Started ? Color.FromArgb(244, 215, 142) : Ink;
            if (cell.LabelBounds is { } labelBounds && cell.Row.InlineLabel is { } label)
                DrawText(graphics, label, labelBounds, ink);
            DrawText(graphics, TimerOverlayPresentation.Countdown(cell.Row, now, seconds), cell.TextBounds, ink);
            // A steady small gold tick distinguishes an active event without introducing a badge.
            if (cell.Row.Started && !cell.Row.Provisional)
            {
                using var active = new SolidBrush(Color.FromArgb(231, 202, 131));
                graphics.FillEllipse(active, cell.IconBounds.Right - 4, cell.IconBounds.Bottom - 4, 3, 3);
            }
        }
        if (!locked)
        {
            using var grip = new Pen(Color.FromArgb(150, 224, 214, 189));
            var width = layout.Size.Width; var height = layout.Size.Height;
            // A filled edit-only corner remains hittable even with the normal surface disabled.
            using var handle = new SolidBrush(Color.FromArgb(180, 43, 54, 64));
            graphics.FillPolygon(handle, new Point[] { new(width - 14, height - 1),
                new(width - 1, height - 14), new(width - 1, height - 1) });
            graphics.DrawLine(grip, width - 10, height - 3, width - 3, height - 10);
            graphics.DrawLine(grip, width - 6, height - 3, width - 3, height - 6);
        }
        graphics.Restore(state);
    }

    private static void DrawText(Graphics graphics, string text, Rectangle bounds, Color color)
    {
        var family = TimerStripTypeface.Family;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)TimerStripTypeface.Style, TimerStripTypeface.Size, bounds, format);
        var inkBounds = path.GetBounds();
        if (inkBounds.Width > bounds.Width - 2)
        {
            var factor = (bounds.Width - 2) / inkBounds.Width;
            using var fit = new Matrix();
            fit.Translate(-bounds.Left, 0, MatrixOrder.Append);
            fit.Scale(factor, 1, MatrixOrder.Append);
            fit.Translate(bounds.Left, 0, MatrixOrder.Append);
            path.Transform(fit);
        }
        using (var cast = (GraphicsPath)path.Clone())
        using (var shift = new Matrix())
        using (var shade = new SolidBrush(Color.FromArgb(170, 8, 12, 16)))
        {
            shift.Translate(0, 1);
            cast.Transform(shift);
            graphics.FillPath(shade, cast);
        }
        using var shadow = new Pen(Color.FromArgb(240, 17, 23, 28), 1.6f) { LineJoin = LineJoin.Round };
        graphics.DrawPath(shadow, path);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    internal static string IconFile(string key) => TimerOverlayIcons.BundledFile(key);

    private static string IconId(AlertDisplayRow row) => row.IconId ?? (row.Key.StartsWith("event:catalog:", StringComparison.Ordinal)
        ? row.Key["event:catalog:".Length..] : row.Key.StartsWith("boss:", StringComparison.Ordinal) ? "boss" : "custom");

    private static void DrawIcon(Graphics graphics, Image image, Rectangle bounds, bool portrait)
    {
        if (!portrait) { graphics.DrawImage(image, bounds); return; }
        var state = graphics.Save();
        var inset = RectangleF.Inflate(bounds, -1.5f, -1.5f);
        using var circle = new GraphicsPath();
        circle.AddEllipse(inset);
        graphics.SetClip(circle, CombineMode.Intersect);
        graphics.DrawImage(image, inset);
        graphics.Restore(state);
        using var shadow = new Pen(Color.FromArgb(210, 17, 23, 28), 2);
        using var gold = new Pen(Color.FromArgb(210, 211, 194, 151), .8f);
        graphics.DrawPath(shadow, circle);
        graphics.DrawPath(gold, circle);
    }
}
