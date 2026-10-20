using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>The same node-interior painter is used by live presentation and offline previews.</summary>
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
        string language, Point origin = default)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try { using var g = Graphics.FromImage(bitmap); g.Clear(Color.Transparent); Paint(g, board, anchors, language, origin); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }

    public void Paint(Graphics graphics, DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors,
        string language, Point origin = default)
    {
        var saved = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var anchor in anchors)
            {
                var node = board.Nodes.FirstOrDefault(n => n.Row == anchor.Row && n.Column == anchor.Column);
                if (node is null || node.Kind == DaevanionNodeKind.Start || node.Effects.Count == 0) continue;
                var outer = new Rectangle(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
                // Register the new 156x154 reference against the native outer frame. Its circular
                // opening covers about 65% of the node; the activation ring and corner colors stay native.
                var canvasWidth = Math.Max(1, (int)Math.Round(outer.Width * .99));
                var canvasHeight = Math.Max(1, (int)Math.Round(outer.Height * .98));
                // User request 2026-10-21: lower the circular contents slightly within the native frame.
                // Scale the correction with the node, keeping background, text and skill masks together.
                var verticalOffset = (int)Math.Round(outer.Height * .02);
                var bounds = new Rectangle(outer.X + (int)Math.Round((outer.Width - canvasWidth) / 2d),
                    outer.Y + (int)Math.Round((outer.Height - canvasHeight) / 2d) + verticalOffset, canvasWidth, canvasHeight);
                var inside = _mask.ContentBounds(bounds.Size);
                if (inside.Width < 12 || inside.Height < 12) continue;
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
                        // Keep glyphs within the circle's chords, rather than the rectangular alpha bbox.
                        if (node.Effects.Count == 1)
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
            Paint(g, board, anchors, language);
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
