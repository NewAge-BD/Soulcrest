using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Diagnose: game window capture must not contain Soulcrest's overlays (saves both pictures).</summary>
public sealed class WindowCaptureExploration(ITestOutputHelper output)
{
    [DiagnosticFact(liveDesktop: true)]
    [Trait("Category", "Diagnostic")]
    [Trait("DiagnosticKind", "LiveDesktop")]
    public void WindowCaptureLeavesOverlaysOut()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var capture = MonitorCapture.TryStartForGame(screen);
        Assert.NotNull(capture);
        Thread.Sleep(300);
        output.WriteLine($"Fenster: {capture.IsWindow}, Bounds {capture.Bounds}");
        using var window = capture.CaptureMat(capture.Bounds, 0.5);
        using var desktopBitmap = ScreenCapture.Capture(screen);
        using var desktop = Soulcrest.Ocr.BitmapMat.ToBgr(desktopBitmap);
        using var desktopSmall = new Mat();
        Cv2.Resize(desktop, desktopSmall, new OpenCvSharp.Size(), 0.5, 0.5, InterpolationFlags.Area);
        if (window is not null)
            Cv2.ImWrite(Path.Combine(Path.GetTempPath(), "soulcrest-window-capture.png"), window);
        Cv2.ImWrite(Path.Combine(Path.GetTempPath(), "soulcrest-desktop-capture.png"), desktopSmall);
    }
}
