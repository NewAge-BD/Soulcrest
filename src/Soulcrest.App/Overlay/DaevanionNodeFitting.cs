using System.Drawing.Drawing2D;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>The selected outer fitting; node quality colors the material, never its content.</summary>
internal static class DaevanionNodeFitting
{
    private static readonly Color Ink = Color.FromArgb(255, 253, 240, 196);
    private static readonly Color Gold = Color.FromArgb(255, 207, 184, 126);
    private readonly record struct Palette(Color Top, Color Bottom, Color Edge, Color Highlight);

    // Includes antialiased fringes; extensions share the measured gap without changing native frames.
    public static RectangleF Bounds(RectangleF frame, double? spacing = null) =>
        Layout(frame, spacing).Box(-.09f, -.06f, 1.18f, 1.24f);

    public static bool CanExtend(RectangleF frame, double? spacing = null)
    {
        var layout = Layout(frame, spacing);
        return frame.Width > 0 && frame.Height > 0 && layout.HorizontalScale > 0
            && layout.Box(0, 1.010f, 1, .120f).Height >= 5;
    }

    public static void PaintOutside(Graphics graphics, DaevanionNode node, RectangleF frame, string language, double? spacing = null)
    {
        if (node.Kind != DaevanionNodeKind.Stat || node.Effects.Count == 0 || !CanExtend(frame, spacing)) return;
        var state = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var palette = MaterialFor(node.Quality);
            var layout = Layout(frame, spacing);
            using var fitting = new GraphicsPath();
            fitting.AddPolygon([
                layout.Point(.075f, -.030f), layout.Point(.925f, -.030f),
                layout.Point(1.068f, .07f), layout.Point(1.068f, .925f),
                layout.Point(.94f, 1.16f), layout.Point(.06f, 1.16f),
                layout.Point(-.068f, .925f), layout.Point(-.068f, .07f)]);
            var fringeScale = Math.Min(layout.HorizontalScale, layout.VerticalScale);
            Material(graphics, fitting, layout.Box(-.068f, -.030f, 1.136f, 1.190f), palette, frame.Width / 60, fringeScale);
            using var bright = new Pen(palette.Highlight, Math.Max(.65f, frame.Width * .011f) * fringeScale);
            foreach (var x in new[] { .16f, .84f })
            {
                var rivet = layout.Box(x - .008f, -.022f, .016f, .016f);
                graphics.DrawEllipse(bright, rivet);
            }
            for (var index = 0; index < node.Effects.Count; index++)
            {
                var width = .9f / node.Effects.Count;
                Text(graphics, Label(node.Effects[index], language), layout.Box(.05f + index * width, 1.010f, width, .120f),
                    frame.Width * .165f, Ink);
            }
            if (node.Effects.Count == 2)
                graphics.DrawLine(bright, layout.Point(.5f, 1.02f), layout.Point(.5f, 1.128f));
        }
        finally { graphics.Restore(state); }
    }

    public static void PaintCenter(Graphics graphics, DaevanionNode node, Rectangle inside, string language)
    {
        if (node.Kind != DaevanionNodeKind.Stat || node.Effects.Count == 0) return;
        var state = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.SetClip(inside, CombineMode.Intersect);
            var frame = new RectangleF(inside.X, inside.Y, inside.Width, inside.Height);
            var context = Context(node);
            if (context.Length > 0)
                Text(graphics, context, Box(frame, .2f, .13f, .6f, .20f), inside.Height * .23f, Gold);
            else
                DaevanionStatSigns.Draw(graphics, node.Effects[0].StatId!, Box(frame, .39f, .12f, .22f, .22f), Gold);
            if (node.Effects.Count == 2)
            {
                using var divider = new Pen(Color.FromArgb(170, 174, 153, 99), Math.Max(.6f, inside.Width * .014f));
                graphics.DrawLine(divider, Point(frame, .5f, .38f), Point(frame, .5f, .78f));
                for (var index = 0; index < node.Effects.Count; index++)
                    Text(graphics, node.Effects[index].DisplayAmount.For(language),
                        Box(frame, .075f + index * .44f, .47f, .41f, .28f), inside.Height * .33f, Ink);
            }
            else
                Text(graphics, node.Effects[0].DisplayAmount.For(language), Box(frame, .075f, .47f, .85f, .30f),
                    inside.Height * .40f, Ink);
        }
        finally { graphics.Restore(state); }
    }

    // User request 2026-10-21: slate for normal, green/blue/orange for the matching node qualities.
    private static Palette MaterialFor(DaevanionNodeQuality quality) => quality switch
    {
        DaevanionNodeQuality.Rare => new(Color.FromArgb(255, 39, 82, 62), Color.FromArgb(255, 18, 38, 29),
            Color.FromArgb(245, 91, 163, 113), Color.FromArgb(235, 158, 205, 159)),
        DaevanionNodeQuality.Legend => new(Color.FromArgb(255, 40, 68, 96), Color.FromArgb(255, 17, 29, 44),
            Color.FromArgb(245, 94, 154, 205), Color.FromArgb(235, 167, 205, 232)),
        DaevanionNodeQuality.Unique => new(Color.FromArgb(255, 120, 63, 25), Color.FromArgb(255, 49, 27, 16),
            Color.FromArgb(245, 225, 137, 62), Color.FromArgb(235, 248, 191, 118)),
        _ => new(Color.FromArgb(255, 70, 81, 91), Color.FromArgb(255, 23, 31, 39),
            Color.FromArgb(245, 137, 156, 173), Color.FromArgb(235, 184, 198, 207))
    };

    private static string Context(DaevanionNode node) =>
        node.Effects.Any(effect => effect.StatId?.StartsWith("BossNpc", StringComparison.Ordinal) == true) ? "Boss" :
        node.Effects.Any(effect => effect.StatId?.StartsWith("PvE", StringComparison.Ordinal) == true) ? "PvE" :
        node.Effects.Any(effect => effect.StatId?.StartsWith("PvP", StringComparison.Ordinal) == true) ? "PvP" :
        node.Effects.Any(effect => effect.StatId is "AbnormalAccuracy" or "AbnormalResistance") ? "Status" : "";

    private static string Label(DaevanionEffect effect, string language)
    {
        var german = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        if (effect.StatId == "AbnormalAccuracy") return german ? "Statuschance" : "Status chance";
        if (effect.StatId == "AbnormalResistance") return german ? "Status-RES" : "Status RES";
        var label = effect.ShortLabel.For(language);
        if (label.StartsWith("PvE ", StringComparison.Ordinal)) return label[4..];
        if (label.StartsWith("PvP ", StringComparison.Ordinal)) return label[4..];
        if (label.StartsWith("Boss ", StringComparison.Ordinal)) return label[5..];
        return label;
    }

    private static RectangleF Box(RectangleF frame, float x, float y, float width, float height) =>
        new(frame.X + frame.Width * x, frame.Y + frame.Height * y, frame.Width * width, frame.Height * height);

    private static PointF Point(RectangleF frame, float x, float y) =>
        new(frame.X + frame.Width * x, frame.Y + frame.Height * y);

    private readonly record struct FittingLayout(RectangleF Frame, float HorizontalScale, float VerticalScale)
    {
        private static float Outside(float value, float scale) => value < 0 ? value * scale : value > 1 ? 1 + (value - 1) * scale : value;
        public PointF Point(float x, float y) => new(Frame.X + Frame.Width * Outside(x, HorizontalScale),
            Frame.Y + Frame.Height * Outside(y, VerticalScale));
        public RectangleF Box(float x, float y, float width, float height)
        {
            var from = Point(x, y); var to = Point(x + width, y + height);
            return RectangleF.FromLTRB(from.X, from.Y, to.X, to.Y);
        }
    }

    private static FittingLayout Layout(RectangleF frame, double? spacing)
    {
        if (spacing is not { } pitch) return new(frame, 1, 1);
        if (!double.IsFinite(pitch) || pitch <= 0 || frame.Width <= 0 || frame.Height <= 0) return new(frame, 0, 0);
        var margin = Math.Max(1f, Math.Min(frame.Width, frame.Height) / 60f);
        var horizontal = (float)Math.Clamp((pitch - frame.Width - margin) / (frame.Width * .18), 0, 1);
        var vertical = (float)Math.Clamp((pitch - frame.Height - margin) / (frame.Height * .24), 0, 1);
        return new(frame, horizontal, vertical);
    }

    private static void Material(Graphics graphics, GraphicsPath shape, RectangleF bounds, Palette palette, float scale, float fringeScale)
    {
        using var fill = new LinearGradientBrush(bounds, palette.Top, palette.Bottom, 90);
        using var shadow = new Pen(Color.FromArgb(255, 9, 14, 14), Math.Max(.8f, scale * 1.5f) * fringeScale) { LineJoin = LineJoin.Round };
        using var edge = new Pen(palette.Edge, Math.Max(.65f, scale * .75f) * fringeScale) { LineJoin = LineJoin.Round };
        graphics.FillPath(fill, shape);
        graphics.DrawPath(shadow, shape);
        graphics.DrawPath(edge, shape);
    }

    private static void Text(Graphics graphics, string value, RectangleF area, float emSize, Color ink)
    {
        if (value.Length == 0 || area.Width <= 0 || area.Height <= 0) return;
        using var family = new FontFamily("Palatino Linotype");
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var path = new GraphicsPath();
        path.AddString(value, family, (int)FontStyle.Bold, emSize, PointF.Empty, format);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var factor = Math.Min(1, Math.Min(area.Width / bounds.Width, area.Height / bounds.Height));
        var left = area.Left + (area.Width - bounds.Width * factor) / 2;
        var top = area.Top + (area.Height - bounds.Height * factor) / 2;
        using var move = new Matrix(factor, 0, 0, factor, left - bounds.Left * factor, top - bounds.Top * factor);
        path.Transform(move);
        using var shadow = new Pen(Color.FromArgb(250, 4, 8, 9), Math.Max(.7f, emSize * factor * .075f)) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(ink);
        graphics.DrawPath(shadow, path);
        graphics.FillPath(fill, path);
    }
}
