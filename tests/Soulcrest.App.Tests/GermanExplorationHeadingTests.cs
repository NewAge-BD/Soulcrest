using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GermanExplorationHeadingTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private static readonly ExplorationPlace[] Places =
    [
        new("vein", "altgard", "dungeon", 0, "Hidden Vein Cave Entrance", "Eingang zur Versteckten Erzaderhöhle"),
        new("den", "altgard", "stronghold", 0, "Graverobber Den", null),
        new("other-map", "verteron", "dungeon", 0, "Hidden Vein Cave Entrance", null),
    ];
    private static OcrLine[] Lines(string heading = "Versiegelter Dunge...", string percentage = "31 %", string map = "Karte: Altgard") =>
        [new(map, 85, 30, 270, 40), new(heading, 120, 220, 310, 30), new(percentage, 365, 270, 65, 25)];
    private static OcrLine Name(double y = 340) => new(Places[0].De!, 40, y, 300, 25);
    private static Bitmap Capture()
    {
        var bitmap = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(65, 65, 65));
        return bitmap;
    }

    [Fact]
    public void ReportedTruncatedHeadingReadsGermanRowsAndTheirEndWithoutInventingChecks()
    {
        using var bitmap = Capture();
        var lines = Lines().Concat([Name(), new OcrLine("Unentdeckt", 40, 470, 180, 25), Name(510)]).ToArray();

        var page = ExplorationList.Read(lines, Places, bitmap, Layout, "altgard");

        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.True(page.End);
        var row = Assert.Single(page.Rows!);
        Assert.Equal("vein", row.Id);
        Assert.False(row.Complete);
        Assert.Empty(page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Theory]
    [InlineData("Versiegelter Dunge...")]
    [InlineData("Versiegelter Dunge…")]
    public void ReportedTruncatedHeadingKeepsTheTwoFrameHundredPercentRule(string heading)
    {
        using var bitmap = Capture();
        var page = ExplorationList.Read(Lines(heading, "100 %"), Places, bitmap, Layout, "altgard");
        Assert.True(page.Full);
        Assert.Equal("dungeon", page.Kind);
        Assert.Equal(["vein"], page.Ids);
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        Assert.False(confirmation.Finished);
        Assert.Equal(["vein"], confirmation.Observe(page));
        Assert.True(confirmation.FullConfirmed);
        Assert.True(confirmation.Finished);
    }

    [Fact]
    public void TruncatedHeadingStillNeedsTheMatchingMapAndItsOwnHeaderPosition()
    {
        using var bitmap = Capture();
        foreach (var lines in new[]
        {
            Lines(percentage: "100 %", map: "Karte: Verteron"),
            Lines(percentage: "100 %").Skip(1).ToArray(),
            Lines(percentage: "100 %").Select((line, index) => index == 1 ? line with { Y = 30 } : line).ToArray(),
            Lines(percentage: "100 %").Select((line, index) => index == 1 ? line with { Y = 400 } : line).ToArray(),
            Lines(percentage: "100 %").Select((line, index) => index == 1 ? line with { X = 600 } : line).ToArray(),
            Lines(percentage: "100 %").Append(new OcrLine("Garnison", 100, 220, 200, 30)).ToArray(),
        })
        {
            var page = ExplorationList.Read(lines, Places, bitmap, Layout, "altgard");
            Assert.Null(page.Kind);
            Assert.False(page.Full);
            Assert.Empty(page.Ids);
        }
        var misplacedPercentage = Lines(percentage: "100 %")
            .Select((line, index) => index == 2 ? line with { Y = 900 } : line).ToArray();
        Assert.False(ExplorationList.Read(misplacedPercentage, Places, bitmap, Layout, "altgard").Full);
    }

    [Fact]
    public void EnhancedTruncatedHeadingCanReadRowsButCannotSupplyHundredPercentCompletion()
    {
        using var bitmap = Capture();
        var original = Lines().Where(line => line.Text != "Versiegelter Dunge...").ToArray();
        var enhanced = Lines(percentage: "100 %").Append(Name()).ToArray();

        var page = ExplorationOcrSelection.Read(original, enhanced, Places, bitmap, Layout, "altgard");

        Assert.Equal("dungeon", page.Kind);
        Assert.Equal("vein", Assert.Single(page.Rows!).Id);
        Assert.False(page.Full);
        Assert.Empty(page.Ids);
        var wrongMap = enhanced.Select((line, index) => index == 0 ? line with { Text = "Karte: Verteron" } : line).ToArray();
        Assert.Null(ExplorationOcrSelection.Read(original, wrongMap, Places, bitmap, Layout, "altgard").Kind);
    }

    [Fact]
    public void ATruncatedHeadingBesideItsCircularIconStillRequiresIndividualWordCoordinates()
    {
        using var bitmap = Capture();
        var original = Lines().Where(line => line.Text != "Versiegelter Dunge...").ToArray();
        var heading = new OcrLine("(O) Versiegelter Dunge...", 57, 210, 375, 39)
        {
            Words = [new("(O)", 57, 210, 40, 39), new("Versiegelter", 124, 220, 170, 26), new("Dunge...", 300, 220, 132, 26)]
        };
        var enhanced = original.Concat([heading, Name()]).ToArray();
        var page = ExplorationOcrSelection.Read(original, enhanced, Places, bitmap, Layout, "altgard");
        Assert.Equal("dungeon", page.Kind);
        Assert.Equal("vein", Assert.Single(page.Rows!).Id);
        Assert.Empty(page.Ids);

        var withoutCoordinates = enhanced.Select(line => line == heading ? line with { Words = [] } : line).ToArray();
        Assert.Null(ExplorationOcrSelection.Read(original, withoutCoordinates, Places, bitmap, Layout, "altgard").Kind);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("auto")]
    public async Task WindowsOcrReadsTheReportedTruncatedGermanTab(string language)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("de-DE"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR de-DE fehlt.");
            return;
        }
        // Synthetic typography from the user's exact text, not a new German game screenshot.
        using var bitmap = Capture();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            using var font = new Font("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
            using var title = new Font("Segoe UI", 37, GraphicsUnit.Pixel);
            graphics.DrawString("Karte: Altgard", title, Brushes.White, 85, 30);
            graphics.DrawString("Versiegelter Dunge...", font, Brushes.White, 120, 220);
            graphics.DrawString("31 %", font, Brushes.White, 365, 270);
        }
        var reader = WindowsOcrLineReader.Create(language, 1);
        var lines = await reader.ReadAsync(bitmap);
        var page = ExplorationList.Read(lines, Places, bitmap, Layout, "altgard");
        var diagnostics = $"Requested={language}; Engine={reader.LanguageTag}; Detected={reader.DetectedLanguage ?? "<null>"}; " +
            $"Bitmap={bitmap.Width}x{bitmap.Height}; Bounds={Layout.Bounds}; Capture={Layout.CaptureBounds}; " +
            $"ShowsMap={Layout.ShowsMap(lines, "altgard")}; PageKind={page.Kind ?? "<null>"}\n" +
            string.Join("\n", lines.Select(line =>
                $"LINE [{line.X:F2},{line.Y:F2},{line.Width:F2},{line.Height:F2}] \"{line.Text}\" " +
                $"Kind={ExplorationLayout.HeadingKind(line.Text) ?? "<null>"}; " +
                $"LanguageHint={ExplorationHeading.ReadLanguageHint(line.Text) ?? "<null>"}\n" +
                string.Join("\n", line.Words.Select(word =>
                    $" WORD [{word.X:F2},{word.Y:F2},{word.Width:F2},{word.Height:F2}] \"{word.Text}\""))));
        Assert.True(page.Kind == "dungeon", diagnostics);
        Assert.False(page.Full);
        if (reader.Automatic) Assert.Equal("de", reader.DetectedLanguage);
    }
}
