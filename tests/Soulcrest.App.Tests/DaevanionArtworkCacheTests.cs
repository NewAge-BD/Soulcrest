using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionArtworkCacheTests
{
    [Fact]
    public void TightPartialAndWideFittingsMatchFreshArtworkAfterReturningToEarlierGeometry()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Stat);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(30, 30, 60, 60), 1);
        using var warm = new DaevanionOverlayRenderer();
        foreach (var pitch in new[] { 62d, 72, 76, 110, 62, 76.125, 72, 62 })
        {
            using var actual = warm.Render(new(130, 130), board, [anchor], "en", spacing: pitch);
            using var fresh = new DaevanionOverlayRenderer();
            using var expected = fresh.Render(new(130, 130), board, [anchor], "en", spacing: pitch);
            Assert.Equal(Pixels(expected), Pixels(actual));
        }
        // No extra entry for saturated wide gaps, nor for a return to previous geometry.
        Assert.Equal(3, warm.ArtworkCacheCount);
    }

    [Fact]
    public void SkillArtworkDoesNotDuplicateForSpacingOrStatAppearanceChanges()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Skill);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(15, 15, 60, 60), 1);
        using var renderer = new DaevanionOverlayRenderer();
        int[]? reference = null;
        foreach (var pitch in new[] { 62d, 72, 76, 110 })
        foreach (var appearance in Enum.GetValues<DaevanionNodeAppearance>())
        {
            using var image = renderer.Render(new(90, 90), board, [anchor with { Appearance = appearance }], "en", spacing: pitch);
            var pixels = Pixels(image);
            if (reference is null) reference = pixels; else Assert.Equal(reference, pixels);
        }
        Assert.Equal(1, renderer.ArtworkCacheCount);
    }

    [Theory]
    [InlineData(48, 540)]
    [InlineData(300, 50)]
    public void ArtworkCacheRemainsBoundedByBothEntryAndPixelBudgets(int size, int count)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Stat);
        using var renderer = new DaevanionOverlayRenderer();
        for (var index = 0; index < count; index++)
        {
            var distinct = node with { Id = node.Id + index + 1 };
            using var image = renderer.Render(new(size + 100, size + 100), board with { Nodes = [distinct] },
                [new(distinct.Row, distinct.Column, new(30, 30, size, size), 1)], "en", spacing: size * 1.4);
            Assert.InRange(renderer.ArtworkCacheCount, 1, 512);
            Assert.InRange(renderer.ArtworkCachePixels, 1, 8_000_000);
        }
        Assert.True(renderer.ArtworkCacheCount < count);
        if (size == 48) Assert.Equal(512, renderer.ArtworkCacheCount);
    }

    [Fact]
    public void SpacingInferenceUsesNativeCoordinatesAcrossHolesAndIgnoresInvalidCells()
    {
        DaevanionNodeAnchor[] anchors = [
            new(15, 1, new(10, 1008, 60, 60), 1),
            new(2, 15, new(1074, 20, 60, 60), 1),
            new(9, 6, new(390, 552, 60, 60), 1),
            new(2, 1, new(10, 20, 60, 60), 1),
            new(2, 1, new(400, 400, 60, 60), .5),
            new(0, 4, new(-1000, -1000, 60, 60), 1)];
        Assert.Equal(76d, DaevanionOverlayRenderer.ResolveSpacing(anchors, null));
        Assert.Equal(76d, DaevanionOverlayRenderer.ResolveSpacing(Enumerable.Reverse(anchors).ToArray(), double.NaN));
        Assert.Equal(82d, DaevanionOverlayRenderer.ResolveSpacing(anchors.Take(1).ToArray(), 82));
        Assert.Null(DaevanionOverlayRenderer.ResolveSpacing(anchors.Take(1).ToArray(), null));
    }

    [Fact]
    public void SpacingInferenceReadsLargeInputsLinearlyInsteadOfComparingEveryPair()
    {
        var anchors = new CountingAnchors(Enumerable.Range(0, 10000).Select(index =>
        {
            var cell = index % 225;
            var row = cell / 15 + 1; var column = cell % 15 + 1;
            return new DaevanionNodeAnchor(row, column, new(30 + column * 76, 20 + row * 76, 60, 60), 1);
        }).ToArray());
        Assert.Equal(76d, DaevanionOverlayRenderer.ResolveSpacing(anchors, null));
        Assert.InRange(anchors.Reads, anchors.Count, anchors.Count * 3 + 1000);
    }

    [Theory]
    [InlineData("ariel", "de", 48)]
    [InlineData("ariel", "en", 48)]
    [InlineData("ariel", "de", 60)]
    [InlineData("ariel", "en", 60)]
    [InlineData("ariel", "de", 90)]
    [InlineData("ariel", "en", 90)]
    [InlineData("azphel", "de", 48)]
    [InlineData("azphel", "en", 48)]
    [InlineData("azphel", "de", 60)]
    [InlineData("azphel", "en", 60)]
    [InlineData("azphel", "de", 90)]
    [InlineData("azphel", "en", 90)]
    public void ChangingOneDoubleBonusNeverChangesOrHidesTheOtherColumn(string family, string language, int size)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = board.Nodes.First(candidate => candidate.Effects.Count == 2);
        using var renderer = new DaevanionOverlayRenderer();
        using var original = Draw(node);
        var center = 12 + size / 2d;
        foreach (var index in new[] { 0, 1 })
        {
            var effects = node.Effects.ToArray();
            effects[index] = effects[index] with { DisplayAmount = new("+987,65 %", "+987.65%") };
            using var changed = Draw(node with { Effects = effects });
            var differences = 0;
            for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
            {
                if (original.GetPixel(x, y).ToArgb() == changed.GetPixel(x, y).ToArgb()) continue;
                differences++;
                if (index == 0) Assert.True(x < center + 1);
                else Assert.True(x >= center - 1);
            }
            Assert.True(differences > 10);
        }

        Bitmap Draw(DaevanionNode displayed) => renderer.Render(new(size + 24, size + 40),
            board with { Nodes = [displayed] },
            [new(displayed.Row, displayed.Column, new(12, 12, size, size), 1)], language, spacing: size * 1.4);
    }

    [Fact]
    public void OriginalStatBackgroundIsBundledAndNeedsNoExternalAssetDirectory()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Stat);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(10, 10, 90, 90), 1);
        using var normal = new DaevanionOverlayRenderer();
        using var missingAssetDirectory = new DaevanionOverlayRenderer(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var expected = normal.Render(new(110, 110), board, [anchor], "en");
        using var actual = missingAssetDirectory.Render(new(110, 110), board, [anchor], "en");
        Assert.True(actual.GetPixel(55, 55).A > 0);
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
            Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }

    [Fact]
    public void NativeSkillPictureAndBadgeDoNotChangeWithStatActivationLighting()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Skill);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(10, 10, 90, 90), 1);
        using var renderer = new DaevanionOverlayRenderer();
        using var dark = renderer.Render(new(110, 110), board, [anchor with { Appearance = DaevanionNodeAppearance.Inactive }], "en");
        using var light = renderer.Render(new(110, 110), board, [anchor with { Appearance = DaevanionNodeAppearance.Active }], "en");
        Assert.True(dark.GetPixel(55, 55).A > 0);
        for (var y = 0; y < dark.Height; y++)
        for (var x = 0; x < dark.Width; x++)
            Assert.Equal(dark.GetPixel(x, y).ToArgb(), light.GetPixel(x, y).ToArgb());
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, result, row * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return result;
    }

    private sealed class CountingAnchors(DaevanionNodeAnchor[] anchors) : IReadOnlyList<DaevanionNodeAnchor>
    {
        public int Reads { get; private set; }
        public int Count => anchors.Length;
        public DaevanionNodeAnchor this[int index] { get { Reads++; return anchors[index]; } }
        public IEnumerator<DaevanionNodeAnchor> GetEnumerator() => ((IEnumerable<DaevanionNodeAnchor>)anchors).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
