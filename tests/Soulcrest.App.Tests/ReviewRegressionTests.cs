using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public void ParallelSettingsUpdatesPersistEveryChange()
    {
        var settings = new SettingsService();
        settings.Update(s => s.OverlayX = 0);
        Parallel.For(0, 100, _ => settings.Update(s => s.OverlayX++));
        Assert.Equal(100, settings.Current.OverlayX);
        Assert.Equal(100, new SettingsService().Current.OverlayX);
        Assert.Empty(Directory.GetFiles(AppPaths.DataDirectory, "settings.json.*.tmp"));
    }

    [Fact]
    public void SnapshotDoesNotShareVotesOrPortraitBytes()
    {
        var entry = new ScanEntry { Key = "test", ThumbnailDataUrl = "", PortraitPng = [1, 2] };
        entry.Votes[(1, 2)] = 1;
        var copy = entry.Snapshot();
        entry.Votes[(2, 3)] = 2;
        entry.PortraitPng[0] = 9;
        copy.Apply = false;
        Assert.Equal((1, 2), copy.Reading);
        Assert.Equal(1, copy.PortraitPng[0]);
        Assert.True(entry.Apply);
    }

    [Fact]
    public void BatchLevelsPersistsAllPetsWithOneNotification()
    {
        var progress = new ProgressService();
        var changes = 0;
        progress.Changed += () => changes++;
        progress.SetLevels([("review-a", 1, 2), ("review-b", 2, 3)]);
        Assert.Equal(1, changes);
        var reloaded = new ProgressService();
        Assert.Equal(progress.Thresholds.Total(1, 2), reloaded.Souls("review-a"));
        Assert.Equal(progress.Thresholds.Total(2, 3), reloaded.Souls("review-b"));
        progress.SetLevels([]);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void NetworkPetWithoutMapEntryIsVisibleAndPersistent()
    {
        var progress = new ProgressService();
        var source = new PetIdEntry(999999, "Review Network Pet", "Fera", null);
        var id = progress.EnsureNetworkPet(source);
        progress.AddSouls(id, 2, "test", "test");
        Assert.Equal(id, progress.EnsureNetworkPet(source));
        var reloaded = new ProgressService();
        Assert.Single(reloaded.Catalog.Pets, p => p.Id == id);
        Assert.Equal(source.En, reloaded.Catalog.Find(id)!.En);
        Assert.Equal(2, reloaded.Souls(id));
    }

    [Fact]
    public void ReconnectWithNewLocalPortRequiresNewCaptureTarget()
    {
        var original = new CaptureTarget("device", "filter", new HashSet<int> { 80, 81 }, new HashSet<int> { 123 }, "old");
        Assert.False(NetworkLootService.SameConnection(original, original with { GamePorts = new HashSet<int> { 124 } }));
        Assert.False(NetworkLootService.SameConnection(original, original with { ServerPorts = new HashSet<int> { 82 } }));
        Assert.True(NetworkLootService.SameConnection(original, original with { ServerPorts = new HashSet<int> { 81, 80 }, Description = "new" }));
    }
}
