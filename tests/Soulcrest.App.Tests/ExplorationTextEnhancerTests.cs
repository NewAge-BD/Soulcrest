using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;
using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationTextEnhancerTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "exploration-list", name + ".png");

    [Fact]
    public void EnhancementPreservesImageSizeAndOriginalEvidence()
    {
        using var original = new Bitmap(Fixture("en-top"));
        using var before = new MemoryStream();
        original.Save(before, ImageFormat.Png);
        var checks = ExplorationCompletionSymbol.Find(original, Layout).ToArray();
        using var enhanced = ExplorationTextEnhancer.Create(original);
        Assert.Equal(original.Size, enhanced.Size);
        Assert.Equal(checks, ExplorationCompletionSymbol.Find(original, Layout));
        using var after = new MemoryStream();
        original.Save(after, ImageFormat.Png);
        Assert.Equal(before.ToArray(), after.ToArray());
        var sample = enhanced.GetPixel(80, 349);
        Assert.Equal(sample.R, sample.G);
        Assert.Equal(sample.G, sample.B);
        Assert.Equal(255, sample.A);
    }

    [Theory]
    [InlineData("en-top", 14, false)]
    [InlineData("en-end", 9, true)]
    public async Task EnhancedOriginalDungeonScreenshotsPreserveAllKnownRows(string fixture, int expected, bool end)
    {
        if (!HasEnglishOcr()) return;
        using var original = new Bitmap(Fixture(fixture));
        using var enhanced = ExplorationTextEnhancer.Create(original);
        var reader = WindowsOcrLineReader.Create("en-US", 2);
        var lines = await reader.ReadAsync(enhanced);
        var places = new ExplorationService(new ProgressService()).Places;
        var originalLines = await WindowsOcrLineReader.Create("en-US", 1).ReadAsync(original);
        var page = ExplorationOcrSelection.Read(originalLines, lines, places, original, Layout, "altgard");
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.Equal(expected, page.Ids.Count);
        Assert.Equal(end, page.End);
        // OCR still reports positions in original capture pixels despite its internal enlargement.
        Assert.All(page.Rows!, row => Assert.InRange(row.Line.Y, 320, original.Height));
        Assert.All(page.Rows!, row => Assert.InRange(row.Line.X, 35, 65));
    }

    [Theory]
    [InlineData("en-top", 14, false)]
    [InlineData("en-end", 9, true)]
    public async Task LowContrastCopyRecoversDimmedNamesUsingOriginalChecks(string fixture, int expected, bool end)
    {
        if (!HasEnglishOcr()) return;
        using var source = new Bitmap(Fixture(fixture));
        using var dimmed = DimNeutralListNames(source, .4);
        var places = new ExplorationService(new ProgressService()).Places;
        var reader = WindowsOcrLineReader.Create("en-US", 2);
        var originalLines = await WindowsOcrLineReader.Create("en-US", 1).ReadAsync(dimmed);
        var before = ExplorationList.Read(originalLines, places, dimmed, Layout, "altgard");
        Assert.True(before.Ids.Count < expected, "The synthetic dimming must actually reproduce an OCR failure.");
        using var enhanced = ExplorationTextEnhancer.Create(dimmed);
        var page = ExplorationOcrSelection.Read(originalLines, await reader.ReadAsync(enhanced), places, dimmed, Layout, "altgard");
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.Equal(expected, page.Ids.Count);
        Assert.Equal(end, page.End);
        Assert.Equal(ExplorationCompletionSymbol.Find(source, Layout), ExplorationCompletionSymbol.Find(dimmed, Layout));
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        Assert.Equal(expected, confirmation.Observe(page).Length);
    }

    [Fact]
    public async Task EnhancedNamesCannotInventACompletionWithoutOriginalCheck()
    {
        if (!HasEnglishOcr()) return;
        using var source = new Bitmap(Fixture("en-top"));
        using var dimmed = DimNeutralListNames(source, .4);
        using (var g = Graphics.FromImage(dimmed))
            g.FillRectangle(Brushes.DarkSlateGray, new Rectangle(365, 320, 65, dimmed.Height - 320));
        using var enhanced = ExplorationTextEnhancer.Create(dimmed);
        var reader = WindowsOcrLineReader.Create("en-US", 2);
        var places = new ExplorationService(new ProgressService()).Places;
        var originalLines = await WindowsOcrLineReader.Create("en-US", 1).ReadAsync(dimmed);
        var page = ExplorationOcrSelection.Read(originalLines, await reader.ReadAsync(enhanced), places, dimmed, Layout, "altgard");
        Assert.NotEmpty(page.Rows!);
        Assert.Empty(page.Ids);
        Assert.All(page.Rows!, row => Assert.False(row.Complete));
        Assert.False(page.Full);
    }

    [Fact]
    public async Task FlatEmptyCaptureDoesNotBecomeAListAfterEnhancement()
    {
        if (!HasEnglishOcr()) return;
        using var source = new Bitmap(563, 1410);
        using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(45, 45, 45));
        using var enhanced = ExplorationTextEnhancer.Create(source);
        var lines = await WindowsOcrLineReader.Create("en-US", 2).ReadAsync(enhanced);
        Assert.Empty(lines);
        var page = ExplorationList.Read(lines, [], source, Layout, "altgard");
        Assert.Null(page.Kind);
        Assert.Empty(page.Ids);
    }

    private static bool HasEnglishOcr()
    {
        if (WindowsOcrLineReader.AvailableLanguages().Contains("en-US")) return true;
        Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR en-US fehlt.");
        return false;
    }

    // Synthetic low-contrast variation of the real screenshot, not a new game capture. Only
    // neutral gray list text is attenuated. Gold checks and the category/map title stay original.
    private static Bitmap DimNeutralListNames(Bitmap source, double factor)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result)) g.DrawImageUnscaled(source, 0, 0);
        var bounds = new Rectangle(0, 0, result.Width, result.Height);
        var data = result.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * result.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var y = 320; y < result.Height; y++)
            for (var x = 35; x < 365; x++)
            {
                var offset = y * data.Stride + x * 4;
                var minimum = Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2]));
                var maximum = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
                if (maximum - minimum >= 12 || minimum < 83 || maximum > 160) continue;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)(45 + (pixels[offset + channel] - 45) * factor);
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { result.UnlockBits(data); }
        return result;
    }
}
