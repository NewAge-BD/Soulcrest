using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayEditorThemeSettingsTests : IDisposable
{
    private readonly byte[]? _backup;
    private readonly string _language;

    public OverlayEditorThemeSettingsTests()
    {
        var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Assert.False(string.IsNullOrWhiteSpace(redirected));
        Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userData)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory)), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(AppPaths.SettingsFile));
        _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        _language = UiText.Language;
    }

    [Theory]
    [InlineData("aion2-stylized", "aion2-stylized")]
    [InlineData("aion2-neutral", "aion2-neutral")]
    [InlineData("obsidian", "obsidian")]
    [InlineData("aurora", "aurora")]
    [InlineData("runic", "runic")]
    [InlineData("soulcrest-legacy", "soulcrest-legacy")]
    [InlineData("aion2", "aion2-stylized")]
    [InlineData("grindcrest", "aion2-stylized")]
    [InlineData("unknown", "aion2-stylized")]
    [InlineData(null, "aion2-stylized")]
    public void SharedThemeSavePublishesOnlyThePersistedCanonicalValueAndKeepsOtherPreferences(string? input, string expected)
    {
        var initial = expected == "runic" ? "obsidian" : "runic";
        var settings = Seed(initial);
        var otherPreferences = WithoutTheme(settings.Current);
        var changes = 0;
        settings.Changed += () =>
        {
            changes++;
            using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
            Assert.Equal(expected, saved.RootElement.GetProperty(nameof(AppSettings.OverlayTheme)).GetString());
        };
        settings.SetOverlayTheme(input);
        Assert.Equal(expected, settings.Current.OverlayTheme);
        Assert.Equal(expected, new SettingsService().Current.OverlayTheme);
        Assert.Equal(otherPreferences, WithoutTheme(settings.Current));
        Assert.Equal(otherPreferences, WithoutTheme(new SettingsService().Current));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FailedSharedThemeSaveRollsBackWithoutPublishingAndCanBeRetried()
    {
        var settings = Seed("obsidian");
        var before = JsonSerializer.Serialize(settings.Current);
        var saved = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);

        var error = Record.Exception(() => settings.SetOverlayTheme("aurora"));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("obsidian", settings.Current.OverlayTheme);
        Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
        Assert.Equal(0, changes);
        Directory.Delete(AppPaths.SettingsFile);
        File.WriteAllBytes(AppPaths.SettingsFile, saved);
        settings.SetOverlayTheme("aurora");
        Assert.Equal("aurora", settings.Current.OverlayTheme);
        Assert.Equal("aurora", new SettingsService().Current.OverlayTheme);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void UnchangedCanonicalThemeDoesNotWriteOrPublish()
    {
        var settings = Seed(OverlayThemeDefinition.DefaultId);
        var before = JsonSerializer.Serialize(settings.Current);
        var changes = 0;
        settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);

        settings.SetOverlayTheme("aion2");

        Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
        Assert.Equal(0, changes);
        Assert.True(Directory.Exists(AppPaths.SettingsFile));
    }

    private static SettingsService Seed(string theme)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 5, OverlayTheme = theme, UiLanguage = "en", NameLanguage = "de",
            LootTrackingEnabled = false, OverlayX = -1280, OverlayY = 37, OverlayScale = 1.35,
            TimerOverlayTimerIds = ["event:catalog:shugo"],
        });
        return new SettingsService();
    }

    private static string WithoutTheme(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
        node.Remove(nameof(AppSettings.OverlayTheme));
        return node.ToJsonString();
    }

    public void Dispose()
    {
        if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
        if (_backup is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
