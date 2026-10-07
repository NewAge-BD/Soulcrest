using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// Second pet window screenshot (tests/fixtures/pet-window/en-2026-10-03-locked.png): scrolled grid with
/// level-1 cards (0/25) and greyed-out locked pets. Complete rows (top to bottom, left to right):
///   0/25, 0/25, 4/5 · 4/5 (selected, "Predator Saraswati"), 4/5, 3/5 · 3/5, 3/5, 2/5 · 2/5, 2/5, 2/5.
/// User report: clicking this pet did not take over its data (the selected card's text was unreadable).
/// </summary>
public sealed class PetScanLockedPetsTests(ITestOutputHelper output)
{
    private static readonly (int Level, int InLevel)[] Expected =
        [(1, 0), (1, 0), (0, 4), (0, 4), (0, 4), (0, 3), (0, 3), (0, 3), (0, 2), (0, 2), (0, 2), (0, 2)];

    [Fact]
    public async Task EveryCardIsReadIncludingTheSelectedLockedPet()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var scan = new PetScanService(settings, progress);
        await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-locked.png"));

        output.WriteLine($"Panel: {scan.PanelText} · Sammlung: {scan.Collection}");
        foreach (var e in scan.Entries)
            output.WriteLine($"{e.Key} pet={e.PetId} conf={e.Confidence} score={e.MatchScore} reading={e.Reading} «{e.LastText}»");

        Assert.Equal(12, scan.Entries.Count);
        Assert.All(scan.Entries, e => Assert.NotNull(e.Reading));
        var readings = scan.Entries.Select(e => e.Reading!.Value).OrderBy(r => r.Level).ThenByDescending(r => r.InLevel).ToList();
        Assert.Equal(Expected.OrderBy(r => r.Level).ThenByDescending(r => r.InLevel), readings);

        var saraswati = Assert.Single(scan.Entries, e => e.PetId == "predator-saraswati");
        Assert.Equal(ScanConfidence.Confirmed, saraswati.Confidence);
        Assert.Equal((0, 4), saraswati.Reading);
        Assert.True(saraswati.Apply);

        scan.ApplySelected();
        Assert.Equal("4/5", progress.LevelText("predator-saraswati"));
    }
}
