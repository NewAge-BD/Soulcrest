using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionFittingTests
{
    [Theory]
    [InlineData(DaevanionNodeQuality.Common)]
    [InlineData(DaevanionNodeQuality.Rare)]
    [InlineData(DaevanionNodeQuality.Legend)]
    [InlineData(DaevanionNodeQuality.Unique)]
    public void OutsideFittingUsesTheNativeQualityColor(DaevanionNodeQuality quality)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat) with { Quality = quality };
        board = board with { Nodes = [node] };
        var frame = new Rectangle(30, 30, 60, 60);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(140, 140), board,
            [new(node.Row, node.Column, new(frame.X, frame.Y, frame.Width, frame.Height), 1)], "en", spacing: 76);
        var exterior = ReadPixels(image).Where(p => !frame.Contains(p.X, p.Y) && p.Color.A > 160).Select(p => p.Color).ToArray();
        Assert.NotEmpty(exterior);
        Assert.Contains(exterior, color => quality switch
        {
            DaevanionNodeQuality.Rare => color.G > color.R * 1.18 && color.G > color.B * 1.05,
            DaevanionNodeQuality.Legend => color.B > color.R * 1.15 && color.B > color.G * 1.02,
            DaevanionNodeQuality.Unique => color.R > color.G * 1.18 && color.G > color.B * 1.1,
            _ => color.B >= color.R && color.G >= color.R && color.B - color.R is >= 4 and < 55 && color.G > 55
        });
    }

    [Theory]
    [InlineData(48, 61)]
    [InlineData(60, 76)]
    [InlineData(90, 114)]
    [InlineData(100, 116)]
    public void EveryStatCombinationFitsTheObservedGridWithoutTouchingItsNativeRing(int nodeSize, int spacing)
    {
        using var renderer = new DaevanionOverlayRenderer();
        var frame = new Rectangle(24, 24, nodeSize, nodeSize);
        var combinations = DaevanionCatalog.Default.Boards.SelectMany(b => b.Nodes.Select(n => (Board: b, Node: n)))
            .Where(pair => pair.Node.Kind == DaevanionNodeKind.Stat)
            .DistinctBy(pair => pair.Node.Quality + ":" + string.Join(";", pair.Node.Effects.Select(e => e.StatId + ":" + e.Amount)));
        foreach (var (board, node) in combinations)
        foreach (var language in new[] { "de", "en" })
        {
            using var image = renderer.Render(new(nodeSize + 64, nodeSize + 64), board,
                [new(node.Row, node.Column, new(frame.X, frame.Y, frame.Width, frame.Height), 1)], language, spacing: spacing);
            var painted = ReadPixels(image).Where(p => p.Color.A > 0).ToArray();
            Assert.NotEmpty(painted);
            var envelope = Rectangle.Union(frame, Rectangle.FromLTRB(painted.Min(p => p.X), painted.Min(p => p.Y),
                painted.Max(p => p.X) + 1, painted.Max(p => p.Y) + 1));
            Assert.True(envelope.Width < spacing && envelope.Height < spacing,
                $"{board.FamilyId}/{node.Id}/{language}: {envelope.Width}x{envelope.Height} cannot fit {spacing}px grid.");
            var center = new PointF(frame.X + nodeSize / 2f, frame.Y + nodeSize / 2f);
            Assert.DoesNotContain(painted, p => frame.Contains(p.X, p.Y)
                && Math.Sqrt(Math.Pow(p.X + .5 - center.X, 2) + Math.Pow(p.Y + .5 - center.Y, 2)) >= nodeSize * .36);
        }
    }

    [Fact]
    public void MissingNeighborAnchorReceivesNoPaintFromTheAdjacentFitting()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat) with { Row = 3, Column = 3 };
        var missing = node with { Id = node.Id + 1, Row = 4 };
        board = board with { Nodes = [node, missing] };
        var missingFrame = new Rectangle(30, 106, 60, 60);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(130, 190), board,
            [new(node.Row, node.Column, new(30, 30, 60, 60), 1)], "en", spacing: 76);
        Assert.DoesNotContain(ReadPixels(image), p => missingFrame.Contains(p.X, p.Y) && p.Color.A != 0);
        Assert.Contains(ReadPixels(image), p => p.Y >= 90 && p.Y < missingFrame.Top && p.Color.A > 0);
    }

    [Fact]
    public void ExtremelyTightGridFallsBackToTheRoundInteriorWithoutOutsidePaint()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
        var frame = new Rectangle(20, 20, 60, 60);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(120, 120), board,
            [new(node.Row, node.Column, new(frame.X, frame.Y, frame.Width, frame.Height), 1)], "en", spacing: 62);
        Assert.False(DaevanionNodeFitting.CanExtend(frame, 62));
        var painted = ReadPixels(image).Where(p => p.Color.A > 0).ToArray();
        Assert.NotEmpty(painted);
        Assert.All(painted, p => Assert.True(frame.Contains(p.X, p.Y)));
    }

    private static IEnumerable<(int X, int Y, Color Color)> ReadPixels(Bitmap image)
    {
        var values = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, values, y * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            yield return (x, y, Color.FromArgb(values[y * image.Width + x]));
    }
}
