using System.Drawing;
using System.Drawing.Drawing2D;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationScanRegressionTests
{
    [Fact]
    public void EndLabelDoesNotStopWhileVisibleNamesAreUnclear()
    {
        var confirmation = new ExplorationScanService.Confirmation();
        var uncertain = new ExplorationScanService.Page(["a"], ["Unreadable grey text"], true, "dungeon");
        confirmation.Observe(uncertain);
        confirmation.Observe(uncertain);
        confirmation.Observe(uncertain);
        Assert.False(confirmation.Finished);
        var resolved = uncertain with { Unmatched = [] };
        confirmation.Observe(resolved);
        Assert.False(confirmation.Finished);
        confirmation.Observe(resolved);
        Assert.True(confirmation.Finished);
    }

    [Fact]
    public void EndLabelAloneCannotFinishWhenAllGreyNamesAreMissing()
    {
        var confirmation = new ExplorationScanService.Confirmation();
        for (var i = 0; i < 3; i++) confirmation.Observe(new([], [], true, "dungeon", false, []));
        Assert.False(confirmation.Finished);
    }

    [Fact]
    public void EndLabelWaitsForRepeatedNamesEvenWhenTheirChecksAreMissing()
    {
        var confirmation = new ExplorationScanService.Confirmation();
        static ExplorationScanService.Page Page(string id) => new([], [], true, "dungeon", false,
            [new(new OcrLine(id, 40, 400, 100, 20), id, false)]);
        confirmation.Observe(Page("a"));
        Assert.False(confirmation.Finished);
        confirmation.Observe(Page("b"));
        Assert.False(confirmation.Finished);
        Assert.Empty(confirmation.Observe(Page("b")));
        Assert.True(confirmation.Finished);
        Assert.Equal(new[] { "b" }, confirmation.Read);
    }

    [Theory]
    [InlineData("en-top", 14, false)]
    [InlineData("en-end", 9, true)]
    public async Task UltrawideListKeepsNamesAndOriginalCompletionSymbolsAligned(string fixture, int count, bool end)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1");
            return;
        }
        var layout = ExplorationLayout.ForWindow(new Size(2560, 1080));
        using var source = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "exploration-list", fixture + ".png"));
        // Simulate height-scaled game UI on 2560x1080 using the real 2559x1439 left-list fixture.
        using var capture = new Bitmap(layout.CaptureBounds.Width, layout.CaptureBounds.Height);
        using (var g = Graphics.FromImage(capture))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(Point.Empty, capture.Size));
        }
        var original = await WindowsOcrLineReader.Create("en-US", 1).ReadAsync(capture);
        using var enhanced = ExplorationTextEnhancer.Create(capture);
        var contrast = await WindowsOcrLineReader.Create("en-US", 2).ReadAsync(enhanced);
        var places = new ExplorationService(new ProgressService()).Places;
        var page = ExplorationOcrSelection.Read(original, contrast, places, capture, layout, "altgard", ["Altgard"]);
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.Equal(count, page.Ids.Count);
        Assert.Equal(end, page.End);
    }

    [Fact]
    public void DiagnosisIncludesExplorationScanWithoutAnotherCapture()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        using var loot = new TrackerService(settings);
        using var map = new MapTrackingService(settings, progress, capture);
        using var pets = new PetScanService(settings, progress, capture);
        using var exploration = new ExplorationScanService(new ExplorationService(progress), capture, settings);
        typeof(ExplorationScanService).GetProperty(nameof(ExplorationScanService.Status))!.SetValue(exploration, "Unklare Zeilen");
        typeof(ExplorationScanService).GetProperty(nameof(ExplorationScanService.DetectedKind))!.SetValue(exploration, "dungeon");
        typeof(ExplorationScanService).GetProperty(nameof(ExplorationScanService.Frames))!.SetValue(exploration, 7);
        typeof(ExplorationScanService).GetProperty(nameof(ExplorationScanService.ContrastEnhanced))!.SetValue(exploration, true);
        using var diagnostics = new DiagnosticsService(settings, capture, loot, map, pets, exploration);
        var report = diagnostics.CaptureReport();
        Assert.Contains("Erkundungsscan: Unklare Zeilen", report);
        Assert.Contains("Kategorie: dungeon", report);
        Assert.Contains("Erkundungsscan Bilder: 7; Kontrastansicht: True", report);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }
}
