using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ProgressionPetGeneraSettingsTests : IDisposable
{
    private readonly byte[]? _backup;
    private readonly string _language;
    private static readonly string[] All = ["cogni", "natura", "varian", "fera"];

    public ProgressionPetGeneraSettingsTests()
    {
        // TestDataFolder redirects the assembly before AppPaths initializes. Check it before any file access.
        var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Assert.False(string.IsNullOrWhiteSpace(redirected));
        Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        var realData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.GetFullPath(realData), Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase));
        _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        _language = UiText.Language;
    }

    [Fact]
    public void FreshInstallationsDefaultToAllFourGeneraWithoutCreatingASettingsFile()
    {
        var first = new AppSettings();
        var second = new AppSettings();
        Assert.Equal(All, first.ProgressionPetGenera);
        Assert.NotSame(first.ProgressionPetGenera, second.ProgressionPetGenera);
        first.ProgressionPetGenera[0] = "unknown";
        Assert.Equal(All, second.ProgressionPetGenera);
        File.Delete(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(All, settings.Current.ProgressionPetGenera);
        Assert.Equal(5, settings.Current.SettingsRevision);
        Assert.False(File.Exists(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SettingsRevision\":5}")]
    [InlineData("{\"SettingsRevision\":5,\"ProgressionPetGenera\":null}")]
    public void MissingOrNullSelectionDefaultsToAllFourWithoutRewritingOldSettings(string json)
    {
        Write(json);
        var original = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(All, settings.Current.ProgressionPetGenera);
        Assert.Equal(5, settings.Current.SettingsRevision);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SettingsFile));
        settings.Update(_ => { });
        Assert.Equal(All, new SettingsService().Current.ProgressionPetGenera);
    }

    [Fact]
    public void ExplicitlyEmptySelectionStaysEmptyAcrossSaveAndReload()
    {
        Write("{\"SettingsRevision\":5,\"ProgressionPetGenera\":[]}");
        var settings = new SettingsService();
        Assert.Empty(settings.Current.ProgressionPetGenera);
        settings.Update(_ => { });
        Assert.Empty(new SettingsService().Current.ProgressionPetGenera);
        Assert.All(All, genus => Assert.False(settings.Current.ProgressionIncludesGenus(genus)));
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Empty(saved.RootElement.GetProperty(nameof(AppSettings.ProgressionPetGenera)).EnumerateArray());
    }

    [Theory]
    [InlineData("cogni", "Cogni")]
    [InlineData("natura", "Natura")]
    [InlineData("varian", "Varian")]
    [InlineData("fera", "Fera")]
    public void EachIndividualGenusSurvivesRestartAndMatchesCatalogLabels(string selected, string catalogLabel)
    {
        Write("{}");
        var settings = new SettingsService();
        settings.Update(s => s.ProgressionPetGenera = [selected]);
        var reloaded = new SettingsService().Current;
        Assert.Equal(new[] { selected }, reloaded.ProgressionPetGenera);
        Assert.True(reloaded.ProgressionIncludesGenus(catalogLabel));
        Assert.True(reloaded.ProgressionIncludesGenus(selected));
        foreach (var other in All.Where(genus => genus != selected))
            Assert.False(reloaded.ProgressionIncludesGenus(other));
    }

    [Theory]
    [InlineData("Special")]
    [InlineData("special")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void SpecialOrUnknownGeneraAreNeverEligibleEvenIfPresentInAnUnnormalizedSelection(string? genus)
    {
        var defaults = new AppSettings();
        Assert.False(defaults.ProgressionIncludesGenus(genus!));
        defaults.ProgressionPetGenera = ["Special", "unknown", "", null!];
        Assert.False(defaults.ProgressionIncludesGenus(genus!));
        defaults.ProgressionPetGenera = null!;
        Assert.False(defaults.ProgressionIncludesGenus(genus!));
        Assert.All(All, allowed => Assert.True(defaults.ProgressionIncludesGenus(allowed)));
    }

    [Fact]
    public void LoadingAndUpdatingNormalizeCaseDuplicatesAndUnknownValuesInCanonicalOrder()
    {
        Write("{\"SettingsRevision\":5,\"ProgressionPetGenera\":[\"fera\",\"Cogni\",\"fera\",\"Special\",null,\"unknown\",\"\",\"VARIAN\"]}");
        var settings = new SettingsService();
        Assert.Equal(new[] { "cogni", "varian", "fera" }, settings.Current.ProgressionPetGenera);
        var changes = 0;
        settings.Changed += () => changes++;
        settings.Update(s => s.ProgressionPetGenera = ["Fera", "NATURA", "fera", "unknown", "special", null!]);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "natura", "fera" }, settings.Current.ProgressionPetGenera);
        Assert.Equal(new[] { "natura", "fera" }, new SettingsService().Current.ProgressionPetGenera);
        using (var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile)))
        {
            Assert.Equal(new[] { "natura", "fera" }, saved.RootElement.GetProperty(nameof(AppSettings.ProgressionPetGenera))
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(5, saved.RootElement.GetProperty(nameof(AppSettings.SettingsRevision)).GetInt32());
        }
        settings.Update(s => s.ProgressionPetGenera = ["unknown", "special", null!]);
        Assert.Empty(settings.Current.ProgressionPetGenera);
        Assert.Empty(new SettingsService().Current.ProgressionPetGenera);
        settings.Update(s => s.ProgressionPetGenera = null!);
        Assert.Equal(All, settings.Current.ProgressionPetGenera);
        Assert.Equal(All, new SettingsService().Current.ProgressionPetGenera);
    }

    [Fact]
    public void SelectionSurvivesChangingMapsAndTargetKindsWithoutChangingIndependentPreferences()
    {
        Write("{\"SettingsRevision\":5,\"ProgressionPetGenera\":[\"cogni\",\"varian\"],\"ProgressionKinds\":[\"pets\",\"dungeon\",\"stronghold\"],"
            + "\"ProgressionGoal\":30,\"ProgressionPetsClosest\":true,\"ProgressionPetsHighestDensity\":true,\"ExclusivePetsOnly\":true,"
            + "\"HideLevel1\":true,\"HideLevel2\":true,\"HideLevel3\":true,\"OverlayX\":-1860,\"OverlayScale\":1.25}");
        var settings = new SettingsService();
        settings.Update(s => { s.ProgressionKinds = ["dungeon", "stronghold"]; s.LastMap = "verteron"; });
        var noPets = new SettingsService().Current;
        Assert.Equal(new[] { "cogni", "varian" }, noPets.ProgressionPetGenera);
        Assert.False(noPets.ProgressionIncludes("pets"));
        settings.Update(s => { s.ProgressionKinds = ["pets", "dungeon", "stronghold"]; s.LastMap = "altgard"; });
        var reloaded = new SettingsService().Current;
        Assert.Equal(new[] { "cogni", "varian" }, reloaded.ProgressionPetGenera);
        Assert.Equal(new[] { "pets", "dungeon", "stronghold" }, reloaded.ProgressionKinds);
        Assert.Equal(30, reloaded.ProgressionGoal);
        Assert.True(reloaded.ProgressionPetsClosest);
        Assert.True(reloaded.ProgressionPetsHighestDensity);
        Assert.True(reloaded.ExclusivePetsOnly);
        Assert.True(reloaded.HideLevel1);
        Assert.True(reloaded.HideLevel2);
        Assert.True(reloaded.HideLevel3);
        Assert.Equal(-1860, reloaded.OverlayX);
        Assert.Equal(1.25, reloaded.OverlayScale);
    }

    private static void Write(string json)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
