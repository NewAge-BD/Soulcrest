using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerStatisticsSettingsTests
{
    [Fact]
    public void DefaultsAndAllPresentationPreferencesSurviveRestartWithoutChangingRecording()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var settings = new SettingsService();
            Assert.Equal(AbyssCombatGrouping.Class, settings.Current.AbyssCombatGrouping);
            Assert.Equal(AbyssCombatSort.KillsDescending, settings.Current.AbyssCombatSort);
            Assert.Equal(LootResourceSort.SuccessesDescending, settings.Current.LifeskillResourceSort);
            Assert.Equal(LootQuantitySort.QuantityDescending, settings.Current.PveLootSort);
            Assert.Equal(LootQuantitySort.QuantityDescending, settings.Current.PveMobSort);
            Assert.Equal(LootTimelineValueMode.Cumulative, settings.Current.LootTimelineValueMode);
            Assert.Equal(LootTrackerViewMode.Mixed, settings.Current.LootTrackerViewMode);
            settings.SetAbyssTrackingEnabled(false); settings.SetMonsterLootTrackingEnabled(false);
            settings.SetAbyssCombatGrouping(AbyssCombatGrouping.Rank); settings.SetAbyssCombatSort(AbyssCombatSort.DeathsDescending);
            settings.SetLifeskillResourceSort(LootResourceSort.FailuresAscending);
            settings.SetPveLootSort(LootQuantitySort.NameAscending); settings.SetPveMobSort(LootQuantitySort.QuantityAscending);
            settings.SetLootTimelineValueMode(LootTimelineValueMode.PerMinute);
            settings.SetLootTrackerViewMode(LootTrackerViewMode.Lifeskill);
            var saved = new SettingsService().Current;
            Assert.Equal(AbyssCombatGrouping.Rank, saved.AbyssCombatGrouping); Assert.Equal(AbyssCombatSort.DeathsDescending, saved.AbyssCombatSort);
            Assert.Equal(LootResourceSort.FailuresAscending, saved.LifeskillResourceSort);
            Assert.Equal(LootQuantitySort.NameAscending, saved.PveLootSort); Assert.Equal(LootQuantitySort.QuantityAscending, saved.PveMobSort);
            Assert.Equal(LootTimelineValueMode.PerMinute, saved.LootTimelineValueMode);
            Assert.Equal(LootTrackerViewMode.Lifeskill, saved.LootTrackerViewMode);
            Assert.False(saved.AbyssTrackingEnabled); Assert.False(saved.MonsterLootTrackingEnabled);
        });
    }

    [Fact]
    public void UnknownValuesNormalizeOnLoadSettersAndGeneralUpdate()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile, """{"AbyssCombatGrouping":999,"AbyssCombatSort":999,"LifeskillResourceSort":999,"PveLootSort":999,"PveMobSort":999,"LootTimelineValueMode":999,"LootTrackerViewMode":999}""");
            var settings = new SettingsService();
            AssertDefaults(settings.Current);
            settings.SetAbyssCombatGrouping((AbyssCombatGrouping)999); settings.SetAbyssCombatSort((AbyssCombatSort)999);
            settings.SetLifeskillResourceSort((LootResourceSort)999); settings.SetPveLootSort((LootQuantitySort)999);
            settings.SetPveMobSort((LootQuantitySort)999); settings.SetLootTimelineValueMode((LootTimelineValueMode)999);
            settings.SetLootTrackerViewMode((LootTrackerViewMode)999);
            AssertDefaults(settings.Current);
            settings.Update(value => { value.AbyssCombatSort = (AbyssCombatSort)999; value.LootTimelineValueMode = (LootTimelineValueMode)999; value.LootTrackerViewMode = (LootTrackerViewMode)999; });
            AssertDefaults(settings.Current);
        });
    }

    [Fact]
    public void WriteFailureRollsBackEntirePresentationValueWithoutPublishingChange()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var settings = new SettingsService(); settings.SetAbyssCombatGrouping(AbyssCombatGrouping.Rank);
            var before = JsonSerializer.Serialize(settings.Current); var changes = 0;
            settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetAbyssCombatGrouping(AbyssCombatGrouping.Class));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(before, JsonSerializer.Serialize(settings.Current)); Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    [Fact]
    public void SmallSessionFilterDefaultsOnPersistsAndClampsItsThreshold()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var settings = new SettingsService();
            Assert.True(settings.Current.LootTrackerHideSmallSessions);
            Assert.Equal(3, settings.Current.LootTrackerSmallSessionMaxActivities);
            settings.SetLootTrackerHideSmallSessions(false); settings.SetLootTrackerSmallSessionMaxActivities(7);
            var saved = new SettingsService().Current;
            Assert.False(saved.LootTrackerHideSmallSessions); Assert.Equal(7, saved.LootTrackerSmallSessionMaxActivities);
            settings.SetLootTrackerSmallSessionMaxActivities(99);
            Assert.Equal(10, new SettingsService().Current.LootTrackerSmallSessionMaxActivities);
            File.WriteAllText(AppPaths.SettingsFile, """{"LootTrackerSmallSessionMaxActivities":0}""");
            settings = new SettingsService();
            Assert.Equal(1, settings.Current.LootTrackerSmallSessionMaxActivities);
            Assert.True(settings.Current.LootTrackerHideSmallSessions);
            settings.Update(value => value.LootTrackerSmallSessionMaxActivities = -5);
            Assert.Equal(1, settings.Current.LootTrackerSmallSessionMaxActivities);
        });
    }

    [Fact]
    public void ItemCleanupDefaultsToTwentyWithoutMigratingTheLegacyActivityPreference()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile, """{"LootTrackerHideSmallSessions":false,"LootTrackerSmallSessionMaxActivities":7}""");
            var settings = new SettingsService();
            Assert.Equal(20, settings.Current.LootTrackerSmallSessionItemThreshold);
            Assert.False(settings.Current.LootTrackerHideSmallSessions);
            Assert.Equal(7, settings.Current.LootTrackerSmallSessionMaxActivities);
            settings.SetLootTrackerSmallSessionItemThreshold(5_000_000_000);
            Assert.Equal(5_000_000_000, new SettingsService().Current.LootTrackerSmallSessionItemThreshold);
            settings.SetLootTrackerSmallSessionItemThreshold(0);
            Assert.Equal(0, new SettingsService().Current.LootTrackerSmallSessionItemThreshold);
            settings.Update(value => value.LootTrackerSmallSessionItemThreshold = -1);
            Assert.Equal(0, settings.Current.LootTrackerSmallSessionItemThreshold);
            File.WriteAllText(AppPaths.SettingsFile, """{"LootTrackerSmallSessionItemThreshold":-5}""");
            Assert.Equal(0, new SettingsService().Current.LootTrackerSmallSessionItemThreshold);
        });
    }

    [Fact]
    public void FailedItemThresholdWritePreservesThePolicyAndDoesNotNotifyTheRecorder()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var settings = new SettingsService();
            settings.SetLootTrackerSmallSessionItemThreshold(80);
            var changes = 0; settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetLootTrackerSmallSessionItemThreshold(20));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(80, settings.Current.LootTrackerSmallSessionItemThreshold);
                Assert.Equal(80, settings.GetLootTrackerSmallSessionItemThreshold());
                Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    [Fact]
    public void ItemCleanupReadsWaitUntilTheSettingsTransactionHasFinished()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            settings.SetLootTrackerSmallSessionItemThreshold(20);
            using var writing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var reading = new ManualResetEventSlim();
            var writer = Task.Run(() => settings.Update(value =>
            {
                value.LootTrackerSmallSessionItemThreshold = 500;
                writing.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }));
            Task<long>? reader = null;
            try
            {
                Assert.True(writing.Wait(TimeSpan.FromSeconds(5)));
                reader = Task.Run(() => { reading.Set(); return settings.GetLootTrackerSmallSessionItemThreshold(); });
                Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(reader.Wait(TimeSpan.FromMilliseconds(100)));
            }
            finally { release.Set(); writer.GetAwaiter().GetResult(); }
            Assert.Equal(500, reader!.GetAwaiter().GetResult());
            Assert.Equal(500, new SettingsService().GetLootTrackerSmallSessionItemThreshold());
        });
    }

    private static void AssertDefaults(AppSettings value)
    {
        Assert.Equal(AbyssCombatGrouping.Class, value.AbyssCombatGrouping); Assert.Equal(AbyssCombatSort.KillsDescending, value.AbyssCombatSort);
        Assert.Equal(LootResourceSort.SuccessesDescending, value.LifeskillResourceSort);
        Assert.Equal(LootQuantitySort.QuantityDescending, value.PveLootSort); Assert.Equal(LootQuantitySort.QuantityDescending, value.PveMobSort);
        Assert.Equal(LootTimelineValueMode.Cumulative, value.LootTimelineValueMode);
        Assert.Equal(LootTrackerViewMode.Mixed, value.LootTrackerViewMode);
    }
    private static void WithSettings(Action action)
    {
        var previous = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try { action(); }
        finally
        {
            if (previous is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, previous);
            UiText.Language = language;
        }
    }
}
