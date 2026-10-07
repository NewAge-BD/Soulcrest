using System.Drawing;
using OpenCvSharp;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Diagnose: scans a saved game window picture (SOULCREST_PETSCAN_IMAGE) like the live scan does, with the
/// map-data portraits and the learned ones, and prints every card.
/// </summary>
public sealed class PetWindowSnapshotExploration(ITestOutputHelper output)
{
    [Fact]
    public async Task ScanSnapshot()
    {
        var path = Environment.GetEnvironmentVariable("SOULCREST_PETSCAN_IMAGE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var matcher = new PortraitMatcher();
        var mapdata = "";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            mapdata = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(mapdata, "pets.json")))
                break;
        }
        if (File.Exists(Path.Combine(mapdata, "pets.json")))
        {
            foreach (var pet in PetCatalog.Load(Path.Combine(mapdata, "pets.json")).Pets.Where(p => p.Icon is not null))
                matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));
        }
        var learned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest", "learned", "portraits");
        if (Directory.Exists(learned))
        {
            foreach (var file in Directory.GetFiles(learned, "*.png"))
                matcher.AddReference(Path.GetFileNameWithoutExtension(file), file);
        }
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", scale: 4), WindowsOcrLineReader.Create("en-US"), matcher,
            WindowsOcrLineReader.Create("en-US", scale: 6));

        // Like the live scan: the middle of the window stays black.
        using var picture = Cv2.ImRead(path);
        var from = (int)(picture.Width * PetWindowScanner.MiddleFrom);
        var to = (int)(picture.Width * PetWindowScanner.MiddleTo);
        picture[new Rect(from, 0, to - from, picture.Height)].SetTo(Scalar.All(0));
        var blacked = Path.Combine(Path.GetTempPath(), "soulcrest-petscan-snapshot.png");
        Cv2.ImWrite(blacked, picture);
        using var image = new Bitmap(blacked);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(image);
        output.WriteLine($"Scan: {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        await scanner.ScanAsync(image);
        output.WriteLine($"Zweiter Scan: {watch.ElapsedMilliseconds} ms");
        using (var bgr = Cv2.ImRead(path))
        {
            watch.Restart();
            foreach (var card in scan.Cards)
            {
                using var portrait = new Mat(bgr, new Rect(card.Bounds.X, card.Bounds.Y, card.Bounds.Width, card.Bounds.Height * 78 / 100));
                matcher.Match(portrait);
            }
            output.WriteLine($"Porträtvergleich allein: {watch.ElapsedMilliseconds} ms für {scan.Cards.Count} Karten, {matcher.ReferenceCount} Referenzen");
        }

        var rows = scan.Cards.Select(c => c.Bounds.Y).Distinct().Order().ToList();
        foreach (var card in scan.Cards.OrderBy(c => c.Bounds.Y).ThenBy(c => c.Column))
        {
            output.WriteLine($"r{rows.IndexOf(card.Bounds.Y) + 1}c{card.Column + 1} @{card.Bounds} sel={card.Selected} «{card.ProgressText}» -> {card.Progress} "
                + $"bar={card.BarFill:F2} src={card.Source} hash={card.PortraitHash:x16} | {card.Match}");
        }
        output.WriteLine($"Panel: {scan.Panel} · Problem: {scan.Problem}");
    }
}
