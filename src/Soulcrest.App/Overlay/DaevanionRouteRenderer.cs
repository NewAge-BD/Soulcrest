using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>Static blue route artwork. Every edge is rebuilt from one complete current frame.</summary>
public sealed class DaevanionRouteRenderer
{
    public static Color RouteColor { get; } = Color.FromArgb(255, 45, 157, 255);
    public static Color AdditionColor { get; } = Color.FromArgb(255, 66, 224, 144);
    public static Color RemovalColor { get; } = Color.FromArgb(255, 255, 126, 66);

    /// <summary>Local edit deltas only. No route, connection or activation order is inferred.</summary>
    public Bitmap RenderChanges(Size size, DaevanionBoard board, DaevanionBoardDetection detection,
        IReadOnlySet<int> addedNodeIds, IReadOnlySet<int> removedNodeIds, Point origin = default)
    {
        var image = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            PaintChanges(graphics, board, detection, addedNodeIds, removedNodeIds, origin);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public void PaintChanges(Graphics graphics, DaevanionBoard board, DaevanionBoardDetection detection,
        IReadOnlySet<int> addedNodeIds, IReadOnlySet<int> removedNodeIds, Point origin = default)
    {
        if (board.FamilyId != detection.Family || !ValidGeometry(detection)
            || addedNodeIds.Count == 0 && removedNodeIds.Count == 0) return;
        var anchors = detection.Nodes.GroupBy(anchor => (anchor.Row, anchor.Column))
            .Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(anchor => anchor.Bounds.Width > 0 && anchor.Bounds.Height > 0
                && double.IsFinite(anchor.Confidence) && anchor.Confidence > 0)
            .ToDictionary(anchor => (anchor.Row, anchor.Column));
        RectangleF BoundsFor(DaevanionNode node)
        {
            if (node.Kind != DaevanionNodeKind.Start && anchors.TryGetValue((node.Row, node.Column), out var anchor))
                return new(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
            return new((float)(detection.Origin.X + (node.Column - 1) * detection.Spacing - detection.NodeSize / 2 - origin.X),
                (float)(detection.Origin.Y + (node.Row - 1) * detection.Spacing - detection.NodeSize / 2 - origin.Y),
                (float)detection.NodeSize, (float)detection.NodeSize);
        }
        var nativeFrames = board.Nodes.Select(BoundsFor).ToArray();
        var state = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var node in board.Nodes)
            {
                var added = addedNodeIds.Contains(node.Id); var removed = removedNodeIds.Contains(node.Id);
                if (node.Kind == DaevanionNodeKind.Start || added == removed
                    || !anchors.ContainsKey((node.Row, node.Column))) continue;
                var frame = BoundsFor(node);
                var outlineState = graphics.Save();
                try
                {
                    var envelope = DaevanionNodeFitting.Bounds(frame, detection.Spacing);
                    graphics.SetClip(envelope, CombineMode.Intersect);
                    foreach (var native in nativeFrames.Where(envelope.IntersectsWith)) graphics.SetClip(native, CombineMode.Exclude);
                    var margin = Math.Max(2f, frame.Width * .045f);
                    using var rim = new Pen(added ? AdditionColor : RemovalColor, Math.Max(2.2f, frame.Width * .045f))
                        { LineJoin = LineJoin.Round };
                    if (removed) rim.DashPattern = [3f, 2f];
                    graphics.DrawRectangle(rim, frame.X - margin, frame.Y - margin,
                        frame.Width + margin * 2, frame.Height + margin * 2);
                }
                finally { graphics.Restore(outlineState); }
            }
        }
        finally { graphics.Restore(state); }
    }

