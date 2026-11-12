using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AppThemeSettingsTests : IDisposable
{
    private readonly byte[]? _backup;
    private readonly string _language = UiText.Language;

    public AppThemeSettingsTests()
    {
        var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Assert.False(string.IsNullOrWhiteSpace(redirected));
        Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.GetFullPath(userData), Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(AppPaths.SettingsFile));
        _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AppTheme\":null}")]
    [InlineData("{\"AppTheme\":\"unknown\"}")]
    [InlineData("{\"AppTheme\":\"GRAPHITE-DARK\"}")]
    public void MissingAndUnknownThemeUseClassicWithoutWritingAtStartup(string json)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        var original = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(AppThemeDefinition.DefaultId, settings.Current.AppTheme);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Null(settings.AppThemeSaveError);
    }

    [Theory]
    [InlineData(AppThemeDefinition.DefaultId)]
    [InlineData(AppThemeDefinition.DarkId)]
    public void CanonicalThemesLoadAndSurviveAnUnrelatedSave(string theme)
    {
        var settings = Seed(theme);
        var original = File.ReadAllBytes(AppPaths.SettingsFile);
        Assert.Equal(theme, settings.Current.AppTheme);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SettingsFile));
        settings.Update(current => current.MainWidth = 1240);
        Assert.Equal(theme, new SettingsService().Current.AppTheme);
    }

    [Theory]
    [InlineData(AppThemeDefinition.DarkId, AppThemeDefinition.DarkId)]
    [InlineData(null, AppThemeDefinition.DefaultId)]
    [InlineData("invalid", AppThemeDefinition.DefaultId)]
    public void SwitchingThemePublishesOnlyPersistedValueAndPreservesOverlayAndOtherPreferences(string? input, string expected)
    {
        var initial = expected == AppThemeDefinition.DarkId ? AppThemeDefinition.DefaultId : AppThemeDefinition.DarkId;
        var settings = Seed(initial);
        var other = WithoutAppTheme(settings.Current);
        var changes = 0;
        settings.Changed += () =>
        {
            changes++;
            using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
            Assert.Equal(expected, saved.RootElement.GetProperty(nameof(AppSettings.AppTheme)).GetString());
        };

        settings.SetAppTheme(input);

        Assert.Equal(expected, settings.Current.AppTheme);
        Assert.Equal(expected, new SettingsService().Current.AppTheme);
        Assert.Equal(other, WithoutAppTheme(settings.Current));
        Assert.Equal(other, WithoutAppTheme(new SettingsService().Current));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FailedSaveRollsBackAndRecordsAnonymousErrorUntilSuccessfulRetry()
    {
        var settings = Seed(AppThemeDefinition.DefaultId);
        var before = JsonSerializer.Serialize(settings.Current);
        var saved = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);

        var error = Record.Exception(() => settings.SetAppTheme(AppThemeDefinition.DarkId));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
        Assert.NotNull(error);
        Assert.Equal(error.GetType().Name, settings.AppThemeSaveError);
        Assert.Equal(0, changes);
        Directory.Delete(AppPaths.SettingsFile);
        File.WriteAllBytes(AppPaths.SettingsFile, saved);
        settings.SetAppTheme(AppThemeDefinition.DarkId);
        Assert.Equal(AppThemeDefinition.DarkId, new SettingsService().Current.AppTheme);
        Assert.Null(settings.AppThemeSaveError);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SelectingCurrentThemeDoesNotWriteOrPublish()
    {
        var settings = Seed(AppThemeDefinition.DefaultId);
        var changes = 0;
        settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);

        settings.SetAppTheme(AppThemeDefinition.DefaultId);

        Assert.Equal(AppThemeDefinition.DefaultId, settings.Current.AppTheme);
        Assert.Equal(0, changes);
        Assert.True(Directory.Exists(AppPaths.SettingsFile));
    }

    [Fact]
    public void GeneralSettingsUpdateNormalizesAnInvalidTheme()
    {
        var settings = Seed(AppThemeDefinition.DarkId);
        settings.Update(current => current.AppTheme = "future-theme");
        Assert.Equal(AppThemeDefinition.DefaultId, settings.Current.AppTheme);
        Assert.Equal(AppThemeDefinition.DefaultId, new SettingsService().Current.AppTheme);
    }

    private static SettingsService Seed(string theme)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 5, MonsterLootAutoSessionRevision = 1, AppTheme = theme,
            OverlayTheme = "runic", UiLanguage = "en", NameLanguage = "de", LootTrackingEnabled = false,
            MainWidth = 1450, OverlayX = -1280, OverlayY = 37, OverlayScale = 1.35,
            TimerOverlayTimerIds = ["event:catalog:shugo"]
        });
        return new SettingsService();
    }

    private static string WithoutAppTheme(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
        node.Remove(nameof(AppSettings.AppTheme));
        return node.ToJsonString();
    }

    public void Dispose()
    {
        if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
