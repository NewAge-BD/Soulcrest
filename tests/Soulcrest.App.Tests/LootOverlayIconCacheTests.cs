using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic local PNGs and caller-owned drawing surfaces only; no host, game or capture.</summary>
public sealed class LootOverlayIconCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SoulcrestLootIcons-" + Guid.NewGuid().ToString("N"));

    public LootOverlayIconCacheTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DrawPreservesColorAlphaAndAspectRatioWithTransparentPadding()
    {
        var path = Png("wide", Color.FromArgb(128, 220, 40, 60), 32, 16);
        using var cache = new LootOverlayIconCache();
        using var surface = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(surface);
        graphics.Clear(Color.Transparent);
        Assert.True(cache.Draw(graphics, path, new(0, 0, 64, 64)));
        Assert.Equal(1, cache.Count);
        var center = surface.GetPixel(32, 32);
        Assert.InRange(center.A, 126, 130);
        Assert.InRange(center.R, 218, 222);
        Assert.InRange(center.G, 38, 42);
        Assert.InRange(center.B, 58, 62);
        Assert.Equal(0, surface.GetPixel(32, 8).A);
        Assert.Equal(0, surface.GetPixel(32, 55).A);
        // The cached surface is detached from its original stream and file.
        File.Delete(path);
        Assert.True(cache.Draw(graphics, path, new(0, 0, 64, 64)));
    }

    [Fact]
    public void OptionalBackingOnlyAppearsAfterAnImageHasLoadedAndMissingAssetsLeaveNoReplacementBox()
    {
        var missing = Path.Combine(_directory, "missing-backed.png");
        var invalid = Path.Combine(_directory, "invalid-backed.png");
        File.WriteAllText(invalid, "Not an image.");
        var transparent = Png("transparent", Color.Transparent, 32, 16);
        using var cache = new LootOverlayIconCache();
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        var original = Color.Magenta;
        var backing = Color.FromArgb(14, 17, 22);
        var bounds = new RectangleF(8, 8, 40, 40);
        graphics.Clear(original);
        foreach (var asset in new[] { missing, invalid, missing })
        {
            Assert.False(cache.Draw(graphics, asset, bounds, background: backing));
            Assert.All(Enumerable.Range(0, 64 * 64), index =>
                Assert.Equal(original.ToArgb(), surface.GetPixel(index % 64, index / 64).ToArgb()));
        }
        // The existing default preserves the source transparency, including letterboxed padding.
        Assert.True(cache.Draw(graphics, transparent, bounds));
        Assert.Equal(original.ToArgb(), surface.GetPixel(24, 24).ToArgb());
        Assert.True(cache.Draw(graphics, transparent, bounds, background: backing));
        Assert.Equal(backing.ToArgb(), surface.GetPixel(24, 24).ToArgb());
        Assert.Equal(backing.ToArgb(), surface.GetPixel(24, 10).ToArgb());
        Assert.Equal(original.ToArgb(), surface.GetPixel(4, 4).ToArgb());
    }

    [Fact]
    public void LeastRecentlyUsedEntriesAreEvictedAndNegativeEntriesShareTheSameBound()
    {
        var first = Png("first", Color.Red);
        var second = Png("second", Color.Green);
        var missing = Path.Combine(_directory, "missing.png");
        using var cache = new LootOverlayIconCache(2);
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.True(cache.Draw(graphics, first, new(0, 0, 64, 64)));
        Assert.True(cache.Draw(graphics, second, new(0, 0, 64, 64)));
        Assert.True(cache.Draw(graphics, first, new(0, 0, 64, 64))); // first is most recent
        File.Delete(second);
        Assert.False(cache.Draw(graphics, missing, new(0, 0, 64, 64))); // evicts second
        Assert.Equal(2, cache.Count);
        Assert.False(cache.Draw(graphics, second, new(0, 0, 64, 64)));
        Assert.Equal(2, cache.Count);
        for (var index = 0; index < 20; index++)
            Assert.False(cache.Draw(graphics, Path.Combine(_directory, "absent-" + index + ".png"), new(0, 0, 64, 64)));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void MissingAndInvalidImagesStayNegativeUntilEvicted()
    {
        var missing = Path.Combine(_directory, "later.png");
        var invalid = Path.Combine(_directory, "invalid.png");
        File.WriteAllText(invalid, "This is not an image.");
        using var cache = new LootOverlayIconCache(1);
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.False(cache.Draw(graphics, missing, new(0, 0, 64, 64)));
        Png("later", Color.Blue);
        Assert.False(cache.Draw(graphics, missing, new(0, 0, 64, 64)));
        Assert.False(cache.Draw(graphics, invalid, new(0, 0, 64, 64)));
        Assert.True(cache.Draw(graphics, missing, new(0, 0, 64, 64)));
        Assert.Equal(1, cache.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.invalid/icon.png")]
    [InlineData(@"\\server\share\icon.png")]
    [InlineData(@"\\?\C:\icon.png")]
    [InlineData("loot-icons/../icon.png")]
    [InlineData("loot-icons/../loot-icons/icon.png")]
    [InlineData("other/icon.png")]
    [InlineData("loot-icons/icon.jpg")]
    [InlineData("loot-icons/CON.png")]
    [InlineData("abyss-rank-icons/../icon.png")]
    [InlineData("abyss-rank-icons/CON.png")]
    [InlineData("class-icons/../icon.png")]
    [InlineData("class-icons/CON.png")]
    public void ForbiddenAssetPathsReturnFalseWithoutCacheEntries(string? asset)
    {
        using var cache = new LootOverlayIconCache();
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.False(cache.Draw(graphics, asset, new(0, 0, 64, 64)));
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData("loot-icons")]
    [InlineData("abyss-rank-icons")]
    [InlineData("class-icons")]
    public void RelativeBundledPathsAreAcceptedAsBoundedMissingEntries(string folder)
    {
        using var cache = new LootOverlayIconCache(1);
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.False(cache.Draw(graphics, folder + "/synthetic-missing-" + Guid.NewGuid().ToString("N") + ".png", new(0, 0, 64, 64)));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void FileAndHeaderLimitsRejectOversizedInputsBeforeDecoding()
    {
        var tooLarge = Path.Combine(_directory, "too-large.png");
        using (var file = File.Create(tooLarge)) file.SetLength(4 * 1024 * 1024 + 1);
        var wideHeader = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 }.CopyTo(wideHeader, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wideHeader.AsSpan(16, 4), 4097);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wideHeader.AsSpan(20, 4), 1);
        var dimensions = Path.Combine(_directory, "dimensions.png");
        File.WriteAllBytes(dimensions, wideHeader);
        using var cache = new LootOverlayIconCache();
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.False(cache.Draw(graphics, tooLarge, new(0, 0, 64, 64)));
        Assert.False(cache.Draw(graphics, dimensions, new(0, 0, 64, 64)));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task ConcurrentDrawAndDisposeUseSeparateCallerSurfacesAndNeverShareDisposedImages()
    {
        var path = Png("concurrent", Color.FromArgb(160, 80, 160, 240));
        using var cache = new LootOverlayIconCache(2);
        using var ready = new CountdownEvent(4);
        using var begin = new ManualResetEventSlim();
        using var firstDraws = new CountdownEvent(4);
        using var disposeNow = new ManualResetEventSlim();
        var draws = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            // Every worker creates and disposes its own GDI objects on its own thread.
            using var surface = new Bitmap(64, 64);
            using var graphics = Graphics.FromImage(surface);
            ready.Signal();
            Assert.True(begin.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(cache.Draw(graphics, path, new(0, 0, 64, 64)));
            firstDraws.Signal();
            Assert.True(disposeNow.Wait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 100; i++) cache.Draw(graphics, path, new(0, 0, 64, 64));
        })).ToArray();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        begin.Set();
        Assert.True(firstDraws.Wait(TimeSpan.FromSeconds(10)));
        var disposer = Task.Run(() => { disposeNow.Set(); cache.Dispose(); });
        await Task.WhenAll(draws.Append(disposer)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, cache.Count);
        using var afterSurface = new Bitmap(64, 64);
        using var afterGraphics = Graphics.FromImage(afterSurface);
        Assert.False(cache.Draw(afterGraphics, path, new(0, 0, 64, 64)));
        cache.Dispose();
    }

    [Fact]
    public void CapacityMustBePositiveAndInvalidDrawingBoundsAreIgnored()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootOverlayIconCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootOverlayIconCache(-1));
        using var cache = new LootOverlayIconCache();
        using var surface = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(surface);
        Assert.False(cache.Draw(graphics, Png("bounds", Color.Red), new(0, 0, float.NaN, 64)));
        Assert.Equal(0, cache.Count);
    }

    private string Png(string name, Color color, int width = 16, int height = 16)
    {
        var path = Path.Combine(_directory, name + ".png");
        using var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        image.Save(path, ImageFormat.Png);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
