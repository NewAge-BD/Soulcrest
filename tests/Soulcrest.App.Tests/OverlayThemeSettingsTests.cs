using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayThemeSettingsTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData("aion2-stylized")]
    [InlineData("aion2-neutral")]
    [InlineData("obsidian")]
    [InlineData("aurora")]
    [InlineData("runic")]
    [InlineData("soulcrest-legacy")]
    public void EachCanonicalThemeLoadsWithoutWritingAndSurvivesSavingAndRestart(string id)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { SettingsRevision = 5, OverlayTheme = id });
        var originalFile = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(id, settings.Current.OverlayTheme);
        Assert.Equal(originalFile, File.ReadAllBytes(AppPaths.SettingsFile));
        settings.Update(_ => { });
        Assert.Equal(id, new SettingsService().Current.OverlayTheme);
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Equal(id, saved.RootElement.GetProperty(nameof(AppSettings.OverlayTheme)).GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SettingsRevision\":5}")]
    [InlineData("{\"OverlayTheme\":null}")]
    [InlineData("{\"OverlayTheme\":\"unknown\"}")]
    [InlineData("{\"OverlayTheme\":\"aion2\"}")]
    public void MissingInvalidAndFormerAion2ThemeMigrateToStylizedOnlyOnExplicitSave(string json)
    {
        Assert.Equal("aion2-stylized", new AppSettings().OverlayTheme);
        File.WriteAllText(AppPaths.SettingsFile, json);
        var originalFile = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(OverlayThemeDefinition.DefaultId, settings.Current.OverlayTheme);
        Assert.Equal(originalFile, File.ReadAllBytes(AppPaths.SettingsFile));
        settings.Update(_ => { });
        Assert.Equal(OverlayThemeDefinition.DefaultId, new SettingsService().Current.OverlayTheme);
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Equal("aion2-stylized", saved.RootElement.GetProperty(nameof(AppSettings.OverlayTheme)).GetString());
    }

    [Fact]
    public void SwitchingEveryThemeRetainsAllLayoutsRulesTimersAndOtherPreferences()
    {
        File.Delete(AppPaths.SettingsFile);
        var settings = new SettingsService();
        settings.Update(current =>
        {
            current.OverlayX = -1888; current.OverlayY = -32; current.OverlayScale = 1.37;
            current.BossOverlayX = 125; current.BossOverlayY = 234; current.BossOverlayScale = 1.1;
            current.LevelingOverlayX = 351; current.LevelingOverlayY = 462; current.LevelingOverlayScale = 1.2;
            current.AlertsOverlayX = 579; current.AlertsOverlayY = 682; current.AlertsOverlayScale = 1.3;
            current.AlertNotificationsX = 795; current.AlertNotificationsY = 806; current.AlertNotificationsWidth = 777;
            current.EventTimelineOverlayX = 914; current.EventTimelineOverlayY = 1025; current.EventTimelineOverlayWidth = 1234;
            current.TimerOverlayTimerIds = ["event:catalog:shugo", "event:catalog:rift"];
            current.OverlayEnabled = false; current.BossOverlayVisible = false; current.OverlayShowMode = "always";
            current.OverlayVisibility["loot"] = new(false, true, false, true);
            current.OverlayVisibility["boss"] = new(true, false, true, false);
        });
        static string OtherSettings(AppSettings current)
        {
            var node = JsonSerializer.SerializeToNode(current)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayTheme));
            return node.ToJsonString();
        }
        var before = OtherSettings(settings.Current);
        var changes = 0;
        settings.Changed += () => changes++;
        foreach (var theme in OverlayThemeDefinition.All)
        {
            settings.Update(current => current.OverlayTheme = theme.Id);
            Assert.Equal(theme.Id, new SettingsService().Current.OverlayTheme);
            Assert.Equal(before, OtherSettings(new SettingsService().Current));
        }
        Assert.Equal(OverlayThemeDefinition.All.Count, changes);
        settings.Update(current => current.OverlayTheme = "aion2");
        Assert.Equal(OverlayThemeDefinition.DefaultId, new SettingsService().Current.OverlayTheme);
        Assert.Equal(before, OtherSettings(new SettingsService().Current));
        settings.Update(current => current.OverlayTheme = "unknown");
        Assert.Equal(OverlayThemeDefinition.DefaultId, new SettingsService().Current.OverlayTheme);
        Assert.Equal(before, OtherSettings(new SettingsService().Current));
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
