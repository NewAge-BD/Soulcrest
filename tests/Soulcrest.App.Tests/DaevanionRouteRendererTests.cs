using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionRouteRendererTests
{
    internal static DaevanionSnapshot Picture(long? at = null)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var stat = original.Nodes.First(node => node.Kind == DaevanionNodeKind.Stat);
        DaevanionNode[] nodes = [original.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start) with { Id = 1 },
            stat with { Id = 2, Row = 8, Column = 9 }, stat with { Id = 3, Row = 8, Column = 10 },
            stat with { Id = 4, Row = 9, Column = 9 }];
        var anchors = nodes.Where(node => node.Kind != DaevanionNodeKind.Start).Select(node => new DaevanionNodeAnchor(
            node.Row, node.Column, new(22 + (node.Column - 8) * 76, 22 + (node.Row - 8) * 76, 60, 60), .95)).ToArray();
        var context = new OwnCharacterContext(new(101, 501, 300, "SyntheticRoute") { ClassId = "Cleric" }, 1);
        return new(context, original with { Nodes = nodes }, new(20, 30, 280, 200), at ?? Stopwatch.GetTimestamp(),
            new("nezekan", new(-480, -480), 76, 60, anchors, .95) { StartVisible = true }, "en");
    }

    private static readonly IReadOnlySet<int> Selected = new HashSet<int> { 1, 2, 3 };
    private static readonly (int FromNodeId, int ToNodeId)[] Edges = [(1, 2), (2, 3)];
    private static Bitmap Render(DaevanionSnapshot source, bool edges = true) => new DaevanionRouteRenderer()
        .Render(source.GameBounds.Size, source.Board, source.Detection, Selected, Edges, edges);

    [Fact]
    public void StaticBlueRouteMarksOnlySelectedNodesAndTheirOrthogonalGaps()
    {
        var source = Picture();
        using var image = Render(source);
        AssertBlue(image.GetPixel(90, 52));
        AssertBlue(image.GetPixel(166, 52));
        AssertBlue(image.GetPixel(96, 52));
        Assert.Equal(0, image.GetPixel(90, 128).A); // Unselected node's outside rim.
        foreach (var node in source.Board.Nodes)
        {
            var bounds = new Rectangle(22 + (node.Column - 8) * 76, 22 + (node.Row - 8) * 76, 60, 60);
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++) Assert.Equal(0, image.GetPixel(x, y).A);
        }
    }

    [Fact]
    public void LocalChangesHaveSolidGreenAndDashedOrangeRimsWithoutLinesOrNativeInteriorPaint()
    {
        var source = Picture(); var renderer = new DaevanionRouteRenderer();
        using var changes = renderer.RenderChanges(source.GameBounds.Size, source.Board, source.Detection,
            new HashSet<int> { 2 }, new HashSet<int> { 3 });
        using var solid = renderer.RenderChanges(source.GameBounds.Size, source.Board, source.Detection,
            new HashSet<int> { 2, 3 }, new HashSet<int>());
        Assert.True(Count(changes, new(92, 16, 72, 72), IsAddition) > 100);
        Assert.True(Count(changes, new(168, 16, 72, 72), IsRemoval) > 50);
        var solidRim = Count(solid, new(168, 16, 72, 72), pixel => pixel.A > 80);
        var dashedRim = Count(changes, new(168, 16, 72, 72), pixel => pixel.A > 80);
        Assert.InRange(dashedRim, 1, solidRim - 1);
        Assert.Equal(0, changes.GetPixel(90, 52).A);
        Assert.Equal(0, changes.GetPixel(166, 52).A);
        Assert.Equal(0, Count(changes, new(16, 16, 72, 72), pixel => pixel.A > 0)); // Start is never edited.
        Assert.Equal(0, Count(changes, new(92, 92, 72, 72), pixel => pixel.A > 0)); // Unchanged node.
        foreach (var anchor in source.Detection.Nodes)
            Assert.Equal(0, Count(changes, new(anchor.Bounds.X, anchor.Bounds.Y, anchor.Bounds.Width, anchor.Bounds.Height),
                pixel => pixel.A > 0));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("confidence")]
    [InlineData("family")]
    public void EditRimsRequireThisFramesUnambiguousVisibleAnchor(string invalid)
    {
        var source = Picture(); var last = source.Detection.Nodes.Single(anchor => anchor.Column == 10);
        var detection = invalid switch
        {
            "missing" => source.Detection with { Nodes = source.Detection.Nodes.Where(anchor => anchor != last).ToArray() },
            "duplicate" => source.Detection with { Nodes = [.. source.Detection.Nodes, last] },
            "confidence" => source.Detection with { Nodes = source.Detection.Nodes.Select(anchor =>
                anchor == last ? anchor with { Confidence = 0 } : anchor).ToArray() },
            _ => source.Detection with { Family = "ariel" }
        };
        using var image = new DaevanionRouteRenderer().RenderChanges(source.GameBounds.Size, source.Board, detection,
            new HashSet<int>(), new HashSet<int> { 3 });
        Assert.Equal(0, Count(image, new(Point.Empty, image.Size), pixel => pixel.A > 0));
    }

    [Fact]
    public void UnknownStartAndConflictingEditIdsNeverProduceArtwork()
    {
        var source = Picture();
        using var image = new DaevanionRouteRenderer().RenderChanges(source.GameBounds.Size, source.Board, source.Detection,
            new HashSet<int> { 1, 2, 999 }, new HashSet<int> { 1, 2, 998 });
        Assert.Equal(0, Count(image, new(Point.Empty, image.Size), pixel => pixel.A > 0));
    }

    [Fact]
    public void ChangeRimsUseThePhysicalOriginAndNeverBorrowHiddenStartVisibility()
    {
        var source = Picture(); var renderer = new DaevanionRouteRenderer();
        using var original = renderer.RenderChanges(source.GameBounds.Size, source.Board, source.Detection,
            new HashSet<int> { 2 }, new HashSet<int>());
        using var shifted = renderer.RenderChanges(source.GameBounds.Size, source.Board,
            source.Detection with { StartVisible = false }, new HashSet<int> { 2 }, new HashSet<int>(), new(7, 11));
        for (var y = 11; y < original.Height; y++)
        for (var x = 7; x < original.Width; x++)
            Assert.Equal(original.GetPixel(x, y), shifted.GetPixel(x - 7, y - 11));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("unselected-node")]
    [InlineData("selected-node")]
    [InlineData("duplicate")]
    [InlineData("family")]
    public void AnyIncompleteFrameRemovesEveryConnectionIncludingUnobscuredEndpoints(string missing)
    {
        var source = Picture();
        var anchors = source.Detection.Nodes;
        source = source with { Detection = missing switch
        {
            "start" => source.Detection with { StartVisible = false },
            "unselected-node" => source.Detection with { Nodes = anchors.Where(node => node.Row != 9).ToArray() },
            "selected-node" => source.Detection with { Nodes = anchors.Where(node => node.Column != 10).ToArray() },
            "duplicate" => source.Detection with { Nodes = [anchors[0], anchors[0], anchors[1]] },
            _ => source.Detection with { Family = "ariel" }
        } };
        Assert.False(DaevanionRouteRenderer.HasCompleteFrame(source.Board, source.Detection));
        using var image = Render(source);
        Assert.Equal(0, image.GetPixel(90, 52).A);
        Assert.Equal(0, image.GetPixel(166, 52).A);
        if (missing != "family" && missing != "duplicate") AssertBlue(image.GetPixel(96, 52));
    }

    [Fact]
    public void HoverSuppressionKeepsFreshNodeRimsWhileClearingAllLinePixels()
    {
        var source = Picture();
        using var visible = Render(source);
        using var hovered = Render(source, edges: false);
        AssertBlue(visible.GetPixel(90, 52));
        Assert.Equal(0, hovered.GetPixel(90, 52).A);
        AssertBlue(hovered.GetPixel(96, 52));
        AssertBlue(hovered.GetPixel(172, 52));
    }

    [Fact]
    public void ReplacingTheWholeFrameCannotLeaveGhostConnectionsAcrossRepeatedTooltipMasks()
    {
        var source = Picture();
        using var original = Render(source);
        var masked = source with { Detection = source.Detection with { StartVisible = false } };
        using var first = Render(masked);
        masked = source with { Detection = source.Detection with { Nodes = source.Detection.Nodes.Take(2).ToArray() } };
        using var second = Render(masked);
        using var restored = Render(source);
        AssertBlue(original.GetPixel(90, 52));
        Assert.Equal(0, first.GetPixel(90, 52).A);
        Assert.Equal(0, second.GetPixel(90, 52).A);
        AssertBlue(restored.GetPixel(90, 52));
    }

    [Fact]
    public void DiagonalUnknownAndUnselectedEdgesNeverReceiveArtwork()
    {
        var source = Picture();
        var renderer = new DaevanionRouteRenderer();
        using var image = renderer.Render(source.GameBounds.Size, source.Board, source.Detection,
            Selected, [(1, 4), (2, 4), (1, 999), (1, 3)], true);
        Assert.Equal(0, image.GetPixel(90, 90).A);
        Assert.Equal(0, image.GetPixel(128, 90).A);
        Assert.Equal(0, image.GetPixel(90, 52).A);
    }

    [Fact]
    public void ProjectionUsesTheSuppliedPhysicalOriginAtDifferentWindowPositions()
    {
        var source = Picture(); var renderer = new DaevanionRouteRenderer();
        using var image = renderer.Render(new(280, 200), source.Board, source.Detection, Selected, Edges, true, new(7, 11));
        AssertBlue(image.GetPixel(83, 41));
        Assert.Equal(0, image.GetPixel(121, 41).A); // Shifted native interior remains untouched.
    }

    [Theory]
    [InlineData("nezekan", 32, 44)]
    [InlineData("zikel", 60, 76)]
    [InlineData("vaizel", 80, 104)]
    [InlineData("triniel", 32, 44)]
    [InlineData("ariel", 60, 76)]
    [InlineData("azphel", 80, 104)]
    public void CompleteQualifiedBoardsKeepNativeInteriorsUntouchedAtDifferentPhysicalScales(string family, int size, int spacing)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var center = size / 2 + 8;
        var anchors = board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start).Select(node => new DaevanionNodeAnchor(
            node.Row, node.Column, new(8 + (node.Column - 1) * spacing, 8 + (node.Row - 1) * spacing, size, size), 1)).ToArray();
        var detection = new DaevanionBoardDetection(family, new(center, center), spacing, size, anchors, 1) { StartVisible = true };
        var edges = board.Nodes.SelectMany(from => board.Nodes.Where(to => from.Id < to.Id
            && Math.Abs(from.Row - to.Row) + Math.Abs(from.Column - to.Column) == 1)
            .Select(to => (FromNodeId: from.Id, ToNodeId: to.Id))).ToArray();
        Assert.True(DaevanionRouteRenderer.HasCompleteFrame(board, detection));
        using var image = new DaevanionRouteRenderer().Render(new(15 * spacing + size, 15 * spacing + size), board,
            detection, board.Nodes.Select(node => node.Id).ToHashSet(), edges, true);
        var split = board.Nodes.Count / 2;
        using var changes = new DaevanionRouteRenderer().RenderChanges(image.Size, board, detection,
            board.Nodes.Take(split).Select(node => node.Id).ToHashSet(),
            board.Nodes.Skip(split).Select(node => node.Id).ToHashSet());
        foreach (var node in board.Nodes)
        {
            Assert.Equal(0, image.GetPixel(center + (node.Column - 1) * spacing, center + (node.Row - 1) * spacing).A);
            Assert.Equal(0, changes.GetPixel(center + (node.Column - 1) * spacing, center + (node.Row - 1) * spacing).A);
        }
        var first = board.Nodes.Single(node => node.Id == edges[0].FromNodeId);
        var second = board.Nodes.Single(node => node.Id == edges[0].ToNodeId);
        AssertBlue(image.GetPixel(center + (first.Column + second.Column - 2) * spacing / 2,
            center + (first.Row + second.Row - 2) * spacing / 2));
    }

    private static void AssertBlue(Color pixel)
    { Assert.True(pixel.A > 160 && pixel.B > pixel.G && pixel.G > pixel.R, $"Expected route blue, got {pixel}."); }
    private static bool IsAddition(Color pixel) => pixel.A > 80 && pixel.G > pixel.B && pixel.B > pixel.R;
    private static bool IsRemoval(Color pixel) => pixel.A > 80 && pixel.R > pixel.G && pixel.G > pixel.B;
    private static int Count(Bitmap image, Rectangle area, Func<Color, bool> matches)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            if (matches(image.GetPixel(x, y))) count++;
        return count;
    }
}

