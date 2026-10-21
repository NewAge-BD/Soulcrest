using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayVisibilityPolicyTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MigrationRetainsLayoutAndPreferencesAndEnablesAllBossContexts(bool followMinimap)
    {
        var old = new AppSettings
        {
            SettingsRevision = 4, EventTimelineFollowMinimap = followMinimap,
            TimerOverlayShowWorldMap = false, TimerOverlayShowMenu = true, TimerOverlayShowInstances = false,
            OverlayX = -1720, OverlayY = 235, OverlayScale = 1.2,
            BossOverlayVisible = false, BossOverlayEnabled = false, TimerOverlayTimerIds = [],
            OverlayHotkeys = new() { ["boss.toggle"] = new(0x42, 3) }
        };
        JsonFile.Save(AppPaths.SettingsFile, old);
        var service = new SettingsService();
        var current = service.Current;
        Assert.Equal(5, current.SettingsRevision);
        Assert.Equal(new(), OverlayVisibilityPolicy.Get(current, "boss"));
        Assert.Equal(new(), OverlayVisibilityPolicy.Get(current, "notifications"));
        Assert.Equal(new(false, false, false, false), OverlayVisibilityPolicy.Get(current, "loot"));
        Assert.Equal(new(false, false, false, false), OverlayVisibilityPolicy.Get(current, "leveling"));
        Assert.Equal(new(true, !followMinimap, true, !followMinimap), OverlayVisibilityPolicy.Get(current, "event-timeline"));
        Assert.Equal(new(false, true, false, true), OverlayVisibilityPolicy.Get(current, "alerts"));
        Assert.Equal((-1720, 235, 1.2), (current.OverlayX, current.OverlayY, current.OverlayScale));
        Assert.False(current.BossOverlayVisible);
        Assert.False(current.BossOverlayEnabled);
        Assert.Empty(current.TimerOverlayTimerIds!);
        Assert.Equal(new OverlayHotkey(0x42, 3), current.OverlayHotkeys["boss.toggle"]);
        service.Update(_ => { });
        Assert.Equal(JsonSerializer.Serialize(current.OverlayVisibility),
            JsonSerializer.Serialize(new SettingsService().Current.OverlayVisibility));
    }

    [Fact]
    public void PartialNewPoliciesPreserveExplicitFalseValuesAndFillMissingEntries()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 5,
            OverlayVisibility = new() { ["boss"] = new(false, false, false, false) }
        });
        var current = new SettingsService().Current;
        Assert.Equal(new(false, false, false, false), current.OverlayVisibility["boss"]);
        Assert.Equal(6, current.OverlayVisibility.Count);
        Assert.Equal(new(), current.OverlayVisibility["notifications"]);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void StoppedTrackingIgnoresAllStaleContextFlags(bool found, bool world, bool menu, bool instance)
    {
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(false, false, false, false),
            new(false, found, world, menu, instance)));
    }

    [Theory]
    [InlineData("boss")]
    [InlineData("notifications")]
    public void BossAndBannerAreIndependentlyVisibleInEveryContextByDefault(string id)
    {
        var settings = new AppSettings();
        var before = JsonSerializer.Serialize(settings);
        for (var bits = 0; bits < 16; bits++)
            Assert.False(OverlayVisibilityPolicy.IsSuppressed(settings, id,
                new(true, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0)));
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void InstanceWithoutMinimapUsesItsOwnPermissionAndOptionalMissingMinimapRestriction()
    {
        var context = new OverlayContext(true, false, false, false, true);
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(true, false, true, true), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, false, true, false), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, false, false, true), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, false, true, true), context with { EscMenuOpen = true }));
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(true, true, true, true), context with { EscMenuOpen = true }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuspendedVisualObservationUsesOnlyTheConfirmedInstancePermission(bool trackingRunning)
    {
        var allowInstanceOnly = new OverlayVisibilityRules(false, false, true, false);
        for (var flags = 0; flags < 8; flags++)
        {
            var context = new OverlayContext(trackingRunning, (flags & 1) != 0, (flags & 2) != 0,
                (flags & 4) != 0, true, true) { VisualObservationSuspended = true };
            Assert.False(OverlayVisibilityPolicy.IsSuppressed(allowInstanceOnly, context));
            Assert.True(OverlayVisibilityPolicy.IsSuppressed(allowInstanceOnly with { Instances = false }, context));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void UnobservedVisualFlagsCannotInventAnInstanceOrMenu(bool? networkInstance)
    {
        var context = new OverlayContext(true, false, true, true, true, networkInstance)
            { VisualObservationSuspended = true };
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(false, false, false, false), context));
    }

    [Fact]
    public void WorldMapAndMenuAndInstanceRequireEachOfTheirPermissions()
    {
        var context = new OverlayContext(true, false, true, true, true);
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(true, true, true, false), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(false, true, true, true), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, false, true, true), context));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, true, false, true), context));
    }

    [Fact]
    public void MissingMinimapOutsideAnInstanceIsAlsoAMenuAndDoesNotWritePreferences()
    {
        var rules = new OverlayVisibilityRules(true, false, true, true);
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(rules, new(true, false, false, false, false)));
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(rules, new(true, true, false, false, false)));
        Assert.True(OverlayVisibilityPolicy.IsSuppressed(rules with { Menu = true, MissingMinimap = false },
            new(true, false, false, false, false)));
    }

    [Theory]
    [InlineData("foreground", false, true)]
    [InlineData("foreground", true, false)]
    [InlineData("always", false, false)]
    [InlineData("always", true, false)]
    public void SharedForegroundPreferenceIsIndependentOfMapTracking(string mode, bool foreground, bool suppressed)
    {
        var settings = new AppSettings { OverlayShowMode = mode };
        Assert.Equal(suppressed, OverlayVisibilityPolicy.IsForegroundSuppressed(settings, foreground));
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(settings, "boss", new(false, false, false, false, false)));
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(settings, "notifications", new(true, false, true, true, true)));
    }

    [Theory]
    [InlineData("{}", "foreground")]
    [InlineData("{\"OverlayShowMode\":null}", "foreground")]
    [InlineData("{\"OverlayShowMode\":\"invalid\"}", "foreground")]
    [InlineData("{\"OverlayShowMode\":\"always\"}", "always")]
    public void OldAndInvalidSettingsDefaultToForegroundWhileAlwaysSurvivesRestart(string json, string expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        var settings = new SettingsService();
        Assert.Equal(expected, settings.Current.OverlayShowMode);
        settings.Update(_ => { });
        Assert.Equal(expected, new SettingsService().Current.OverlayShowMode);
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
