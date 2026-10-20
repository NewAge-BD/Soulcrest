using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNodeMaskTests
{
    [Fact]
    public void EmbeddedAssetIsTheUnchangedUserOriginalAndNativeContourMatchesItsAlpha()
    {
        using var source = typeof(DaevanionNodeMask).Assembly.GetManifestResourceStream("Soulcrest.DaevanionNodeRoundMask.png")!;
        using var copy = new MemoryStream(); source.CopyTo(copy);
        Assert.Equal("0a4da5b4e2e659972a7b748cc1865c2d826d2776c9c1a4cafbbe9bd05f3ae04d", Convert.ToHexStringLower(SHA256.HashData(copy.ToArray())));
        copy.Position = 0; using var original = new Bitmap(copy);
        using var mask = new DaevanionNodeMask();
        Assert.Equal(new Size(156, 154), original.Size);
        Assert.Equal(new Rectangle(25, 24, 104, 104), mask.ContentBounds(original.Size));
        using var target = Solid(original.Size, Color.White);
        mask.Apply(target);
        var interiorPixels = 0;
        for (var y = 0; y < original.Height; y++)
        for (var x = 0; x < original.Width; x++)
        {
            var expected = 255 - original.GetPixel(x, y).A;
            Assert.Equal(expected, target.GetPixel(x, y).A);
            if (expected > 0) interiorPixels++;
        }
        Assert.Equal(8660, interiorPixels);
        Assert.Equal(255, target.GetPixel(78, 77).A);
        Assert.Equal(0, target.GetPixel(0, 0).A);
        Assert.Equal(0, target.GetPixel(26, 25).A); // The round contour leaves its bounding-box corner clear.
    }

    [Fact]
    public void BundledBackgroundKeepsOriginalRgbaAndRegistersThroughItsAlphaBounds()
    {
        using var source = typeof(DaevanionNodeMask).Assembly.GetManifestResourceStream("Soulcrest.DaevanionNodeBackground.png")!;
        using var copy = new MemoryStream(); source.CopyTo(copy);
        Assert.Equal("8e08bbda3354f7b6b019a1454ce98c0ffbbabbdba8c2c22a07dd348195672dc2", Convert.ToHexStringLower(SHA256.HashData(copy.ToArray())));
        copy.Position = 0; using var background = new Bitmap(copy);
        Assert.Equal(new Size(1254, 1254), background.Size);
        Assert.Equal(252, background.GetPixel(627, 627).A);
        Assert.Equal(0, background.GetPixel(0, 0).A);
        var left = background.Width; var top = background.Height; var right = -1; var bottom = -1; var count = 0;
        var data = background.LockBits(new Rectangle(Point.Empty, background.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[background.Width * 4];
            for (var y = 0; y < background.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (var x = 0; x < background.Width; x++)
                {
                    if (row[x * 4 + 3] == 0) continue;
                    left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); count++;
                }
            }
        }
        finally { background.UnlockBits(data); }
        Assert.Equal(new Rectangle(230, 242, 793, 769), Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
        Assert.Equal(480293, count);
    }

    [Fact]
    public void RgbGhostsAndIsolatedExteriorAntialiasingCannotBecomeIconHoles()
    {
        using var original = new Bitmap(9, 9, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(original)) g.Clear(Color.Black);
        original.SetPixel(4, 4, Color.FromArgb(0, 230, 15, 100));
        original.SetPixel(5, 4, Color.FromArgb(128, 30, 240, 20));
        original.SetPixel(0, 0, Color.FromArgb(1, 255, 255, 255));
        using var mask = new DaevanionNodeMask(original);
        using var target = Solid(original.Size, Color.White);
        mask.Apply(target);
        Assert.Equal(new Rectangle(4, 4, 2, 1), mask.ContentBounds(original.Size));
        Assert.Equal(255, target.GetPixel(4, 4).A);
        Assert.Equal(127, target.GetPixel(5, 4).A);
        Assert.Equal(0, target.GetPixel(0, 0).A);
    }

    [Fact]
    public void FeatherMultipliesPremultipliedColorAndAlphaTogether()
    {
        using var original = new Bitmap(3, 3, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(original)) g.Clear(Color.Black);
        original.SetPixel(1, 1, Color.FromArgb(127, 10, 20, 30));
        using var mask = new DaevanionNodeMask(original);
        using var target = Solid(original.Size, Color.FromArgb(128, 200, 100, 50));
        var before = RawPixel(target, 1, 1);
        mask.Apply(target);
        var after = RawPixel(target, 1, 1);
        Assert.Equal(before.Select(channel => (byte)((channel * 128 + 127) / 255)), after);
        Assert.True(after[0] <= after[3] && after[1] <= after[3] && after[2] <= after[3]);
    }

    [Theory]
    [InlineData(40, 38)]
    [InlineData(60, 60)]
    [InlineData(158, 154)]
    [InlineData(100, 75)]
    public void ScalingKeepsFrameTransparentAndHasSmoothConnectedCoverage(int width, int height)
    {
        using var mask = new DaevanionNodeMask();
        var size = new Size(width, height);
        var bounds = mask.ContentBounds(size);
        Assert.Equal(bounds, mask.ContentBounds(size));
        using var first = Solid(size, Color.White); using var second = Solid(size, Color.White);
        mask.Apply(first); mask.Apply(second);
        Assert.Equal(255, first.GetPixel(width / 2, height / 2).A);
        var partial = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var alpha = first.GetPixel(x, y).A;
            Assert.Equal(alpha, second.GetPixel(x, y).A);
            if (alpha > 0) Assert.True(bounds.Contains(x, y));
            if (alpha is > 0 and < 255) partial++;
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1) Assert.Equal(0, alpha);
        }
        Assert.True(partial > 0);
    }

    [Fact]
    public void InvalidFormatAndDisposedMaskFailExplicitly()
    {
        var mask = new DaevanionNodeMask();
        using var straightAlpha = new Bitmap(20, 20, PixelFormat.Format32bppArgb);
        Assert.Throws<ArgumentException>(() => mask.Apply(straightAlpha));
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.ContentBounds(Size.Empty));
        mask.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mask.ContentBounds(new(20, 20)));
    }

    private static Bitmap Solid(Size size, Color color)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image); g.Clear(color);
        return image;
    }

    private static byte[] RawPixel(Bitmap image, int x, int y)
    {
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { var bytes = new byte[4]; Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride + x * 4), bytes, 0, 4); return bytes; }
        finally { image.UnlockBits(data); }
    }
}
