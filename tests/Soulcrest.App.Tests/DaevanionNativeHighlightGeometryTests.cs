using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNativeHighlightGeometryTests
{
    [Theory]
    [InlineData(32, 44)]
    [InlineData(48, 61)]
    [InlineData(60, 76)]
    [InlineData(90, 114)]
    [InlineData(100, 116)]
    [InlineData(60, 62)]
    public void TheNativeContourHasEqualMarginsAndFitsTheExistingPruningEnvelope(int size, int spacing)
    {
        var frame = new RectangleF(24, 24, size, size);
        var actual = DaevanionHighlightRenderer.ContourBounds(frame, spacing);
        var conservative = DaevanionNodeFitting.Bounds(frame, spacing);
        var padding = frame.Left - actual.Left;
        Assert.True(padding > 0);
        Assert.Equal(padding, actual.Right - frame.Right, 4);
        Assert.Equal(padding, frame.Top - actual.Top, 4);
        Assert.Equal(padding, actual.Bottom - frame.Bottom, 4);
        const float tolerance = .0001f;
        Assert.True(actual.Left >= conservative.Left - tolerance && actual.Top >= conservative.Top - tolerance
            && actual.Right <= conservative.Right + tolerance && actual.Bottom <= conservative.Bottom + tolerance);
        Assert.True(actual.Width < spacing && actual.Height < spacing);
    }

    [Theory]
    [InlineData("ariel", 48, 61)]
    [InlineData("ariel", 60, 76)]
    [InlineData("azphel", 60, 76)]
    [InlineData("azphel", 100, 116)]
    public void EveryPhaseFollowsTheNativeFrameWithoutTheFormerFooter(string family, int size, int spacing)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = original.Nodes.First(item => item.Kind == DaevanionNodeKind.Stat);
        var board = original with { Nodes = [node] };
        var frame = new Rectangle(24, 24, size, size);
        var envelope = DaevanionHighlightRenderer.ContourBounds(frame, spacing);
        var anchors = new[] { new DaevanionNodeAnchor(node.Row, node.Column,
            new(frame.X, frame.Y, frame.Width, frame.Height), 1) };
        var selected = new HashSet<string>([node.Effects[0].StatId!], StringComparer.Ordinal);
        var renderer = new DaevanionHighlightRenderer();
        for (var phase = 0; phase < 8; phase++)
        {
            using var image = renderer.Render(new(size + 64, size + 64), board, anchors, selected,
                spacing: spacing, phase: phase / 8d);
            var alpha = Alpha(image);
            Assert.Contains(alpha, value => value > 180);
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (alpha[y * image.Width + x] == 0) continue;
                Assert.False(frame.Contains(x, y));
                Assert.True(envelope.IntersectsWith(new RectangleF(x, y, 1, 1)));
            }
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    public void AGapWithNoReservedMarginReceivesNoContour(int spacing)
    {
        var frame = new RectangleF(24, 24, 60, 60);
        Assert.Equal(frame, DaevanionHighlightRenderer.ContourBounds(frame, spacing));
    }

    private static byte[] Alpha(Bitmap image)
    {
        var result = new byte[image.Width * image.Height];
        var row = new int[image.Width];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < row.Length; x++) result[y * image.Width + x] = (byte)(row[x] >> 24);
            }
        }
        finally { image.UnlockBits(data); }
        return result;
    }
}
