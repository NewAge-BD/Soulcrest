using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// Game window capture 2026-10-03 (`en-2026-10-03-row2-live.png`), values by eye: 21, 16 (Kerubar; first noted as 18 by mistake), 16 · 14 (Kailin),
/// 14 (Young Kailin, selected), 12 · 12, 12, 11 · 10, 10, 10 · 10, 9, 9 (all /25).
/// User report: clicking row 2 left asked for row 2 right again and vice versa: the two near-identical
/// Kailin portraits shared one unknown entry. Also read wrong before: 22 for 21 (bar only),
/// 1 for 11 (six agreeing passes against the bar).
/// </summary>
public sealed class PetScanRow2Tests(ITestOutputHelper output)
{
    private static readonly int[] Souls = [21, 16, 16, 14, 14, 12, 12, 12, 11, 10, 10, 10, 10, 9, 9];

    [Fact]
    public async Task EveryVisibleCardIsItsOwnEntryAndNoValueIsWrong()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var scan = new PetScanService(settings, progress);
        await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-row2-live.png"));

        foreach (var e in scan.Entries)
            output.WriteLine($"{e.Key} pet={e.PetId} reading={e.Reading} «{e.LastText}»");
        Assert.Equal(15, scan.Entries.Count);
        var readings = scan.Entries.Where(e => e.Reading is not null).Select(e => e.Reading!.Value).ToList();
        Assert.All(readings, r => Assert.Equal(1, r.Level));
        // Every read value occurs at most as often as on the cards.
        foreach (var group in readings.GroupBy(r => r.InLevel))
            Assert.True(group.Count() <= Souls.Count(s => s == group.Key), $"{group.Key}/25 zu oft gelesen");
        Assert.True(readings.Count >= 10, $"nur {readings.Count} von 15 gelesen");
    }
}
