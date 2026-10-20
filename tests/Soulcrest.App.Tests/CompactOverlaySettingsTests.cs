using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CompactOverlaySettingsTests : IDisposable
{
    private readonly byte[]? _saved = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    public void Dispose()
    {
        if (_saved is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _saved);
        UiText.Language = _language;
    }

    [Fact]
    public void FreshSettingsUseCompactIndependentDefaults()
    {
        File.Delete(AppPaths.SettingsFile);
        var current = new SettingsService().Current;
        Assert.Equal(5, current.SettingsRevision);
        Assert.Equal((104, 36), (current.AlertsOverlayX, current.AlertsOverlayY));
        Assert.Equal(340, current.AlertsOverlayWidth);
        Assert.Equal(720, current.EventTimelineOverlayWidth);
        Assert.Equal(340, current.AlertNotificationsWidth);
        Assert.Equal(1, current.AlertsOverlayScale);
        Assert.Equal(1, current.EventTimelineOverlayScale);
        Assert.Equal(1, current.AlertNotificationsScale);
        Assert.Equal(420, current.AlertNotificationsX);
        Assert.Equal(520, current.AlertNotificationsY);
        Assert.True(current.MapTrackingActive);
        Assert.True(current.FollowPlayer);
        Assert.Empty(current.BossAlertIds);
        Assert.Empty(current.BossReminderOverrides);
    }

    [Fact]
    public void OldProfileInheritsItsHudPlacementAndScaleOnlyOnce()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 3, AlertsOverlayX = -900, AlertsOverlayY = 175,
            AlertsOverlayWidth = 865, AlertsOverlayScale = 1.35,
            MapTrackingActive = false, FollowPlayer = false, BossAlertsEnabled = false,
            BossAlertIds = ["fixture-boss"],
            BossReminderOverrides = new() { ["fixture-boss"] = new() { Enabled = false, LeadSeconds = 300 } }
        });
        var settings = new SettingsService();
        Assert.Equal(5, settings.Current.SettingsRevision);
        Assert.Equal(-900, settings.Current.AlertNotificationsX);
        Assert.Equal(175, settings.Current.AlertNotificationsY);
        Assert.Equal(865, settings.Current.AlertNotificationsWidth);
        Assert.Equal(1.35, settings.Current.AlertNotificationsScale);
        settings.Update(current =>
        {
            current.AlertsOverlayX = 100;
            current.AlertsOverlayY = 200;
            current.AlertsOverlayWidth = 450;
            current.AlertsOverlayScale = .8;
        });
        var reloaded = new SettingsService().Current;
        Assert.Equal(-900, reloaded.AlertNotificationsX);
        Assert.Equal(175, reloaded.AlertNotificationsY);
        Assert.Equal(865, reloaded.AlertNotificationsWidth);
        Assert.Equal(1.35, reloaded.AlertNotificationsScale);
        Assert.False(reloaded.MapTrackingActive);
        Assert.False(reloaded.FollowPlayer);
        Assert.False(reloaded.BossAlertsEnabled);
        Assert.Equal(["fixture-boss"], reloaded.BossAlertIds);
        Assert.Equal(new BossReminderPreference { Enabled = false, LeadSeconds = 300 },
            Assert.Single(reloaded.BossReminderOverrides).Value);
    }

    [Theory]
    [InlineData("1e309", 1)]
    [InlineData("0.1", .6)]
    [InlineData("3", 2)]
    public void OldProfileInheritsTheNormalizedHudScale(string value, double expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, $$"""
            {"SettingsRevision":3,"AlertsOverlayX":110,"AlertsOverlayY":220,"AlertsOverlayScale":{{value}}}
            """);
        var current = new SettingsService().Current;
        Assert.Equal(expected, current.AlertsOverlayScale);
        Assert.Equal(expected, current.AlertNotificationsScale);
        Assert.Equal(340, current.AlertNotificationsWidth);
        Assert.Equal(110, current.AlertNotificationsX);
        Assert.Equal(220, current.AlertNotificationsY);
    }

    [Fact]
    public void IndependentSizesAndPositionsSurviveSavingAndRestart()
    {
        File.Delete(AppPaths.SettingsFile);
        var settings = new SettingsService();
        settings.Update(current =>
        {
            current.AlertsOverlayX = 110;
            current.AlertsOverlayY = 120;
            current.AlertsOverlayWidth = 480;
            current.AlertsOverlayScale = 1.1;
            current.AlertNotificationsX = -600;
            current.AlertNotificationsY = 260;
            current.AlertNotificationsWidth = 300;
            current.AlertNotificationsScale = .75;
            current.EventTimelineOverlayX = 40;
            current.EventTimelineOverlayY = 700;
            current.EventTimelineOverlayWidth = 1300;
            current.EventTimelineOverlayScale = 1.4;
            current.EventTimelineHours = 48;
            current.EventTimelineFollowMinimap = false;
        });
        var reloaded = new SettingsService().Current;
        Assert.Equal((110, 120, 480, 1.1), (reloaded.AlertsOverlayX, reloaded.AlertsOverlayY, reloaded.AlertsOverlayWidth, reloaded.AlertsOverlayScale));
        Assert.Equal((-600, 260, 300, .75), (reloaded.AlertNotificationsX, reloaded.AlertNotificationsY, reloaded.AlertNotificationsWidth, reloaded.AlertNotificationsScale));
        Assert.Equal((40, 700, 1300, 1.4), (reloaded.EventTimelineOverlayX, reloaded.EventTimelineOverlayY, reloaded.EventTimelineOverlayWidth, reloaded.EventTimelineOverlayScale));
        Assert.Equal(48, reloaded.EventTimelineHours);
        Assert.False(reloaded.EventTimelineFollowMinimap);
    }

    [Theory]
    [InlineData(-1, -1, -1, 240, 360, 240)]
    [InlineData(2000, 2000, 2000, 1000, 1600, 1000)]
    [InlineData(500, 900, 650, 500, 900, 650)]
    public void LoadedWidthsStayWithinTheirRespectiveBounds(int hud, int timeline, int banner,
        int expectedHud, int expectedTimeline, int expectedBanner)
    {
        File.WriteAllText(AppPaths.SettingsFile, $$"""
            {"SettingsRevision":4,"AlertsOverlayWidth":{{hud}},"EventTimelineOverlayWidth":{{timeline}},"AlertNotificationsWidth":{{banner}}}
            """);
        var current = new SettingsService().Current;
        Assert.Equal(expectedHud, current.AlertsOverlayWidth);
        Assert.Equal(expectedTimeline, current.EventTimelineOverlayWidth);
        Assert.Equal(expectedBanner, current.AlertNotificationsWidth);
    }

    [Theory]
    [InlineData("1e309", 1)]
    [InlineData("-1e309", 1)]
    [InlineData("0.1", .6)]
    [InlineData("3", 2)]
    [InlineData("1.25", 1.25)]
    public void LoadedScalesAreFiniteAndClampedForAllThreeOverlays(string value, double expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, $$"""
            {"SettingsRevision":4,"AlertsOverlayScale":{{value}},"EventTimelineOverlayScale":{{value}},"AlertNotificationsScale":{{value}}}
            """);
        var current = new SettingsService().Current;
        Assert.Equal(expected, current.AlertsOverlayScale);
        Assert.Equal(expected, current.EventTimelineOverlayScale);
        Assert.Equal(expected, current.AlertNotificationsScale);
    }
}
