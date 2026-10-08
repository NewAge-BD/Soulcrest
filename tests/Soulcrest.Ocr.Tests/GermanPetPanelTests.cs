using System.Drawing;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class GermanPetPanelTests
{
    [Fact]
    public void LockedGermanPanelHasPreviewLevelButDoesNotInventOwnedProgress()
    {
        OcrLine[] lines = [new("Rotflamme Ignus", 10, 100, 220, 28),
            new("Pet-Kenntnis", 10, 180, 150, 20), new("St. 1", 300, 180, 100, 20)];
        var panel = PetWindowScanner.ParsePanel(lines, 1440);
        Assert.Equal(1, panel?.Level);
        Assert.Null(panel?.Progress);
    }

    [Theory]
    [InlineData("Pet-Kenntnis")]
    [InlineData("Pet Kenntnis")]
    [InlineData("PetKenntnis")]
    public void GermanCaptionFindsNameAndCounterFromSeparateOcrLines(string caption)
    {
        OcrLine[] lines = [new("Arbeiter (Duduka)", 10, 100, 220, 28), new(caption, 10, 180, 150, 20), new("(15/75)", 300, 180, 100, 20)];
        var panel = PetWindowScanner.ParsePanel(lines, 1440);
        Assert.NotNull(panel);
        Assert.Equal("Arbeiter (Duduka)", panel.Name);
        Assert.Equal("de", panel.Language);
        Assert.Equal(2, panel.Level);
        Assert.Equal(15, panel.Progress?.SoulsInLevel);
    }

    [Fact]
    public void MissingCaptionAndCollectionFooterCannotBecomeAPetPanel()
    {
        OcrLine[] lines = [new("Arbeiter (Duduka)", 10, 100, 220, 28), new("(15/75)", 300, 180, 100, 20)];
        Assert.Null(PetWindowScanner.ParsePanel(lines, 1440));
        Assert.Null(PetWindowScanner.ParsePanel([.. lines, new("(3/25)", 300, 240, 100, 20)], 1440));
        Assert.False(PetWindowScanner.IsNameLine("Sammlungsfortschritt 174/200", 100, 22, 180, 18, 1440));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("auto")]
    public async Task WindowsOcrReadsConfirmedGermanPetCaptionAndProgress(string language)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("de-DE"))
        {
            // Without the German language pack (e.g. the GitHub build) skipped, as all OCR tests; required locally.
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR de-DE fehlt.");
            return;
        }
        // Deliberately synthetic, with user-confirmed caption and a sourced catalog name.
        using var bitmap = new Bitmap(900, 240);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(25, 25, 25));
            using var name = new Font("Segoe UI", 30, GraphicsUnit.Pixel);
            using var details = new Font("Segoe UI", 24, GraphicsUnit.Pixel);
            graphics.DrawString("Arbeiter (Duduka)", name, Brushes.White, 30, 40);
            graphics.DrawString("Pet-Kenntnis", details, Brushes.White, 30, 130);
            graphics.DrawString("(15/75)", details, Brushes.White, 450, 130);
        }
        var reader = WindowsOcrLineReader.Create(language, 1);
        var lines = await reader.ReadAsync(bitmap);
        var panel = PetWindowScanner.ParsePanel(lines, 1440);
        Assert.NotNull(panel);
        Assert.Equal("Arbeiter (Duduka)", panel.Name);
        Assert.Equal("de", panel.Language);
        Assert.Equal(15, panel.Progress?.SoulsInLevel);
        if (reader.Automatic) Assert.Equal("de", reader.DetectedLanguage);
    }
}
