using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssRankMorphCacheTests
{
    [Fact]
    public void EveryCatalogNeighborHasOneEmbeddedAtlasWith61FramesAndThreeEmptyCells()
    {
        var assembly = typeof(AbyssRankMorphCache).Assembly;
        var resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith("Soulcrest.AbyssRankMorphs.", StringComparison.Ordinal)).ToArray();
        var stages = AbyssRankCatalog.Default.Stages;
        Assert.Equal(stages.Count - 1, resources.Length);
        for (var stage = 0; stage < stages.Count - 1; stage++)
        {
            var name = "Soulcrest.AbyssRankMorphs." + stages[stage].Id + "--" + stages[stage + 1].Id + ".png";
            Assert.Contains(name, resources);
            using var stream = assembly.GetManifestResourceStream(name);
            Assert.NotNull(stream);
            using var atlas = new Bitmap(stream);
            Assert.Equal(new Size(512, 512), atlas.Size);
            Assert.Equal(ImageFormat.Png.Guid, atlas.RawFormat.Guid);
            for (var frame = 0; frame < 64; frame++)
            {
                using var tile = atlas.Clone(new Rectangle(frame % 8 * 64, frame / 8 * 64, 64, 64), PixelFormat.Format32bppPArgb);
                var pixels = Pixels(tile);
                Assert.Equal(frame < AbyssRankMorphCache.FrameCount,
                    Enumerable.Range(0, 64 * 64).Any(index => pixels[index * 4 + 3] > 0));
            }
        }
    }

    [Fact]
    public void BundledDrawNeedsWarmupAndNeverFallsBackToDirectoryFiles()
    {
        using var cache = new AbyssRankMorphCache();
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        Assert.False(cache.IsPairReady(0, 1));
        Assert.False(cache.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CachedPairCount);
        Assert.True(cache.PreloadPair(0, 1));
        Assert.True(cache.IsPairReady(1, 0));
        Assert.True(cache.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.CachedPairCount);
        Assert.False(cache.Draw(graphics, 0, 1, 0, new RectangleF(0, 0, 64, 64)));
        Assert.False(cache.Draw(graphics, 0, 1, 1, new RectangleF(0, 0, 64, 64)));
    }

    [Fact]
    public async Task WarmupMakesEveryForwardAndReverseFrameAvailableWithinFixedMemoryBounds()
    {
        using var cache = new AbyssRankMorphCache();
        Assert.True(await cache.PreloadAllAsync());
        Assert.Equal(AbyssRankCatalog.Default.Stages.Count - 1, cache.CachedPairCount);
        Assert.InRange(cache.CachedPairCount, 1, AbyssRankMorphCache.MaximumPairCount);
        Assert.Equal(0, cache.Count);
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        for (var pair = 0; pair < AbyssRankCatalog.Default.Stages.Count - 1; pair++)
        {
            Assert.True(cache.IsPairReady(pair, pair + 1));
            Assert.True(cache.IsPairReady(pair + 1, pair));
            for (var frame = 0; frame < AbyssRankMorphCache.FrameCount; frame++)
            {
                var progress = Math.Clamp(frame / 60d, .000001, .999999);
                Assert.True(cache.Draw(graphics, pair, pair + 1, progress, new RectangleF(0, 0, 64, 64)));
                Assert.True(cache.Draw(graphics, pair + 1, pair, progress, new RectangleF(0, 0, 64, 64)));
            }
        }
        Assert.Equal(AbyssRankMorphCache.FrameCapacity, cache.Count);
        // Frame eviction does not require PNG decoding: its atlas remains warm.
        Assert.True(cache.IsPairReady(0, 1));
        Assert.True(cache.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
        Assert.Equal(AbyssRankMorphCache.FrameCapacity, cache.Count);
        cache.Dispose();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CachedPairCount);
        Assert.False(cache.IsPairReady(0, 1));
        Assert.False(cache.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
        Assert.False(await cache.PreloadAllAsync());
    }

    [Fact]
    public async Task PackedEndpointsUseTheSame64PixelSourceCanvasAsOriginalIcons()
    {
        using var cache = new AbyssRankMorphCache();
        Assert.True(await cache.PreloadAllAsync());
        using var icons = new LootOverlayIconCache();
        var stages = AbyssRankCatalog.Default.Stages;
        for (var pair = 0; pair < stages.Count - 1; pair++)
        foreach (var target in new[] { false, true })
        {
            using var expected = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            using var actual = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(expected))
                Assert.True(icons.Draw(graphics, stages[pair + (target ? 1 : 0)].IconAsset, new RectangleF(0, 0, 64, 64)));
            using (var graphics = Graphics.FromImage(actual))
                Assert.True(cache.Draw(graphics, pair, pair + 1, target ? .999999 : .000001, new RectangleF(0, 0, 64, 64)));
            var originalPixels = Pixels(expected);
            var packedPixels = Pixels(actual);
            // PNG unpremultiplication and re-premultiplication may round one
            // channel by one level. Exact timeline endpoints use originals.
            Assert.InRange(originalPixels.Zip(packedPixels, (a, b) => Math.Abs(a - b)).Max(), 0, 1);
        }
    }

    [Fact]
    public async Task RepeatedConcurrentWarmupsShareTheSameBoundedAtlases()
    {
        using var cache = new AbyssRankMorphCache();
        var results = await Task.WhenAll(cache.PreloadAllAsync(), cache.PreloadAllAsync(), cache.PreloadPairAsync(17, 18));
        Assert.All(results, Assert.True);
        Assert.Equal(18, cache.CachedPairCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task CanceledOrDisposedWarmupNeverRetainsLateSurfaces()
    {
        using var cache = new AbyssRankMorphCache();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PreloadAllAsync(cancellation.Token));
        Assert.Equal(0, cache.CachedPairCount);
        var warmup = cache.PreloadAllAsync();
        cache.Dispose();
        await warmup;
        Assert.Equal(0, cache.CachedPairCount);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.PreloadPair(0, 1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(18, 19)]
    [InlineData(int.MinValue, int.MaxValue)]
    public async Task InvalidPairsCannotLoadResources(int from, int to)
    {
        using var cache = new AbyssRankMorphCache();
        Assert.False(cache.IsPairReady(from, to));
        Assert.False(cache.PreloadPair(from, to));
        Assert.False(await cache.PreloadPairAsync(from, to));
        Assert.Equal(0, cache.CachedPairCount);
    }

    private static byte[] Pixels(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * image.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { image.UnlockBits(data); }
    }
}
