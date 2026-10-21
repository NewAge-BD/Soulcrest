using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Diagnose report on this PC (SOULCREST_DIAG=1), on the test data folder.</summary>
public sealed class DiagnosticsExploration(ITestOutputHelper output)
{
    [DiagnosticFact(liveDesktop: true)]
    [Trait("Category", "Diagnostic")]
    [Trait("DiagnosticKind", "LiveDesktop")]
    public void WritesTheReport()
    {
        TestPrerequisites.RequiredFlag("SOULCREST_DIAG");
        var settings = new SettingsService();
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        var loot = new TrackerService(settings);
        using var map = new MapTrackingService(settings, progress, capture);
        using var diagnostics = new DiagnosticsService(settings, capture, loot, map);
        using (capture.Grab(new System.Drawing.Rectangle(0, 0, 200, 200))) { }
        diagnostics.Check();
        output.WriteLine(File.ReadAllText(diagnostics.ReportPath));
    }
}
