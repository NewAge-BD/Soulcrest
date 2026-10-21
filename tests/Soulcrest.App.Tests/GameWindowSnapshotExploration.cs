using OpenCvSharp;
using Soulcrest.App.Capture;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Diagnose: saves the current game window (WGC, without Soulcrest's overlays) as a fixture candidate.</summary>
public sealed class GameWindowSnapshotExploration(ITestOutputHelper output)
{
    [DiagnosticFact(liveDesktop: true)]
    [Trait("Category", "Diagnostic")]
    [Trait("DiagnosticKind", "LiveDesktop")]
    public void SaveGameWindow()
    {
        var target = TestPrerequisites.RequiredValue("SOULCREST_SNAPSHOT");
        using var capture = MonitorCapture.TryStartForGame(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);
        Assert.NotNull(capture);
        Thread.Sleep(300);
        using var image = capture.CaptureMat(capture.Bounds);
        Assert.NotNull(image);
        Cv2.ImWrite(target, image);
        output.WriteLine($"{target}: {image.Width}x{image.Height}, Fenster {capture.IsWindow}");
    }
}
