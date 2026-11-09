using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AchievementOverlaySettingsTests
{
    [Fact]
    public void MissingPreferenceDefaultsToTwentySecondsForFreshAndExistingSettings()
    {
        WithSettings(() =>
        {
            Assert.Equal(20, new AppSettings().AchievementOverlayDisplaySeconds);
            Assert.Equal(20, new SettingsService().Current.AchievementOverlayDisplaySeconds);
            File.WriteAllText(AppPaths.SettingsFile, """{"AchievementOverlayEnabled":false,"AchievementOverlayScale":1.4}""");
            var settings = new SettingsService();
            Assert.Equal(20, settings.Current.AchievementOverlayDisplaySeconds);
            Assert.False(settings.Current.AchievementOverlayEnabled);
            Assert.Equal(1.4, settings.Current.AchievementOverlayScale);
        });
    }

    [Theory]
    [InlineData(int.MinValue, 5)]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(20, 20)]
    [InlineData(45, 45)]
    [InlineData(300, 300)]
    [InlineData(int.MaxValue, 300)]
    public void DurationNormalizesOnLoadAndPersistsWithoutChangingOtherPreferences(int requested, int expected)
    {
        WithSettings(() =>
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { AchievementOverlayDisplaySeconds = requested,
                AchievementOverlayEnabled = false, AchievementOverlayX = 123, AchievementOverlayScale = 1.2 });
            var loaded = new SettingsService();
            Assert.Equal(expected, loaded.Current.AchievementOverlayDisplaySeconds);
            var settings = new SettingsService();
            var changes = 0; settings.Changed += () => changes++;
            settings.SetAchievementOverlayDisplaySeconds(20);
            var before = JsonSerializer.Serialize(settings.Current);
            changes = 0;
            settings.SetAchievementOverlayDisplaySeconds(requested);
            Assert.Equal(expected, new SettingsService().Current.AchievementOverlayDisplaySeconds);
            Assert.Equal(expected == 20 ? 0 : 1, changes);
            settings.SetAchievementOverlayDisplaySeconds(requested);
            Assert.Equal(expected == 20 ? 0 : 1, changes);
            settings.SetAchievementOverlayDisplaySeconds(20);
            Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
        });
    }

    [Fact]
    public void FailedSaveRetainsPreviousDurationAndDoesNotPublishAChange()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            settings.SetAchievementOverlayDisplaySeconds(45);
            var before = JsonSerializer.Serialize(settings.Current);
            var changes = 0; settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile);
            Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetAchievementOverlayDisplaySeconds(30));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
                Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    private static void WithSettings(Action test)
    {
        var realData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.NotEqual(Path.GetFullPath(realData), Path.GetFullPath(AppPaths.DataDirectory));
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try { File.Delete(AppPaths.SettingsFile); test(); }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = language;
        }
    }
}
