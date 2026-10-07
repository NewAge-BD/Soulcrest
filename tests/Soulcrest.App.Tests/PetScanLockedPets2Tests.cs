using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// Fourth pet window screenshot (tests/fixtures/pet-window/en-2026-10-03-locked2.png). Rows top to bottom:
///   0/25, 0/25, 0/25 · 0/25, 0/25, 4/5 · 4/5, 4/5, 3/5 (selected, "Cadaver Insectoid") · 3/5, 3/5, 2/5 · 2/5, 2/5, 2/5.
/// User report "das wird nicht richtig erkannt": the ice-crystal card (0/25, cyan portrait, empty bar) was
/// read as MAX, and the dim selection outline (ratio 0.12 instead of ~0.3) was not detected.
/// </summary>
public sealed class PetScanLockedPets2Tests(ITestOutputHelper output)
{
    private static readonly (int Level, int InLevel)[] Expected =
    [
        (1, 0), (1, 0), (1, 0), (1, 0), (1, 0), (0, 4), (0, 4), (0, 4), (0, 3), (0, 3), (0, 3), (0, 2), (0, 2), (0, 2), (0, 2),
    ];

    [Fact]
    public async Task NoFalseMaxAndTheDimlySelectedPetIsConfirmed()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var scan = new PetScanService(settings, progress);
        await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-locked2.png"));

        output.WriteLine($"Panel: {scan.PanelText} · Sammlung: {scan.Collection}");
        foreach (var e in scan.Entries)
            output.WriteLine($"{e.Key} pet={e.PetId} conf={e.Confidence} score={e.MatchScore} reading={e.Reading} «{e.LastText}»");

        var readings = scan.Entries.Where(e => e.Reading is not null).Select(e => e.Reading!.Value).ToList();
        Assert.DoesNotContain(readings, r => r.Level == 3);
        // Since the bar alone no longer sets x/25 without backing in the text, the 0/25 card with empty text
        // stays unread (2026-10-03); every value that is read must be right.
        Assert.True(readings.Count >= 14, $"nur {readings.Count} von 15 gelesen");
        foreach (var group in readings.GroupBy(r => r))
            Assert.True(group.Count() <= Expected.Count(e => e == group.Key), $"{group.Key} zu oft gelesen");

        var insectoid = Assert.Single(scan.Entries, e => e.PetId == "cadaver-insectoid");
        Assert.Equal(ScanConfidence.Confirmed, insectoid.Confidence);
        Assert.Equal((0, 3), insectoid.Reading);

        scan.ApplySelected();
        Assert.Equal("3/5", progress.LevelText("cadaver-insectoid"));
    }
}
