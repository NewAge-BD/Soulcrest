using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionBackgroundCacheTests
{
    [Theory]
    [InlineData(32, 31)]
    [InlineData(39, 39)]
    [InlineData(59, 58)]
    public void CachedMaterialIsPixelIdenticalToTheOriginalDirectComposition(int width, int height)
    {
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var quality in new[] { DaevanionNodeQuality.Common, DaevanionNodeQuality.Rare,
            DaevanionNodeQuality.Legend, DaevanionNodeQuality.Unique })
        foreach (var appearance in Enum.GetValues<DaevanionNodeAppearance>())
        foreach (var extended in new[] { false, true })
        {
            using var direct = Draw(true);
            using var cached = Draw(false);
            using var reused = Draw(false);
            Assert.Equal(Pixels(direct), Pixels(cached));
            Assert.Equal(Pixels(cached), Pixels(reused));

            Bitmap Draw(bool useDirect)
            {
                var image = new Bitmap(width + 20, height + 20, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Transparent);
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var inside = new Rectangle(10, 10, width, height);
                graphics.SetClip(inside);
                if (useDirect) renderer.PaintBackgroundDirect(graphics, inside, quality, appearance, extended);
                else renderer.PaintBackground(graphics, inside, quality, appearance, extended);
                return image;
            }
        }
        // Four active palettes and two inactive shades, shared across appearance aliases.
        Assert.Equal(6, renderer.BackgroundCacheCount);
    }

    [Fact]
    public void EqualSizedMaterialsPreserveDifferentNodeLocalRasterOrigins()
    {
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var offset in new[] { 0, 10, 13, 0, 10 })
        {
            var inside = new Rectangle(offset, offset, 59, 58);
            using var direct = Draw(true);
            using var cached = Draw(false);
            Assert.Equal(Pixels(direct), Pixels(cached));

            Bitmap Draw(bool useDirect)
            {
                var image = new Bitmap(90, 90, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Transparent);
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SetClip(inside);
                if (useDirect) renderer.PaintBackgroundDirect(graphics, inside, DaevanionNodeQuality.Common,
                    DaevanionNodeAppearance.Inactive, false);
                else renderer.PaintBackground(graphics, inside, DaevanionNodeQuality.Common,
                    DaevanionNodeAppearance.Inactive, false);
                return image;
            }
        }
        Assert.Equal(3, renderer.BackgroundCacheCount);
    }

    [Fact]
    public void MaterialIsSharedAcrossColdNodeArtworkAndLanguages()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var nodes = board.Nodes.Where(candidate => candidate.Kind == DaevanionNodeKind.Stat).Take(20).ToArray();
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var node in nodes)
        foreach (var language in new[] { "de", "en" })
        foreach (var appearance in new[] { DaevanionNodeAppearance.Unknown, DaevanionNodeAppearance.Inactive })
        {
            using var image = renderer.Render(new(100, 100), board,
                [new(node.Row, node.Column, new(20, 20, 60, 60), 1) { Appearance = appearance }], language, spacing: 76);
        }
        Assert.True(renderer.ArtworkCacheCount > 20);
        Assert.Equal(1, renderer.BackgroundCacheCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterialCacheHonorsEntryAndPixelBudgetsAndIsClearedOnDispose(bool large)
    {
        var renderer = new DaevanionOverlayRenderer();
        try
        {
            var count = large ? 20 : 100;
            for (var index = 0; index < count; index++)
            {
                var width = (large ? 250 : 20) + index;
                var height = large ? 250 : 20;
                using var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(image);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                renderer.PaintBackground(graphics, new(Point.Empty, image.Size), DaevanionNodeQuality.Common,
                    DaevanionNodeAppearance.Inactive, true);
                Assert.InRange(renderer.BackgroundCacheCount, 1, 64);
                Assert.InRange(renderer.BackgroundCachePixels, 1, 1_000_000);
            }
            Assert.True(renderer.BackgroundCacheCount < count);
            if (!large) Assert.Equal(64, renderer.BackgroundCacheCount);
        }
        finally { renderer.Dispose(); }
        Assert.Equal(0, renderer.ArtworkCacheCount);
        Assert.Equal(0, renderer.ArtworkCachePixels);
        Assert.Equal(0, renderer.BackgroundCacheCount);
        Assert.Equal(0, renderer.BackgroundCachePixels);
    }

    private static int[] Pixels(Bitmap image)
    {
        var pixels = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return pixels;
    }
}
