using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Services;
using Soulcrest.Core.PetWindow;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>German names from the local gaming.tools catalog; status phrases confirmed by the user 2026-10-08.</summary>
public sealed class GermanScanTests
{
    [Theory]
    [InlineData("Bindung abgeschlossen", true)]
    [InlineData("Bindung nicht abgeschlossen", false)]
    public void GermanKibeliskStatusRequiresExactMeaning(string status, bool bound)
    {
        OcrLine[] lines = [new("1. Shulak-Straßenstand", 30, 20, 280, 20), new(status, 30, 50, 300, 20)];
        var row = Assert.Single(KibeliskList.Read(lines, 200).Rows);
        Assert.Equal(bound, row.Bound);
        var places = new[] { new ExplorationPlace("shulak", "altgard", "kibelisk", 0, "Shulak Street Stall", "Shulak-Straßenstand") };
        var session = new KibeliskSession(places);
        session.Observe(new([row], false));
        Assert.Empty(session.Confirmed);
        session.Observe(new([row], false));
        Assert.Contains("shulak", bound ? session.Result().Bound : session.Result().Unbound);
        Assert.Empty(bound ? session.Result().Unbound : session.Result().Bound);
        Assert.Empty(KibeliskList.Read([lines[0], lines[1] with { Text = "Bindung" }], 200).Rows);
    }

    [Theory]
    [InlineData("altgard", "dungeon")]
    [InlineData("altgard", "stronghold")]
    [InlineData("verteron", "dungeon")]
    [InlineData("verteron", "stronghold")]
    public void GermanExplorationNamesUseTheSameIdsAndStopAtUnentdeckt(string map, string kind)
    {
        var places = new ExplorationService(new ProgressService()).Places.Where(p => p.Map == map && p.Kind == kind).DistinctBy(p => p.Id).ToArray();
        Assert.NotEmpty(places);
        Assert.All(places, p => Assert.False(string.IsNullOrWhiteSpace(p.De)));
        foreach (var place in places)
            Assert.Equal(place.Id, ExplorationService.Match(place.De!, places)?.Id);
        var page = ExplorationScanService.ReadNames([
            new(places[0].De!, 20, 10, 400, 20), new("Unentdeckt", 20, 40, 200, 20), new(places[1].De!, 20, 70, 400, 20)], places);
        Assert.True(page.End);
        Assert.Equal([places[0].Id], page.Ids);
    }

    [Theory]
    [InlineData("altgard")]
    [InlineData("verteron")]
    public void GermanKibeliskNamesAndOneLostLetterResolveToTheExistingMarkers(string map)
    {
        var places = new ExplorationService(new ProgressService()).Places.Where(p => p.Map == map && p.Kind == "kibelisk").ToArray();
        Assert.NotEmpty(places);
        foreach (var place in places)
            Assert.Contains(place.Id, KibeliskList.Candidates(new(1, place.De!, false, true), places).Select(p => p.Id));
        var unique = places.First(p => p.De!.Length >= 12 && places.Count(q => q.De == p.De) == 1);
        Assert.Contains(unique.Id, KibeliskList.Candidates(new(1, unique.De![1..], false, true), places).Select(p => p.Id));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("auto")]
    public async Task WindowsOcrReadsBothConfirmedGermanKibeliskStates(string language)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("de-DE"))
        {
            // Without the German language pack (e.g. the GitHub build) skipped, as all OCR tests; required locally.
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR de-DE fehlt.");
            return;
        }
        // Synthetic typography exercises OCR and parser together; this is not a German game screenshot.
        using var bitmap = new Bitmap(1000, 300);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(25, 25, 25));
            using var font = new Font("Segoe UI", 24, GraphicsUnit.Pixel);
            graphics.DrawString("1. Shulak-Straßenstand", font, Brushes.White, 30, 20);
            graphics.DrawString("Bindung abgeschlossen", font, Brushes.White, 30, 54);
            graphics.DrawString("2. Bogengebiet am Basfelt-Wasserfall", font, Brushes.White, 30, 140);
            graphics.DrawString("Bindung nicht abgeschlossen", font, Brushes.White, 30, 174);
        }
        var reader = WindowsOcrLineReader.Create(language, 1);
        var page = KibeliskList.Read(await reader.ReadAsync(bitmap), bitmap.Height);
        Assert.Equal([true, false], page.Rows.Select(r => r.Bound));
        if (reader.Automatic) Assert.Equal("de", reader.DetectedLanguage);
    }

    [Fact]
    public void GermanPanelNameKeepsTheIdAndProgressOfAnEnglishLearnedPet()
    {
        var backup = new[] { AppPaths.LearnedPetsFile, AppPaths.ProgressFile }.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllText(p) : null);
        using var picture = new Bitmap(40, 50);
        using var png = new MemoryStream();
        picture.Save(png, ImageFormat.Png);
        var progress = new ProgressService();
        var pet = progress.LearnPet("English Sample " + Guid.NewGuid().ToString("N"), png.ToArray());
        try
        {
            progress.SetLevels([(pet.Id, 2, 9)]);
            var souls = progress.Souls(pet.Id);
            using var scan = new PetScanService(new SettingsService(), progress);
            var value = new PetCardProgress(2, 9, 75, false);
            var card = new PetCardScan(0, new Rectangle(10, 10, 150, 180), value, "9/75", new(pet.Id, 42, null, 0), true, png.ToArray(), 123);
            var entry = scan.Merge(card);
            var name = "Prüfpet " + Guid.NewGuid().ToString("N");
            var read = new PetWindowScan([card], new(name, 2, "Pet-Kenntnis (9/75)", value, "de"), null, null);
            List<(PetCardScan Card, ScanEntry Entry)> frame = [(card, entry)];
            scan.LearnFromPanel(read, frame, false);
            Assert.Null(progress.Catalog.Find(pet.Id)!.De);
            scan.LearnFromPanel(read, frame, false);
            scan.LearnFromPanel(read, frame, false);
            Assert.Equal(pet.Id, Assert.Single(scan.Entries).PetId);
            var reloaded = new ProgressService();
            Assert.Equal(name, reloaded.Catalog.Find(pet.Id)!.De);
            Assert.Equal(pet.En, reloaded.Catalog.Find(pet.Id)!.En);
            Assert.Equal(souls, reloaded.Souls(pet.Id));
            Assert.Equal(pet.Id, reloaded.FindByName(name)!.Id);
        }
        finally
        {
            File.Delete(Path.Combine(AppPaths.LearnedPortraitsDirectory, pet.Id + ".png"));
            foreach (var (path, content) in backup)
                if (content is null) File.Delete(path); else File.WriteAllText(path, content);
        }
    }
}
