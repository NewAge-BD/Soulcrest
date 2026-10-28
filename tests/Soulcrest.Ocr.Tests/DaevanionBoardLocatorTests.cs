using OpenCvSharp;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Ocr.Tests;

public sealed class DaevanionBoardLocatorTests
{
    private static readonly IReadOnlyList<DaevanionBoardPattern> Patterns = DaevanionCatalog.Default.Boards
        .Where(b => b.ClassId == "Cleric")
        .Select(b => new DaevanionBoardPattern(b.FamilyId, b.Nodes.Select(n =>
            new DaevanionPatternCell(n.Row, n.Column, n.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();

    [Fact]
    public void RealBoardUsesNativeIndicesAndConfirmsDarkNodes()
    {
        using var screenshot = ReadFixture();
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(screenshot, Patterns));
        Assert.Equal("nezekan", result.Family);
        Assert.InRange(result.Spacing, 115, 117);
        Assert.InRange(result.NodeSize, 97, 104);
        Assert.Equal(1, result.Confidence);
        Assert.True(result.Nodes.Count == 88, "Missing: " + string.Join(", ", Patterns.Single(p => p.Family == "nezekan").Cells
            .Where(c => !c.IsStart && !result.Nodes.Any(n => n.Row == c.Row && n.Column == c.Column)).Select(c => $"({c.Row},{c.Column})")));
        Assert.Contains(result.Nodes, n => n.Row == 5 && n.Column == 4);
        Assert.DoesNotContain(result.Nodes, n => n.Row == 8 && n.Column == 8);
        var top = Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 3);
        Assert.InRange(top.Bounds.X + top.Bounds.Width / 2d, 77, 81);
        Assert.InRange(top.Bounds.Y + top.Bounds.Height / 2d, 77, 81);
        Assert.Equal(DaevanionNodeAppearance.Active, top.Appearance);
        Assert.Equal(DaevanionNodeAppearance.Inactive, Assert.Single(result.Nodes, n => n.Row == 5 && n.Column == 4).Appearance);
    }

    [Theory]
    [InlineData(.65, 45, 25, 1.0)]
    [InlineData(.75, 35, 40, 1.0)]
    [InlineData(.85, 30, 60, .65)]
    [InlineData(1.25, 65, 35, 1.15)]
    public void RealBoardFollowsTranslationScaleAndCaptureBrightness(double scale, int left, int top, double brightness)
    {
        using var screenshot = ReadFixture();
        using var scaled = new Mat(); Cv2.Resize(screenshot, scaled, new Size(), scale, scale, InterpolationFlags.Linear);
        using var canvas = new Mat(scaled.Height + top + 30, scaled.Width + left + 30, MatType.CV_8UC3, new Scalar(23, 25, 26));
        using (var target = new Mat(canvas, new Rect(left, top, scaled.Width, scaled.Height))) scaled.CopyTo(target);
        canvas.ConvertTo(canvas, MatType.CV_8UC3, brightness);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(canvas, Patterns));
        Assert.Equal("nezekan", result.Family);
        Assert.InRange(result.Spacing, 115 * scale, 117.5 * scale);
        Assert.True(result.Nodes.Count == 88, "Missing: " + string.Join(", ", Patterns.Single(p => p.Family == "nezekan").Cells
            .Where(c => !c.IsStart && !result.Nodes.Any(n => n.Row == c.Row && n.Column == c.Column)).Select(c => $"({c.Row},{c.Column})")));
        var node = Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 3);
        Assert.InRange(node.Bounds.X + node.Bounds.Width / 2d, left + 77 * scale, left + 82 * scale);
        Assert.Equal(DaevanionNodeAppearance.Active, node.Appearance);
    }

    [Theory]
    [InlineData("nezekan")]
    [InlineData("zikel")]
    [InlineData("vaizel")]
    [InlineData("triniel")]
    [InlineData("ariel")]
    [InlineData("azphel")]
    public void EveryFamilyUsesOccupiedAndEmptyCellMask(string family)
    {
        using var image = RenderReference(family);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        Assert.Equal(family, result.Family);
        Assert.Equal(Patterns.Single(p => p.Family == family).Cells.Count - 1, result.Nodes.Count);
        Assert.InRange(result.Confidence, .98, 1);
    }

    [Fact]
    public void CoveredNodeIsOmittedButUncoveredNeighborsRemain()
    {
        using var image = ReadFixture();
        Cv2.Rectangle(image, new Rect(22, 22, 114, 114), new Scalar(30, 33, 35), -1);
        Cv2.PutText(image, "Tooltip", new Point(28, 88), HersheyFonts.HersheySimplex, .65, Scalar.White, 1);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        Assert.DoesNotContain(result.Nodes, n => n.Row == 3 && n.Column == 3);
        Assert.Contains(result.Nodes, n => n.Row == 3 && n.Column == 4);
    }

