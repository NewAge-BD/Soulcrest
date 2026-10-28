using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerSettingsTests
{
    [Fact]
    public void AutoShowIsOptInAndOnlyANewSessionRequestOverridesTheOverlayToggle()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            settings.SetLootOverlayEnabled(false); settings.SetLootOverlayShowOnSessionStart(false);
            Assert.False(settings.TryShowLootOverlayForSessionStart());
            Assert.False(settings.Current.LootOverlayEnabled);
            settings.SetLootOverlayShowOnSessionStart(true);
            Assert.False(settings.Current.LootOverlayEnabled);
            Assert.True(settings.TryShowLootOverlayForSessionStart());
            Assert.True(new SettingsService().Current.LootOverlayEnabled);
            Assert.False(settings.TryShowLootOverlayForSessionStart());
            settings.SetLootOverlayEnabled(false);
            Assert.False(new SettingsService().Current.LootOverlayEnabled);
            Assert.True(settings.TryShowLootOverlayForSessionStart());
        });
    }

    [Fact]
    public void SessionOnlyIsOptInPersistsAndRollsBackWithoutChangingOtherTrackingOrDisplayPreferences()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            Assert.False(new AppSettings().LootOverlayOnlyDuringSession);
            settings.SetLootOverlayEnabled(true); settings.SetLootOverlayShowOnSessionStart(false);
            var events = 0; settings.Changed += () => events++;
            var before = JsonSerializer.Serialize(settings.Current);
            settings.SetLootOverlayOnlyDuringSession(true);
            Assert.True(new SettingsService().Current.LootOverlayOnlyDuringSession); Assert.Equal(1, events);
            settings.SetLootOverlayOnlyDuringSession(true); Assert.Equal(1, events);
            settings.SetLootOverlayOnlyDuringSession(false); Assert.Equal(2, events);
            Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
            File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetLootOverlayOnlyDuringSession(true));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.False(settings.Current.LootOverlayOnlyDuringSession); Assert.Equal(2, events);
                Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    [Fact]
    public void BackgroundOpacityPersistsAndNormalizesWithoutChangingOtherOverlayPreferences()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            settings.SetLootOverlayEnabled(false); settings.SetLootOverlayShowOnSessionStart(true);
            settings.SetLootOverlayBackgroundOpacity(.35);
            Assert.Equal(.35, new SettingsService().Current.LootOverlayBackgroundOpacity);
            settings.SetLootOverlayBackgroundOpacity(double.NaN);
            Assert.Equal(1, new SettingsService().Current.LootOverlayBackgroundOpacity);
            settings.SetLootOverlayBackgroundOpacity(-2);
            Assert.Equal(0, new SettingsService().Current.LootOverlayBackgroundOpacity);
            Assert.False(settings.Current.LootOverlayEnabled);
            Assert.True(settings.Current.LootOverlayShowOnSessionStart);
        });
    }

    [Fact]
    public void FormerFalseDefaultMigratesOnceAndLaterOptOutSurvivesRestart()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile, """{"SettingsRevision":5,"MonsterLootTrackingEnabled":false,"LootOverlayEnabled":false}""");
            var settings = new SettingsService();
            Assert.True(settings.Current.MonsterLootTrackingEnabled);
            Assert.False(settings.Current.LootOverlayEnabled);
            Assert.Equal(1, settings.Current.MonsterLootAutoSessionRevision);
            settings.SetMonsterLootTrackingEnabled(false);
            var restarted = new SettingsService();
            Assert.False(restarted.Current.MonsterLootTrackingEnabled);
            Assert.Equal(1, restarted.Current.MonsterLootAutoSessionRevision);
            restarted.SetLootOverlayEnabled(true);
            Assert.False(new SettingsService().Current.MonsterLootTrackingEnabled);
        });
    }

    [Fact]
    public void FreshSettingsEnablePveWithoutEnablingOverlayAndPreserveGlobalNetworkOptOut()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var fresh = new SettingsService();
            Assert.True(fresh.Current.MonsterLootTrackingEnabled); Assert.False(fresh.Current.LootOverlayEnabled);
            Assert.True(fresh.Current.LootDropAlertsEnabled); Assert.True(fresh.Current.LootDropSoundEnabled);
            Assert.Equal(.6, fresh.Current.LootDropSoundVolume);
            fresh.Update(current => { current.LootTrackingEnabled = false; current.MonsterLootTrackingEnabled = false; });
            var restarted = new SettingsService();
            Assert.False(restarted.Current.LootTrackingEnabled); Assert.False(restarted.Current.MonsterLootTrackingEnabled);
        });
    }

    [Fact]
    public void DropOptionsPersistAndNormalizeVolumeWithoutChangingRecording()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService(); settings.SetMonsterLootTrackingEnabled(false);
            settings.SetLootDropAlertsEnabled(false); settings.SetLootDropSoundEnabled(false);
            settings.SetLootDropAnimationsEnabled(false); settings.SetLootDropSoundVolume(2);
            var saved = new SettingsService().Current;
            Assert.False(saved.MonsterLootTrackingEnabled); Assert.False(saved.LootDropAlertsEnabled);
            Assert.False(saved.LootDropSoundEnabled); Assert.False(saved.LootDropAnimationsEnabled);
            Assert.Equal(1, saved.LootDropSoundVolume);
            settings.SetLootDropSoundVolume(double.NaN); Assert.Equal(.6, new SettingsService().Current.LootDropSoundVolume);
        });
    }

    private static void WithSettings(Action action)
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try { action(); }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = language;
        }
    }

    [Fact]
    public void FailedIdleTimeoutSaveKeepsPriorStateAndDoesNotPublishAChange()
    {
        // TestDataFolder redirects this entire assembly before AppPaths is initialized.
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userData)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory)),
            StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(AppPaths.SettingsFile));
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings
            {
                LootTrackerIdleTimeoutMinutes = 7,
                LootTrackingEnabled = false,
                NameLanguage = "en",
                UiLanguage = "en",
            });
            var settings = new SettingsService();
            var before = JsonSerializer.Serialize(settings.Current);
            var changes = 0;
            settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile);
            Directory.CreateDirectory(AppPaths.SettingsFile);

            var error = Record.Exception(() => settings.SetLootTrackerIdleTimeoutMinutes(9));

            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(7, settings.Current.LootTrackerIdleTimeoutMinutes);
            Assert.False(settings.Current.LootTrackingEnabled);
            Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
            Assert.Equal(0, changes);
        }
        finally
        {
            if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
            if (backup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = language;
        }
    }
}
