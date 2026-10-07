using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Diagnose report on this PC (SOULCREST_DIAG=1), on the test data folder.</summary>
public sealed class DiagnosticsExploration(ITestOutputHelper output)
{
    [Fact]
    public void WritesTheReport()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_DIAG") != "1")
            return;
        var settings = new SettingsService();
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        var loot = new TrackerService(settings);
        using var map = new MapTrackingService(settings, progress, capture);
        using var scan = new PetScanService(settings, progress, capture);
        using var diagnostics = new DiagnosticsService(settings, capture, loot, map, scan);
        using (capture.Grab(new System.Drawing.Rectangle(0, 0, 200, 200))) { }
        diagnostics.Check();
        output.WriteLine(File.ReadAllText(diagnostics.ReportPath));
    }
}
