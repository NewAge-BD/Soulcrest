using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ExplorationScanOverlayTests : IDisposable
{
    private readonly string _language = UiText.Language;

    public ExplorationScanOverlayTests() => UiText.Language = "de";
    public void Dispose() => UiText.Language = _language;

    [Theory]
    [InlineData("dungeon", "Soulcrest · Sealed Dungeons")]
    [InlineData("stronghold", "Soulcrest · Strongholds")]
    [InlineData("kibelisk", "Soulcrest · Kibelisks")]
    [InlineData("all", "Soulcrest · Erkundungsscan")]
    [InlineData(null, "Soulcrest · Erkundungsscan")]
    public void HeadingNamesTheSelectedCategoryInsteadOfFallingThroughTheSwitch(string? kind, string expected) =>
        Assert.Equal(expected, ExplorationScanOverlayForm.CategoryTitle(kind));

    [Theory]
    [InlineData(2560, 1080)]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1024, 600)]
    public void AssistanceFitsTheGameWindowAndLeavesTheListVisible(int width, int height)
    {
        var window = new Rectangle(-width, 40, width, height);
        var list = ExplorationLayout.ForWindow(window.Size).Bounds;
        var panel = ExplorationScanOverlayForm.PanelBounds(window.Size, list);
        var feedback = new ExplorationScanFeedback(window, list,
            "Sichtbare Zeilen geprüft – weiterscrollen. Nur Haken zählen als fertig.", 7, 9, [], false);
        using var image = ExplorationScanOverlayForm.CreateImage(feedback, "dungeon", feedback.Message, 41, true, out var area);

        Assert.True(new Rectangle(Point.Empty, window.Size).Contains(area));
        Assert.True(area.Contains(list));
        Assert.True(area.Contains(panel));
        Assert.False(panel.IntersectsWith(list));
        Assert.Equal(area.Size, image.Size);
        Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
        Assert.Equal(0, image.GetPixel(0, 0).A);

        var folder = Environment.GetEnvironmentVariable("SOULCREST_EXPLORATION_OVERLAY_PREVIEW");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            image.Save(Path.Combine(folder, $"exploration-overlay-{width}x{height}.png"), ImageFormat.Png);
        }
    }

    [Fact]
    public void StoppingKeepsTheSummaryButRemovesRowHighlights()
    {
        var list = new Rectangle(0, 130, 420, 820);
        ExplorationRowMark[] marks = [new(new Rectangle(45, 205, 260, 25), true), new(new Rectangle(45, 265, 230, 25), false)];
        var feedback = new ExplorationScanFeedback(new Rectangle(0, 0, 1920, 1080), list, "Liste wird gelesen – kurz stillhalten.", 1, 2, marks, false);
        using var running = ExplorationScanOverlayForm.CreateImage(feedback, "dungeon", feedback.Message, 4, true, out var area);
        using var stopped = ExplorationScanOverlayForm.CreateImage(feedback, "dungeon", "Scan gestoppt.", 4, false, out var stoppedArea);

        Assert.Equal(area, stoppedArea);
        Assert.True(HasInk(running, area, marks[0].Bounds));
        Assert.True(HasInk(running, area, marks[1].Bounds));
        Assert.False(HasInk(stopped, area, marks[0].Bounds));
        Assert.False(HasInk(stopped, area, marks[1].Bounds));
        var panel = ExplorationScanOverlayForm.PanelBounds(feedback.Window.Size, list);
        Assert.True(stopped.GetPixel(panel.X + 20 - area.X, panel.Y + 20 - area.Y).A > 200);
    }

    [Fact]
    public void VisibleCountRemainsIndependentOfTheScansCumulativeImportedEntries()
    {
        var feedback = new ExplorationScanFeedback(new Rectangle(0, 0, 1280, 720), new Rectangle(0, 86, 282, 619),
            "Liste wird gelesen – kurz stillhalten.", 2, 5, [], false);
        Assert.Equal("2 / 5", ExplorationScanOverlayForm.VisibleCount(feedback));
        Assert.Equal("5 / 5", ExplorationScanOverlayForm.VisibleCount(feedback with { Read = 18 }));
        Assert.Equal("0 / 0", ExplorationScanOverlayForm.VisibleCount(feedback with { Read = 18, Total = 0 }));
        Assert.Equal("Namen werden geprüft", ExplorationScanOverlayForm.PhaseLabel(false, true));
        Assert.Equal("Bereit zum Weiterscrollen", ExplorationScanOverlayForm.PhaseLabel(true, true));
        Assert.Equal("Scan beendet", ExplorationScanOverlayForm.PhaseLabel(true, false));
    }

    [Fact]
    public void OversizedOrMissingListCoordinatesCannotPlaceTheImageOutsideTheGame()
    {
        var window = new Rectangle(-1280, -100, 1280, 720);
        var feedback = new ExplorationScanFeedback(window, new Rectangle(-20, 60, 320, 900), "Bereit.", 0, 0, [], false);
        using var oversized = ExplorationScanOverlayForm.CreateImage(feedback, "all", feedback.Message, 0, true, out var area);
        Assert.True(new Rectangle(Point.Empty, window.Size).Contains(area));
        using var missing = ExplorationScanOverlayForm.CreateImage(feedback with { List = null }, "all", feedback.Message, 0, true, out var missingArea);
        Assert.True(new Rectangle(Point.Empty, window.Size).Contains(missingArea));
    }

    private static bool HasInk(Bitmap image, Rectangle area, Rectangle row)
    {
        row.Inflate(4, 4);
        row.Offset(-area.X, -area.Y);
        row.Intersect(new Rectangle(Point.Empty, image.Size));
        for (var y = row.Top; y < row.Bottom; y++)
            for (var x = row.Left; x < row.Right; x++)
                if (image.GetPixel(x, y).A > 100) return true;
        return false;
    }
}
