using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossSchedulePersistenceTests
{
    [Fact]
    public async Task SwitchingMapsRetainsOtherCountdownsAndWarnsWithoutChangingCurrentMapNavigation()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1010:101001"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 300000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 900000));
        var first = fixture.Bosses.Schedule.ToDictionary(row => row.Place.Key);
        Assert.True(first["1110:111001"].Cached);
        Assert.False(first["1010:101001"].Cached);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        Assert.Equal("verteron", Assert.Single(fixture.Targets.BossNavigationTargets).MapId);
        Assert.Equal("verteron", Assert.Single(fixture.Bosses.OverlaySpawns).Place.Map);
        using var alerts = fixture.CreateAlerts();
        fixture.Clock.Advance(TimeSpan.FromSeconds(250));
        fixture.Refresh();
        var rows = alerts.TimerOverlayRows.ToDictionary(row => row.Key);
        Assert.True(rows["boss:1110:111001"].Provisional);
        Assert.False(rows["boss:1010:101001"].Provisional);
        Assert.Equal(TimeSpan.FromSeconds(50), rows["boss:1110:111001"].At - fixture.Clock.GetUtcNow());
        var warning = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal("1110:111001", warning.Key);
        Assert.True(warning.Cached);
        Assert.False(warning.Spawned);
        Assert.Equal(rows["boss:1110:111001"].At, Assert.Single(alerts.NotificationRows).At);
        fixture.Clock.Advance(TimeSpan.FromSeconds(700));
        fixture.Refresh();
        Assert.All(fixture.Bosses.Schedule, row => { Assert.False(row.Spawned); Assert.Equal(TimeSpan.Zero, row.Remaining); });
        Assert.Empty(fixture.Bosses.Alerts);
    }

    [Fact]
    public async Task RestartAfterSeveralDaysKeepsAllCapturedMapsProvisionalAndDoesNotConfirmElapsedSpawns()
    {
        await using var fixture = new Fixture();
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 60000));
        fixture.Observe(1010, new BossSpawn(101001, true, fixture.UnixNow - 1000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Clock.Advance(TimeSpan.FromDays(3));
        fixture.Restart();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1010:101001"];
        fixture.Refresh();
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.All(fixture.Bosses.Schedule, row => { Assert.True(row.Cached); Assert.True(row.Age >= TimeSpan.FromDays(3)); });
        var altgard = Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "altgard");
        Assert.False(altgard.Spawned);
        Assert.Equal(TimeSpan.Zero, altgard.Remaining);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        Assert.True(fixture.Bosses.View.Cached);
        Assert.Empty(fixture.Targets.BossNavigationTargets); // A days-old spawn never starts a new navigation minute.
        Assert.Empty(fixture.Bosses.Alerts);
        using var alerts = fixture.CreateAlerts();
        Assert.All(alerts.TimerOverlayRows, row => { Assert.True(row.Provisional); Assert.Equal("?", TimerOverlayPresentation.Countdown(row, fixture.Clock.GetUtcNow())); });
        Assert.Equal(2, alerts.TimerOverlayRows.Count);
        Assert.DoesNotContain(alerts.NotificationRows, row => row.Key.StartsWith("boss:", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath));
    }

    [Fact]
    public async Task RetainedMapsUseMonotonicTimeAndAConfirmedListReplacesOnlyItsOwnMap()
    {
        await using var fixture = new Fixture();
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Clock.ShiftWall(TimeSpan.FromDays(-2));
        Assert.Equal(TimeSpan.FromSeconds(590), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "altgard").Remaining);
        Assert.Equal(TimeSpan.FromSeconds(1190), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "verteron").Remaining);
        fixture.Clock.ShiftWall(TimeSpan.FromDays(2));
        fixture.Observe(1110, new BossSpawn(111002, false, fixture.UnixNow + 1800000));
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.DoesNotContain(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111001");
        Assert.False(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111002").Cached);
        Assert.True(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1010:101001").Cached);
        fixture.Network.Stop();
        Assert.All(fixture.Bosses.Schedule, row => Assert.True(row.Cached));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1795), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111002").Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownFullListCannotExposeKnownPartsOrReviveItsOldMapCache(bool unknownTime)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow),
            new BossSpawn(999999, !unknownTime, unknownTime ? null : fixture.UnixNow));
        var only = Assert.Single(fixture.Bosses.Schedule);
        Assert.Equal("verteron", only.Place.Map);
        Assert.True(only.Cached);
        Assert.Null(fixture.Bosses.View.Boss);
        Assert.Empty(fixture.Bosses.OverlaySpawns);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath));
    }

    [Fact]
    public async Task MixedMissingTimesSurviveRestartAndCannotChooseUnknownTargetsOrAlerts()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1110:111002"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000), new(111002, false, null));
        Assert.Equal("1110:111001", Assert.Single(fixture.Bosses.Schedule).Place.Key);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Restart();
        var restored = Assert.Single(fixture.Bosses.Schedule);
        Assert.True(restored.Cached);
        Assert.Equal("1110:111001", restored.Place.Key);
        Assert.Equal(TimeSpan.FromSeconds(590), restored.Remaining);
        fixture.Refresh();
        Assert.Equal("Synthetic boss one", Assert.Single(fixture.Targets.BossNavigationTargets).Name);
        Assert.Empty(fixture.Bosses.Alerts);
        using var alerts = fixture.CreateAlerts();
        Assert.Equal("boss:1110:111001", Assert.Single(alerts.TimerOverlayRows).Key);
        var fullList = new BossTimerCache(fixture.CachePath, fixture.Clock).Load();
        Assert.NotNull(fullList);
        Assert.Equal(2, fullList.Bosses.Count);
        Assert.Null(fullList.Bosses.Single(b => b.SpawnId == 111002).SpawnUnixMs);
        fixture.Observe(1110, new BossSpawn(111001, false, null), new(111002, false, null));
        Assert.Empty(fixture.Bosses.Schedule);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(alerts.TimerOverlayRows);
    }

    [Fact]
    public async Task UnknownSavedListsAreRejectedWholeAndRestoredMapsNeedTheirOwnFreshClockToBecomeLive()
    {
        await using var fixture = new Fixture();
        var cache = new BossTimerCache(fixture.CachePath, fixture.Clock);
        Assert.True(cache.Save(new(1110, [new BossSpawn(111001, false, fixture.UnixNow + 600000), new BossSpawn(999999, false, fixture.UnixNow + 600000)], fixture.UnixNow, TimeSpan.Zero)));
        Assert.True(cache.Save(new(1010, [new BossSpawn(101001, false, fixture.UnixNow + 1200000)], fixture.UnixNow, TimeSpan.Zero)));
        fixture.Restart();
        Assert.Equal("verteron", Assert.Single(fixture.Bosses.Schedule).Place.Map);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        var list = ListMessage(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        fixture.Bosses.OnMessage("fresh", list);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Bosses.OnMessage("other", [0, 0x36, .. BitConverter.GetBytes(fixture.UnixNow)]);
        fixture.Bosses.OnMessage("fresh", list);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Bosses.OnMessage("fresh", [0, 0x36, .. BitConverter.GetBytes(fixture.UnixNow)]);
        fixture.Bosses.OnMessage("fresh", list);
        Assert.False(Assert.Single(fixture.Bosses.Schedule).Cached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BossWithoutASingleVerifiedCoordinateRetainsItsTimerButNeverCreatesATarget(bool spawned)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111002"];
        fixture.Observe(1110, new BossSpawn(111002, spawned, fixture.UnixNow + 600000));
        Assert.Equal("1110:111002", Assert.Single(fixture.Bosses.Schedule).Place.Key);
        Assert.NotNull(fixture.Bosses.View.Boss);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        using var alerts = fixture.CreateAlerts();
        Assert.Equal("boss:1110:111002", Assert.Single(alerts.TimerOverlayRows).Key);
    }

    [Theory]
    [InlineData("de", "Auf Karte zeigen")]
    [InlineData("en", "Show on map")]
    public async Task AnUnlocatedNextBossDoesNotOfferToFocusALaterPreview(string language, string mapAction)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.UiLanguage = fixture.Settings.Current.NameLanguage = UiText.Language = language;
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111002, false, fixture.UnixNow + 60000),
            new BossSpawn(111003, false, fixture.UnixNow + 120000));
        Assert.Equal(111002, fixture.Bosses.View.Boss!.SpawnId);
        var preview = Assert.Single(fixture.Targets.BossNavigationTargets);
        Assert.Equal(111003, preview.BossSpawnId);
        Assert.Equal("preview", preview.BossRole);
        Assert.Equal("boss:1110:111002", preview.After);

        var html = System.Net.WebUtility.HtmlDecode(await fixture.RenderBossRush());

        Assert.Contains("<h3>Synthetic boss without single location</h3>", html);
        Assert.DoesNotContain(mapAction, html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BossRushTargetStatisticsRemainOnTheCurrentMapWhileTheListRetainsOtherMaps(string language)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.UiLanguage = fixture.Settings.Current.NameLanguage = UiText.Language = language;
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 1000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 600000));
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        var html = await fixture.RenderBossRush();
        Assert.Matches("boss-dashboard-stat spawned[\\s\\S]*?<dd>0</dd>", html);
        Assert.Matches("boss-dashboard-stat upcoming[\\s\\S]*?<dd>1</dd>", html);
        Assert.Contains("Synthetic boss one", html); // Historic map remains available in the shared timer list.
        Assert.Contains("Synthetic boss two", html);
        Assert.Equal("verteron", Assert.Single(fixture.Targets.BossNavigationTargets).MapId);
    }

    [Fact]
    public async Task EqualSpawnDeadlinesSortBySpawnIdAndNavigationIgnoresIndependentOverlayCount()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Settings.Current.BossNavigationPreviewCount = 1;
        fixture.Settings.Current.BossOverlayCount = 1;
        var at = fixture.UnixNow + 60000;
        fixture.Observe(1110, new BossSpawn(111004, false, at), new(111003, false, at), new(111001, false, at));
        Assert.Equal(new int?[] { 111001, 111003 }, fixture.Targets.BossNavigationTargets.Select(target => target.BossSpawnId));
        Assert.Equal("next", fixture.Targets.BossNavigationTargets[0].BossRole);
        Assert.Equal("boss:1110:111001", fixture.Targets.BossNavigationTargets[1].After);
        Assert.Single(fixture.Bosses.OverlaySpawns);
        fixture.Settings.Update(settings => settings.BossNavigationPreviewCount = 0);
        Assert.Equal(111001, Assert.Single(fixture.Targets.BossNavigationTargets).BossSpawnId);
        fixture.Settings.Update(settings => settings.BossOverlayCount = 12);
        Assert.Equal(3, fixture.Bosses.OverlaySpawns.Count);
        Assert.Single(fixture.Targets.BossNavigationTargets);
    }

    [Fact]
    public async Task NavigationFollowsOnlyTheCurrentMapAndTimerRefreshesDoNotPublishGeometryChanges()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111003, true, fixture.UnixNow - 1000), new(111001, false, fixture.UnixNow + 1000));
        var snapshot = fixture.Targets.BossNavigationTargets;
        Assert.Equal(2, snapshot.Count);
        var changes = 0;
        fixture.Targets.Changed += () => changes++;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Refresh();
        Assert.Equal(0, changes);
        Assert.Same(snapshot, fixture.Targets.BossNavigationTargets);
        Assert.Equal(TimeSpan.Zero, fixture.Bosses.Schedule.Single(row => row.Place.Boss.SpawnId == 111001).Remaining);
        Assert.Equal("next", snapshot.Single(target => target.BossSpawnId == 111001).BossRole);
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 60000));
        Assert.Equal("verteron", Assert.Single(fixture.Targets.BossNavigationTargets).MapId);
        Assert.Equal(3, fixture.Bosses.Schedule.Count); // Historic map timers remain in the list, not navigation.
        Assert.Equal(1, changes);
        fixture.Network.Stop();
        Assert.True(Assert.Single(fixture.Targets.BossNavigationTargets).BossProvisional);
        Assert.Equal(2, changes);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Refresh();
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(59999, true)]
    [InlineData(60000, true)]
    [InlineData(60001, false)]
    public async Task NewlyReceivedListsUseTheActualSpawnTimestampForTheOneMinuteWindow(int elapsedMilliseconds, bool visible)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - elapsedMilliseconds));

        var row = Assert.Single(fixture.Bosses.Schedule);
        Assert.True(row.Spawned);
        Assert.Equal(TimeSpan.Zero, row.Age); // This is the fresh list's observation age, not the spawn age.
        Assert.Equal(TimeSpan.FromMilliseconds(elapsedMilliseconds), row.SpawnElapsed);
        Assert.Equal(visible ? 1 : 0, fixture.Targets.BossNavigationTargets.Count);
        Assert.Equal(111001, fixture.Bosses.View.Boss!.SpawnId);
    }

    [Fact]
    public async Task FutureSpawnTimestampsRemainUnknownInsteadOfStartingALiveCounterAtZero()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow + 1000));

        var row = Assert.Single(fixture.Bosses.Schedule);
        Assert.True(row.Spawned);
        Assert.Null(row.SpawnElapsed);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
    }

    [Fact]
    public async Task RefreshingTheSameNativeListCannotRestartAnAlreadyExpiredSpawnWindow()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        var spawnedAt = fixture.UnixNow - 50000;
        fixture.Observe(1110, new BossSpawn(111001, true, spawnedAt));
        Assert.Single(fixture.Targets.BossNavigationTargets);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Observe(1110, new BossSpawn(111001, true, spawnedAt));

        var row = Assert.Single(fixture.Bosses.Schedule);
        Assert.Equal(TimeSpan.Zero, row.Age);
        Assert.Equal(TimeSpan.FromSeconds(70), row.SpawnElapsed);
        Assert.True(row.Spawned);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
    }

    [Fact]
    public async Task OnlyCrossingTheSpawnWindowBoundaryPublishesANavigationChange()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 20000),
            new BossSpawn(111003, false, fixture.UnixNow + 120000));
        var before = fixture.Targets.BossNavigationTargets;
        var changes = 0;
        fixture.Targets.Changed += () => changes++;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(39999));
        fixture.Refresh();
        Assert.Equal(TimeSpan.FromMilliseconds(59999), fixture.Bosses.Schedule.Single(row => row.Spawned).SpawnElapsed);
        Assert.Same(before, fixture.Targets.BossNavigationTargets);
        Assert.Equal(0, changes);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        fixture.Refresh();
        Assert.Equal(TimeSpan.FromSeconds(60), fixture.Bosses.Schedule.Single(row => row.Spawned).SpawnElapsed);
        Assert.Same(before, fixture.Targets.BossNavigationTargets);
        Assert.Equal(0, changes);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        fixture.Refresh();
        Assert.Equal(1, changes);
        var after = fixture.Targets.BossNavigationTargets;
        Assert.Equal("next", Assert.Single(after).BossRole);
        Assert.True(fixture.Bosses.Schedule.Single(row => row.Place.Boss.SpawnId == 111001).Spawned);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Refresh();
        Assert.Equal(1, changes);
        Assert.Same(after, fixture.Targets.BossNavigationTargets);
    }

    [Fact]
    public async Task ServerClockCorrectionsRebaseSpawnElapsedIndependentlyOfListAgeAndWallClock()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        var serverNow = fixture.UnixNow;
        fixture.Observe(1110, new BossSpawn(111001, true, serverNow - 20000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Clock.ShiftWall(TimeSpan.FromDays(2));
        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(fixture.Bosses.Schedule).SpawnElapsed);
        fixture.Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(serverNow + 45000)]);
        fixture.Refresh();
        var corrected = Assert.Single(fixture.Bosses.Schedule);
        Assert.Equal(TimeSpan.Zero, corrected.Age);
        Assert.Equal(TimeSpan.FromSeconds(65), corrected.SpawnElapsed);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        fixture.Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(serverNow - 21000)]);
        fixture.Refresh();
        Assert.Null(Assert.Single(fixture.Bosses.Schedule).SpawnElapsed);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath)); // Clocks alone do not persist a new list.
    }

    [Fact]
    public async Task CachedAndRestartedSpawnCountersKeepAgingWithoutOpeningANewVisibilityWindow()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 20000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Network.Stop();
        Assert.True(Assert.Single(fixture.Targets.BossNavigationTargets).BossProvisional);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Restart();
        fixture.Refresh();
        var restored = Assert.Single(fixture.Bosses.Schedule);
        Assert.True(restored.Cached);
        Assert.Equal(TimeSpan.FromSeconds(40), restored.SpawnElapsed);
        Assert.Equal(TimeSpan.FromSeconds(20), restored.Age);
        Assert.True(Assert.Single(fixture.Targets.BossNavigationTargets).BossFaded);
        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        fixture.Refresh();
        Assert.Equal(TimeSpan.FromSeconds(61), Assert.Single(fixture.Bosses.Schedule).SpawnElapsed);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        fixture.Restart();
        fixture.Refresh();
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Spawned);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath));
    }

    [Theory]
    [InlineData("de", "Auf Karte zeigen", false)]
    [InlineData("en", "Show on map", false)]
    [InlineData("de", "Auf Karte zeigen", true)]
    [InlineData("en", "Show on map", true)]
    public async Task AnExpiredSpawnHasMapFocusOnlyWhenOlderSpawnsAreExplicitlyEnabled(string language, string mapAction, bool showOlderSpawns)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.UiLanguage = fixture.Settings.Current.NameLanguage = UiText.Language = language;
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Settings.Current.BossRushShowOlderSpawns = showOlderSpawns;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 60001),
            new BossSpawn(111003, false, fixture.UnixNow + 60000));
        Assert.Equal(111001, fixture.Bosses.View.Boss!.SpawnId);
        Assert.Equal(showOlderSpawns ? 2 : 1, fixture.Targets.BossNavigationTargets.Count);
        Assert.Equal("next", Assert.Single(fixture.Targets.BossNavigationTargets, target => target.BossRole == "next").BossRole);

        var html = System.Net.WebUtility.HtmlDecode(await fixture.RenderBossRush());

        Assert.Contains("<h3>Synthetic boss one</h3>", html);
        if (showOlderSpawns) Assert.Contains(mapAction, html);
        else Assert.DoesNotContain(mapAction, html);
    }

    [Fact]
    public async Task OlderSpawnPreferenceUpdatesNavigationWithoutMutatingTheKnownSchedule()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 65000),
            new BossSpawn(111003, true, fixture.UnixNow - 20000), new BossSpawn(111004, false, fixture.UnixNow + 120000));
        var scheduleBefore = fixture.Bosses.Schedule.ToArray();
        Assert.Equal(2, fixture.Targets.BossNavigationTargets.Count);

        fixture.Settings.Update(settings => settings.BossRushShowOlderSpawns = true);

        Assert.Equal(3, fixture.Targets.BossNavigationTargets.Count);
        Assert.Equal("#4ade80", Assert.Single(fixture.Targets.BossNavigationTargets, target => target.BossSpawnId == 111001).Color);
        Assert.Equal(scheduleBefore, fixture.Bosses.Schedule);
        fixture.Settings.Update(settings => settings.BossRushShowOlderSpawns = false);
        Assert.Equal(2, fixture.Targets.BossNavigationTargets.Count);
        Assert.Equal(scheduleBefore, fixture.Bosses.Schedule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedSchedulesWarnAcrossRestartsWithoutNewListsAndPreserveStageReceipts(bool untilSpawn)
    {
        await using var fixture = new Fixture();
        const string key = "1110:111001";
        UiText.Language = "en";
        fixture.Settings.Current.BossRushEnabled = false;
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertUntilSpawn = untilSpawn;
        fixture.Settings.Current.BossAlertDurationSeconds = 15;
        fixture.Settings.Current.BossAlertIds = [key, "1010:101001"];
        fixture.Settings.Current.BossReminderOverrides[key] = new() { LeadSecondsList = [300, 60] };
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:" + key];
        var deadline = fixture.UnixNow + 1200000;
        fixture.Observe(1110, new BossSpawn(111001, false, deadline));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 900000));
        var cacheBytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        fixture.Restart();
        using (var restored = fixture.CreateAlerts(persistent: true))
        {
            restored.SetBossPresentation(key, new() { PlaySound = true });
            List<AlertNotice> notices = [];
            restored.NoticeRaised += notices.Add;
            fixture.Refresh(); // No map/list/clock message after the restart.
            var first = Assert.Single(notices);
            Assert.EndsWith(":lead:300", first.Key);
            Assert.True(first.PlaySound);
            Assert.Contains("saved", first.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, fixture.Bosses.Schedule.Count);
            Assert.All(fixture.Bosses.Schedule, row => Assert.True(row.Cached));
            Assert.False(fixture.Settings.Current.BossRushEnabled);
            Assert.Empty(fixture.Targets.BossNavigationTargets);
            var strip = Assert.Single(restored.TimerOverlayRows);
            Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(4), strip.At);
            Assert.Equal(strip.At, first.At);
            fixture.Clock.Advance(TimeSpan.FromMinutes(3));
            fixture.Refresh();
            Assert.Equal(2, notices.Count);
            Assert.EndsWith(":lead:60", notices[^1].Key);
            Assert.Single(restored.NotificationRows);
            Assert.Equal(first.At, notices[^1].At);
            if (untilSpawn) Assert.Equal(first.Expires, notices[^1].Expires);
        }
        fixture.Restart();
        using var restartedAgain = fixture.CreateAlerts(persistent: true);
        List<AlertNotice> deliveredAgain = [];
        restartedAgain.NoticeRaised += deliveredAgain.Add;
        fixture.Refresh();
        Assert.Empty(deliveredAgain); // The 60-second receipt survived another process lifetime.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Refresh();
        Assert.Empty(deliveredAgain);
        Assert.Empty(restartedAgain.NotificationRows);
        Assert.False(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == key).Spawned);
        Assert.Equal("?", TimerOverlayPresentation.Countdown(Assert.Single(restartedAgain.TimerOverlayRows), fixture.Clock.GetUtcNow()));
        Assert.Equal(cacheBytes, File.ReadAllBytes(fixture.CachePath));
        fixture.Observe(1110, new BossSpawn(111001, true, deadline));
        Assert.EndsWith(":spawn", Assert.Single(deliveredAgain).Key);
        Assert.False(Assert.Single(fixture.Bosses.Alerts).Cached);
    }

    [Fact]
    public async Task PeriodicSchedulerWarnsAfterRestartWithoutAnyUiOrNewNetworkMessages()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001"];
        fixture.Settings.Current.BossReminderOverrides["1110:111001"] = new() { LeadSecondsList = [60] };
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossRushEnabled = false;
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 120000));
        fixture.Restart();
        using var alerts = fixture.CreateAlerts();
        var delivered = new TaskCompletionSource<AlertNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.NoticeRaised += notice => delivered.TrySetResult(notice);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var warning = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.EndsWith(":lead:60", warning.Key);
        Assert.Equal(Assert.Single(alerts.TimerOverlayRows).At, warning.At);
        Assert.True(Assert.Single(fixture.Bosses.Alerts).Cached);
    }

    [Fact]
    public async Task ASourceGapKeepsFutureWarningsAndFreshListsDoNotRepeatConsumedStages()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossReminderOverrides["1110:111001"] = new() { LeadSecondsList = [300, 60] };
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        var deadline = fixture.UnixNow + 600000;
        fixture.Observe(1110, new BossSpawn(111001, false, deadline));
        using var alerts = fixture.CreateAlerts();
        List<AlertNotice> notices = [];
        alerts.NoticeRaised += notices.Add;
        fixture.Network.Stop();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.Refresh();
        Assert.EndsWith(":lead:300", Assert.Single(notices).Key);
        fixture.Observe(1110, new BossSpawn(111001, false, deadline));
        Assert.Single(notices);
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        fixture.Refresh();
        Assert.Equal(2, notices.Count);
        Assert.EndsWith(":lead:60", notices[^1].Key);
        Assert.False(Assert.Single(fixture.Bosses.Alerts).Cached);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RenderRestoredScheduleWarningWithoutOpeningAnyAppOrGameWindow(string language)
    {
        var output = TestPrerequisites.RequiredValue("SOULCREST_BOSS_PERSISTENCE_PREVIEW");
        Directory.CreateDirectory(output);
        await using var fixture = new Fixture();
        fixture.Settings.Current.UiLanguage = fixture.Settings.Current.NameLanguage = fixture.Settings.Current.OverlayLanguage = UiText.Language = language;
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossRushEnabled = false;
        fixture.Settings.Current.BossAlertUntilSpawn = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossReminderOverrides["1110:111001"] = new() { LeadSecondsList = [300] };
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000));
        fixture.Clock.Advance(TimeSpan.FromSeconds(301));
        fixture.Restart();
        using var alerts = fixture.CreateAlerts(persistent: true);
        fixture.Refresh();
        var now = fixture.Clock.GetUtcNow();
        var notification = Assert.Single(alerts.NotificationRows);
        Assert.Equal(Assert.Single(alerts.TimerOverlayRows).At, notification.At);
        var rows = alerts.NotificationRows.Select(notice => new AlertDisplayRow(notice.Key, notice.Title, notice.Message, notice.Color, notice.At, notice.At <= now)).ToArray();
        var theme = OverlayThemeDefinition.Aion2;
        var strip = TimerStripRenderer.Layout(alerts.TimerOverlayRows);
        const int width = 520, gap = 12, scale = 2;
        using var bitmap = new Bitmap(width * scale, (strip.Size.Height + gap + AlertsOverlayForm.LogicalHeight(rows.Length, theme)) * scale, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(scale, scale);
            TimerStripRenderer.Paint(graphics, strip, now, theme: theme);
            graphics.TranslateTransform(0, strip.Size.Height + gap);
            AlertsOverlayForm.PaintNotifications(graphics, rows, now, true, width, theme);
        }
        bitmap.Save(Path.Combine(output, "restored-boss-warning-" + language + ".png"), ImageFormat.Png);
        var html = await fixture.RenderBossRush();
        var styles = string.Join("", Directory.GetFiles(Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css"), "*.css")
            .Select(path => "<link rel=\"stylesheet\" href=\"/css/" + Path.GetFileName(path) + "\">"));
        await File.WriteAllTextAsync(Path.Combine(output, "boss-persistence-" + language + ".html"), "<!doctype html><html><head><meta charset=\"utf-8\">" + styles + "</head><body>" + html + "</body></html>");
    }

    private static byte[] ListMessage(int mapId, params BossSpawn[] bosses)
    {
        List<byte> message = [1, 0x91, 0, 0, .. BitConverter.GetBytes(mapId)];
        Varint(bosses.Length);
        for (var index = 0; index < bosses.Length; index++)
        {
            var boss = bosses[index];
            message.Add((byte)(boss.Spawned ? 1 : 0));
            Varint(boss.SpawnId);
            if (boss.Spawned) message.AddRange(new byte[12]);
            if (index % 8 == 0)
            {
                byte mask = 0;
                for (var bit = 0; bit < 8 && index + bit < bosses.Length; bit++)
                    if (bosses[index + bit].Spawned) mask |= (byte)(1 << bit);
                message.Add(mask);
            }
            message.AddRange(BitConverter.GetBytes(boss.SpawnUnixMs ?? 0));
        }
        message.AddRange([0, 0, 0]);
        return message.ToArray();

        void Varint(int value)
        {
            var remaining = (uint)value;
            while (remaining >= 128) { message.Add((byte)(remaining | 128)); remaining >>= 7; }
            message.Add((byte)remaining);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-persistence-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        public SettingsService Settings { get; }
        public NetworkLootService Network { get; }
        public MapTargetsService Targets { get; }
        public BossRushService Bosses { get; private set; }
        public ManualTime Clock { get; } = new();
        public long UnixNow => Clock.GetUtcNow().ToUnixTimeMilliseconds();
        public string CachePath => Path.Combine(_folder, "boss-timers.json");

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            Directory.CreateDirectory(Path.Combine(_folder, "verteron"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[
                {"spawnId":111001,"npcId":2400017,"en":"Synthetic boss one","x":100,"y":200},
                {"spawnId":111002,"npcId":2400074,"en":"Synthetic boss without single location","x":null,"y":null},
                {"spawnId":111003,"npcId":2400140,"en":"Synthetic third boss","x":500,"y":600},
                {"spawnId":111004,"npcId":2400141,"en":"Synthetic fourth boss","x":700,"y":800}]}
                """);
            File.WriteAllText(Path.Combine(_folder, "verteron", "bosses.json"), """
                {"mapId":1010,"bosses":[{"spawnId":101001,"npcId":2400101,"en":"Synthetic boss two","x":300,"y":400}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<MapTargetsService>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Current.BossRushEnabled = Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossRushShowOlderSpawns = false;
            Settings.Current.BossNavigationPreviewCount = 1;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossReminderOverrides = [];
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.TimerOverlayTimerIds = [];
            Settings.Current.TimerOverlayLeadMinutes = 0;
            Network = _provider.GetRequiredService<NetworkLootService>();
            Targets = _provider.GetRequiredService<MapTargetsService>();
            Bosses = CreateBosses();
        }

        private BossRushService CreateBosses() => new(_provider.GetRequiredService<ProgressService>(), Settings, Targets, Network, Clock, CachePath);
        public void Restart() { Bosses.Dispose(); Bosses = CreateBosses(); }
        public void Refresh() => Settings.Update(_ => { });
        public AlertsService CreateAlerts(bool persistent = false) => new(Path.Combine(_folder, persistent ? "alerts.json" : Guid.NewGuid().ToString("N") + "-alerts.json"), Clock, runTimer: false, bosses: Bosses, settings: Settings);
        public void Observe(int mapId, params BossSpawn[] bosses)
        {
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(UnixNow)]);
            Bosses.OnMessage("fixture", ListMessage(mapId, bosses));
        }

        public async Task<string> RenderBossRush()
        {
            using var alerts = CreateAlerts();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Bosses);
            services.AddSingleton(Settings);
            services.AddSingleton(Targets);
            services.AddSingleton(_provider.GetRequiredService<ProgressService>());
            services.AddSingleton(alerts);
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString());
        }

        public async ValueTask DisposeAsync()
        {
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1791547200000);
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _stamp += (long)elapsed.TotalMilliseconds; }
        public void ShiftWall(TimeSpan elapsed) => _now += elapsed;
    }
}
