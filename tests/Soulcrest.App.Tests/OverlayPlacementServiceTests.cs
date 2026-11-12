using System.Drawing;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayPlacementServiceTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;
    private static readonly OverlayMonitor Left = new("left", "Left", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040));
    private static readonly OverlayMonitor Right = new("right", "Right", new(0, 0, 2560, 1440), new(0, 0, 2560, 1400));

    [Fact]
    public void StartStopAndViewOptionsNeverSaveOrRelocateLayouts()
    {
        var settings = Fresh();
        settings.Update(s => { s.OverlayX = 8000; s.OverlayY = -2400; s.OverlayEnabled = false; });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        using var placement = new OverlayPlacementService(settings, () => [Left, Right], () => Left.Bounds);
        Assert.True(placement.Start());
        Assert.True(placement.Active);
        Assert.Equal(Left, placement.Monitor);
        placement.GridVisible = false;
        placement.SnapToGrid = false;
        placement.SelectedOverlayId = "boss";
        placement.Stop();
        Assert.False(placement.Active);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.False(settings.Current.OverlayEnabled);
    }

    [Fact]
    public void AScopedRunOnlyOffersItsFamiliesAndTheNextDefaultRunRestoresTheFullInventoryWithoutSaving()
    {
        var settings = Fresh();
        settings.Update(current =>
        {
            current.LootOverlayEnabled = true;
            current.AbyssRankOverlayEnabled = true;
            current.LootOverlayX = 812;
            current.AbyssRankOverlayY = 427;
        });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        using var placement = new OverlayPlacementService(settings, () => [Left], () => null);
        placement.SelectedOverlayId = "boss";
        string[] families = ["loot", "boss", "leveling", "alerts", "notifications", "event-timeline", "map", "merchant", "daevanion"];
        Assert.True(placement.Start(overlayIds: families));
        Assert.Equal(families, placement.Overlays.Select(overlay => overlay.Id));
        Assert.Equal(6, placement.PlaceableOverlays.Count());
        Assert.DoesNotContain(placement.PlaceableOverlays, overlay => overlay.Id is "farm-loot" or "abyss-rank");
        Assert.Equal("boss", placement.SelectedOverlayId);
        families[0] = "farm-loot"; // callers cannot change an active run's inventory after Start
        Assert.Equal("loot", placement.Overlays[0].Id);
        Assert.Throws<ArgumentException>(() => placement.SelectedOverlayId = "farm-loot");
        Assert.Equal("boss", placement.SelectedOverlayId);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        placement.Stop();
        Assert.Same(OverlayRegistry.All, placement.Overlays);
        Assert.True(placement.Start());
        Assert.Equal(8, placement.PlaceableOverlays.Count());
        Assert.Contains(placement.PlaceableOverlays, overlay => overlay.Id == "achievements");
        Assert.Contains(placement.PlaceableOverlays, overlay => overlay.Id == "genus-insight");
        Assert.DoesNotContain(placement.PlaceableOverlays, overlay => overlay.Id is "farm-loot" or "abyss-rank");
        placement.Stop();
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.True(settings.Current.LootOverlayEnabled);
        Assert.True(settings.Current.AbyssRankOverlayEnabled);
        Assert.Equal(812, settings.Current.LootOverlayX);
        Assert.Equal(427, settings.Current.AbyssRankOverlayY);
    }

    [Fact]
    public void AutoMonitorPrefersLargestGameIntersectionThenOwnerWithoutGame()
    {
        var settings = Fresh();
        Rectangle? game = new(-100, 20, 1200, 800);
        using var placement = new OverlayPlacementService(settings, () => [Right, Left], () => game);
        placement.RefreshMonitors(Left.Bounds);
        Assert.Equal("right", placement.PreferredMonitorId);
        Assert.True(placement.Start());
        Assert.Equal(Right, placement.Monitor);
        placement.Stop();
        game = null;
        Assert.True(placement.Start());
        Assert.Equal(Left, placement.Monitor);
        placement.Stop();
        Assert.True(placement.Start("right"));
        Assert.Equal(Right, placement.Monitor);
    }

    [Fact]
    public void CompletedGestureWritesOnlyItsLayoutAndRetainsItAfterStopAndReload()
    {
        var settings = Fresh();
        settings.Update(s => { s.OverlayEnabled = false; s.BossAlertIds = ["fixture-boss"]; s.TimerOverlayTimerIds = []; });
        var boss = OverlayRegistry.ReadLayout(settings.Current, "boss");
        using var placement = new OverlayPlacementService(settings, () => [Left], () => null);
        var events = 0;
        placement.Changed += () => events++;
        placement.UpdateLayout("notifications", new(-1500, 210, 1.25, 620));
        Assert.Equal(1, events);
        placement.UpdateLayout("notifications", new(-1500, 210, 1.25, 620));
        Assert.Equal(1, events); // unchanged pointer completion does not write again
        placement.Start();
        placement.Stop();
        var loaded = new SettingsService().Current;
        Assert.Equal(new(-1500, 210, 1.25, 620), OverlayRegistry.ReadLayout(loaded, "notifications"));
        Assert.Equal(boss, OverlayRegistry.ReadLayout(loaded, "boss"));
        Assert.False(loaded.OverlayEnabled);
        Assert.Equal(["fixture-boss"], loaded.BossAlertIds);
        Assert.Empty(loaded.TimerOverlayTimerIds!);
    }

    [Fact]
    public void MonitorRemovalEndsModeWithoutRevertingCompletedChanges()
    {
        var settings = Fresh();
        IReadOnlyList<OverlayMonitor> screens = [Left, Right];
        using var placement = new OverlayPlacementService(settings, () => screens, () => null);
        Assert.True(placement.Start("left"));
        placement.UpdateLayout("loot", new(-1800, 64, 1.2));
        screens = [Right];
        placement.RefreshMonitors();
        Assert.False(placement.Active);
        Assert.Null(placement.Monitor);
        Assert.NotNull(placement.Error);
        Assert.Equal(new(-1800, 64, 1.2), OverlayRegistry.ReadLayout(new SettingsService().Current, "loot"));
        Assert.False(placement.Start("missing"));
        Assert.NotNull(placement.Error);
    }

    [Theory]
    [InlineData("loot", .6, 2.5)]
    [InlineData("genus-insight", .6, 2)]
    [InlineData("boss", .6, 2)]
    [InlineData("leveling", .7, 1.6)]
    [InlineData("alerts", .6, 2)]
    [InlineData("notifications", .6, 2)]
    [InlineData("event-timeline", .6, 2)]
    public void LayoutAdaptersRespectExistingScaleLimits(string id, double minimum, double maximum)
    {
        Assert.Equal(minimum, OverlayRegistry.NormalizeLayout(id, new(0, 0, -100)).Scale);
        Assert.Equal(maximum, OverlayRegistry.NormalizeLayout(id, new(0, 0, 100)).Scale);
        Assert.Equal(1, OverlayRegistry.NormalizeLayout(id, new(0, 0, double.NaN)).Scale);
    }

    [Fact]
    public void AnchoredOverlaysRejectPlacementAndWidthsRemainIndependent()
    {
        Assert.Throws<ArgumentException>(() => OverlayRegistry.ReadLayout(new(), "map"));
        Assert.Throws<ArgumentException>(() => OverlayRegistry.NormalizeLayout("merchant", new(0, 0, 1)));
        Assert.Equal(240, OverlayRegistry.NormalizeLayout("notifications", new(0, 0, 1, 10)).Width);
        Assert.Equal(1600, OverlayRegistry.NormalizeLayout("event-timeline", new(0, 0, 1, 9999)).Width);
        Assert.Equal(11, OverlayRegistry.All.Count);
        Assert.Equal(8, OverlayRegistry.All.Count(item => item.CanPlace));
    }

    [Theory]
    [InlineData("farm-loot")]
    [InlineData("abyss-rank")]
    public void RetiredNativeHudsCannotBeSelectedEnabledOrPlaced(string id)
    {
        var settings = Fresh();
        using var placement = new OverlayPlacementService(settings, () => [Left], () => null);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        Assert.Throws<ArgumentException>(() => OverlayRegistry.Get(id));
        Assert.Throws<ArgumentException>(() => OverlayRegistry.ReadLayout(settings.Current, id));
        Assert.Throws<ArgumentException>(() => OverlayRegistry.SetEnabled(settings.Current, id, true));
        Assert.Throws<ArgumentException>(() => placement.SelectedOverlayId = id);
        Assert.Throws<ArgumentException>(() => placement.UpdateLayout(id, new(0, 0, 1)));
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    private static SettingsService Fresh()
    {
        File.Delete(AppPaths.SettingsFile);
        var settings = new SettingsService();
        settings.Update(_ => { });
        return settings;
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