    public Bitmap Render(Size size, DaevanionBoard board, DaevanionBoardDetection detection,
        IReadOnlySet<int> nodeIds, IReadOnlyList<(int FromNodeId, int ToNodeId)> edges,
        bool showConnections, Point origin = default)
    {
        var image = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            Paint(graphics, board, detection, nodeIds, edges, showConnections, origin);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public void Paint(Graphics graphics, DaevanionBoard board, DaevanionBoardDetection detection,
        IReadOnlySet<int> nodeIds, IReadOnlyList<(int FromNodeId, int ToNodeId)> edges,
        bool showConnections, Point origin = default)
    {
        if (board.FamilyId != detection.Family || !ValidGeometry(detection) || nodeIds.Count == 0) return;
        var nodes = board.Nodes.ToDictionary(node => node.Id);
        var anchors = detection.Nodes.GroupBy(anchor => (anchor.Row, anchor.Column))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var start = board.Nodes.SingleOrDefault(node => node.Kind == DaevanionNodeKind.Start);
        var selected = nodeIds.Where(id => nodes.ContainsKey(id)).ToHashSet();
        if (!selected.Any(id => nodes[id].Kind != DaevanionNodeKind.Start)) return;
        if (start is not null) selected.Add(start.Id);
        RectangleF BoundsFor(DaevanionNode node)
        {
            if (node.Kind != DaevanionNodeKind.Start && anchors.TryGetValue((node.Row, node.Column), out var anchor))
                return new(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y, anchor.Bounds.Width, anchor.Bounds.Height);
            return new((float)(detection.Origin.X + (node.Column - 1) * detection.Spacing - detection.NodeSize / 2 - origin.X),
                (float)(detection.Origin.Y + (node.Row - 1) * detection.Spacing - detection.NodeSize / 2 - origin.Y),
                (float)detection.NodeSize, (float)detection.NodeSize);
        }
        var nativeFrames = board.Nodes.Select(BoundsFor).ToArray();
        var state = graphics.Save();
        try
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // A tooltip can cover the start or a gap while leaving both edge endpoints visible.
            // Endpoint visibility alone therefore never authorizes a connecting line.
            if (showConnections && HasCompleteFrame(board, detection))
            {
                var lineState = graphics.Save();
                try
                {
                    foreach (var frame in nativeFrames) graphics.SetClip(frame, CombineMode.Exclude);
                    using var halo = new Pen(Color.FromArgb(65, RouteColor), Math.Max(2, (float)detection.NodeSize * .09f));
                    using var line = new Pen(RouteColor, Math.Max(1.3f, (float)detection.NodeSize * .035f))
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    foreach (var edge in edges.Select(edge => edge.FromNodeId < edge.ToNodeId ? edge
                        : (FromNodeId: edge.ToNodeId, ToNodeId: edge.FromNodeId)).Distinct())
                    {
                        if (!selected.Contains(edge.FromNodeId) || !selected.Contains(edge.ToNodeId)
                            || !nodes.TryGetValue(edge.FromNodeId, out var from) || !nodes.TryGetValue(edge.ToNodeId, out var to)
                            || Math.Abs(from.Row - to.Row) + Math.Abs(from.Column - to.Column) != 1) continue;
                        var a = BoundsFor(from); var b = BoundsFor(to);
                        var first = new PointF(a.X + a.Width / 2, a.Y + a.Height / 2);
                        var second = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
                        graphics.DrawLine(halo, first, second);
                        graphics.DrawLine(line, first, second);
                    }
                }
                finally { graphics.Restore(lineState); }
            }
            foreach (var id in selected)
            {
                var node = nodes[id];
                if (node.Kind == DaevanionNodeKind.Start ? !detection.StartVisible
                    : !anchors.ContainsKey((node.Row, node.Column))) continue;
                var frame = BoundsFor(node);
                if (frame.Width <= 0 || frame.Height <= 0) continue;
                var outlineState = graphics.Save();
                try
                {
                    var envelope = DaevanionNodeFitting.Bounds(frame, detection.Spacing);
                    graphics.SetClip(envelope, CombineMode.Intersect);
                    foreach (var native in nativeFrames.Where(envelope.IntersectsWith)) graphics.SetClip(native, CombineMode.Exclude);
                    var margin = Math.Max(1.5f, Math.Min(frame.Width, frame.Height) * .035f);
                    using var rim = new Pen(RouteColor, Math.Max(1.2f, frame.Width * .028f)) { LineJoin = LineJoin.Round };
                    graphics.DrawRectangle(rim, frame.X - margin, frame.Y - margin, frame.Width + margin * 2, frame.Height + margin * 2);
                }
                finally { graphics.Restore(outlineState); }
            }
        }
        finally { graphics.Restore(state); }
    }

    internal static bool HasCompleteFrame(DaevanionBoard board, DaevanionBoardDetection detection)
    {
        if (!detection.StartVisible || board.FamilyId != detection.Family || !ValidGeometry(detection)) return false;
        var expected = board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start)
            .Select(node => (node.Row, node.Column)).ToHashSet();
        return expected.Count > 0 && detection.Nodes.Count == expected.Count
            && detection.Nodes.Select(anchor => (anchor.Row, anchor.Column)).Distinct().Count() == expected.Count
            && detection.Nodes.All(anchor => expected.Contains((anchor.Row, anchor.Column))
                && anchor.Bounds.Width > 0 && anchor.Bounds.Height > 0 && double.IsFinite(anchor.Confidence) && anchor.Confidence > 0);
    }

    private static bool ValidGeometry(DaevanionBoardDetection detection) => double.IsFinite(detection.Origin.X)
        && double.IsFinite(detection.Origin.Y) && double.IsFinite(detection.NodeSize) && detection.NodeSize > 0
        && double.IsFinite(detection.Spacing) && detection.Spacing > detection.NodeSize;
}
