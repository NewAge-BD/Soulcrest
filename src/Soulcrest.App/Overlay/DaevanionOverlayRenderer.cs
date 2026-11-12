using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>The same native material, round content and exact values serve live presentation and previews.</summary>
public sealed partial class DaevanionOverlayRenderer : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, Bitmap> _images = new(StringComparer.Ordinal);
    private readonly DaevanionNodeMask _mask = new();
    private readonly DaevanionCatalog _catalog;
    private const string RuneFont = "Palatino Linotype";
    private static readonly Color Ink = Color.FromArgb(252, 240, 207);
    private static readonly Color MutedInk = Color.FromArgb(187, 184, 165);

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
            foreach (var anchor in anchors)
            {
                var node = board.Nodes.FirstOrDefault(n => n.Row == anchor.Row && n.Column == anchor.Column);
                if (node is null || node.Kind == DaevanionNodeKind.Start || node.Effects.Count == 0) continue;
                var outer = new Rectangle(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
                var artwork = GetArtwork(board, node, outer.Size, language, anchor.Appearance, out var cached);
                try
                {
                    graphics.DrawImageUnscaled(artwork.Image, outer.X - artwork.Padding, outer.Y - artwork.Padding);
                }
                finally { if (!cached) artwork.Image.Dispose(); }
            }
        }
        finally { graphics.Restore(saved); }
    }

    private void PaintNode(Graphics graphics, DaevanionNode node, Rectangle bounds,
        string language, DaevanionNodeAppearance appearance)
    {
        // User request 2026-11-12: native engraving (proposal 1). Keep all content
        // inside the original round cutout; native frames and activation rings remain visible.
        var inside = _mask.ContentBounds(bounds.Size);
        if (inside.Width < 12 || inside.Height < 12) return;
        Bitmap? skillImage = null;
        if (node.Kind == DaevanionNodeKind.Skill)
        {
            var effect = node.Effects.Single();
            var skill = effect.SkillId is { } id ? _catalog.GetSkill(id) : null;
            if (skill is not null && _catalog.GetAsset(skill.ImageAssetId) is { } asset)
                skillImage = Image(asset.Path);
            // A missing image must leave the original game symbol visible.
            if (skillImage is null) return;
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
            var area = new RectangleF(inside.X, inside.Y, inside.Width, inside.Height);
            if (node.Kind == DaevanionNodeKind.Skill)
            {
                canvas.DrawImage(skillImage!, inside);
                var badge = Box(area, .47f, .64f, .48f, .30f);
                using var shade = new SolidBrush(Color.FromArgb(185, 13, 18, 23));
                canvas.FillEllipse(shade, badge);
                Text(canvas, "+1", badge, inside.Height * .27f, Ink);
            }
            else
            {
                PaintBackground(canvas, new Rectangle(Point.Empty, bounds.Size), inside, node.Quality,
                    appearance, node.Effects.Count > 1);
                if (node.Effects.Count == 1)
                {
                    var effect = node.Effects[0];
                    Text(canvas, effect.ShortLabel.For(language), Box(area, .02f, .12f, .96f, .25f),
                        inside.Height * .19f, MutedInk);
                    Text(canvas, effect.DisplayAmount.For(language), Box(area, .01f, .39f, .98f, .43f),
                        inside.Height * .34f, Ink);
                    DaevanionNativeArtwork.Dot.Paint(canvas, Box(area, .41f, .84f, .18f, .18f));
                }
                else for (var index = 0; index < node.Effects.Count; index++)
                {
                    var effect = node.Effects[index];
                    var row = Box(area, .01f, .04f + index * .46f, .98f, .44f);
                    Text(canvas, effect.ShortLabel.For(language), Box(row, .02f, 0, .96f, .45f),
                        inside.Height * .13f, MutedInk);
                    Text(canvas, effect.DisplayAmount.For(language), Box(row, .02f, .40f, .96f, .60f),
                        inside.Height * .23f, Ink);
                }
            }
        }
        _mask.Apply(surface);
        graphics.DrawImageUnscaled(surface, bounds.Location);
    }

    private static RectangleF Box(RectangleF box, float x, float y, float width, float height) =>
        new(box.X + box.Width * x, box.Y + box.Height * y, box.Width * width, box.Height * height);

    private static void Text(Graphics graphics, string text, RectangleF area, float size, Color ink)
    {
        using var family = new FontFamily(RuneFont);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, Math.Max(7, size), PointF.Empty, format);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0 || area.Width <= 2 || area.Height <= 2) return;
        // Fit actual glyph bounds in both directions. Long qualified PvE/PvP/Boss labels
        // and localized percentages must never clip or spill into the other bonus row.
        var factor = Math.Min(1, Math.Min((area.Width - 2) / bounds.Width, (area.Height - 2) / bounds.Height));
        using var transform = new Matrix(factor, 0, 0, factor,
            area.Left + (area.Width - bounds.Width * factor) / 2 - bounds.Left * factor,
            area.Top + (area.Height - bounds.Height * factor) / 2 - bounds.Top * factor);
        path.Transform(transform);
        using var outline = new Pen(Color.FromArgb(220, 5, 8, 9), Math.Max(.8f, size * factor * .075f)) { LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        graphics.DrawPath(outline, path); graphics.FillPath(brush, path);
    }

    public Bitmap Preview(string classId = "Cleric", string family = "nezekan", string language = "en",
        bool nodeContentVisible = true, bool showToggle = false,
        IReadOnlySet<string>? highlightedStatIds = null, bool showStatRail = false, DaevanionBoardPlan? route = null)
    {
        var board = _catalog.GetBoard(classId, family) ?? throw new ArgumentException("Unknown Daevanion board.");
        const int spacing = 76, nodeSize = 60;
        var header = showToggle || showStatRail ? 52 : 0;
        var minRow = board.Nodes.Min(n => n.Row); var minCol = board.Nodes.Min(n => n.Column);
        var size = new Size((board.Nodes.Max(n => n.Column) - minCol + 1) * spacing + 12 + (showStatRail ? 208 : 0),
            (board.Nodes.Max(n => n.Row) - minRow + 1) * spacing + 12 + header);
        var anchors = board.Nodes.Select(n => new DaevanionNodeAnchor(n.Row, n.Column,
            new(12 + (n.Column - minCol) * spacing, 12 + header + (n.Row - minRow) * spacing, nodeSize, nodeSize), 1) { Appearance = DaevanionNodeAppearance.Active }).ToArray();
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
            if (nodeContentVisible) Paint(g, board, anchors, language, spacing: spacing);
            var detection = new DaevanionBoardDetection(family,
                new(12 + nodeSize / 2d - (minCol - 1) * spacing, 12 + header + nodeSize / 2d - (minRow - 1) * spacing),
                spacing, nodeSize, anchors, 1);
            var selected = highlightedStatIds ?? new HashSet<string>(StringComparer.Ordinal);
            if (selected.Count > 0) new DaevanionHighlightRenderer().Paint(g, board, anchors, selected, spacing: spacing);
            var toggle = showToggle ? DaevanionToggleLayout.Bounds(board, detection, size) : Rectangle.Empty;
            if (showToggle) DaevanionToggleRenderer.Paint(g, toggle, nodeContentVisible, language);
            if (showStatRail)
            {
                var rail = DaevanionStatRailLayout.Build(board, detection, size, selected, language, toggle);
                if (rail.HasSpace) DaevanionStatRailRenderer.Paint(g, rail);
            }
            if (route is not null && route.BoardId == board.Id && route.FamilyId == board.FamilyId)
            {
                var routeDetection = detection with { StartVisible = true, Nodes = anchors.Where(anchor =>
                    board.Nodes.Single(node => node.Row == anchor.Row && node.Column == anchor.Column).Kind != DaevanionNodeKind.Start).ToArray() };
                new DaevanionRouteRenderer().Paint(g, board, routeDetection, route.NodeIds.ToHashSet(),
                    route.Edges.Select(edge => (edge.FromNodeId, edge.ToNodeId)).ToArray(), true);
            }
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

    public void Dispose() { ClearArtwork(); foreach (var image in _images.Values) image.Dispose(); _images.Clear(); _mask.Dispose(); }
}
