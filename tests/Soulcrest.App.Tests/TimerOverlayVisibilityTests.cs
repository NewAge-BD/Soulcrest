using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class TimerOverlayVisibilityTests : IDisposable
{
    private readonly byte[]? _settingsBackup;
    private readonly string _language = UiText.Language;

    public TimerOverlayVisibilityTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    }

    [Fact]
    public void MissingContextChoicesKeepWorldMapEnabledAndMenuAndInstancesDisabled()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(settings.TimerOverlayShowWorldMap);
        Assert.False(settings.TimerOverlayShowMenu);
        Assert.False(settings.TimerOverlayShowInstances);
        Assert.False(Suppressed(settings, found: true));
        Assert.False(Suppressed(settings, worldMap: true));
        Assert.True(Suppressed(settings));
        Assert.True(Suppressed(settings, found: true, menu: true));
        Assert.True(Suppressed(settings, instance: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryCombinationAllowsOnlyItsChosenContextsAndKeepsOpenWorldVisible(int flags)
    {
        var settings = new AppSettings
        {
            TimerOverlayShowWorldMap = (flags & 1) != 0,
            TimerOverlayShowMenu = (flags & 2) != 0,
            TimerOverlayShowInstances = (flags & 4) != 0
        };
        var before = JsonSerializer.Serialize(settings);
        Assert.False(Suppressed(settings, found: true));
        Assert.Equal(!settings.TimerOverlayShowWorldMap, Suppressed(settings, worldMap: true));
        Assert.Equal(!settings.TimerOverlayShowMenu, Suppressed(settings));
        Assert.Equal(!settings.TimerOverlayShowMenu, Suppressed(settings, found: true, menu: true));
        // An inferred instance must not also be treated as a generic menu just because it has no minimap.
        Assert.Equal(!settings.TimerOverlayShowInstances, Suppressed(settings, instance: true));
        Assert.Equal(!settings.TimerOverlayShowMenu || !settings.TimerOverlayShowInstances,
            Suppressed(settings, menu: true, instance: true));
        Assert.Equal(!settings.TimerOverlayShowWorldMap || !settings.TimerOverlayShowInstances,
            Suppressed(settings, worldMap: true, instance: true));
        Assert.Equal(!settings.TimerOverlayShowWorldMap || !settings.TimerOverlayShowMenu,
            Suppressed(settings, worldMap: true, menu: true));
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoppedTrackingDoesNotSuppressTheTimerForUnreliableOrStaleContexts(bool found)
    {
        var settings = new AppSettings
        {
            TimerOverlayShowWorldMap = false, TimerOverlayShowMenu = false, TimerOverlayShowInstances = false
        };
        Assert.False(Suppressed(settings, false, found, true, true, true));
        Assert.False(Suppressed(settings, false, found, false, false, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public void LoadingSettingsRemovesTheHiddenLegacyTimeWindowAndPreservesExplicitContextChoices(int revision)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = revision,
            TimerOverlayLeadMinutes = 17,
            TimerOverlayShowWorldMap = false, TimerOverlayShowMenu = true, TimerOverlayShowInstances = true,
            TimerOverlayTimerIds = ["event:catalog:shugo"],
            AlertsOverlayScale = 1.4, AlertsOverlayX = 123, AlertsOverlayY = 234,
            BossAlertsEnabled = false, EventTimelineOverlayEnabled = true
        });
        var service = new SettingsService();
        var settings = service.Current;
        Assert.Equal(0, settings.TimerOverlayLeadMinutes);
        Assert.False(settings.TimerOverlayShowWorldMap);
        Assert.True(settings.TimerOverlayShowMenu);
        Assert.True(settings.TimerOverlayShowInstances);
        Assert.Equal(new[] { "event:catalog:shugo" }, settings.TimerOverlayTimerIds);
        Assert.Equal((1.4, 123, 234), (settings.AlertsOverlayScale, settings.AlertsOverlayX, settings.AlertsOverlayY));
        Assert.False(settings.BossAlertsEnabled);
        Assert.True(settings.EventTimelineOverlayEnabled);
        service.Update(_ => { });
        Assert.Equal(0, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile))!.TimerOverlayLeadMinutes);
        var restored = new SettingsService().Current;
        Assert.Equal((false, true, true), (restored.TimerOverlayShowWorldMap, restored.TimerOverlayShowMenu, restored.TimerOverlayShowInstances));
    }

    [Fact]
    public void PositiveContextChoicesPersistWithoutEnablingTheOverlayOrChangingOtherPresentationSettings()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 4, AlertsOverlayEnabled = false,
            AlertNotificationsWidth = 516, AlertNotificationsScale = 1.25,
            EventTimelineOverlayWidth = 999, EventTimelineFollowMinimap = false,
            TimerOverlayTimerIds = []
        });
        var service = new SettingsService();
        service.Update(settings =>
        {
            settings.TimerOverlayShowWorldMap = false;
            settings.TimerOverlayShowMenu = true;
            settings.TimerOverlayShowInstances = true;
        });
        var restored = new SettingsService().Current;
        Assert.Equal((false, true, true), (restored.TimerOverlayShowWorldMap, restored.TimerOverlayShowMenu, restored.TimerOverlayShowInstances));
        Assert.False(restored.AlertsOverlayEnabled);
        Assert.Equal((516, 1.25, 999, false), (restored.AlertNotificationsWidth, restored.AlertNotificationsScale,
            restored.EventTimelineOverlayWidth, restored.EventTimelineFollowMinimap));
        Assert.NotNull(restored.TimerOverlayTimerIds);
        Assert.Empty(restored.TimerOverlayTimerIds!);
    }

    private static bool Suppressed(AppSettings settings, bool found = false, bool worldMap = false, bool menu = false, bool instance = false) =>
        Suppressed(settings, true, found, worldMap, menu, instance);

    // The timer strip is the "alerts" panel of the shared overlay visibility policy.
    private static bool Suppressed(AppSettings settings, bool trackingRunning, bool found, bool worldMap, bool menu, bool instance) =>
        OverlayVisibilityPolicy.IsSuppressed(settings, "alerts", new(trackingRunning, found, worldMap, menu, instance));

    public void Dispose()
    {
        if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        UiText.Language = _language;
    }
}
