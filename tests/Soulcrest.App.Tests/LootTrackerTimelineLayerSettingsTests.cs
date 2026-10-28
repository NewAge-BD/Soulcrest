using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerTimelineLayerSettingsTests
{
    [Fact]
    public void DefaultsEnableEveryLayerAndEachViewPersistsItsOwnSelectionIncludingEmpty()
    {
        using var fixture = new Fixture(); var settings = new SettingsService();
        foreach (var view in Enum.GetValues<LootTrackerViewMode>())
            Assert.Equal(LootTrackerTimelineLayers.All.Count, settings.GetLootTimelineLayers(view).Length);
        settings.SetLootTimelineLayers(LootTrackerViewMode.Mixed, ["rp", "ap", "rp", "invalid"]);
        settings.SetLootTimelineLayers(LootTrackerViewMode.Lifeskill, []);
        var restarted = new SettingsService();
        Assert.Equal(["ap", "rp"], restarted.GetLootTimelineLayers(LootTrackerViewMode.Mixed));
        Assert.Empty(restarted.GetLootTimelineLayers(LootTrackerViewMode.Lifeskill));
        Assert.Equal(LootTrackerTimelineLayers.All.Count, restarted.GetLootTimelineLayers(LootTrackerViewMode.Abyss).Length);
        var copy = restarted.GetLootTimelineLayers(LootTrackerViewMode.Mixed); copy[0] = "mutated";
        Assert.Equal("ap", restarted.GetLootTimelineLayers(LootTrackerViewMode.Mixed)[0]);
        Assert.Equal(AbyssPointRatePeriod.Hour, restarted.Current.AbyssPointRatePeriod);
    }

    [Fact]
    public void SaveFailureRestoresDictionaryAndDoesNotNotifyOrTouchAnotherView()
    {
        using var fixture = new Fixture(); var settings = new SettingsService();
        settings.SetLootTimelineLayers(LootTrackerViewMode.Mixed, ["ap"]);
        var prior = settings.Current.LootTimelineLayersByView; var changes = 0; settings.Changed += () => changes++;
        var bytes = File.ReadAllBytes(AppPaths.SettingsFile);
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            Assert.ThrowsAny<Exception>(() => settings.SetLootTimelineLayers(LootTrackerViewMode.Abyss, []));
            Assert.Same(prior, settings.Current.LootTimelineLayersByView);
            Assert.Equal(["ap"], settings.GetLootTimelineLayers(LootTrackerViewMode.Mixed));
            Assert.Equal(LootTrackerTimelineLayers.All.Count, settings.GetLootTimelineLayers(LootTrackerViewMode.Abyss).Length);
            Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); File.WriteAllBytes(AppPaths.SettingsFile, bytes); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        public Fixture() => JsonFile.Save(AppPaths.SettingsFile, new AppSettings());
        public void Dispose()
        { if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup); }
    }
}
