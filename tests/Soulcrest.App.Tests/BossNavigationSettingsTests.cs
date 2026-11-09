using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossNavigationSettingsTests : IDisposable
{
    private readonly byte[]? _settingsBackup;
    private readonly string _language = UiText.Language;

    public BossNavigationSettingsTests()
    {
        var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Assert.False(string.IsNullOrWhiteSpace(redirected));
        Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    }

    [Fact]
    public void MissingNavigationPreferenceDefaultsToOneAdditionalBossWithoutChangingSpawnOverlayCount()
    {
        Assert.Equal(1, new AppSettings().BossNavigationPreviewCount);
        JsonFile.Save(AppPaths.SettingsFile, new { SettingsRevision = 5, BossOverlayCount = 7 });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Equal(1, settings.Current.BossNavigationPreviewCount);
        Assert.Equal(7, settings.Current.BossOverlayCount);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(23, 23)]
    [InlineData(24, 23)]
    [InlineData(int.MaxValue, 23)]
    public void LoadAndUpdateClampNavigationRangeAndRetainItAcrossRestart(int supplied, int expected)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { BossNavigationPreviewCount = supplied, BossOverlayCount = 8 });
        var settings = new SettingsService();
        Assert.Equal(expected, settings.Current.BossNavigationPreviewCount);
        settings.Update(current => current.BossNavigationPreviewCount = supplied);
        Assert.Equal(expected, settings.Current.BossNavigationPreviewCount);
        var reloaded = new SettingsService().Current;
        Assert.Equal(expected, reloaded.BossNavigationPreviewCount);
        Assert.Equal(8, reloaded.BossOverlayCount);
    }

    [Fact]
    public void MissingOlderSpawnPreferenceDefaultsToFalseWithoutRewritingExistingSettings()
    {
        Assert.False(new AppSettings().BossRushShowOlderSpawns);
        JsonFile.Save(AppPaths.SettingsFile, new { SettingsRevision = 5, BossNavigationPreviewCount = 3, BossOverlayCount = 7 });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);

        var settings = new SettingsService();

        Assert.False(settings.Current.BossRushShowOlderSpawns);
        Assert.Equal(3, settings.Current.BossNavigationPreviewCount);
        Assert.Equal(7, settings.Current.BossOverlayCount);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderSpawnPreferenceSurvivesSaveAndRestartIndependentlyOfNavigationAndOverlay(bool showOlderSpawns)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            BossRushEnabled = false,
            BossNavigationPreviewCount = 3,
            BossOverlayCount = 7,
            BossOverlayEnabled = false,
        });
        var settings = new SettingsService();
        var changes = 0;
        settings.Changed += () => changes++;

        settings.Update(current => current.BossRushShowOlderSpawns = showOlderSpawns);

        Assert.Equal(1, changes);
        var reloaded = new SettingsService().Current;
        Assert.Equal(showOlderSpawns, reloaded.BossRushShowOlderSpawns);
        Assert.False(reloaded.BossRushEnabled);
        Assert.Equal(3, reloaded.BossNavigationPreviewCount);
        Assert.Equal(7, reloaded.BossOverlayCount);
        Assert.False(reloaded.BossOverlayEnabled);
    }

    [Fact]
    public void MissingRushFiltersDefaultToFalseWithoutRewritingStoredSelections()
    {
        Assert.False(new AppSettings().BossRushOnlyUniqueGearBosses);
        Assert.False(new AppSettings().BossRushOnlyAlertBosses);
        JsonFile.Save(AppPaths.SettingsFile, new { BossAlertIds = new[] { "1110:111001" }, BossAlertsEnabled = false });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);

        var settings = new SettingsService();

        Assert.False(settings.Current.BossRushOnlyUniqueGearBosses);
        Assert.False(settings.Current.BossRushOnlyAlertBosses);
        Assert.Equal(new[] { "1110:111001" }, settings.Current.BossAlertIds);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RushFiltersPersistIndependentlyAcrossRestart(bool uniqueOnly, bool alertOnly)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { BossRushEnabled = false, BossAlertsEnabled = false,
            BossRushShowOlderSpawns = true, BossNavigationPreviewCount = 3, BossOverlayCount = 7,
            BossAlertIds = ["1110:111001"], BossReminderOverrides = new() { ["1110:111001"] = new() { Enabled = false } } });
        var settings = new SettingsService();

        settings.Update(current => { current.BossRushOnlyUniqueGearBosses = uniqueOnly; current.BossRushOnlyAlertBosses = alertOnly; });

        var reloaded = new SettingsService().Current;
        Assert.Equal(uniqueOnly, reloaded.BossRushOnlyUniqueGearBosses);
        Assert.Equal(alertOnly, reloaded.BossRushOnlyAlertBosses);
        Assert.False(reloaded.BossRushEnabled);
        Assert.False(reloaded.BossAlertsEnabled);
        Assert.True(reloaded.BossRushShowOlderSpawns);
        Assert.Equal(3, reloaded.BossNavigationPreviewCount);
        Assert.Equal(7, reloaded.BossOverlayCount);
        Assert.Equal(new[] { "1110:111001" }, reloaded.BossAlertIds);
        Assert.False(reloaded.BossReminderOverrides["1110:111001"].Enabled);
    }

    public void Dispose()
    {
        if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        UiText.Language = _language;
    }
}
