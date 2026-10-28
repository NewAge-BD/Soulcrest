using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerReplayMarkerSettingsTests
{
    [Fact]
    public void RankIsDefaultForNewAndHistoricalSettingsAndSelectionSurvivesRestart()
    {
        WithSettings(() =>
        {
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, new AppSettings().LootReplayKillIconMode);
            File.WriteAllText(AppPaths.SettingsFile, "{}");
            var settings = new SettingsService();
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, settings.Current.LootReplayKillIconMode);
            var changes = 0; settings.Changed += () => changes++;
            settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Class);
            settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Class);
            Assert.Equal(1, changes);
            Assert.Equal(LootTrackerReplayKillIconMode.Class, new SettingsService().Current.LootReplayKillIconMode);
            settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Rank);
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, new SettingsService().Current.LootReplayKillIconMode);
            Assert.Equal(2, changes);
        });
    }

    [Fact]
    public void InvalidValuesNormalizeOnLoadSetterAndGeneralUpdate()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile, """{"LootReplayKillIconMode":999}""");
            var settings = new SettingsService();
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, settings.Current.LootReplayKillIconMode);
            settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Class);
            settings.SetLootReplayKillIconMode((LootTrackerReplayKillIconMode)(-1));
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, settings.Current.LootReplayKillIconMode);
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, new SettingsService().Current.LootReplayKillIconMode);
            settings.Update(value => value.LootReplayKillIconMode = (LootTrackerReplayKillIconMode)999);
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, settings.Current.LootReplayKillIconMode);
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, new SettingsService().Current.LootReplayKillIconMode);
        });
    }

    [Fact]
    public void FailedSaveRestoresEntireSettingsAndDoesNotPublishAChange()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile, "{}");
            var settings = new SettingsService();
            settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Class);
            var before = JsonSerializer.Serialize(settings.Current); var changes = 0;
            settings.Changed += () => changes++;
            var bytes = File.ReadAllBytes(AppPaths.SettingsFile);
            File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetLootReplayKillIconMode(LootTrackerReplayKillIconMode.Rank));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
                Assert.Equal(LootTrackerReplayKillIconMode.Class, settings.Current.LootReplayKillIconMode);
                Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); File.WriteAllBytes(AppPaths.SettingsFile, bytes); }
            Assert.Equal(LootTrackerReplayKillIconMode.Class, new SettingsService().Current.LootReplayKillIconMode);
        });
    }

    private static void WithSettings(Action action)
    {
        var bytes = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try { action(); }
        finally
        {
            if (bytes is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, bytes);
            UiText.Language = language;
        }
    }
}
