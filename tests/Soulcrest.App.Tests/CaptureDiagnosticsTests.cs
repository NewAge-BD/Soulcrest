using System.IO.Compression;
using System.Reflection;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CaptureDiagnosticsTests
{
    [Fact]
    public void BasicReportIncludesTheStoppedPetScanFailureWithoutWaitingForSystemChecks()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        using var loot = new TrackerService(settings);
        using var map = new MapTrackingService(settings, progress, capture);
        using var scan = new PetScanService(settings, progress, capture);
        typeof(PetScanService).GetProperty(nameof(PetScanService.Status))!.SetValue(scan, "Scan abgebrochen: Testfehler");
        typeof(PetScanService).GetProperty(nameof(PetScanService.StatusIsError))!.SetValue(scan, true);
        using var diagnostics = new DiagnosticsService(settings, capture, loot, map, scan);
        var report = diagnostics.CaptureReport();
        Assert.Contains("Pet-Scan: Scan abgebrochen: Testfehler", report);
        Assert.Contains("Pet-Scan läuft: False", report);
        Assert.Contains("Pet-Scan Fehler: True", report);
        Assert.Contains("GPU-bedingt übersprungene Bilder: 0", report);
    }

    [Fact]
    public async Task DiagnosticsRemainReadableWhileCaptureLockIsHeld()
    {
        using var capture = new GameCaptureService();
        var gate = typeof(GameCaptureService).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockedDriver = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var reading = Task.Run(() => { Assert.Equal("GDI", capture.Method); return capture.Diagnostics; });
            var result = await reading.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("Bereit", result.Stage);
            Assert.Equal(0, result.Grabs);
        }
        finally { release.Set(); await blockedDriver; }
    }

    [Fact]
    public void PackageAlwaysContainsCaptureReportEvenWithoutCompletedCheck()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            DiagnosticsService.AddReport(zip, "Schritt: GPU-Lesekopie", null, "Test error");
        bytes.Position = 0;
        using var read = new ZipArchive(bytes, ZipArchiveMode.Read);
        using var reader = new StreamReader(read.GetEntry("diagnose.txt")!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("Schritt: GPU-Lesekopie", text);
        Assert.Contains("Noch kein Gesamtbericht", text);
        Assert.Contains("Test error", text);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForeverForBlockedCapture()
    {
        var capture = new GameCaptureService();
        var gate = typeof(GameCaptureService).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var driver = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(capture.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Throws<ObjectDisposedException>(() => capture.Grab(new System.Drawing.Rectangle(0, 0, 10, 10)));
            capture.Dispose();
        }
        finally { release.Set(); await driver; }
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public void PackageUsesExistingPreviewWithoutTakingAnotherScreenshot(string type)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            DiagnosticsService.AddPreview(zip, "test", $"data:image/{type};base64,AQID");
        bytes.Position = 0;
        using var read = new ZipArchive(bytes, ZipArchiveMode.Read);
        var entry = Assert.Single(read.Entries);
        Assert.Equal($"bilder/test.{(type == "jpeg" ? "jpg" : "png")}", entry.FullName);
        using var data = entry.Open();
        using var content = new MemoryStream();
        data.CopyTo(content);
        Assert.Equal(new byte[] { 1, 2, 3 }, content.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("data:image/jpeg;base64,invalid!")]
    public void MissingOrInvalidPreviewProducesDiagnosticNote(string? preview)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            DiagnosticsService.AddPreview(zip, "test", preview);
        bytes.Position = 0;
        using var read = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Equal("bilder/test-fehlt.txt", Assert.Single(read.Entries).FullName);
    }
}
