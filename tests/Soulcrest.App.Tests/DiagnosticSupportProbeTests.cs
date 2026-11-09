using System.Drawing;
using System.Text.Json;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DiagnosticSupportProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static GameCaptureService.CaptureDiagnostics Capture(string stage, DateTime since) =>
        new("Spielfenster (WGC)", new Rectangle(0, 0, 1280, 720), 15, Now.UtcDateTime, 15, 0, null)
        { Stage = stage, StageSince = since, Hdr = "HDR, SDR-Weiß 200 nits" };

    [Theory]
    [InlineData("WGC starten / Fenster suchen", "wgc-start")]
    [InlineData("WGC: Surface-COM-Zugriff", "wgc-surface-com")]
    [InlineData("D3D11: Map (DO_NOT_WAIT)", "d3d-map")]
    [InlineData("OpenCV: Bild konvertieren", "opencv-convert")]
    public void StalledCaptureStepWarnsEvenIfTheLastFrameWasRecent(string stage, string code)
    {
        var capture = Capture(stage, Now.UtcDateTime.AddSeconds(-6));
        var result = DiagnosticContributors.CaptureSnapshot(capture, true, false, Now, true, "Allowed");
        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal(code, result.Facts["stage"]);
        Assert.Equal(true, result.Facts["stageStalled"]);
        Assert.Equal(6000d, result.Facts["stageElapsedMs"]);
        Assert.Equal(Now.AddSeconds(-6), result.Facts["stageSinceUtc"]);
        Assert.Equal(true, result.Facts["fresh"]);
        Assert.Equal("hdr", result.Facts["colorMode"]);
        Assert.Equal(200, result.Facts["sdrWhiteNits"]);
        Assert.Equal("Allowed", result.Facts["borderPermission"]);
    }

    [Fact]
    public void IdleCaptureStepDoesNotBecomeAStallAndMissingTimesStayUnknown()
    {
        var idle = DiagnosticContributors.CaptureSnapshot(Capture("Bereit", Now.UtcDateTime.AddHours(-1)), true, null, Now);
        Assert.Equal(DiagnosticStatus.Healthy, idle.Status);
        Assert.Equal(false, idle.Facts["stageStalled"]);
        var missing = DiagnosticContributors.CaptureSnapshot(Capture("D3D11: Flush", default), true, null, Now);
        Assert.Null(missing.Facts["stageSinceUtc"]);
        Assert.Null(missing.Facts["stageElapsedMs"]);
        Assert.Equal(false, missing.Facts["stageStalled"]);
    }

    [Fact]
    public void CaptureSupportFactsCannotExportArbitraryDriverStageMethodHdrOrPermissionText()
    {
        const string privateText = "Synthetic private identity C:/private/driver 192.0.2.50";
        var capture = Capture(privateText, Now.UtcDateTime.AddSeconds(-10)) with
        { Method = privateText, Hdr = privateText, LastProblem = privateText };
        var result = DiagnosticContributors.CaptureSnapshot(capture, true, null, Now, true, privateText);
        Assert.Equal("unknown", result.Facts["stage"]);
        Assert.Equal("unknown", result.Facts["method"]);
        Assert.Equal("unknown", result.Facts["colorMode"]);
        Assert.Null(result.Facts["sdrWhiteNits"]);
        Assert.Null(result.Facts["borderPermission"]);
        Assert.Equal(true, result.Facts["problemReported"]);
        Assert.DoesNotContain(privateText, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("142.0.3595.65", "142.0.3595.65")]
    [InlineData("142.0.3595.65 beta", "142.0.3595.65")]
    [InlineData("Synthetic private runtime C:/private", null)]
    [InlineData(null, null)]
    public void RuntimeVersionOnlyExportsValidatedVersionNumbers(string? input, string? expected) =>
        Assert.Equal(expected, DiagnosticSupportProbe.NormalizeWebViewVersion(input));

    [Fact]
    public void SystemSupportMetadataContainsOnlyBoundedAnonymousScalars()
    {
        var facts = DiagnosticSupportProbe.SystemFacts();
        Assert.Contains(facts, fact => fact.Key == "webView2RuntimeInstalled");
        Assert.Contains(facts, fact => fact.Key == "gameProcessCount");
        Assert.Contains(facts, fact => fact.Key == "soulcrestElevated");
        Assert.DoesNotContain(facts, fact => fact.Key.Contains("title", StringComparison.OrdinalIgnoreCase)
            || fact.Key.Contains("pid", StringComparison.OrdinalIgnoreCase) || fact.Key.Contains("path", StringComparison.OrdinalIgnoreCase)
            || fact.Key.Contains("deviceName", StringComparison.OrdinalIgnoreCase));
        Assert.All(facts, fact => Assert.True(fact.Value is null or bool or int or string));
        Assert.Equal(facts.Count, facts.Select(fact => fact.Key).Distinct().Count());
        Assert.True(facts.Count <= 80);
    }
}
