using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Overlay scan mode: page status of the live capture (15 cards, a few unreadable or unknown).</summary>
public sealed class PetScanPageStatusTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PageBecomesStableAndListsWhatToClick()
    {
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var scan = new PetScanService(new SettingsService(), progress);
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-live-2560.png");

        await scan.ScanFileAsync(file);
        var first = scan.Page!;
        Assert.Equal(15, first.Cards);
        Assert.False(first.Stable); // first frame: nothing to compare with yet

        await scan.ScanFileAsync(file);
        var second = scan.Page!;
        output.WriteLine($"{second.Read}/{second.Cards} · stabil {second.Stable} · fehlt: {string.Join(", ", second.Missing)}");
        Assert.True(second.Stable);
        Assert.Equal(second.Cards - second.Read, second.Missing.Count);
        Assert.All(second.Missing, m => Assert.Matches(@"^Reihe [1-5] (links|Mitte|rechts) \((Wert unlesbar|Pet unbekannt)\)$", m));
        Assert.Equal(second.Missing.Count == 0, second.Ready);
    }
}
