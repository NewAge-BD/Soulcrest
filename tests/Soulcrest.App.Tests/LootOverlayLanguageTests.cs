using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootOverlayLanguageTests
{
    private static readonly MonsterName Monster = new("Drana Mutant", "Dranamutant");
    private static readonly PetDefinition Pet = new() { Id = "drana-mutant-brute", En = "Drana Mutant Brute", De = "Dranamutant-Rohling", Genus = "Fera" };

    [Theory]
    [InlineData("en", "de", "auto", "Dranamutant")]
    [InlineData("de", "en", "auto", "Drana Mutant")]
    [InlineData("en", "de", "en", "Drana Mutant")]
    [InlineData("de", "en", "de", "Dranamutant")]
    public void FocusAndLootRowsUseTheGameOrExplicitOverlayLanguage(string ui, string game, string overlay, string expected)
    {
        var settings = new AppSettings { UiLanguage = ui, NameLanguage = ui, DetectedGameLanguage = game, OverlayLanguage = overlay };
        Assert.Equal(expected, PetOverlayForm.RowName(settings, Monster, Pet, Pet.Id));
        settings.UiLanguage = settings.NameLanguage = ui == "de" ? "en" : "de";
        Assert.Equal(expected, PetOverlayForm.RowName(settings, Monster, Pet, Pet.Id));
    }

    [Fact]
    public void ManualGameLanguageTakesPrecedenceOverAnOlderDetection()
    {
        var settings = new AppSettings { AutoOcrLanguage = false, OcrLanguage = "de-DE", DetectedGameLanguage = "en", NameLanguage = "en" };
        Assert.Equal("Dranamutant", PetOverlayForm.RowName(settings, Monster, Pet, Pet.Id));
        settings.OverlayLanguage = "en";
        Assert.Equal("Drana Mutant", PetOverlayForm.RowName(settings, Monster, Pet, Pet.Id));
    }

    [Fact]
    public void UnrecognizedLanguageAndMissingNamesHaveSafeFallbacks()
    {
        var settings = new AppSettings { NameLanguage = "de" };
        Assert.Equal("Dranamutant", PetOverlayForm.RowName(settings, Monster, Pet, Pet.Id));
        Assert.Equal(Pet.De, PetOverlayForm.RowName(settings, null, Pet, Pet.Id));
        Assert.Equal(Monster.En, PetOverlayForm.RowName(settings, Monster with { De = null }, Pet, Pet.Id));
        Assert.Equal(Pet.En, PetOverlayForm.RowName(settings, null, Pet with { De = null }, Pet.Id));
        Assert.Equal(Pet.Id, PetOverlayForm.RowName(settings, null, null, Pet.Id));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("de")]
    [InlineData("en")]
    public void DetectionAndOverlaySelectionSurviveRestartAndIgnoreUncertainFrames(string overlay)
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        try
        {
            var settings = new SettingsService();
            settings.Update(s => { s.UiLanguage = s.NameLanguage = "en"; s.AutoOcrLanguage = true;
                s.OverlayLanguage = overlay; s.DetectedGameLanguage = null; });
            var changes = 0;
            settings.Changed += () => changes++;
            settings.RememberGameLanguage("de");
            settings.RememberGameLanguage(null);
            settings.RememberGameLanguage("auto");
            settings.RememberGameLanguage("de");
            Assert.Equal(1, changes);
            var reloaded = new SettingsService().Current;
            Assert.Equal("de", reloaded.DetectedGameLanguage);
            Assert.Equal(overlay, reloaded.OverlayLanguage);
            Assert.Equal("en", reloaded.UiLanguage);
            Assert.Equal("en", reloaded.NameLanguage);
            Assert.Equal(overlay == "en" ? Monster.En : Monster.De, PetOverlayForm.RowName(reloaded, Monster, Pet, Pet.Id));
            settings.RememberGameLanguage("en");
            Assert.Equal(2, changes);
            Assert.Equal("en", new SettingsService().Current.DetectedGameLanguage);
        }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, backup);
            _ = new SettingsService();
        }
    }

    [Fact]
    public async Task PetScanFeedsTheDetectedLanguageWithoutChangingOverlayOrInterfacePreferences()
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        try
        {
            var settings = new SettingsService();
            settings.Update(s => { s.UiLanguage = s.NameLanguage = "de"; s.AutoOcrLanguage = true;
                s.OverlayLanguage = "de"; s.DetectedGameLanguage = null; });
            using var scan = new PetScanService(settings, new ProgressService());
            await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
            Assert.Equal("en", settings.Current.DetectedGameLanguage);
            Assert.Equal("en", new SettingsService().Current.DetectedGameLanguage);
            Assert.Equal("de", settings.Current.OverlayLanguage);
            Assert.Equal("de", settings.Current.UiLanguage);
            Assert.Equal("Dranamutant", PetOverlayForm.RowName(settings.Current, Monster, Pet, Pet.Id));
        }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, backup);
            _ = new SettingsService();
        }
    }
}