    [Fact]
    public void InteriorOcclusionCannotBeRecoveredFromAnUncoveredFrame()
    {
        using var image = ReadFixture();
        Cv2.Rectangle(image, new Rect(45, 45, 69, 69), new Scalar(30, 33, 35), -1);
        Cv2.PutText(image, "Text", new Point(48, 88), HersheyFonts.HersheySimplex, .55, Scalar.White, 1);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        Assert.DoesNotContain(result.Nodes, n => n.Row == 3 && n.Column == 3);
        Assert.Contains(result.Nodes, n => n.Row == 3 && n.Column == 4);
    }

    [Fact]
    public void CoveringTheDistinguishingCellsMakesFamilyAmbiguous()
    {
        using var image = RenderReference("nezekan");
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var other = Patterns.Single(p => p.Family == "zikel").Cells.Select(c => (c.Row, c.Column)).ToHashSet();
        foreach (var cell in Patterns.Single(p => p.Family == "nezekan").Cells.Where(c => !other.Contains((c.Row, c.Column))))
            Cv2.Rectangle(image, new Rect(140 + (cell.Column - 1) * 68 - 33, 100 + (cell.Row - 1) * 68 - 33, 66, 66),
                new Scalar(29, 33, 34), -1);
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns));
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns, preferred));
    }

    [Fact]
    public void CoveredStartOrMissingBoardReturnsNothing()
    {
        using var image = ReadFixture();
        Cv2.Rectangle(image, new Rect(570, 570, 180, 180), new Scalar(30, 33, 35), -1);
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns));
        using var empty = new Mat(1000, 1500, MatType.CV_8UC3, new Scalar(40, 40, 40));
        Assert.Null(DaevanionBoardLocator.Find(empty, Patterns));
    }

    [Fact]
    public void OrdinarySquareGridIsNotABoard()
    {
        using var image = new Mat(1100, 1400, MatType.CV_8UC3, new Scalar(25, 25, 25));
        foreach (var cell in Patterns.Single(p => p.Family == "nezekan").Cells)
        {
            var rect = new Rect(80 + cell.Column * 64, 25 + cell.Row * 64, 54, 54);
            Cv2.Rectangle(image, rect, new Scalar(150, 165, 175), 2);
            Cv2.PutText(image, "+5", new Point(rect.X + 6, rect.Y + 35), HersheyFonts.HersheySimplex, .7, Scalar.White, 1);
        }
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns));
    }

    [Theory]
    [InlineData("esc-menu", "user-2026-10-10.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-small.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-medium.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-large.png")]
    [InlineData("map-tracking", "live-2026-10-03-screen.png")]
    [InlineData("map-tracking", "live-2026-10-03-lost-screen.png")]
    [InlineData("map-tracking", "live-2026-10-03-worldmap-drift-screen.png")]
    public void ExistingRealGameMenusAndWorldMapsAreNotBoards(string folder, string file)
    {
        using var frame = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", folder, file));
        Assert.False(frame.Empty());
        Assert.True(frame.Width >= 220 && frame.Height >= 220);
        Assert.Null(DaevanionBoardLocator.Find(frame, Patterns));
        using var board = ReadFixture();
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(board, Patterns));
        Assert.Null(DaevanionBoardLocator.Find(frame, Patterns, preferred));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void GrayAndBgraFramesAreAccepted(int channels)
    {
        using var image = ReadFixture(); using var converted = new Mat();
        Cv2.CvtColor(image, converted, channels == 1 ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGR2BGRA);
        Assert.Equal("nezekan", DaevanionBoardLocator.Find(converted, Patterns)?.Family);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(.35, -.25, .8)]
    [InlineData(-.35, .25, 1.1)]
    public void FreshPixelsConfirmTheExactPreferredRaster(double dx, double dy, double brightness)
    {
        using var original = ReadFixture();
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(original, Patterns));
        using var transform = Mat.FromArray(new double[,] { { 1, 0, dx }, { 0, 1, dy } });
        using var current = new Mat();
        Cv2.WarpAffine(original, current, transform, original.Size(), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(29, 33, 34));
        current.ConvertTo(current, MatType.CV_8UC3, brightness);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(current, Patterns, preferred));
        Assert.Equal(preferred.Origin, result.Origin);
        Assert.Equal(preferred.Spacing, result.Spacing);
        Assert.Equal(preferred.NodeSize, result.NodeSize);
        Assert.Equal(88, result.Nodes.Count);
        Assert.Equal(preferred.Nodes.Select(n => n.Bounds), result.Nodes.Select(n => n.Bounds));
    }

    [Theory]
    [InlineData("nezekan")]
    [InlineData("zikel")]
    [InlineData("vaizel")]
    [InlineData("triniel")]
    [InlineData("ariel")]
    [InlineData("azphel")]
    public void PreferredRasterIndependentlyReconfirmsEveryFamily(string family)
    {
        using var image = RenderReference(family);
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
        Assert.Equal(family, result.Family);
        Assert.Equal(preferred.Origin, result.Origin);
        Assert.Equal(preferred.Nodes.Count, result.Nodes.Count);
        Assert.Equal(preferred.Nodes.Select(n => n.Bounds), result.Nodes.Select(n => n.Bounds));
    }

    [Fact]
    public void PreferredRasterNeverRestoresCoveredNodeOrStart()
    {
        using var image = ReadFixture();
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        Cv2.Rectangle(image, new Rect(45, 45, 69, 69), new Scalar(30, 33, 35), -1);
        var current = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
        Assert.DoesNotContain(current.Nodes, n => n.Row == 3 && n.Column == 3);
        Assert.Equal(preferred.Origin, current.Origin);
        // A tooltip over the start keeps the confirmed raster, but adds no node behind it.
        var cover = new Rect(570, 570, 180, 180);
        Cv2.Rectangle(image, cover, new Scalar(30, 33, 35), -1);
        var tooltip = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
        Assert.Equal(preferred.Origin, tooltip.Origin);
        Assert.DoesNotContain(tooltip.Nodes, n => cover.Contains(new Point(n.Bounds.X + n.Bounds.Width / 2, n.Bounds.Y + n.Bounds.Height / 2)));
        Assert.True(tooltip.Nodes.Count < preferred.Nodes.Count);
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns));
        // Without enough independently visible native frames the hypothesis is rejected.
        Cv2.Rectangle(image, new Rect(300, 300, 700, 700), new Scalar(30, 33, 35), -1);
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns, preferred));
    }

    [Fact]
    public void PreferredRasterDoesNotOverrideDifferentFamilyOrRealMenu()
    {
        using var original = RenderReference("nezekan");
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(original, Patterns));
        using var switched = RenderReference("zikel");
        Assert.Equal("zikel", DaevanionBoardLocator.Find(switched, Patterns, preferred)?.Family);
        using var menu = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "esc-menu", "user-2026-10-10.png"));
        Assert.False(menu.Empty());
        Assert.Null(DaevanionBoardLocator.Find(menu, Patterns, preferred));
        using var blank = new Mat(original.Size(), original.Type(), new Scalar(29, 33, 34));
        Assert.Null(DaevanionBoardLocator.Find(blank, Patterns, preferred));
    }

    [Fact]
    public void ArielAndAzphelHaveDistinctNativeMasksForEveryClass()
    {
        var ariel = Patterns.Single(p => p.Family == "ariel");
        var azphel = Patterns.Single(p => p.Family == "azphel");
        var arielCells = ariel.Cells.Select(c => (c.Row, c.Column)).ToHashSet();
        var azphelCells = azphel.Cells.Select(c => (c.Row, c.Column)).ToHashSet();
        Assert.Equal(153, ariel.Cells.Count);
        Assert.Equal(153, azphel.Cells.Count);
        Assert.Equal((8, 8), (ariel.Cells.Single(c => c.IsStart).Row, ariel.Cells.Single(c => c.IsStart).Column));
        Assert.Equal(46, arielCells.Except(azphelCells).Count());
        Assert.Equal(46, azphelCells.Except(arielCells).Count());
        foreach (var family in new[] { "ariel", "azphel" })
        {
            var reference = Patterns.Single(p => p.Family == family).Cells.Select(c => (c.Row, c.Column, c.IsStart)).ToHashSet();
            foreach (var board in DaevanionCatalog.Default.Boards.Where(b => b.FamilyId == family))
                Assert.True(reference.SetEquals(board.Nodes.Select(n => (n.Row, n.Column, n.Kind == DaevanionNodeKind.Start))));
        }
    }

    [Theory]
    [InlineData("ariel", "azphel")]
    [InlineData("azphel", "ariel")]
    public void EqualSizedBoardsSwitchByTheirFreshNativeOccupancy(string previousFamily, string currentFamily)
    {
        using var previous = RenderReference(previousFamily);
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(previous, Patterns));
        Assert.Equal(previousFamily, preferred.Family);
        using var current = RenderReference(currentFamily);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(current, Patterns, preferred));
        Assert.Equal(currentFamily, result.Family);
        Assert.Equal(152, result.Nodes.Count);
        Assert.Equal(1, result.Confidence);
    }

    [Theory]
    [InlineData("ariel", "azphel")]
    [InlineData("azphel", "ariel")]
    public void CoveredDistinctiveNodesCannotKeepAnEqualSizedFamilyAlive(string family, string otherFamily)
    {
        using var current = RenderReference(family);
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(current, Patterns));
        var other = Patterns.Single(p => p.Family == otherFamily).Cells.Select(c => (c.Row, c.Column)).ToHashSet();
        foreach (var cell in Patterns.Single(p => p.Family == family).Cells.Where(c => !other.Contains((c.Row, c.Column))))
            Cv2.Rectangle(current, new Rect(140 + (cell.Column - 1) * 68 - 33, 100 + (cell.Row - 1) * 68 - 33, 66, 66),
                new Scalar(29, 33, 34), -1);
        Assert.Null(DaevanionBoardLocator.Find(current, Patterns));
        Assert.Null(DaevanionBoardLocator.Find(current, Patterns, preferred));
    }

    [Fact]
    public void StaleOrInvalidPreferredGeometryFallsBackToFreshSearch()
    {
        using var image = ReadFixture();
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var stale = preferred with { Origin = new(preferred.Origin.X + 17, preferred.Origin.Y - 9) };
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, stale));
        Assert.NotEqual(stale.Origin, result.Origin);
        Assert.Equal(preferred.Origin, result.Origin);
        Assert.Equal(88, result.Nodes.Count);
        var invalid = preferred with { Spacing = double.NaN };
        Assert.Equal(preferred.Origin, DaevanionBoardLocator.Find(image, Patterns, invalid)?.Origin);
    }

    [Theory]
    [InlineData("early")]
    [InlineData("peak")]
    [InlineData("trough")]
    [InlineData("selected-minimum")]
    [InlineData("contour-minimum")]
    public void ActualLevelUpPulseIsRecognizedFromColdPixels(string phase)
    {
        using var image = ReadAnimation(phase);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        Assert.Equal("vaizel", result.Family);
        Assert.InRange(result.NodeSize, 64, 73);
        Assert.True(result.Nodes.Count == 88, "Missing: " + string.Join(", ", Patterns.Single(p => p.Family == "vaizel").Cells
            .Where(c => !c.IsStart && !result.Nodes.Any(n => n.Row == c.Row && n.Column == c.Column)).Select(c => $"({c.Row},{c.Column})")));
        Assert.Contains(result.Nodes, n => n.Row == 11 && n.Column == 12); // Selected node, including gold/blue animation.
    }

    [Fact]
    public void ActualPulseKeepsFreshNodesAndTheConfirmedRasterAcrossPhases()
    {
        DaevanionBoardDetection? preferred = null;
        foreach (var phase in new[] { "early", "selected-minimum", "peak", "contour-minimum", "trough", "peak", "early" })
        {
            using var image = ReadAnimation(phase);
            var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
            Assert.Equal("vaizel", result.Family);
            Assert.True(result.Nodes.Count == 88, phase + " missing: " + string.Join(", ", Patterns.Single(p => p.Family == "vaizel").Cells
                .Where(c => !c.IsStart && !result.Nodes.Any(n => n.Row == c.Row && n.Column == c.Column)).Select(c => $"({c.Row},{c.Column})")));
            if (preferred is not null)
            {
                Assert.Equal(preferred.Origin, result.Origin);
                Assert.Equal(preferred.Spacing, result.Spacing);
                Assert.Equal(preferred.NodeSize, result.NodeSize);
                Assert.Equal(preferred.Nodes.Select(n => n.Bounds), result.Nodes.Select(n => n.Bounds));
            }
            preferred = result;
        }
    }

    [Fact]
    public void BlueLevelAndGoldSelectionAnimationDoNotActivateDormantNodes()
    {
        DaevanionBoardDetection? preferred = null;
        foreach (var phase in new[] { "early", "peak", "trough", "selected-minimum", "contour-minimum" })
        {
            using var image = ReadAnimation(phase);
            var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
            Assert.Equal(DaevanionNodeAppearance.Active, Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 3).Appearance);
            Assert.Equal(DaevanionNodeAppearance.Active, Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 4).Appearance);
            Assert.Equal(DaevanionNodeAppearance.Active, Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 5).Appearance);
            Assert.Equal(DaevanionNodeAppearance.Inactive, Assert.Single(result.Nodes, n => n.Row == 3 && n.Column == 9).Appearance);
            Assert.Equal(DaevanionNodeAppearance.Inactive, Assert.Single(result.Nodes, n => n.Row == 11 && n.Column == 12).Appearance);
            preferred = result;
        }
    }

    [Fact]
    public void PreferredRasterRereadsChangedNativeActivationArtwork()
    {
        using var original = RenderReference("nezekan");
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(original, Patterns));
        using var current = RenderReference("nezekan", inactive: true);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(current, Patterns, preferred));
        Assert.Equal(preferred.Origin, result.Origin);
        Assert.Equal(preferred.Nodes.Count, result.Nodes.Count);
        Assert.All(result.Nodes, node => Assert.Equal(DaevanionNodeAppearance.Inactive, node.Appearance));
        Assert.Contains(preferred.Nodes, node => node.Appearance == DaevanionNodeAppearance.Active);
    }

    [Fact]
    public void ActualPulseDoesNotAllowPaintingOverAnInteriorTooltipOrDifferentMenu()
    {
        using var image = ReadAnimation("peak");
        var preferred = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns));
        var node = Assert.Single(preferred.Nodes, n => n.Row == 5 && n.Column == 8);
        var covered = new Rect(node.Bounds.X + node.Bounds.Width / 8, node.Bounds.Y + node.Bounds.Height / 8,
            node.Bounds.Width * 3 / 4, node.Bounds.Height * 3 / 4);
        Cv2.Rectangle(image, covered, new Scalar(30, 33, 35), -1);
        Cv2.PutText(image, "Info", new Point(covered.X + 4, covered.Y + covered.Height / 2), HersheyFonts.HersheySimplex, .5, Scalar.White, 1);
        var result = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, preferred));
        Assert.DoesNotContain(result.Nodes, n => n.Row == node.Row && n.Column == node.Column);
        using var menu = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "esc-menu", "user-2026-10-10.png"));
        Assert.Null(DaevanionBoardLocator.Find(menu, Patterns, result));
        var start = Patterns.Single(p => p.Family == preferred.Family).Cells.Single(c => c.IsStart);
        var center = new Point((int)Math.Round(preferred.Origin.X + (start.Column - 1) * preferred.Spacing),
            (int)Math.Round(preferred.Origin.Y + (start.Row - 1) * preferred.Spacing));
        var radius = (int)Math.Ceiling(preferred.NodeSize);
        Cv2.Rectangle(image, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2), new Scalar(30, 33, 35), -1);
        // A covered start only keeps the confirmed raster; the covered node is still not painted.
        var covering = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(image, Patterns, result));
        Assert.DoesNotContain(covering.Nodes, n => n.Row == node.Row && n.Column == node.Column);
        Assert.Null(DaevanionBoardLocator.Find(menu, Patterns, covering));
        Assert.Null(DaevanionBoardLocator.Find(image, Patterns));
    }

    private static Mat ReadAnimation(string phase) => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "daevanion", $"vaizel-animation-{phase}.png"));

    private static Mat ReadFixture() => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "daevanion", "nezekan-board.png"));

    private static Mat RenderReference(string family, bool inactive = false)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var image = new Mat(1200, 1300, MatType.CV_8UC3, new Scalar(29, 33, 34));
        var assembly = typeof(DaevanionBoardLocator).Assembly;
        foreach (var node in board.Nodes)
        {
            var suffix = node.Kind == DaevanionNodeKind.Start ? "ut_fwindow_daevanion_node_startbg_sprite.png"
                : "ut_fwindow_daevanion_node_" + node.Quality.ToString().ToLowerInvariant() + (inactive ? "_disabled_sprite.png" : "_sprite.png");
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            using var source = Cv2.ImDecode(memory.ToArray(), ImreadModes.Unchanged);
            var targetSize = node.Kind == DaevanionNodeKind.Start ? 94 : 63;
            using var resized = new Mat(); Cv2.Resize(source, resized, new Size(targetSize, targetSize));
            var cx = 140 + (node.Column - 1) * 68; var cy = 100 + (node.Row - 1) * 68;
            var height = resized.Height; var width = resized.Width;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = resized.At<Vec4b>(y, x); var alpha = color.Item3 / 255d;
                var yy = cy - targetSize / 2 + y; var xx = cx - targetSize / 2 + x;
                var background = image.At<Vec3b>(yy, xx);
                image.Set(yy, xx, new Vec3b((byte)Math.Round(color.Item0 * alpha + background.Item0 * (1 - alpha)),
                    (byte)Math.Round(color.Item1 * alpha + background.Item1 * (1 - alpha)),
                    (byte)Math.Round(color.Item2 * alpha + background.Item2 * (1 - alpha))));
            }
        }
        return image;
    }
}
