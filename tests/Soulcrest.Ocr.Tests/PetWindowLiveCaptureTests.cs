using System.Drawing;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Live capture as Soulcrest takes it (GDI, 2560x1440, 2026-10-03): 15 level-1 cards, values read off
/// by eye: 8,8,7 · 6,6,6 · 6,5,5 · 5 (Tog, selected),5,5 · 5,4,4 (all /25). Panel: "Lv. 1 (5/25)".
/// The text sits lower on the card than in the 2000px screenshots, so fixed bands failed here (3/14).
/// Rule: no wrong value, ever; unreadable cards are shown in the overlay for clicking.
/// </summary>
public sealed class PetWindowLiveCaptureTests(ITestOutputHelper output)
{
    private static readonly int[,] Souls = { { 8, 8, 7 }, { 6, 6, 6 }, { 6, 5, 5 }, { 5, 5, 5 }, { 5, 4, 4 } };

    [Fact]
    public async Task NoWrongValueAndMostCardsRead()
    {
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "pets.json")))
                mapdata = candidate;
        }
        if (mapdata is null || !WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var matcher = new PortraitMatcher();
        foreach (var pet in PetCatalog.Load(Path.Combine(mapdata, "pets.json")).Pets.Where(p => p.Icon is not null))
            matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4), WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-live-2560.png"));

        var scan = await scanner.ScanAsync(image);

        Assert.Equal(15, scan.Cards.Count);
        var rows = scan.Cards.Select(c => c.Bounds.Y).Distinct().Order().ToList();
        Assert.Equal(5, rows.Count);
        var read = 0;
        foreach (var card in scan.Cards)
        {
            var row = rows.IndexOf(card.Bounds.Y);
            var expected = Souls[row, card.Column];
            output.WriteLine($"r{row}c{card.Column} erwartet {expected}/25, gelesen {card.Progress} «{card.ProgressText}»");
            if (card.Progress is null)
                continue;
            Assert.Equal(new Soulcrest.Core.PetWindow.PetCardProgress(1, expected, 25, false), card.Progress);
            read++;
        }
        Assert.True(read >= 14, $"nur {read} von 15 gelesen");
        var selected = Assert.Single(scan.Cards, c => c.Selected);
        Assert.Equal((3, 0), (rows.IndexOf(selected.Bounds.Y), selected.Column));
        Assert.Equal("Tog", scan.Panel?.Name);
        Assert.Equal(new Soulcrest.Core.PetWindow.PetCardProgress(1, 5, 25, false), scan.Panel?.Progress);
    }
}
