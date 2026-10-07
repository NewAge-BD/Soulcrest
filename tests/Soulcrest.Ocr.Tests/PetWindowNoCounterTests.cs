using System.Drawing;
using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Live capture 2026-10-03 (2560x1440), user report "viele nicht erkannt, aber alle bis auf 1. Reihe links
/// haben keine counter": locked pets without souls show no counter and no bar (= 0/5); only the top-left
/// card shows 1/5. These dark cards made the row detection cut cards short and drop a whole row, and the
/// ring-shaped orb (top left) scored 63 raw feature matches against the spider icon.
/// </summary>
public sealed class PetWindowNoCounterTests
{
    [Fact]
    public async Task LockedCardsWithoutCounterAreZeroOfFive()
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
        var matcherKnows = new HashSet<string>();
        foreach (var pet in PetCatalog.Load(Path.Combine(mapdata, "pets.json")).Pets.Where(p => p.Icon is not null))
        {
            matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));
            matcherKnows.Add(pet.Id);
        }
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4), WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-nocounter-live.png"));

        var scan = await scanner.ScanAsync(image);

        Assert.Equal(15, scan.Cards.Count);
        var rows = scan.Cards.Select(c => c.Bounds.Y).Distinct().Order().ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(scan.Cards, c => Assert.True(c.Bounds.Height >= 190, $"Karte zu kurz: {c.Bounds}"));
        Assert.All(scan.Cards, c => Assert.True(c.Locked));
        foreach (var card in scan.Cards)
        {
            var expected = rows.IndexOf(card.Bounds.Y) == 0 && card.Column == 0 ? 1 : 0;
            Assert.Equal(new PetCardProgress(0, expected, 5, false), card.Progress);
        }
        // The orb is Silver Blade Rotan (portrait on aion2.gaming.tools); the catalog only has it since the
        // Abyss soul sources (2026-10-04). Without it the orb must stay unknown, not become the spider.
        var orb = scan.Cards.Single(c => c.Bounds.Y == rows[0] && c.Column == 0);
        if (matcherKnows.Contains("silver-blade-rotan"))
            Assert.True(orb.Match.IsPlausible && orb.Match.PetId == "silver-blade-rotan", $"Kugel als {orb.Match.PetId} ({orb.Match.Score}) erkannt");
        else
            Assert.False(orb.Match.IsPlausible, $"Kugel als {orb.Match.PetId} ({orb.Match.Score}) erkannt");
        Assert.DoesNotContain(scan.Cards, c => c.Selected);
    }
}
