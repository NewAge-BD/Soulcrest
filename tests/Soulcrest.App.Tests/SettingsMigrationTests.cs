using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The completion radius default went from 40 to 15 map pixels (2026-10-05); the tutorial is for new installations (2026-10-07).</summary>
[Collection("Settings file")] // both write settings.json of the test data folder
public sealed class SettingsMigrationTests
{
    // User request 2026-10-11: only new/missing settings default on; a saved off stays off.
    [Fact]
    public void FreshSettingsEnablePlayerTrackingAndFollowingAfterRestart()
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.Delete(path);
            var settings = new SettingsService();
            Assert.True(settings.Current.MapTrackingActive);
            Assert.True(settings.Current.FollowPlayer);
            Assert.Null(settings.Current.MapRegion); // enabling does not fabricate a calibration
            settings.Update(s => s.UiLanguage = "en");
            var reloaded = new SettingsService().Current;
            Assert.True(reloaded.MapTrackingActive);
            Assert.True(reloaded.FollowPlayer);
        }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
            _ = new SettingsService();
        }
    }

    [Theory]
    [InlineData("{}", true, true)]
    [InlineData("{\"MapTrackingActive\":false}", false, true)]
    [InlineData("{\"FollowPlayer\":false}", true, false)]
    [InlineData("{\"MapTrackingActive\":false,\"FollowPlayer\":false}", false, false)]
    [InlineData("{\"SettingsRevision\":3,\"MapTrackingActive\":false,\"FollowPlayer\":false}", false, false)]
    [InlineData("{\"SettingsRevision\":3,\"MapTrackingActive\":true,\"FollowPlayer\":true}", true, true)]
    public void SavedPlayerTrackingChoicesSurviveMigrationAndRestart(string json, bool tracking, bool following)
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, json);
            var settings = new SettingsService();
            Assert.Equal(tracking, settings.Current.MapTrackingActive);
            Assert.Equal(following, settings.Current.FollowPlayer);
            settings.Update(s => s.UiLanguage = "en");
            var reloaded = new SettingsService().Current;
            Assert.Equal(tracking, reloaded.MapTrackingActive);
            Assert.Equal(following, reloaded.FollowPlayer);
        }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
            _ = new SettingsService();
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ExplicitScanLanguageSurvivesRestartWithoutChangingTheInterface(string language)
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            var settings = new SettingsService();
            settings.Update(s => { s.UiLanguage = "en"; s.AutoOcrLanguage = false; s.OcrLanguage = language; });
            var reloaded = new SettingsService().Current;
            Assert.Equal(language, reloaded.EffectiveOcrLanguage);
            Assert.Equal("en", reloaded.UiLanguage);
            settings.Update(s => s.AutoOcrLanguage = true);
            Assert.Equal("auto", new SettingsService().Current.EffectiveOcrLanguage);
        }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
            _ = new SettingsService();
        }
    }

    [Theory]
    [InlineData(40, 15)] // the old default saved with every settings file
    [InlineData(60, 60)] // chosen by the user: kept
    public void OldDefaultRadiusBecomesFifteen(int saved, int expected)
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, $$"""{"ExplorationCompletionRadius":{{saved}}}""");
            var settings = new SettingsService();
            Assert.Equal(expected, settings.Current.ExplorationCompletionRadius);
            Assert.Equal(4, settings.Current.SettingsRevision);
        }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
        }
    }

    [Fact]
    public void OnlyAFreshInstallationShowsTheTutorial()
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.Delete(path);
            Assert.False(new SettingsService().Current.TutorialDone); // no settings yet: first start
            File.WriteAllText(path, """{"SettingsRevision":1}""");
            Assert.True(new SettingsService().Current.TutorialDone); // used before the tutorial existed
            File.WriteAllText(path, """{"SettingsRevision":2,"TutorialDone":false}""");
            Assert.False(new SettingsService().Current.TutorialDone); // new installation, tutorial not closed yet
        }
        finally
        {
            if (backup is null) File.Delete(path); else File.WriteAllText(path, backup);
        }
    }
}
