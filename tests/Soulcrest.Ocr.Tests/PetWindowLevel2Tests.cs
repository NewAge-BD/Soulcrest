using System.Drawing;
using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Game window capture 2026-10-03 (WGC, 2560×1440): level-2 cards, values by eye: MAX, 42, 20 · 17, 15, 14 ·
/// 13, 9, 7 · 3, 2, 2 · 2, 1, 1 (all /75). User report: "Reihe 3 Mitte wird oft nicht richtig erkannt".
/// Small x/75 values have no visible bar (it lies under the level badge), so the bar cannot confirm them;
/// "1/73" had been taken as 1/75 for 2/75. Rule: never a wrong value; unreadable cards are clicked.
/// </summary>
public sealed class PetWindowLevel2Tests(ITestOutputHelper output)
{
    private static readonly int?[,] Souls = { { null, 42, 20 }, { 17, 15, 14 }, { 13, 9, 7 }, { 3, 2, 2 }, { 2, 1, 1 } };

    [Fact]
    public async Task NoWrongValueOnSmallLevelTwoCards()
    {
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "pets.json")))
                mapdata = candidate;
        }
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-level2-live.png");
        if (mapdata is null || !File.Exists(file) || !WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var matcher = new PortraitMatcher();
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4), WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var image = new Bitmap(file);

        var scan = await scanner.ScanAsync(image);

        Assert.Equal(15, scan.Cards.Count);
        var rows = scan.Cards.Select(c => c.Bounds.Y).Distinct().Order().ToList();
        var read = 0;
        foreach (var card in scan.Cards)
        {
            var expected = Souls[rows.IndexOf(card.Bounds.Y), card.Column];
            output.WriteLine($"r{rows.IndexOf(card.Bounds.Y)}c{card.Column} erwartet {(expected is null ? "MAX" : $"{expected}/75")}, gelesen {card.Progress} «{card.ProgressText}»");
            if (card.Progress is null)
                continue;
            Assert.Equal(expected is null ? new PetCardProgress(3, 0, 0, true) : new PetCardProgress(2, expected.Value, 75, false), card.Progress);
            read++;
        }
        // Native colour preprocessing adds Dratona 9/75 and Tayga 2/75; Rafflesia 3/75 remains ambiguous.
        Assert.True(read >= 14, $"nur {read} von 15 gelesen");
    }
}
