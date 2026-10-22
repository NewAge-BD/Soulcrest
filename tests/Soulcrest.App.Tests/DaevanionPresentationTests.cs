using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionPresentationTests
{
    private static DaevanionSnapshot Picture()
    {
        var catalog = DaevanionCatalog.Default;
        var source = catalog.GetBoard("Cleric", "ariel")!;
        var nodes = source.Nodes.Where(n => n.Kind == DaevanionNodeKind.Stat).Take(3)
            .Select((node, index) => node with { Row = 3, Column = 3 + index }).ToArray();
        var board = source with { Nodes = nodes };
        var anchors = nodes.Select((node, index) => new DaevanionNodeAnchor(node.Row, node.Column,
            new(20 + index * 76, 20, 60, 60), .95)).ToArray();
        var context = new OwnCharacterContext(new(101, 501, 300, "SyntheticBoard") { ClassId = "Cleric" }, 1);
        return new(context, board, new(0, 0, 300, 140), Stopwatch.GetTimestamp(),
            new("ariel", new(-102, -102), 76, 60, anchors, .95), "en");
    }

    private static DaevanionSnapshot Visible(DaevanionSnapshot source, params int[] indices) => source with
    {
        FrameAt = Stopwatch.GetTimestamp(),
        Detection = source.Detection with { Nodes = indices.Select(index => source.Detection.Nodes[index]).ToArray() }
    };

    [Fact]
    public void TooltipPublishedDuringPaintingRemovesOnlyCoveredArtworkAndKeepsTheFreshSourceTime()
    {
        var source = Picture();
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(source.GameBounds.Size, source.Board, source.Detection.Nodes, source.Language, spacing: 76);
        using var before = (Bitmap)image.Clone();
        var current = Visible(source, 0, 2);
        var artwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, source, current, Point.Empty));
        Assert.Equal(current.FrameAt, artwork.FrameAt);
        Assert.Equal(new[] { 3, 5 }, artwork.Detection.Nodes.Select(node => node.Column));
        Assert.True(DaevanionOverlayForm.HasSameContent(artwork, current));
        AssertNodeTransparent(image, source.Detection.Nodes[1]);
        AssertNodeUnchanged(before, image, source.Detection.Nodes[0]);
        AssertNodeUnchanged(before, image, source.Detection.Nodes[2]);
    }

    [Fact]
    public void TwoDifferentTooltipMasksDuringOneRenderKeepTheirCommonNodesWithoutAWholeBoardBlank()
    {
        var source = Picture();
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(source.GameBounds.Size, source.Board, source.Detection.Nodes, source.Language, spacing: 76);
        using var before = (Bitmap)image.Clone();
        var first = Visible(source, 0, 2);
        var firstArtwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, source, first, Point.Empty));
        var latest = Visible(source, 0, 1);
        var latestArtwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, firstArtwork, latest, Point.Empty));
        Assert.Single(latestArtwork.Detection.Nodes);
        Assert.Equal(source.Detection.Nodes[0].Column, latestArtwork.Detection.Nodes[0].Column);
        AssertNodeTransparent(image, source.Detection.Nodes[1]);
        AssertNodeTransparent(image, source.Detection.Nodes[2]);
        AssertNodeUnchanged(before, image, source.Detection.Nodes[0]);
        Assert.False(DaevanionOverlayForm.HasSameContent(latestArtwork, latest));
        Assert.Equal(DaevanionOverlayForm.SurfaceBounds(source), DaevanionOverlayForm.SurfaceBounds(latestArtwork));
        // The next current render may add the returning node, without reviving the covered one.
        using var next = renderer.Render(latest.GameBounds.Size, latest.Board, latest.Detection.Nodes, latest.Language, spacing: 76);
        AssertNodeTransparent(next, source.Detection.Nodes[2]);
        Assert.True(HasPaint(next, source.Detection.Nodes[1]));
        AssertNodeUnchanged(before, next, source.Detection.Nodes[0]);
    }

    [Theory]
    [InlineData(48, 61)]
    [InlineData(60, 76)]
    [InlineData(86, 104.5)]
    [InlineData(100, 116)]
    public void VerticalOcclusionPreservesEveryOtherPixelOnAFullTwoDimensionalGrid(int nodeSize, double spacing)
    {
        var source = Picture();
        var nodes = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!.Nodes
            .Where(node => node.Kind == DaevanionNodeKind.Stat).Take(9)
            .Select((node, index) => node with { Row = 3 + index / 3, Column = 3 + index % 3 }).ToArray();
        var anchors = nodes.Select((node, index) => new DaevanionNodeAnchor(node.Row, node.Column,
            new((int)Math.Round(30.25 + index % 3 * spacing), (int)Math.Round(30.75 + index / 3 * spacing), nodeSize, nodeSize), .95)).ToArray();
        var side = (int)Math.Ceiling(2 * spacing + nodeSize + 70);
        source = source with
        {
            Board = source.Board with { Nodes = nodes }, GameBounds = new(0, 0, side, side),
            Detection = new("ariel", new(30.25 + nodeSize / 2d - 2 * spacing, 30.75 + nodeSize / 2d - 2 * spacing),
                spacing, nodeSize, anchors, .95)
        };
        var origin = new Point(7, 11);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(source.GameBounds.Size, source.Board, source.Detection.Nodes, source.Language, origin, spacing);
        using var expected = (Bitmap)image.Clone();
        var artwork = source;
        // First hide the center; the next fresh tooltip grows down over its direct neighbor.
        foreach (var removed in new[] { 4, 7 })
        {
            var current = artwork with
            {
                FrameAt = Stopwatch.GetTimestamp(), Detection = artwork.Detection with
                {
                    Nodes = artwork.Detection.Nodes.Where(node => node.Row != anchors[removed].Row || node.Column != anchors[removed].Column).ToArray()
                }
            };
            using var removedArtwork = renderer.Render(source.GameBounds.Size, source.Board, [anchors[removed]], source.Language, origin, spacing);
            for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                if (removedArtwork.GetPixel(x, y).A != 0) expected.SetPixel(x, y, Color.Transparent);
            artwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, artwork, current, origin));
            Assert.True(DaevanionOverlayForm.HasSameContent(artwork, current));
            for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), image.GetPixel(x, y));
        }
        Assert.Equal(7, artwork.Detection.Nodes.Count);
    }

    [Fact]
    public void AppearanceChangeRequiresNewArtworkAndCannotReuseThePreviousBackground()
    {
        var source = Visible(Picture(), 0);
        var active = source with { Detection = source.Detection with
        {
            Nodes = [source.Detection.Nodes[0] with { Appearance = DaevanionNodeAppearance.Active }]
        } };
        Assert.True(DaevanionOverlayForm.HasSameBoard(source, active));
        Assert.False(DaevanionOverlayForm.HasSameContent(source, active));
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(source.GameBounds.Size, source.Board, source.Detection.Nodes, source.Language, spacing: 76);
        var artwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, source, active, Point.Empty));
        Assert.Empty(artwork.Detection.Nodes);
        AssertNodeTransparent(image, source.Detection.Nodes[0]);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("board")]
    [InlineData("language")]
    [InlineData("bounds")]
    [InlineData("origin")]
    [InlineData("spacing")]
    public void ReconciliationCannotAcceptAnotherBoardOrContext(string change)
    {
        var source = Picture();
        var current = change switch
        {
            "context" => source with { Context = source.Context with { Generation = 2 } },
            "board" => source with { Board = DaevanionCatalog.Default.GetBoard("Cleric", "azphel")! },
            "language" => source with { Language = "de" },
            "bounds" => source with { GameBounds = source.GameBounds with { X = 1 } },
            "origin" => source with { Detection = source.Detection with { Origin = new(-101, -102) } },
            _ => source with { Detection = source.Detection with { Spacing = 77 } }
        };
        using var image = new Bitmap(1, 1);
        Assert.Null(DaevanionOverlayForm.ReconcileRendered(image, source, current, Point.Empty));
    }

    private static Rectangle ArtworkBounds(DaevanionNodeAnchor node) => Rectangle.FromLTRB(node.Bounds.X - 6, node.Bounds.Y - 4,
        node.Bounds.Right + 6, node.Bounds.Bottom + 12);

    private static bool HasPaint(Bitmap image, DaevanionNodeAnchor node)
    {
        var area = ArtworkBounds(node);
        return Enumerable.Range(area.Top, area.Height).Any(y => Enumerable.Range(area.Left, area.Width).Any(x => image.GetPixel(x, y).A > 0));
    }

    private static void AssertNodeTransparent(Bitmap image, DaevanionNodeAnchor node)
    {
        var area = ArtworkBounds(node);
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            Assert.Equal(0, image.GetPixel(x, y).A);
    }

    private static void AssertNodeUnchanged(Bitmap expected, Bitmap actual, DaevanionNodeAnchor node)
    {
        var area = ArtworkBounds(node);
        Assert.True(HasPaint(actual, node));
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }
}
