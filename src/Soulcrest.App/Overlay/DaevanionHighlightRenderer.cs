using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>Stat selection is a separate presentation layer, independent of node artwork and activation.</summary>
public sealed class DaevanionHighlightRenderer
{
    public Bitmap Render(Size size, DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors,
        IReadOnlySet<string> selected, Point origin = default, double? spacing = null, double phase = 0)
    {
        var image = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            Paint(graphics, board, anchors, selected, origin, spacing, phase);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public void Paint(Graphics graphics, DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors,
        IReadOnlySet<string> selected, Point origin = default, double? spacing = null, double phase = 0)
    {
        if (!double.IsFinite(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        phase -= Math.Floor(phase);
        if (selected.Count == 0 || anchors.Count == 0) return;
        var saved = graphics.Save();
        // All nodes in one frame share the phase. Rasterize the animated contour once per physical size/pitch.
        var stamps = new Dictionary<(int Width, int Height, double? Spacing), (Bitmap Image, Point Offset)>();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            spacing = ResolveSpacing(anchors, spacing);
            // Without a pitch, an unseen neighbor cannot be safely excluded. Live snapshots always supply it.
            if (spacing is null && board.Nodes.Count > 1) return;
            foreach (var anchor in anchors)
            {
                var node = board.Nodes.FirstOrDefault(n => n.Row == anchor.Row && n.Column == anchor.Column);
                if (node is null || node.Kind != DaevanionNodeKind.Stat
                    || !node.Effects.Any(effect => effect.Kind == DaevanionEffectKind.StatBonus
                        && effect.StatId is { } stat && selected.Contains(stat))) continue;
                var frame = new RectangleF(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y,
                    anchor.Bounds.Width, anchor.Bounds.Height);
                if (frame.Width <= 0 || frame.Height <= 0) continue;
                var key = (anchor.Bounds.Width, anchor.Bounds.Height, spacing);
                if (!stamps.TryGetValue(key, out var stamp))
                {
                    stamp = CreateStamp(new(anchor.Bounds.Width, anchor.Bounds.Height), spacing, phase);
                    stamps.Add(key, stamp);
                }
                var state = graphics.Save();
                try
                {
                    // The same measured envelope as the node fitting bounds every antialiased fringe.
                    var envelope = DaevanionNodeFitting.Bounds(frame, spacing);
                    graphics.SetClip(envelope, CombineMode.Intersect);
                    graphics.SetClip(frame, CombineMode.Exclude);
                    foreach (var other in anchors)
                    {
                        var native = new Rectangle(other.Bounds.X - origin.X, other.Bounds.Y - origin.Y,
                            other.Bounds.Width, other.Bounds.Height);
                        if (envelope.IntersectsWith(native)) graphics.ExcludeClip(native);
                    }
                    if (spacing is { } pitch)
                        foreach (var neighbor in board.Nodes.Where(n => n.Id != node.Id))
                        {
                            var native = new RectangleF(frame.X + (float)((neighbor.Column - node.Column) * pitch),
                                frame.Y + (float)((neighbor.Row - node.Row) * pitch), frame.Width, frame.Height);
                            if (envelope.IntersectsWith(native)) graphics.SetClip(native, CombineMode.Exclude);
                        }
                    graphics.DrawImageUnscaled(stamp.Image, (int)frame.X + stamp.Offset.X, (int)frame.Y + stamp.Offset.Y);
                }
                finally { graphics.Restore(state); }
            }
        }
        finally
        {
            foreach (var stamp in stamps.Values) stamp.Image.Dispose();
            graphics.Restore(saved);
        }
    }

    private static (Bitmap Image, Point Offset) CreateStamp(Size size, double? spacing, double phase)
    {
        var envelope = DaevanionNodeFitting.Bounds(new RectangleF(PointF.Empty, size), spacing);
        var left = (int)Math.Floor(envelope.Left); var top = (int)Math.Floor(envelope.Top);
        var image = new Bitmap(Math.Max(1, (int)Math.Ceiling(envelope.Right) - left),
            Math.Max(1, (int)Math.Ceiling(envelope.Bottom) - top), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var frame = new RectangleF(-left, -top, size.Width, size.Height);
            var localEnvelope = DaevanionNodeFitting.Bounds(frame, spacing);
            graphics.SetClip(localEnvelope, CombineMode.Intersect);
            graphics.SetClip(frame, CombineMode.Exclude);
            PaintContour(graphics, frame, localEnvelope, phase);
            return (image, new(left, top));
        }
        catch { image.Dispose(); throw; }
    }

    private static void PaintContour(Graphics graphics, RectangleF frame, RectangleF envelope, double phase)
    {
        var horizontal = Math.Clamp((frame.Left - envelope.Left) / (frame.Width * .09f), 0, 1);
        var vertical = Math.Clamp((frame.Top - envelope.Top) / (frame.Height * .06f), 0, 1);
        if (horizontal <= 0 || vertical <= 0) return;
        static float Outside(float value, float scale) => value < 0 ? value * scale : value > 1 ? 1 + (value - 1) * scale : value;
        PointF At(float x, float y) => new(frame.X + frame.Width * Outside(x, horizontal),
            frame.Y + frame.Height * Outside(y, vertical));
        PointF[] points = [
            At(.075f, -.030f), At(.925f, -.030f), At(1.068f, .07f), At(1.068f, .925f),
            At(.94f, 1.16f), At(.06f, 1.16f), At(-.068f, .925f), At(-.068f, .07f)];
        using var contour = new GraphicsPath();
        contour.AddPolygon(points);
        var scale = Math.Min(frame.Width, frame.Height) / 60f;
        var available = Math.Min(frame.Width * .09f * horizontal, frame.Height * .06f * vertical);
        using var foundation = new Pen(Color.FromArgb(100, 211, 205, 177), Math.Min(.75f * scale, Math.Max(.2f, available)))
            { LineJoin = LineJoin.Round };
        graphics.DrawPath(foundation, contour);
        // User request 2026-10-22: one clockwise ivory light trail; native activation does not pulse or change color.
        var lengths = new double[points.Length + 1];
        for (var i = 0; i < points.Length; i++)
        {
            var next = points[(i + 1) % points.Length];
            lengths[i + 1] = lengths[i] + Math.Sqrt(Math.Pow(next.X - points[i].X, 2) + Math.Pow(next.Y - points[i].Y, 2));
        }
        var total = lengths[^1];
        var end = phase * total;
        const double fraction = .27;
        using var trail = Arc(points, lengths, end - total * fraction, end);
        using var halo = new Pen(Color.FromArgb(70, 255, 244, 202), Math.Min(3.8f * scale, available * 2))
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawPath(halo, trail);
        const int pieces = 16;
        for (var i = 0; i < pieces; i++)
        {
            var strength = (i + 1d) / pieces;
            using var segment = Arc(points, lengths, end - total * fraction * (1 - i / (double)pieces),
                end - total * fraction * (1 - (i + 1d) / pieces));
            using var light = new Pen(Color.FromArgb((int)Math.Round(35 + 220 * strength * strength), 255, 248, 220),
                Math.Min(1.4f * scale, Math.Max(.35f, available)))
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawPath(light, segment);
        }
    }

    private static GraphicsPath Arc(PointF[] points, double[] lengths, double start, double end)
    {
        var total = lengths[^1];
        PointF Position(double distance)
        {
            distance = (distance % total + total) % total;
            var side = 0;
            while (side < points.Length - 1 && lengths[side + 1] < distance) side++;
            var t = (float)((distance - lengths[side]) / (lengths[side + 1] - lengths[side]));
            var a = points[side]; var b = points[(side + 1) % points.Length];
            return new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
        var vertices = new List<PointF> { Position(start) };
        for (var cycle = (int)Math.Floor(start / total); cycle <= (int)Math.Floor(end / total); cycle++)
        for (var side = 0; side < points.Length; side++)
        {
            var boundary = cycle * total + lengths[side + 1];
            if (boundary > start && boundary < end) vertices.Add(points[(side + 1) % points.Length]);
        }
        vertices.Add(Position(end));
        var path = new GraphicsPath(); path.AddLines(vertices.ToArray()); return path;
    }

    private static double? ResolveSpacing(IReadOnlyList<DaevanionNodeAnchor> anchors, double? spacing)
    {
        if (spacing is { } supplied) return double.IsFinite(supplied) && supplied > 0 ? supplied : null;
        var distances = new List<double>();
        for (var i = 0; i < anchors.Count; i++)
        for (var j = i + 1; j < anchors.Count; j++)
        {
            var a = anchors[i]; var b = anchors[j];
            if (a.Row == b.Row && a.Column != b.Column)
                distances.Add(Math.Abs((b.Bounds.X + b.Bounds.Width / 2d - a.Bounds.X - a.Bounds.Width / 2d) / (b.Column - a.Column)));
            else if (a.Column == b.Column && a.Row != b.Row)
                distances.Add(Math.Abs((b.Bounds.Y + b.Bounds.Height / 2d - a.Bounds.Y - a.Bounds.Height / 2d) / (b.Row - a.Row)));
        }
        var valid = distances.Where(value => double.IsFinite(value) && value > 0).Order().ToArray();
        return valid.Length == 0 ? null : valid[valid.Length / 2];
    }
}
