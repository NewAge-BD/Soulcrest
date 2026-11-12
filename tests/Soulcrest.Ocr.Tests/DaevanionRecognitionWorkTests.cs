using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class DaevanionRecognitionWorkTests
{
    private static readonly IReadOnlyList<DaevanionBoardPattern> Patterns = DaevanionCatalog.Default.Boards
        .Where(board => board.ClassId == "Cleric").Select(board => new DaevanionBoardPattern(board.FamilyId,
            board.Nodes.Select(node => new DaevanionPatternCell(node.Row, node.Column, node.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();

    [Fact]
    public void MetricsDescribeActualGlobalAndPreferredWorkWithoutChangingRecognition()
    {
        using var image = Read("nezekan-board.png");
        var original = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var cold = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, null, out var global));
        Assert.Equal(original, cold with { Nodes = original.Nodes });
        Assert.Equal(original.Nodes, cold.Nodes);
        Assert.Equal(DaevanionRecognitionPath.GlobalSearch, global.Path);
        Assert.True(global.ComputedScores > 0);
        Assert.True(global.ReusedScores > 0);
        Assert.True(double.IsFinite(global.ElapsedMilliseconds));
        Assert.True(global.ElapsedMilliseconds >= 0);
        var current = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, cold, out var preferred));
        Assert.Equal(DaevanionRecognitionPath.PreferredRaster, preferred.Path);
        Assert.True(preferred.ComputedScores > 0);
        Assert.True(preferred.ReusedScores > 0);
        Assert.Equal(cold.Origin, current.Origin);
        Assert.Equal(cold.Spacing, current.Spacing);
        Assert.Equal(cold.NodeSize, current.NodeSize);
        Assert.Equal(cold.Nodes, current.Nodes);
    }

    [Fact]
    public void ARepeatedMatWithNewPixelsCannotReuseAnEarlierFramesNodeProof()
    {
        using var image = Read("nezekan-board.png");
        var original = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var covered = original.Nodes.Single(node => node.Row == 3 && node.Column == 3);
        Cv2.Rectangle(image, new Rect(covered.Bounds.X + 5, covered.Bounds.Y + 5,
            covered.Bounds.Width - 10, covered.Bounds.Height - 10), new Scalar(30, 33, 35), -1);
        Cv2.PutText(image, "Info", new Point(covered.Bounds.X + 9, covered.Bounds.Y + covered.Bounds.Height / 2),
            HersheyFonts.HersheySimplex, .5, Scalar.White, 1);
        var current = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, original, out var metrics));
        Assert.Equal(DaevanionRecognitionPath.PreferredRaster, metrics.Path);
        Assert.DoesNotContain(current.Nodes, node => node.Row == 3 && node.Column == 3);
        Assert.Contains(current.Nodes, node => node.Row == 3 && node.Column == 4);
        Assert.Equal(original.Origin, current.Origin);
    }

    [Fact]
    public async Task ConcurrentFramesHaveIndependentScoreCaches()
    {
        using var original = Read("nezekan-board.png");
        using var pulse = Read("vaizel-animation-peak.png");
        var first = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(original, Patterns));
        var second = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(pulse, Patterns));
        // Compare the same preferred path: native-size revalidation can legitimately refine confidence.
        var firstExpected = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(original, Patterns, first));
        var secondExpected = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(pulse, Patterns, second));
        var tasks = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            var expected = index % 2 == 0 ? firstExpected : secondExpected;
            var image = index % 2 == 0 ? original : pulse;
            var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, expected, out var metrics));
            Assert.Equal(expected.Family, result.Family);
            Assert.Equal(expected.Origin, result.Origin);
            Assert.Equal(expected.Nodes, result.Nodes);
            Assert.Equal(DaevanionRecognitionPath.PreferredRaster, metrics.Path);
            Assert.True(metrics.ReusedScores > 0);
        }));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void InvalidAndUnrelatedImagesReportNoImaginaryRecognitionWork()
    {
        using var empty = new Mat();
        Assert.Null(DaevanionBoardLocator.Find(empty, Patterns, null, out var invalid));
        Assert.Equal(DaevanionRecognitionPath.InvalidInput, invalid.Path);
        Assert.Equal(0, invalid.ComputedScores);
        Assert.Equal(0, invalid.ReusedScores);
        using var unrelated = new Mat(800, 1000, MatType.CV_8UC3, new Scalar(30, 33, 35));
        Assert.Null(DaevanionBoardLocator.Find(unrelated, Patterns, null, out var global));
        Assert.Equal(DaevanionRecognitionPath.GlobalSearch, global.Path);
        Assert.Equal(0, global.ComputedScores);
        Assert.Equal(0, global.ReusedScores);
    }

    private static Mat Read(string file)
    {
        var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "daevanion", file));
        Assert.False(image.Empty());
        return image;
    }
}