/// <summary>Anonymous offline production render; never opens a window or reads a game/profile source.</summary>
public sealed class DaevanionRouteRenderExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void ExportAnonymousBoardChanges()
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_DAEVANION_CHANGES_PNG"));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"))
            + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        const int spacing = 76, size = 60;
        var minRow = board.Nodes.Min(node => node.Row); var minColumn = board.Nodes.Min(node => node.Column);
        var anchors = board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start).Select(node => new DaevanionNodeAnchor(
            node.Row, node.Column, new(12 + (node.Column - minColumn) * spacing,
                12 + (node.Row - minRow) * spacing, size, size), 1)).ToArray();
        var detection = new DaevanionBoardDetection(board.FamilyId,
            new(12 + size / 2d - (minColumn - 1) * spacing, 12 + size / 2d - (minRow - 1) * spacing),
            spacing, size, anchors, 1) { StartVisible = true };
        var examples = board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start)
            .OrderBy(node => node.Row).ThenBy(node => node.Column).ToArray();
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Preview("Cleric", board.FamilyId, "en");
        using (var graphics = Graphics.FromImage(image))
            new DaevanionRouteRenderer().PaintChanges(graphics, board, detection,
                examples.Where((_, index) => index % 4 == 0).Select(node => node.Id).ToHashSet(),
                examples.Where((_, index) => index % 4 == 2).Select(node => node.Id).ToHashSet());
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        image.Save(target, System.Drawing.Imaging.ImageFormat.Png);
    }
}

