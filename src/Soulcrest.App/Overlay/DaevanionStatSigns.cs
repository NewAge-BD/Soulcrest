using System.Drawing;
using System.Drawing.Drawing2D;

namespace Soulcrest.App.Overlay;

/// <summary>Compact vector stat signs used by the selected fitting, not original game icon assets.</summary>
internal static class DaevanionStatSigns
{
    public static void Draw(Graphics graphics, string statId, RectangleF box, Color ink)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentException.ThrowIfNullOrWhiteSpace(statId);
        if (box.Width <= 0 || box.Height <= 0) return;

        using var path = new GraphicsPath();
        switch (statId)
        {
            case "PvEAddDamage":
            case "PvPAddDamage":
            case "BossNpcAddDamage":
                Sword(path);
                break;
            case "PvEDamageDefense":
            case "PvPDamageDefense":
            case "BossNpcDefense":
                Shield(path);
                break;
            case "PvEAmplifyDamage":
            case "PvPAmplifyDamage":
            case "BossNpcAmplifyDamage":
                Sword(path);
                Figure(path, [new(3, 3), new(3, 8)]);
                Figure(path, [new(.5f, 5.5f), new(5.5f, 5.5f)]);
                break;
            case "PvEDecreaseDamage":
            case "PvPDecreaseDamage":
            case "BossNpcDecreaseDamage":
                Shield(path);
                Figure(path, [new(7, 9), new(10, 12), new(13, 9)]);
                break;
            case "PvEAccuracy":
            case "PvPAccuracy":
                path.AddEllipse(4, 4, 12, 12);
                Figure(path, [new(10, 1), new(10, 7)]);
                Figure(path, [new(10, 13), new(10, 19)]);
                Figure(path, [new(1, 10), new(7, 10)]);
                Figure(path, [new(13, 10), new(19, 10)]);
                break;
            case "PvEEvasion":
            case "PvPEvasion":
                // A swept wing has a different silhouette from a crosshair even at 10px.
                Figure(path, [new(3, 16), new(7, 8), new(17, 3), new(12, 10), new(17, 8)]);
                Figure(path, [new(5, 13), new(13, 10)]);
                Figure(path, [new(6, 10), new(14, 7)]);
                break;
            case "Critical":
            case "PvPCritical":
                Diamond(path, 10, 10, 4);
                Figure(path, [new(10, 1), new(10, 4)]);
                Figure(path, [new(10, 16), new(10, 19)]);
                Figure(path, [new(1, 10), new(4, 10)]);
                Figure(path, [new(16, 10), new(19, 10)]);
                break;
            case "CriticalResist":
            case "PvPCriticalResist":
                Shield(path);
                Diamond(path, 10, 9, 3);
                break;
            case "AbnormalAccuracy":
                path.AddEllipse(2, 2, 16, 16);
                StatusStar(path, 10, 10, 4.5f);
                break;
            case "AbnormalResistance":
                Shield(path);
                StatusStar(path, 10, 9, 3.4f);
                break;
            case "HPMax":
                Heart(path);
                break;
            case "MPMax":
                Drop(path);
                break;
            default:
                Diamond(path, 10, 10, 7);
                break;
        }
        Paint(graphics, path, box, ink);
    }

    public static void Crown(Graphics graphics, RectangleF box, Color ink)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (box.Width <= 0 || box.Height <= 0) return;
        using var path = new GraphicsPath();
        // This context mark is intentionally separate, so small stat signs do not lose their shape.
        Figure(path, [new(3, 15), new(2, 6), new(7, 10), new(10, 3), new(13, 10), new(18, 6), new(17, 15)], true);
        Figure(path, [new(4, 18), new(16, 18)]);
        Paint(graphics, path, box, ink);
    }

    private static void Paint(Graphics graphics, GraphicsPath path, RectangleF box, Color ink)
    {
        var state = graphics.Save();
        try
        {
            var scale = Math.Min(box.Width, box.Height) / 20f;
            graphics.TranslateTransform(box.X + (box.Width - scale * 20) / 2,
                box.Y + (box.Height - scale * 20) / 2);
            graphics.ScaleTransform(scale, scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var shadow = new Pen(Color.FromArgb(240, 4, 8, 9), 3.5f)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var line = new Pen(ink, 1.9f)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawPath(shadow, path);
            graphics.DrawPath(line, path);
        }
        finally { graphics.Restore(state); }
    }

    private static void Sword(GraphicsPath path)
    {
        Figure(path, [new(6, 13), new(13, 3), new(18, 2), new(17, 7), new(8, 15)]);
        Figure(path, [new(3, 11), new(10, 18)]);
        Figure(path, [new(6, 14), new(2, 18)]);
    }

    private static void Shield(GraphicsPath path)
    {
        path.StartFigure();
        path.AddLines([new PointF(3, 4), new(10, 2), new(17, 4), new(16, 11)]);
        path.AddBezier(16, 11, 15, 14, 13, 17, 10, 19);
        path.AddBezier(10, 19, 7, 17, 5, 14, 4, 11);
        path.AddLine(4, 11, 3, 4);
        path.CloseFigure();
    }

    private static void Diamond(GraphicsPath path, float x, float y, float radius)
        => Figure(path, [new(x, y - radius), new(x + radius, y), new(x, y + radius), new(x - radius, y)], true);

    private static void StatusStar(GraphicsPath path, float x, float y, float radius)
    {
        var points = new PointF[12];
        for (var index = 0; index < points.Length; index++)
        {
            var angle = -Math.PI / 2 + index * Math.PI / 6;
            var length = radius * (index % 2 == 0 ? 1 : .38f);
            points[index] = new(x + (float)Math.Cos(angle) * length, y + (float)Math.Sin(angle) * length);
        }
        Figure(path, points, true);
    }

    private static void Heart(GraphicsPath path)
    {
        path.StartFigure();
        path.AddBezier(10, 18, 8, 16, 2, 11, 2, 7);
        path.AddBezier(2, 7, 2, 1, 8, 1, 10, 6);
        path.AddBezier(10, 6, 12, 1, 18, 1, 18, 7);
        path.AddBezier(18, 7, 18, 11, 12, 16, 10, 18);
        path.CloseFigure();
    }

    private static void Drop(GraphicsPath path)
    {
        path.StartFigure();
        path.AddBezier(10, 1, 8, 5, 3, 9, 3, 13);
        path.AddBezier(3, 13, 3, 21, 17, 21, 17, 13);
        path.AddBezier(17, 13, 17, 9, 12, 5, 10, 1);
        path.CloseFigure();
        Figure(path, [new(6, 13), new(7, 16), new(10, 17)]);
    }

    private static void Figure(GraphicsPath path, PointF[] points, bool closed = false)
    {
        path.StartFigure();
        path.AddLines(points);
        if (closed) path.CloseFigure();
    }
}
