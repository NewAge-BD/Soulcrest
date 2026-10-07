using System.Drawing;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>Diagnostic dump of the pet window scanner for every fixture image (cards, texts, matches, panel).</summary>
public sealed class PetWindowFileExploration(ITestOutputHelper output)
{
    [Fact]
    public async Task DumpAllPetWindowFixtures()
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
        var catalog = PetCatalog.Load(Path.Combine(mapdata, "pets.json"));
        using var matcher = new PortraitMatcher();
        foreach (var pet in catalog.Pets.Where(p => p.Icon is not null))
            matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4), WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window"), "*.png").Order())
        {
            using var image = new Bitmap(file);
            var scan = await scanner.ScanAsync(image);
            output.WriteLine($"== {Path.GetFileName(file)} {image.Width}x{image.Height}: {scan.Cards.Count} Karten, Panel {scan.Panel?.Name}/{scan.Panel?.Level} «{scan.Panel?.RawText}», Sammlung {scan.Collection}, Problem {scan.Problem}");
            foreach (var c in scan.Cards.OrderBy(c => c.Bounds.Y / 40).ThenBy(c => c.Column))
                output.WriteLine($"  c{c.Column} {c.Bounds} sel={c.Selected} locked={c.Locked} bar={c.BarFill:0.00} src={c.Source} «{c.ProgressText}» -> {c.Progress} | {c.Match.PetId}({c.Match.Score}) 2nd {c.Match.SecondId}({c.Match.SecondScore})");
        }
    }
}
