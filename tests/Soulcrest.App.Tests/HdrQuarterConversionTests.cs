using System.Runtime.InteropServices;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The faster HDR world-map conversion must preserve the existing samples and exact bytes.</summary>
public sealed class HdrQuarterConversionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.001)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    [InlineData(4.0)]
    [InlineData(6.0)] // 480 nits on the profiled monitor
    [InlineData(10.0)]
    [InlineData(16.0)]
    public void EveryHalfBitPatternAndRandomRgbaRemainPixelIdentical(double white)
    {
        // The step=2 source grid contains every ushort bit pattern in each RGB channel:
        // signed zeros, subnormals, finite values, infinities and every NaN payload.
        const int width = 512, height = 512, pitch = width * 8 + 48;
        var source = new ushort[pitch / 2 * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * (pitch / 2) + x * 4;
                var bits = (y / 2) * (width / 2) + x / 2;
                source[i] = (ushort)bits;
                source[i + 1] = (ushort)(bits * 4051 + 17);
                source[i + 2] = (ushort)(bits * 2017 + 433);
                source[i + 3] = (ushort)(bits * 73); // alpha is ignored, including NaNs
            }
        }
        AssertSameConversion(source, pitch, width, height, HdrDisplay.Lookup(white), 0.25);

        const int randomWidth = 640, randomHeight = 480, randomPitch = randomWidth * 8 + 256;
        var random = new byte[randomPitch * randomHeight];
        new Random(0x54231).NextBytes(random); // random alpha and padding too
        AssertSameConversion(random, randomPitch, randomWidth, randomHeight, HdrDisplay.Lookup(white), 0.25);
    }

    [Theory]
    [InlineData(4, 4, 0, 0.25)]
    [InlineData(8, 12, 24, 0.25)]
    [InlineData(2560, 1440, 0, 0.25)]
    [InlineData(2560, 1440, 256, 0.25)]
    [InlineData(257, 259, 24, 0.25)]
    [InlineData(511, 513, 256, 0.25)]
    [InlineData(512, 513, 48, 0.25)]
    [InlineData(513, 512, 48, 0.25)]
    [InlineData(6, 8, 24, 0.25)]
    [InlineData(8, 6, 24, 0.25)]
    [InlineData(257, 259, 24, 0.5)]
    [InlineData(256, 256, 256, 1.0)]
    [InlineData(512, 512, 0, 0.3)]
    [InlineData(512, 512, 0, 0.75)]
    [InlineData(16, 20, 0, 0.250000001)]
    [InlineData(16, 20, 0, 0.4999999)]
    [InlineData(16, 20, 0, 0.5000001)]
    [InlineData(16, 20, 0, 0.9999995)]
    public void DimensionsRowPitchAndOtherScalesKeepTheExistingResult(int width, int height, int padding, double scale)
    {
        var pitch = width * 8 + padding;
        var source = new byte[pitch * height];
        new Random(0x4156).NextBytes(source);
        AssertSameConversion(source, pitch, width, height, HdrDisplay.Lookup(6.0), scale);
    }

    [Fact]
    public void EveryPossibleTwoByTwoChannelSumMatchesOpenCvRounding()
    {
        var lookup = IdentityLookup(65536);
        const int width = 1021 * 4, height = 4, pitch = width * 8 + 24;
        var source = new ushort[pitch / 2 * height];
        for (var sum = 0; sum <= 1020; sum++)
        {
            var remaining = sum;
            for (var sample = 0; sample < 4; sample++)
            {
                var value = Math.Min(255, remaining);
                remaining -= value;
                var pixel = (sample / 2 * 2) * (pitch / 2) + (sum * 4 + sample % 2 * 2) * 4;
                source[pixel] = source[pixel + 1] = source[pixel + 2] = (ushort)value;
                source[pixel + 3] = 0xffff;
            }
        }
        WithPinned(source, data =>
        {
            using var expected = ExistingConversion(data, pitch, width, height, lookup, 0.25);
            using var actual = HdrDisplay.ToBgrScaled(data, pitch, width, height, lookup, 0.25);
            AssertPixelsEqual(expected, actual);
            for (var sum = 0; sum <= 1020; sum++)
            {
                var rounded = (byte)((sum + 2) >> 2);
                Assert.Equal(new Vec3b(rounded, rounded, rounded), actual.At<Vec3b>(0, sum));
            }
        });
    }

    [Fact]
    public void QuarterUsesTheOriginalSampleGridAndBgrOrder()
    {
        const int width = 4, height = 4, pitch = width * 8 + 16;
        var source = Enumerable.Repeat((ushort)255, pitch / 2 * height).ToArray();
        for (var sample = 0; sample < 4; sample++)
        {
            var pixel = (sample / 2 * 2) * (pitch / 2) + (sample % 2 * 2) * 4;
            source[pixel] = (ushort)sample;
            source[pixel + 1] = (ushort)(10 + sample);
            source[pixel + 2] = (ushort)(20 + sample);
            source[pixel + 3] = 0xffff;
        }
        WithPinned(source, data =>
        {
            using var actual = HdrDisplay.ToBgrScaled(data, pitch, width, height, IdentityLookup(65536), 0.25);
            Assert.Equal(new OpenCvSharp.Size(1, 1), actual.Size());
            Assert.Equal(new Vec3b(22, 12, 2), actual.At<Vec3b>(0, 0));
        });
    }

    [Theory]
    [InlineData(256)]
    [InlineData(65535)]
    [InlineData(65537)]
    public void NonstandardLookupLengthsRetainTheExistingFallback(int lookupLength)
    {
        const int width = 8, height = 12, pitch = width * 8 + 24;
        var source = new ushort[pitch / 2 * height];
        for (var i = 0; i < source.Length; i++) source[i] = (ushort)(i % 256);
        AssertSameConversion(source, pitch, width, height, IdentityLookup(lookupLength), 0.25);
    }

    private static byte[] IdentityLookup(int length)
    {
        var table = new byte[length];
        for (var i = 0; i < table.Length; i++) table[i] = (byte)(i % 256);
        return table;
    }

    private static void AssertSameConversion(Array source, int pitch, int width, int height, byte[] lookup, double scale)
    {
        WithPinned(source, data =>
        {
            using var expected = ExistingConversion(data, (uint)pitch, width, height, lookup, scale);
            using var actual = HdrDisplay.ToBgrScaled(data, (uint)pitch, width, height, lookup, scale);
            AssertPixelsEqual(expected, actual);
        });
    }

    // Independent reference: the original MonitorCapture HDR branch, including odd-size
    // truncation by ToBgr and OpenCV's output dimension/rounding rules.
    private static Mat ExistingConversion(nint data, uint pitch, int width, int height, byte[] lookup, double scale)
    {
        var step = scale <= 0.5 ? 2 : 1;
        var converted = HdrDisplay.ToBgr(data, pitch, width, height, lookup, step);
        var rest = scale * step;
        if (Math.Abs(rest - 1) < 1e-6) return converted;
        using (converted)
        {
            var result = new Mat();
            Cv2.Resize(converted, result, new OpenCvSharp.Size(), rest, rest, InterpolationFlags.Area);
            return result;
        }
    }

    private static void WithPinned(Array source, Action<nint> action)
    {
        var pinned = GCHandle.Alloc(source, GCHandleType.Pinned);
        try { action(pinned.AddrOfPinnedObject()); }
        finally { pinned.Free(); }
    }

    private static void AssertPixelsEqual(Mat expected, Mat actual)
    {
        Assert.Equal(expected.Size(), actual.Size());
        Assert.Equal(MatType.CV_8UC3, actual.Type());
        var rows = expected.Rows;
        var rowBytes = expected.Cols * 3;
        var expectedBytes = new byte[rows * rowBytes];
        var actualBytes = new byte[rows * rowBytes];
        for (var y = 0; y < rows; y++)
        {
            Marshal.Copy(expected.Ptr(y), expectedBytes, y * rowBytes, rowBytes);
            Marshal.Copy(actual.Ptr(y), actualBytes, y * rowBytes, rowBytes);
        }
        Assert.Equal(expectedBytes, actualBytes);
    }
}