public sealed class DaevanionRouteVisibilityTests
{
    [Fact]
    public void PointerExitNeedsANewCompleteFrameRatherThanThePreviouslyVisibleBoard()
    {
        var now = Stopwatch.GetTimestamp(); var source = DaevanionRouteRendererTests.Picture(now - 10);
        var policy = new DaevanionRouteVisibility();
        Assert.True(policy.Observe(source, false, now));
        Assert.False(policy.Observe(source, true, now + 10));
        var capturedDuringHover = source with { FrameAt = now + 15 };
        Assert.False(policy.Observe(capturedDuringHover, true, now + 20));
        Assert.False(policy.Observe(capturedDuringHover, false, now + 30));
        Assert.False(policy.Observe(capturedDuringHover, false, now + 40));
        var incomplete = source with { FrameAt = now + 45, Detection = source.Detection with { StartVisible = false } };
        Assert.False(policy.Observe(incomplete, false, now + 50));
        var returned = source with { FrameAt = now + 55 };
        Assert.True(policy.Observe(returned, false, now + 60));
    }

    [Fact]
    public void CurrentStartVisibilitySurvivesStabilizationWithoutBorrowingTheOldStart()
    {
        var now = Stopwatch.GetTimestamp(); var source = DaevanionRouteRendererTests.Picture(now - 30);
        var stability = new DaevanionLayoutStability();
        Assert.False(stability.Update("own", source.GameBounds, now - 30, source.Detection));
        Assert.True(stability.Update("own", source.GameBounds, now - 20, source.Detection with { StartVisible = false }));
        Assert.False(stability.Confirmed!.StartVisible);
        Assert.True(stability.Update("own", source.GameBounds, now - 10, source.Detection));
        Assert.True(stability.Confirmed!.StartVisible);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("start")]
    [InlineData("node")]
    public void MissingOrExpiredEvidenceNeverAuthorizesAConnection(string invalid)
    {
        var now = Stopwatch.GetTimestamp(); var source = DaevanionRouteRendererTests.Picture(now - 10);
        source = invalid switch
        {
            "stale" => source with { FrameAt = now - Stopwatch.Frequency * 2 },
            "future" => source with { FrameAt = now + 10 },
            "start" => source with { Detection = source.Detection with { StartVisible = false } },
            _ => source with { Detection = source.Detection with { Nodes = source.Detection.Nodes.Take(2).ToArray() } }
        };
        Assert.False(new DaevanionRouteVisibility().Observe(source, false, now));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("board")]
    [InlineData("geometry")]
    [InlineData("start")]
    [InlineData("frame")]
    [InlineData("appearance")]
    public void AnyChangedFrameIdentityRequiresACompleteReplacementSurface(string changed)
    {
        var source = DaevanionRouteRendererTests.Picture();
        var current = changed switch
        {
            "context" => source with { Context = new(source.Context.Identity, source.Context.Generation + 1) },
            "board" => source with { Board = source.Board with { Id = source.Board.Id + 1 } },
            "geometry" => source with { Detection = source.Detection with { Spacing = 77 } },
            "start" => source with { Detection = source.Detection with { StartVisible = false } },
            "frame" => source with { FrameAt = source.FrameAt + 1 },
            _ => source with { Detection = source.Detection with { Nodes = source.Detection.Nodes.Select(node =>
                node with { Appearance = DaevanionNodeAppearance.Active }).ToArray() } }
        };
        Assert.False(DaevanionRouteForm.SameFrame(source, current));
        Assert.True(DaevanionRouteForm.SameFrame(source, source with { }));
    }
}
