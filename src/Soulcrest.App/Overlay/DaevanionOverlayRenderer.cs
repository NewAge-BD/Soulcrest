using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>The same masked content and native-style fittings serve live presentation and previews.</summary>
public sealed class DaevanionOverlayRenderer : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, Bitmap> _images = new(StringComparer.Ordinal);
    private readonly DaevanionNodeMask _mask = new();
    private readonly Bitmap _background = ReadBackground();
    private readonly DaevanionCatalog _catalog;
    private const string RuneFont = "Palatino Linotype";
    // Visible alpha bounds of the user's original 1254px background. Ignore hidden RGB outside it.
    private static readonly Rectangle BackgroundCrop = new(230, 242, 793, 769);

    public DaevanionOverlayRenderer(string? assetRoot = null, DaevanionCatalog? catalog = null)
    { _root = Path.GetFullPath(assetRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot")); _catalog = catalog ?? DaevanionCatalog.Default; }

    public Bitmap Render(Size size, DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors,
        string language, Point origin = default, double? spacing = null)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try { using var g = Graphics.FromImage(bitmap); g.Clear(Color.Transparent); Paint(g, board, anchors, language, origin, spacing); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }

    public void Paint(Graphics graphics, DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors,
        string language, Point origin = default, double? spacing = null)
    {
        var saved = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            spacing = ResolveSpacing(anchors, spacing);
            foreach (var anchor in anchors)
            {
                var node = board.Nodes.FirstOrDefault(n => n.Row == anchor.Row && n.Column == anchor.Column);
                if (node is null || node.Kind == DaevanionNodeKind.Start || node.Effects.Count == 0) continue;
                var outer = new Rectangle(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
                // Register the new 156x154 reference against the native outer frame. Its circular
                // opening covers about 65% of the node; the activation ring and corner colors stay native.
                var canvasWidth = Math.Max(1, (int)Math.Round(outer.Width * .99));
                var canvasHeight = Math.Max(1, (int)Math.Round(outer.Height * .98));
                var bounds = new Rectangle(outer.X + (int)Math.Round((outer.Width - canvasWidth) / 2d),
                    outer.Y + (int)Math.Round((outer.Height - canvasHeight) / 2d), canvasWidth, canvasHeight);
                var inside = _mask.ContentBounds(bounds.Size);
                if (inside.Width < 12 || inside.Height < 12) continue;
                var extended = node.Kind == DaevanionNodeKind.Stat && DaevanionNodeFitting.CanExtend(outer, spacing);
                Bitmap? skillImage = null;
                if (node.Kind == DaevanionNodeKind.Skill)
                {
                    var effect = node.Effects.Single();
                    var skill = effect.SkillId is { } id ? _catalog.GetSkill(id) : null;
                    if (skill is not null && _catalog.GetAsset(skill.ImageAssetId) is { } asset)
                        skillImage = Image(asset.Path);
                    // A missing image must leave the original game symbol visible.
                    if (skillImage is null) continue;
                }
                if (extended)
                {
                    var fittingState = graphics.Save();
                    try
                    {
                        // Decoration uses only the gaps. The original frame, activation ring,
                        // start ornament and even unconfirmed neighboring frames stay untouched.
                        graphics.ExcludeClip(outer);
                        if (spacing is { } step)
                            foreach (var neighbor in board.Nodes.Where(n => n.Id != node.Id
                                && Math.Abs(n.Row - node.Row) <= 1 && Math.Abs(n.Column - node.Column) <= 1))
                                graphics.SetClip(new RectangleF(outer.X + (float)((neighbor.Column - node.Column) * step),
                                    outer.Y + (float)((neighbor.Row - node.Row) * step), outer.Width, outer.Height), CombineMode.Exclude);
                        DaevanionNodeFitting.PaintOutside(graphics, node, outer, language, spacing);
                    }
                    finally { graphics.Restore(fittingState); }
                }
                using var surface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                using (var canvas = Graphics.FromImage(surface))
                {
                    canvas.Clear(Color.Transparent);
                    canvas.PageUnit = GraphicsUnit.Pixel;
                    canvas.SmoothingMode = SmoothingMode.AntiAlias;
                    canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    canvas.SetClip(inside);
                    if (node.Kind == DaevanionNodeKind.Skill)
                    {
                        canvas.DrawImage(skillImage!, inside);
                        var badge = new RectangleF(inside.X + inside.Width * .35f, inside.Y + inside.Height * .61f,
                            inside.Width * .45f, inside.Height * .28f);
                        Text(canvas, "+1", badge, inside.Height * .29f, Color.FromArgb(255, 249, 232, 180));
                    }
                    else
                    {
                        canvas.DrawImage(_background, inside, BackgroundCrop, GraphicsUnit.Pixel);
                        if (extended)
                        {
                            using var shade = new SolidBrush(Color.FromArgb(68, 9, 14, 14));
                            canvas.FillRectangle(shade, inside);
                            DaevanionNodeFitting.PaintCenter(canvas, node, inside, language);
                        }
                        // Keep glyphs within the circle's chords, rather than the rectangular alpha bbox.
                        else if (node.Effects.Count == 1)
                        {
                            var effect = node.Effects[0];
                            Text(canvas, Label(board, effect, language), new(inside.X + inside.Width * .08f,
                                inside.Y + inside.Height * .13f, inside.Width * .84f, inside.Height * .33f),
                                inside.Height * .26f, Color.FromArgb(255, 230, 223, 187));
                            Text(canvas, effect.DisplayAmount.For(language), new(inside.X + inside.Width * .08f,
                                inside.Y + inside.Height * .44f, inside.Width * .84f, inside.Height * .36f),
                                inside.Height * .35f, Color.FromArgb(255, 253, 240, 196));
                        }
                        else for (var i = 0; i < node.Effects.Count; i++)
                        {
                            var effect = node.Effects[i];
                            var line = Label(board, effect, language) + " " + effect.DisplayAmount.For(language);
                            Text(canvas, line, new(inside.X + inside.Width * .06f,
                                inside.Y + (i == 0 ? .18f : .49f) * inside.Height, inside.Width * .88f, inside.Height * .32f),
                                inside.Height * .24f, Color.FromArgb(255, 253, 240, 196));
                        }
                    }
                }
                _mask.Apply(surface);
                graphics.DrawImageUnscaled(surface, bounds.Location);
            }
        }
        finally { graphics.Restore(saved); }
    }

    private static double? ResolveSpacing(IReadOnlyList<DaevanionNodeAnchor> anchors, double? spacing)
    {
        if (spacing is { } supplied && double.IsFinite(supplied) && supplied > 0) return supplied;
        var candidates = new List<double>();
        for (var i = 0; i < anchors.Count; i++)
        for (var j = i + 1; j < anchors.Count; j++)
        {
            var a = anchors[i]; var b = anchors[j];
            if (a.Row == b.Row && a.Column != b.Column)
                candidates.Add(Math.Abs((b.Bounds.X + b.Bounds.Width / 2d - a.Bounds.X - a.Bounds.Width / 2d) / (b.Column - a.Column)));
            else if (a.Column == b.Column && a.Row != b.Row)
                candidates.Add(Math.Abs((b.Bounds.Y + b.Bounds.Height / 2d - a.Bounds.Y - a.Bounds.Height / 2d) / (b.Row - a.Row)));
        }
        var sorted = candidates.Where(value => double.IsFinite(value) && value > 0).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[sorted.Length / 2];
    }

    private static string Label(DaevanionBoard board, DaevanionEffect effect, string language)
    {
        var label = effect.ShortLabel.For(language);
        // Azphel is the PvP board. Avoid repeating that prefix in its small node interiors;
        // the full, qualified stat names remain available in the settings legend.
        return board.FamilyId == "azphel" && label.StartsWith("PvP ", StringComparison.Ordinal) ? label[4..] : label;
    }

    private static void Text(Graphics graphics, string text, RectangleF area, float size, Color ink, StringAlignment alignment = StringAlignment.Center)
    {
        using var measureFont = new Font(RuneFont, Math.Max(6, size), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = alignment, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None };
        var width = graphics.MeasureString(text, measureFont, int.MaxValue, format).Width;
        using var font = new Font(RuneFont, Math.Max(4, measureFont.Size * Math.Min(1, (area.Width - 2) / Math.Max(1, width))), FontStyle.Bold, GraphicsUnit.Pixel);
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, area, format);
        using var shadow = new Pen(Color.FromArgb(230, 6, 9, 10), Math.Max(1, size * .12f)) { LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        graphics.DrawPath(shadow, path); graphics.FillPath(brush, path);
    }

    public Bitmap Preview(string classId = "Cleric", string family = "nezekan", string language = "en")
    {
        var board = _catalog.GetBoard(classId, family) ?? throw new ArgumentException("Unknown Daevanion board.");
        const int spacing = 76, nodeSize = 60;
        var minRow = board.Nodes.Min(n => n.Row); var minCol = board.Nodes.Min(n => n.Column);
        var size = new Size((board.Nodes.Max(n => n.Column) - minCol + 1) * spacing + 12,
            (board.Nodes.Max(n => n.Row) - minRow + 1) * spacing + 12);
        var anchors = board.Nodes.Select(n => new DaevanionNodeAnchor(n.Row, n.Column,
            new(12 + (n.Column - minCol) * spacing, 12 + (n.Row - minRow) * spacing, nodeSize, nodeSize), 1)).ToArray();
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(image); g.Clear(Color.FromArgb(255, 26, 33, 35));
            foreach (var anchor in anchors)
            {
                var node = board.Nodes.Single(n => n.Row == anchor.Row && n.Column == anchor.Column);
                var frame = _catalog.GetAsset(node.FrameAssetId) is { } asset ? Image(asset.Path) : null;
                if (frame is not null)
                {
                    var box = new RectangleF(anchor.Bounds.X, anchor.Bounds.Y, anchor.Bounds.Width, anchor.Bounds.Height);
                    if (node.Kind != DaevanionNodeKind.Start)
                    {
                        // Standard sprites have a 134px canvas with visible bounds (5,6,126,125).
                        // Fit those visible bounds to the same outer-node anchors used by live detection.
                        box = new(box.X - box.Width * 5 / 126, box.Y - box.Height * 6 / 125,
                            box.Width * 134 / 126, box.Height * 134 / 125);
                    }
                    g.DrawImage(frame, box);
                }
            }
            Paint(g, board, anchors, language, spacing: spacing);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private Bitmap? Image(string relative)
    {
        if (_images.TryGetValue(relative, out var found)) return found;
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = File.OpenRead(path); using var source = System.Drawing.Image.FromStream(stream);
            return _images[relative] = new Bitmap(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private static Bitmap ReadBackground()
    {
        using var stream = typeof(DaevanionOverlayRenderer).Assembly.GetManifestResourceStream("Soulcrest.DaevanionNodeBackground.png")
            ?? throw new InvalidDataException("Missing embedded Daevanion node background.");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    public void Dispose() { foreach (var image in _images.Values) image.Dispose(); _images.Clear(); _mask.Dispose(); _background.Dispose(); }
}
