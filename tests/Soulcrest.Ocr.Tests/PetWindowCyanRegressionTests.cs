using System.Drawing;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class PetWindowCyanRegressionTests
{
    [Fact]
    public async Task CyanPortraitAreaCannotTurnALevelTwoCardIntoMax()
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR en-US fehlt.");
            return;
        }
        using var matcher = new PortraitMatcher();
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4),
            WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-level2-live.png"));
        var before = await scanner.ScanAsync(image);
        var small = before.Cards.Where(c => c.Progress is { IsMax: false, SoulsInLevel: <= 7 }).ToList();
        Assert.True(small.Count >= 3);
        using (var graphics = Graphics.FromImage(image))
        using (var cyan = new SolidBrush(Color.FromArgb(0, 220, 255)))
            foreach (var card in small)
            {
                // A turquoise portrait highlight in the old MAX detection region, beside the fraction.
                var r = card.Bounds;
                graphics.FillRectangle(cyan, r.X + r.Width * .36f, r.Y + r.Height * .87f,
                    r.Width * .22f, r.Height * .10f);
            }
        var after = await scanner.ScanAsync(image);
        Assert.Equal(before.Cards.Count, after.Cards.Count);
        foreach (var card in small)
        {
            var read = Assert.Single(after.Cards, c => c.Bounds == card.Bounds);
            Assert.False(read.Progress?.IsMax ?? false);
            if (read.Progress is not null)
                Assert.Equal(card.Progress, read.Progress);
            Assert.NotEqual(card.Evidence, read.Evidence);
        }
    }
}
