using System.Drawing;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Pet window fixture (tests/fixtures/pet-window/en-2026-10-03-all.png, 2000x1125, English client):
/// 15 complete cards in 3 columns, rows 1-4 MAX, row 5 = 42/75, 20/75, 17/75; selected card = Swarm (Lv. 3);
/// Collection Status 94/200. Portraits verified visually; the blue bird (row 4, column 3) matches the public Pagati portrait (pet catalog added 2026-10-04).
/// </summary>
public sealed class PetWindowScannerTests(ITestOutputHelper output)
{
    private static readonly string?[] ExpectedPets =
    [
        "swarm", "mumu-worker", "slink",
        "withered-branch-spider", "unstable-spider", "aberrant-bee",
        "young-slink", "large-leaf-gravi", "superior-fire-spirit",
        "agrint", "faded-mutant", "pagati",
        "magic-gravi", "lesser-fire-spirit", "skyray",
    ];

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    [Fact]
    public async Task ReadsGridProgressPanelAndIdentifiesPortraits()
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR en-US fehlt.");
            return;
        }
        var mapdata = FindMapData();
        if (mapdata is null)
        {
            output.WriteLine("Übersprungen: Kartendatenpaket (imports/generated/mapdata) fehlt.");
            return;
        }
        var catalog = PetCatalog.Load(Path.Combine(mapdata, "pets.json"));
        using var matcher = new PortraitMatcher();
        foreach (var pet in catalog.Pets.Where(p => p.Icon is not null))
            matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));

        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", scale: 4), WindowsOcrLineReader.Create("en-US"), matcher,
            WindowsOcrLineReader.Create("en-US", scale: 6));
        using var image = new Bitmap(Fixture("pet-window", "en-2026-10-03-all.png"));
        var scan = await scanner.ScanAsync(image);

        foreach (var card in scan.Cards)
            output.WriteLine($"col {card.Column} @{card.Bounds} sel={card.Selected} «{card.ProgressText}» -> {card.Progress} | {card.Match}");
        output.WriteLine($"Panel: {scan.Panel} · Sammlung: {scan.Collection} · Problem: {scan.Problem}");

        Assert.Null(scan.Problem);
        Assert.Equal(15, scan.Cards.Count);
        var ordered = scan.Cards.OrderBy(c => c.Bounds.Y / 50).ThenBy(c => c.Column).ToList();
        for (var i = 0; i < 15; i++)
        {
            var card = ordered[i];
            var expectedLevel = i < 12 ? 3 : 2;
            Assert.Equal(expectedLevel, card.Progress?.Level);
            if (ExpectedPets[i] is { } pet)
                Assert.True(card.Match.PetId == pet && card.Match.IsPlausible, $"Karte {i}: erwartet {pet}, erkannt {card.Match}");
            else
                Assert.False(card.Match.IsConfident, $"Karte {i}: unbekanntes Pet sicher zugeordnet: {card.Match}");
        }
        Assert.Equal([42, 20, 17], ordered.Skip(12).Select(c => c.Progress!.SoulsInLevel));
        Assert.True(ordered[0].Selected);
        Assert.Single(scan.Cards, c => c.Selected);
        Assert.Equal("Swarm", scan.Panel?.Name);
        Assert.Equal(3, scan.Panel?.Level);
        Assert.Equal((94, 200), scan.Collection);
    }

    private static string? FindMapData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "pets.json")))
                return candidate;
        }
        return null;
    }
}
