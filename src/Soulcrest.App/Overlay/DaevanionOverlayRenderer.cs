using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>The same masked content and native-style fittings serve live presentation and previews.</summary>
public sealed partial class DaevanionOverlayRenderer : IDisposable
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
            Span<int> cells = stackalloc int[225];
            cells.Fill(-1);
            for (var index = 0; index < board.Nodes.Count; index++)
            {
                var cell = CellIndex(board.Nodes[index].Row, board.Nodes[index].Column);
                if (cell >= 0) cells[cell] = index;
            }
            foreach (var anchor in anchors)
            {
                var cell = CellIndex(anchor.Row, anchor.Column);
                var node = cell >= 0 && cells[cell] >= 0 ? board.Nodes[cells[cell]] : null;
                if (node is null || node.Kind == DaevanionNodeKind.Start || node.Effects.Count == 0) continue;
                var outer = new Rectangle(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
                var artwork = GetArtwork(board, node, outer.Size, language, anchor.Appearance, spacing, out var cached);
                try
                {
                    graphics.DrawImageUnscaled(artwork.Image, outer.X - artwork.Padding, outer.Y - artwork.Padding);
                }
                finally { if (!cached) artwork.Image.Dispose(); }
            }
        }
        finally { graphics.Restore(saved); }
    }

    private void PaintNode(Graphics graphics, DaevanionBoard board, DaevanionNode node, Rectangle outer,
        string language, DaevanionNodeAppearance appearance, double? spacing)
    {
        // User request 2026-11-12: restore the large numbers and colored exterior fitting.
        // Register the user's 156x154 reference against the native outer frame. Its circular
        // opening covers about 65% of the node; the activation ring and corner colors stay native.
        var canvasWidth = Math.Max(1, (int)Math.Round(outer.Width * .99));
        var canvasHeight = Math.Max(1, (int)Math.Round(outer.Height * .98));
        var bounds = new Rectangle(outer.X + (int)Math.Round((outer.Width - canvasWidth) / 2d),
            outer.Y + (int)Math.Round((outer.Height - canvasHeight) / 2d), canvasWidth, canvasHeight);
        var inside = _mask.ContentBounds(bounds.Size);
        if (inside.Width < 12 || inside.Height < 12) return;
        var extended = node.Kind == DaevanionNodeKind.Stat && DaevanionNodeFitting.CanExtend(outer, spacing);
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
                PaintBackground(canvas, inside, node.Quality, appearance, extended);
                if (extended)
                {
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

    private static int CellIndex(int row, int column) => row is >= 1 and <= 15 && column is >= 1 and <= 15
        ? (row - 1) * 15 + column - 1 : -1;

    internal static double? ResolveSpacing(IReadOnlyList<DaevanionNodeAnchor> anchors, double? spacing)
    {
        // Live presentation always supplies the complete confirmed raster pitch. Never
        // replace it with a value inferred from the currently unoccluded subset.
        if (spacing is { } supplied && double.IsFinite(supplied) && supplied > 0) return supplied;
        Span<int> cells = stackalloc int[225];
        cells.Fill(-1);
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var cell = CellIndex(anchor.Row, anchor.Column);
            if (cell >= 0 && anchor.Bounds.Width > 0 && anchor.Bounds.Height > 0
                && (cells[cell] < 0 || anchor.Confidence > anchors[cells[cell]].Confidence)) cells[cell] = index;
        }
        // One predecessor in each native row and column suffices, even across holes.
        // Input work is linear; the remaining raster and sample sort have a fixed 15x15 bound.
        Span<int> previousColumns = stackalloc int[15];
        previousColumns.Fill(-1);
        Span<double> candidates = stackalloc double[420];
        var count = 0;
        for (var row = 0; row < 15; row++)
        {
            var previousRow = -1;
            for (var column = 0; column < 15; column++)
            {
                var index = cells[row * 15 + column];
                if (index < 0) continue;
                var anchor = anchors[index];
                if (previousRow >= 0)
                {
                    var other = anchors[previousRow];
                    var pitch = Math.Abs((anchor.Bounds.X + anchor.Bounds.Width / 2d
                        - other.Bounds.X - other.Bounds.Width / 2d) / (anchor.Column - other.Column));
                    if (double.IsFinite(pitch) && pitch > 0) candidates[count++] = pitch;
                }
                if (previousColumns[column] >= 0)
                {
                    var other = anchors[previousColumns[column]];
                    var pitch = Math.Abs((anchor.Bounds.Y + anchor.Bounds.Height / 2d
                        - other.Bounds.Y - other.Bounds.Height / 2d) / (anchor.Row - other.Row));
                    if (double.IsFinite(pitch) && pitch > 0) candidates[count++] = pitch;
                }
                previousRow = index;
                previousColumns[column] = index;
            }
        }
        if (count == 0) return null;
        var samples = candidates[..count];
        samples.Sort();
        return samples[count / 2];
    }

    private static string Label(DaevanionBoard board, DaevanionEffect effect, string language)
    {
        var label = effect.ShortLabel.For(language);
        // Azphel is the PvP board. Avoid repeating that prefix in its small node interiors;
        // the full, qualified stat names remain available in the settings legend.
        return board.FamilyId == "azphel" && label.StartsWith("PvP ", StringComparison.Ordinal) ? label[4..] : label;
    }

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

    private static Bitmap ReadBackground()
    {
        using var stream = typeof(DaevanionOverlayRenderer).Assembly.GetManifestResourceStream("Soulcrest.DaevanionNodeBackground.png")
            ?? throw new InvalidDataException("Missing embedded Daevanion node background.");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    public void Dispose() { ClearArtwork(); foreach (var image in _images.Values) image.Dispose(); _images.Clear(); _mask.Dispose(); _background.Dispose(); }
}
