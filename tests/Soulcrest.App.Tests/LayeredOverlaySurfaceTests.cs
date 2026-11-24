using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LayeredOverlaySurfaceTests
{
    [Fact]
    public void AReusableNativeBufferCopiesPremultipliedPixelsAndReplacesOldTransparentAreas()
    {
        using var reusable = new LayeredOverlaySurface();
        using var image = new Bitmap(73, 61, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            using var ink = new SolidBrush(Color.FromArgb(113, 239, 184, 91));
            graphics.FillEllipse(ink, 4, 8, 53, 41);
        }
        Assert.True(reusable.TryCopyFrom(image));
        Assert.Equal(Pixels(image), NativePixels(reusable));
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Transparent);
        Assert.True(reusable.TryCopyFrom(image));
        Assert.All(NativePixels(reusable), pixel => Assert.Equal(0, pixel));
        Assert.Equal(1, reusable.AllocationCount);
    }

    [Fact]
    public void GeometryChangesReplaceTheBufferAndResetAndDisposeReleaseIt()
    {
        var reusable = new LayeredOverlaySurface();
        using var small = new Bitmap(29, 31, PixelFormat.Format32bppPArgb);
        using var large = new Bitmap(91, 87, PixelFormat.Format32bppPArgb);
        Assert.True(reusable.TryCopyFrom(small));
        Assert.True(reusable.TryCopyFrom(small));
        Assert.Equal(1, reusable.AllocationCount);
        Assert.True(reusable.TryCopyFrom(large));
        Assert.Equal(large.Size, reusable.Size);
        Assert.Equal(2, reusable.AllocationCount);
        reusable.Reset();
        Assert.Equal(Size.Empty, reusable.Size);
        Assert.Equal(0, Address(reusable));
        Assert.True(reusable.TryCopyFrom(small));
        Assert.Equal(3, reusable.AllocationCount);
        reusable.Dispose(); reusable.Dispose();
        Assert.Equal(Size.Empty, reusable.Size);
        Assert.Equal(0, Address(reusable));
        Assert.Throws<ObjectDisposedException>(() => reusable.TryCopyFrom(small));
    }

    [Fact]
    public void NonPremultipliedInputIsRejectedWithoutAllocatingANativeSurface()
    {
        using var reusable = new LayeredOverlaySurface();
        using var image = new Bitmap(20, 20, PixelFormat.Format32bppArgb);
        Assert.Throws<ArgumentException>(() => reusable.TryCopyFrom(image));
        Assert.Equal(0, reusable.AllocationCount);
        Assert.Equal(Size.Empty, reusable.Size);
    }

    [Fact]
    public void ABottomUpInputIsCopiedInItsLogicalRowOrder()
    {
        const int width = 8, height = 7, stride = width * 4;
        var storage = Marshal.AllocHGlobal(stride * height);
        try
        {
            var values = Enumerable.Range(0, width * height).Select(index => unchecked((int)0xff000000)
                | ((index / width + 1) << 16) | ((index % width + 1) << 8)).ToArray();
            Marshal.Copy(values, 0, storage, values.Length);
            using var image = new Bitmap(width, height, -stride, PixelFormat.Format32bppPArgb, storage + (height - 1) * stride);
            using var reusable = new LayeredOverlaySurface();
            Assert.True(reusable.TryCopyFrom(image));
            Assert.Equal(Pixels(image), NativePixels(reusable));
        }
        finally { Marshal.FreeHGlobal(storage); }
    }

    private static nint Address(LayeredOverlaySurface surface) =>
        (nint)typeof(LayeredOverlaySurface).GetField("_bits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;

    private static int[] NativePixels(LayeredOverlaySurface surface)
    {
        var result = new int[surface.Size.Width * surface.Size.Height];
        Marshal.Copy(Address(surface), result, 0, result.Length);
        return result;
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, result, row * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return result;
    }
}
