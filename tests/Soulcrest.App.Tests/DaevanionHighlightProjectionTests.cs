using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionHighlightProjectionTests
{
    [Fact]
    public Task TheWorkingSurfaceDoesNotLeavePreviousSelectionOrOcclusionPixelsBehind() => OnSta(() =>
    {
        var source = Source();
        var selected = source.Board.Nodes.SelectMany(node => node.Effects).Select(effect => effect.StatId!).ToHashSet(StringComparer.Ordinal);
        using var form = new DaevanionHighlightForm(() => null, () => selected);
        var raster = typeof(DaevanionHighlightForm).GetMethod("Raster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = (Bitmap)raster.Invoke(form, [source, selected, new Rectangle(Point.Empty, source.GameBounds.Size), .42])!;
        var current = source with { Detection = source.Detection with { Nodes = [source.Detection.Nodes[1]] } };
        var actual = (Bitmap)raster.Invoke(form, [current, selected, new Rectangle(Point.Empty, source.GameBounds.Size), .42])!;
        using var expected = Render(current);
        Assert.Equal(Pixels(expected), Pixels(actual));
        var cleared = (Bitmap)raster.Invoke(form, [current, new HashSet<string>(StringComparer.Ordinal),
            new Rectangle(Point.Empty, source.GameBounds.Size), .8])!;
        Assert.All(Pixels(cleared), pixel => Assert.Equal(0, pixel));
        Assert.False(form.IsHandleCreated);
    });

    [Fact]
    public void AnUnchangedSourceKeepsTheSameProofAndPixels()
    {
        var source = Source();
        using var image = Render(source);
        var before = Pixels(image);
        var current = DaevanionHighlightForm.ReconcileHighlight(image, source, source, Point.Empty);
        Assert.Same(source, current);
        Assert.Equal(source.FrameAt, current!.FrameAt);
        Assert.Equal(before, Pixels(image));
    }

    [Fact]
    public void NativeAppearanceChangesDoNotEraseAnAppearanceIndependentLightTrail()
    {
        var source = Source();
        var current = source with
        {
            FrameAt = 200,
            Detection = source.Detection with
            {
                Nodes = source.Detection.Nodes.Select(node => node with { Appearance = DaevanionNodeAppearance.Active }).ToArray()
            }
        };
        using var image = Render(source);
        var before = Pixels(image);
        Assert.True(DaevanionHighlightForm.HasSameVisibleGeometry(source, current));
        Assert.Same(current, DaevanionHighlightForm.ReconcileHighlight(image, source, current, Point.Empty));
        Assert.Equal(before, Pixels(image));
        Assert.All(current.Detection.Nodes, node => Assert.Equal(DaevanionNodeAppearance.Active, node.Appearance));
    }

    [Theory]
    [InlineData(32, 44)]
    [InlineData(60, 62)]
    [InlineData(60, 76)]
    public void ANewTooltipMaskClearsTheWholeHiddenEnvelopeAndKeepsTheVisibleNeighbor(int nodeSize, int spacing)
    {
        var source = Source(nodeSize, spacing);
        var current = source with
        {
            FrameAt = 200,
            Detection = source.Detection with { Nodes = [source.Detection.Nodes[1]] }
        };
        using var image = Render(source);
        using var expected = Render(current);
        Assert.Same(current, DaevanionHighlightForm.ReconcileHighlight(image, source, current, Point.Empty));
        Assert.Equal(Pixels(expected), Pixels(image));
    }

    [Fact]
    public void ReturningNodesRemainAbsentUntilTheirFreshArtworkIsDrawn()
    {
        var current = Source();
        var previous = current with
        {
            FrameAt = 50,
            Detection = current.Detection with { Nodes = [current.Detection.Nodes[0]] }
        };
        using var image = Render(previous);
        var proof = DaevanionHighlightForm.ReconcileHighlight(image, previous, current, Point.Empty);
        Assert.NotNull(proof);
        Assert.NotSame(current, proof);
        Assert.Equal(current.FrameAt, proof.FrameAt);
        Assert.Single(proof.Detection.Nodes);
        Assert.Equal(current.Detection.Nodes[0], proof.Detection.Nodes[0]);
        using var expected = Render(previous);
        Assert.Equal(Pixels(expected), Pixels(image));
    }

    [Fact]
    public void ReorderedFreshNodesDoNotChangeTheLightTrailOrRetainTheOldTimestamp()
    {
        var previous = Source();
        var current = previous with
        {
            FrameAt = 200,
            Detection = previous.Detection with { Nodes = previous.Detection.Nodes.Reverse().ToArray() }
        };
        using var image = Render(previous);
        var before = Pixels(image);
        Assert.True(DaevanionHighlightForm.HasSameVisibleGeometry(previous, current));
        Assert.Same(current, DaevanionHighlightForm.ReconcileHighlight(image, previous, current, Point.Empty));
        Assert.Equal(before, Pixels(image));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("position")]
    [InlineData("board")]
    public void ChangedSourceBindingsCannotReuseThePreviousHighlightSurface(string changed)
    {
        var previous = Source();
        var current = changed switch
        {
            "context" => previous with { Context = new(previous.Context.Identity, previous.Context.Generation + 1) },
            "position" => previous with { Detection = previous.Detection with { Origin = new(80, 90) } },
            _ => previous with { Board = DaevanionCatalog.Default.GetBoard("Cleric", "azphel")! }
        };
        using var image = Render(previous);
        Assert.False(DaevanionHighlightForm.HasSameVisibleGeometry(previous, current));
        Assert.Null(DaevanionHighlightForm.ReconcileHighlight(image, previous, current, Point.Empty));
    }

    private static DaevanionSnapshot Source(int nodeSize = 60, int spacing = 76)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var seed = original.Nodes.First(node => node.Kind == DaevanionNodeKind.Stat) with { Row = 3, Column = 3 };
        var right = seed with { Id = seed.Id + 1, Column = 4 };
        var board = original with { Nodes = [seed, right] };
        DaevanionNodeAnchor[] anchors = [new(3, 3, new(24, 24, nodeSize, nodeSize), 1),
            new(3, 4, new(24 + spacing, 24, nodeSize, nodeSize), 1)];
        var size = new Size(240, 160);
        return new(new(new(101, 501, 300, "SyntheticHighlightProjection") { ClassId = "Cleric" }, 1),
            board, new(Point.Empty, size), 100,
            new("ariel", new(24 + nodeSize / 2d - 2 * spacing, 24 + nodeSize / 2d - 2 * spacing), spacing, nodeSize, anchors, 1), "en");
    }

    private static Bitmap Render(DaevanionSnapshot source) => new DaevanionHighlightRenderer().Render(source.GameBounds.Size,
        source.Board, source.Detection.Nodes, source.Board.Nodes.SelectMany(node => node.Effects)
            .Select(effect => effect.StatId!).ToHashSet(StringComparer.Ordinal), spacing: source.Detection.Spacing, phase: .42);

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, result, row * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return result;
    }
}
