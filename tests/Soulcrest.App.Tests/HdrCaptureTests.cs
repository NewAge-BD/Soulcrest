using Soulcrest.App.Capture;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>FP16 (scRGB) capture on HDR monitors, converted with the SDR white (user request 2026-10-07).</summary>
public sealed class HdrCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void SdrWhiteBecomesWhiteAndSdrToneStaysAsOnAnSdrScreen()
    {
        const double white = 2.5; // 200 nits
        Assert.Equal(255, HdrDisplay.Encode(2.5f, white));
        Assert.Equal(255, HdrDisplay.Encode(6f, white));      // highlights clip
        Assert.Equal(0, HdrDisplay.Encode(0f, white));
        Assert.Equal(0, HdrDisplay.Encode(-1f, white));
        Assert.Equal(0, HdrDisplay.Encode(float.NaN, white));
        // 18 % grey of SDR content: sRGB 118 on an SDR screen, also here.
        Assert.Equal(118, HdrDisplay.Encode((float)(0.18 * white), white));
        var lookup = HdrDisplay.Lookup(white);
        byte last = 0;
        for (var v = 0f; v < 10f; v += 0.01f) // monotone
        {
            var b = lookup[BitConverter.HalfToUInt16Bits((Half)v)];
            Assert.True(b >= last);
            last = b;
        }
    }

    [Fact]
    public void RgbaHalfPixelsBecomeBgrWithRowPitch()
    {
        const double white = 1;
        // 2 × 2 pixels, row pitch 3 pixels (24 bytes): red, green / blue, white.
        var halves = new ushort[2 * 3 * 4];
        void Set(int x, int y, float r, float g, float b)
        {
            var i = (y * 3 + x) * 4;
            halves[i] = BitConverter.HalfToUInt16Bits((Half)r);
            halves[i + 1] = BitConverter.HalfToUInt16Bits((Half)g);
            halves[i + 2] = BitConverter.HalfToUInt16Bits((Half)b);
            halves[i + 3] = BitConverter.HalfToUInt16Bits((Half)1);
        }
        Set(0, 0, 1, 0, 0);
        Set(1, 0, 0, 1, 0);
        Set(0, 1, 0, 0, 1);
        Set(1, 1, 1, 1, 1);
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(halves, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var bgr = HdrDisplay.ToBgr(pinned.AddrOfPinnedObject(), 24, 2, 2, HdrDisplay.Lookup(white));
            Assert.Equal(new OpenCvSharp.Vec3b(0, 0, 255), bgr.At<OpenCvSharp.Vec3b>(0, 0));
            Assert.Equal(new OpenCvSharp.Vec3b(0, 255, 0), bgr.At<OpenCvSharp.Vec3b>(0, 1));
            Assert.Equal(new OpenCvSharp.Vec3b(255, 0, 0), bgr.At<OpenCvSharp.Vec3b>(1, 0));
            Assert.Equal(new OpenCvSharp.Vec3b(255, 255, 255), bgr.At<OpenCvSharp.Vec3b>(1, 1));
        }
        finally
        {
            pinned.Free();
        }
    }

    [Fact]
    public void MonitorsReportTheirHdrState()
    {
        // Read only: shows what this PC reports (no assertion on the hardware).
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var monitor = MonitorFromPoint(new Point(screen.Bounds.X + 1, screen.Bounds.Y + 1), 2);
            var (hdr, white) = HdrDisplay.Read(monitor);
            output.WriteLine($"{screen.DeviceName}: HDR {hdr}, SDR-Weiß {white * 80:0} nits");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point(int x, int y)
    {
        public int X = x, Y = y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);
}
