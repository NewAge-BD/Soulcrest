using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNodeProjectionTests
{
    [Theory]
    [InlineData(DaevanionNodeKind.Stat, DaevanionNodeAppearance.Unknown, DaevanionNodeAppearance.Inactive, true)]
    [InlineData(DaevanionNodeKind.Stat, DaevanionNodeAppearance.Inactive, DaevanionNodeAppearance.Unknown, true)]
    [InlineData(DaevanionNodeKind.Stat, DaevanionNodeAppearance.Active, DaevanionNodeAppearance.Unknown, false)]
    [InlineData(DaevanionNodeKind.Stat, DaevanionNodeAppearance.Inactive, DaevanionNodeAppearance.Active, false)]
    [InlineData(DaevanionNodeKind.Skill, DaevanionNodeAppearance.Unknown, DaevanionNodeAppearance.Active, true)]
    [InlineData(DaevanionNodeKind.Skill, DaevanionNodeAppearance.Active, DaevanionNodeAppearance.Inactive, true)]
    public void PresentationEqualityFollowsTheActualProductionPixels(DaevanionNodeKind kind,
        DaevanionNodeAppearance beforeAppearance, DaevanionNodeAppearance afterAppearance, bool samePixels)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = original.Nodes.First(item => item.Kind == kind);
        var board = original with { Nodes = [node] };
        var before = Source(board, [new(node.Row, node.Column, new(25, 25, 60, 60), .95) { Appearance = beforeAppearance }]);
        var after = before with { FrameAt = 200, Detection = before.Detection with
        {
            Nodes = [before.Detection.Nodes[0] with { Appearance = afterAppearance, Confidence = .80 }]
        } };
        using var renderer = new DaevanionOverlayRenderer();
        using var first = renderer.Render(new(120, 120), board, before.Detection.Nodes, "en", spacing: 76);
        using var second = renderer.Render(new(120, 120), board, after.Detection.Nodes, "en", spacing: 76);
        Assert.Equal(samePixels, Pixels(first).SequenceEqual(Pixels(second)));
        Assert.Equal(samePixels, DaevanionOverlayForm.HasSameContent(before, after));
        if (!samePixels) return;
        var originalPixels = Pixels(first);
        Assert.Same(after, DaevanionOverlayForm.EquivalentCurrent(before, after));
        Assert.Same(after, DaevanionOverlayForm.ReconcileRendered(first, before, after, Point.Empty));
        Assert.Equal(originalPixels, Pixels(first));
        Assert.Equal(200, after.FrameAt);
        Assert.Equal(afterAppearance, after.Detection.Nodes[0].Appearance);
    }

    [Fact]
    public void OcclusionDuringAnEquivalentDarkAppearanceChangeRetainsFreshAnchorsAndOnlyAddsFreshReturners()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var nodes = original.Nodes.Where(item => item.Kind == DaevanionNodeKind.Stat).Take(3)
            .Select((item, index) => item with { Row = 3, Column = 3 + index }).ToArray();
        var board = original with { Nodes = nodes };
        var anchors = nodes.Select((item, index) => new DaevanionNodeAnchor(item.Row, item.Column,
            new(25 + index * 76, 25, 60, 60), .95)).ToArray();
        var before = Source(board, anchors[..2]);
        var current = before with { FrameAt = 200, Detection = before.Detection with
        {
            Nodes = [anchors[0] with { Appearance = DaevanionNodeAppearance.Inactive, Confidence = .77 }, anchors[2]]
        } };
        var origin = new Point(7, 11);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(300, 140), board, before.Detection.Nodes, "en", origin, 76);
        using var unchanged = (Bitmap)image.Clone();
        var retained = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, before, current, origin));
        Assert.Equal(current.FrameAt, retained.FrameAt);
        Assert.Same(current.Detection.Nodes[0], Assert.Single(retained.Detection.Nodes));
        Assert.Same(current.Detection.Nodes[1], Assert.Single(DaevanionOverlayForm.MissingCurrentNodes(retained, current)));
        var hidden = Envelope(anchors[1], origin);
        var kept = Envelope(anchors[0], origin);
        var hadFooter = false;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (hidden.Contains(x, y))
            {
                if (unchanged.GetPixel(x, y).A > 0 && y >= anchors[1].Bounds.Bottom - origin.Y) hadFooter = true;
                Assert.Equal(0, image.GetPixel(x, y).A);
            }
            if (kept.Contains(x, y)) Assert.Equal(unchanged.GetPixel(x, y), image.GetPixel(x, y));
        }
        Assert.True(hadFooter);
    }

    [Fact]
    public void FullBoardReconciliationAndMembershipChecksReadAnchorsLinearly()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var nodes = board.Nodes.Where(item => item.Kind == DaevanionNodeKind.Stat).Select(item =>
            new DaevanionNodeAnchor(item.Row, item.Column,
                new(12 + (item.Column - 1) * 76, 12 + (item.Row - 1) * 76, 60, 60), .95)).ToArray();
        var drawn = new CountingNodes(nodes);
        var fresh = new CountingNodes(nodes.Skip(1).Reverse().Select(item => item with
            { Appearance = DaevanionNodeAppearance.Inactive, Confidence = .85 }).ToArray());
        var before = Source(board, drawn);
        var after = before with { FrameAt = 200, Detection = before.Detection with { Nodes = fresh } };
        using var image = new Bitmap(1220, 1220, PixelFormat.Format32bppPArgb);
        var result = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, before, after, Point.Empty));
        Assert.Equal(fresh.Count, result.Detection.Nodes.Count);
        Assert.Equal(after.FrameAt, result.FrameAt);
        Assert.True(DaevanionOverlayForm.HasSameContent(result, after));
        Assert.Empty(DaevanionOverlayForm.MissingCurrentNodes(result, after));
        Assert.InRange(drawn.Reads + fresh.Reads, 1, nodes.Length * 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrDuplicateCurrentCellsCannotPublishAReconciledSurface(bool duplicate)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var nodes = board.Nodes.Where(item => item.Kind == DaevanionNodeKind.Stat).Take(2).ToArray();
        var anchors = nodes.Select((item, index) => new DaevanionNodeAnchor(item.Row, item.Column,
            new(20 + index * 76, 20, 60, 60), .95)).ToArray();
        var before = Source(board, anchors);
        var after = before with { FrameAt = 200, Detection = before.Detection with
            { Nodes = [anchors[0], duplicate ? anchors[0] : anchors[1] with { Row = 16 }] } };
        using var image = new Bitmap(10, 10, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
        var original = Pixels(image);
        Assert.False(DaevanionOverlayForm.HasSameContent(before, after));
        Assert.Null(DaevanionOverlayForm.ReconcileRendered(image, before, after, Point.Empty));
        Assert.Equal(original, Pixels(image));
    }

    private static DaevanionSnapshot Source(DaevanionBoard board, IReadOnlyList<DaevanionNodeAnchor> anchors) =>
        new(new OwnCharacterContext(new(101, 501, 300, "SyntheticProjection") { ClassId = "Cleric" }, 1), board,
            new(0, 0, 1220, 1220), 100, new(board.FamilyId, new(42, 42), 76, 60, anchors, .95), "en");

    private static Rectangle Envelope(DaevanionNodeAnchor anchor, Point origin)
    {
        var bounds = DaevanionNodeFitting.Bounds(new Rectangle(anchor.Bounds.X - origin.X, anchor.Bounds.Y - origin.Y,
            anchor.Bounds.Width, anchor.Bounds.Height), 76);
        return Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
            (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < image.Height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, result,
                row * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return result;
    }

    private sealed class CountingNodes(DaevanionNodeAnchor[] nodes) : IReadOnlyList<DaevanionNodeAnchor>
    {
        internal int Reads { get; private set; }
        public int Count => nodes.Length;
        public DaevanionNodeAnchor this[int index] { get { Reads++; return nodes[index]; } }
        public IEnumerator<DaevanionNodeAnchor> GetEnumerator()
        { for (var index = 0; index < nodes.Length; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
