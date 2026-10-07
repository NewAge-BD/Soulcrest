using System.Diagnostics;
using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.Ocr;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Diagnose: cost of each step of the world-map fast loop on this machine (prints only).</summary>
public sealed class CaptureTimingExploration(ITestOutputHelper output)
{
    [Fact]
    public void MeasureWorldMapLoopSteps()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        double Time(Action action, int runs = 20)
        {
            action();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < runs; i++)
                action();
            return watch.Elapsed.TotalMilliseconds / runs;
        }
        output.WriteLine($"Vollbild-Kopie {screen.Width}x{screen.Height}: {Time(() => ScreenCapture.Capture(screen).Dispose()):0.0} ms");
        output.WriteLine($"StretchBlt 1/4: {Time(() => ScreenCapture.CaptureScaled(screen, 0.25).Dispose()):0.0} ms");
        using (var wgc = MonitorCapture.TryStart(screen))
        {
            output.WriteLine($"WGC verfügbar: {wgc is not null}");
            if (wgc is not null)
            {
                Thread.Sleep(300);
                output.WriteLine($"WGC Vollbild: {Time(() => wgc.Capture(screen)?.Dispose()):0.0} ms");
                output.WriteLine($"WGC Vollbild 1/4: {Time(() => wgc.Capture(screen, 0.25)?.Dispose()):0.0} ms");
                output.WriteLine($"WGC Mat 1/4: {Time(() => wgc.CaptureMat(screen, 0.25)?.Dispose()):0.0} ms");
                output.WriteLine($"WGC Mat Kleinkarte: {Time(() => wgc.CaptureMat(new Rectangle(1792, 960, 518, 469))?.Dispose()):0.0} ms");
                var region = new Rectangle(1792, 960, 518, 469);
                output.WriteLine($"WGC Kleinkarte 518x469: {Time(() => wgc.Capture(region)?.Dispose()):0.0} ms");
                using var check = wgc.Capture(region);
                output.WriteLine($"WGC Bild: {check?.Width}x{check?.Height}");
                check?.Save(Path.Combine(Path.GetTempPath(), "soulcrest-wgc-region.png"));
            }
        }
        using var bitmap = ScreenCapture.CaptureScaled(screen, 0.25);
        output.WriteLine($"Bitmap->Mat 1/4: {Time(() => BitmapMat.ToBgr(bitmap).Dispose()):0.0} ms");
        using var frame = BitmapMat.ToBgr(bitmap);
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var flow = new MapFlow();
        flow.Step(gray);
        output.WriteLine($"Fluss 1/4: {Time(() => flow.Step(gray)):0.0} ms");
        var fullOverlay = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        output.WriteLine($"Overlay-Bitmap löschen Vollbild: {Time(() => { using var g = Graphics.FromImage(fullOverlay); g.Clear(Color.Transparent); }):0.0} ms");
        output.WriteLine($"GetHbitmap Vollbild: {Time(() => { var h = fullOverlay.GetHbitmap(Color.FromArgb(0)); Soulcrest.App.Tests.Gdi.DeleteObject(h); }, 10):0.0} ms");
    }
}

internal static class Gdi
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint handle);
}
