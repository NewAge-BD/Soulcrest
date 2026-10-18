using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossUntilSpawnTests
{
    [Fact]
    public async Task MissingGlobalModeDoesNotPromoteLegacyPerBossValuesAndRetainsGlobalSeconds()
    {
        await using var fixture = new Fixture();
        File.WriteAllText(AppPaths.SettingsFile, """
            {"BossAlertDurationSeconds":23,"BossReminderOverrides":{
                "1110:111001":{"Enabled":false,"LeadSeconds":321,"DurationSeconds":8,"UntilSpawn":true},
                "1110:111002":{"Enabled":true,"LeadSeconds":12,"DurationSeconds":55,"UntilSpawn":false}}}
            """);
        var restored = new SettingsService();
        Assert.False(restored.Current.BossAlertUntilSpawn);
        Assert.Equal(23, restored.Current.BossAlertDurationSeconds);
        Assert.Equal(new BossReminderPreference { Enabled = false, LeadSeconds = 321 },
            restored.Current.BossReminderOverrides["1110:111001"]);
        Assert.Equal(new BossReminderPreference { LeadSeconds = 12 }, restored.Current.BossReminderOverrides["1110:111002"]);
        restored.Update(settings => settings.BossAlertUntilSpawn = true);
        Assert.True(new SettingsService().Current.BossAlertUntilSpawn);
        Assert.Equal(23, new SettingsService().Current.BossAlertDurationSeconds);
        var json = File.ReadAllText(AppPaths.SettingsFile);
        Assert.DoesNotContain("\"UntilSpawn\"", json);
        Assert.DoesNotContain("\"DurationSeconds\"", json);
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(999, 60)]
    public async Task GlobalSecondsRemainNormalizedIndependentlyOfTheHoldMode(int input, int expected)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Update(settings => { settings.BossAlertDurationSeconds = input; settings.BossAlertUntilSpawn = true; });
        Assert.Equal(expected, new SettingsService().Current.BossAlertDurationSeconds);
        Assert.True(new SettingsService().Current.BossAlertUntilSpawn);
    }

    [Fact]
    public async Task CountdownZeroKeepsOriginalWarningAndShowsAwaitingConfirmationWithoutInferringSpawn()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.ServerOffset = TimeSpan.FromHours(4);
        var planned = fixture.Clock.GetUtcNow().AddSeconds(60);
        fixture.Observe(fixture.Upcoming(1, 60));
        var original = Assert.Single(fixture.Bosses.Alerts);
        Assert.True(original.UntilSpawn);
        Assert.Equal(DateTimeOffset.MaxValue, original.Expires);
        Assert.Equal(planned, Assert.Single(fixture.Alerts.NotificationRows).At);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        fixture.Refresh();
        var held = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal(original.Id, held.Id);
        Assert.Equal(original.SpawnUnixMs, held.SpawnUnixMs);
        Assert.Equal(TimeSpan.Zero, held.Remaining);
        Assert.False(held.Spawned);
        var banner = Assert.Single(fixture.Alerts.NotificationRows);
        Assert.True(banner.AwaitingConfirmation);
        Assert.Equal(planned, banner.At);
        Assert.NotEqual(DateTimeOffset.MaxValue, banner.At);
        var compatibility = Assert.Single(fixture.Alerts.OverlayRows, row => row.Key == "boss:1110:111001");
        Assert.True(compatibility.AwaitingConfirmation);
        Assert.False(compatibility.Started);
        Assert.False(Assert.Single(fixture.Bosses.Schedule).Spawned);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task SameCycleClockCorrectionBeyondDeadlineImmediatelyWaitsWithoutConfirmingOrRepeating()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        var serverStart = fixture.ServerNow;
        fixture.Observe(fixture.Upcoming(1, 60));
        var original = Assert.Single(fixture.Bosses.Alerts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.ClockMessage(serverStart + 120000);
        var held = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal(original.Id, held.Id);
        Assert.Equal(original.SpawnUnixMs, held.SpawnUnixMs);
        Assert.Equal(original.PlannedAt, held.PlannedAt);
        Assert.Equal(TimeSpan.Zero, held.Remaining);
        Assert.False(held.Spawned);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        fixture.Alerts.Tick();
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task SourceGapAndSeveralDaysWithoutConfirmationKeepTheHeldSourceAndBanner()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var id = Assert.Single(fixture.Bosses.Alerts).Id;
        fixture.Network.Stop();
        fixture.Clock.Advance(TimeSpan.FromDays(4));
        fixture.Refresh();
        fixture.Alerts.Tick();
        Assert.Equal(id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task MapChangesKeepWarningsAndFreshConfirmationOnlyEndsThatBossIdentity()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var first = Assert.Single(fixture.Bosses.Alerts);
        fixture.ObserveMap(1010, new BossSpawn(101001, false, fixture.ServerNow + 5000));
        Assert.Equal(2, fixture.Bosses.Alerts.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Observe(new BossSpawn(111001, true, fixture.ServerNow));
        Assert.Null(fixture.Bosses.UntilSpawnNotice(first.Id));
        Assert.Single(fixture.Bosses.Alerts, notice => notice.Key == "1010:101001" && notice.UntilSpawn);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows, notice => notice.Key.StartsWith("boss:1010:", StringComparison.Ordinal)).AwaitingConfirmation);
        Assert.Single(fixture.Bosses.Alerts, notice => notice.Key == "1110:111001" && notice.Spawned && !notice.UntilSpawn);
        Assert.Equal(3, fixture.Delivered.Count);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(600)]
    public async Task NewFutureTimestampInsideOrOutsideLeadDoesNotReplaceOrRepeatAnUnconfirmedWarning(int seconds)
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var original = Assert.Single(fixture.Bosses.Alerts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Observe(fixture.Upcoming(1, seconds));
        var held = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal(original.Id, held.Id);
        Assert.Equal(original.SpawnUnixMs, held.SpawnUnixMs);
        Assert.Equal(original.PlannedAt, held.PlannedAt);
        Assert.Equal(TimeSpan.Zero, held.Remaining);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task CachedFlagsClockPacketsAndUnknownFullListsCannotConfirmTheHeldBoss()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var id = Assert.Single(fixture.Bosses.Alerts).Id;
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.RestoreCached(new BossSpawn(111001, true, fixture.ServerNow));
        fixture.ClockMessage(fixture.ServerNow);
        fixture.Refresh();
        Assert.NotNull(fixture.Bosses.UntilSpawnNotice(id));
        fixture.Observe(new BossSpawn(111001, true, fixture.ServerNow), new BossSpawn(999999, false, fixture.ServerNow + 1000));
        Assert.NotNull(fixture.Bosses.UntilSpawnNotice(id));
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Delivered);
        fixture.Observe(new BossSpawn(111001, true, fixture.ServerNow));
        Assert.Null(fixture.Bosses.UntilSpawnNotice(id));
        Assert.True(Assert.Single(fixture.Bosses.Alerts).Spawned);
        Assert.Equal(2, fixture.Delivered.Count);
    }

    [Fact]
    public async Task MalformedSpawnFlagWithoutRequiredTimestampCannotConfirmOrEndTheHeldWarning()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var id = Assert.Single(fixture.Bosses.Alerts).Id;
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Reject(new BossSpawn(111001, true, null));
        Assert.Equal(id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.False(Assert.Single(fixture.Bosses.Alerts).Spawned);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task FreshConfirmedSpawnReplacesWarningWithGlobalFixedSeconds()
    {
        await using var fixture = new Fixture();
        fixture.Configure(duration: 13);
        fixture.Observe(fixture.Upcoming(1, 60));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var now = fixture.Clock.GetUtcNow();
        fixture.Observe(new BossSpawn(111001, true, fixture.ServerNow));
        var notice = Assert.Single(fixture.Bosses.Alerts);
        Assert.True(notice.Spawned);
        Assert.False(notice.UntilSpawn);
        Assert.Equal(now.AddSeconds(13), notice.Expires);
        Assert.False(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.EndsWith(":spawn", Assert.Single(fixture.Alerts.NotificationRows).Key);
        Assert.Equal(2, fixture.Delivered.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        Assert.Empty(fixture.Alerts.NotificationRows);
    }

    [Fact]
    public async Task PreviewForTheSameBossExpiresSeparatelyAndPreservesItsOriginalHeldWarning()
    {
        await using var fixture = new Fixture();
        fixture.Configure(duration: 13);
        fixture.Observe(fixture.Upcoming(1, 5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var held = Assert.Single(fixture.Alerts.NotificationRows);
        var id = Assert.Single(fixture.Bosses.Alerts).Id;
        var now = fixture.Clock.GetUtcNow();
        fixture.Bosses.PreviewAlert("1110:111001");
        Assert.Equal(2, fixture.Alerts.NotificationRows.Count);
        Assert.Equal(now.AddSeconds(13), Assert.Single(fixture.Bosses.Alerts, notice => notice.Preview).Expires);
        Assert.Equal(id, Assert.Single(fixture.Bosses.Alerts, notice => notice.UntilSpawn).Id);
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        fixture.Alerts.Tick();
        var restored = Assert.Single(fixture.Alerts.NotificationRows);
        Assert.Equal(held.Key, restored.Key);
        Assert.True(restored.AwaitingConfirmation);
        Assert.Equal(2, fixture.Delivered.Count);
    }

    [Fact]
    public async Task HeldWarningsDoNotOccupyTheThreeFixedNoticeQueueOrBlockLaterWarnings()
    {
        await using var fixture = new Fixture();
        fixture.Configure(duration: 13);
        fixture.Observe(Enumerable.Range(1, 7).Select(number => fixture.Upcoming(number, 5)).ToArray());
        Assert.Equal(7, fixture.Bosses.Alerts.Count);
        Assert.Equal(7, fixture.Alerts.NotificationRows.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Bosses.PreviewAlert("1110:111008");
        var preview = Assert.Single(fixture.Bosses.Alerts, notice => notice.Preview);
        Assert.Equal(fixture.Clock.GetUtcNow(), preview.CreatedAt);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(13), preview.Expires);
        Assert.Equal(8, fixture.Alerts.NotificationRows.Count);
        Assert.Equal(8, fixture.Delivered.Count);
        Assert.All(fixture.Bosses.Alerts.Where(notice => notice.UntilSpawn), notice => Assert.Equal(TimeSpan.Zero, notice.Remaining));
    }

    [Fact]
    public async Task FixedGlobalDurationKeepsTheExistingThreeNoticeQueueAndPerBossLeadStillApplies()
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13);
        Assert.True(fixture.Bosses.SetReminder("1110:111005", new() { LeadSeconds = 1 }));
        fixture.Observe(Enumerable.Range(1, 5).Select(number => fixture.Upcoming(number, 30)).ToArray());
        Assert.Equal(3, fixture.Bosses.Alerts.Count);
        Assert.Equal(3, fixture.Delivered.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        fixture.Refresh();
        var queued = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal("1110:111004", queued.Key);
        Assert.False(queued.UntilSpawn);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(13), queued.Expires);
        Assert.Equal(4, fixture.Delivered.Count);
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("master")]
    [InlineData("reminder")]
    public async Task ExplicitUserCancellationStillEndsHeldWarnings(string change)
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        if (change == "selected") fixture.Settings.Update(settings => settings.BossAlertIds = []);
        else if (change == "master") fixture.Settings.Update(settings => settings.BossAlertsEnabled = false);
        else Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = false }));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Empty(fixture.Alerts.Active);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task GlobalModeChangesAffectFutureWarningsWhileExistingWarningsKeepTheirConfirmationRequirement()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        var original = fixture.Upcoming(1, 5);
        fixture.Observe(original);
        fixture.Settings.Update(settings => { settings.BossAlertUntilSpawn = false; settings.BossAlertDurationSeconds = 13; });
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Observe(original, fixture.Upcoming(2, 30));
        Assert.Single(fixture.Bosses.Alerts, notice => notice.UntilSpawn && notice.Key == "1110:111001");
        Assert.Single(fixture.Bosses.Alerts, notice => !notice.UntilSpawn && notice.Key == "1110:111002");
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
    }

    [Fact]
    public async Task WallClockRollbackDoesNotDeleteOrRestartAnAlreadyDeliveredHeldWarning()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var id = Assert.Single(fixture.Bosses.Alerts).Id;
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Network.Stop();
        fixture.Clock.ShiftWall(TimeSpan.FromMinutes(-10));
        fixture.Alerts.Tick();
        Assert.Equal(id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.Equal(TimeSpan.Zero, Assert.Single(fixture.Bosses.Alerts).Remaining);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Alerts.Active);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task SoundAndLegacyBannerWindowChangesCannotHideOrRepeatTheHeldNotification()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var key = Assert.Single(fixture.Alerts.NotificationRows).Key;
        var presentation = fixture.Alerts.GetBossPresentation("1110:111001");
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", presentation with { PlaySound = true, Sound = "beep", OverlayLeadMinutes = 1 }));
        Assert.Equal(key, Assert.Single(fixture.Alerts.NotificationRows).Key);
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", presentation with { ShowOverlay = false }));
        Assert.Equal(key, Assert.Single(fixture.Alerts.NotificationRows).Key);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).ShowOverlay);
        Assert.False(fixture.Alerts.GetBossPresentation("1110:111001").ShowOverlay);
        Assert.Empty(fixture.Alerts.CountdownRows); // Only the legacy countdown projection retains that flag.
        Assert.Single(fixture.Alerts.Active);
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", presentation));
        Assert.Equal(key, Assert.Single(fixture.Alerts.NotificationRows).Key);
        fixture.Alerts.Tick();
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task ASourceProducedFreshCanBeDeliveredForTheFirstTimeAfterASourceGap()
    {
        await using var fixture = new Fixture(deliver: false);
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        fixture.Network.Stop();
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        using var consumer = fixture.NewConsumer();
        var delivered = new List<AlertNotice>();
        consumer.NoticeRaised += delivered.Add;
        consumer.Tick();
        Assert.True(Assert.Single(consumer.NotificationRows).AwaitingConfirmation);
        consumer.Tick();
        Assert.Single(delivered);
    }

    [Fact]
    public async Task AnAlreadyDeliveredHoldDoesNotReplayWhenBoundedReceiptHistoryForgetsItsKey()
    {
        await using var fixture = new Fixture();
        fixture.Configure();
        fixture.Observe(fixture.Upcoming(1, 5));
        var key = Assert.Single(fixture.Alerts.NotificationRows).Key;
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var receipts = (System.Collections.IList)typeof(AlertsService).GetField("_receipts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fixture.Alerts)!;
        receipts.Clear(); // Model the existing bounded receipt history evicting a very old lead key.
        fixture.Alerts.Tick();
        fixture.Alerts.Tick();
        Assert.Equal(key, Assert.Single(fixture.Alerts.NotificationRows).Key);
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task CalendarOccurrenceNeverConfirmsSpawnOrEndsItsHeldWarning()
    {
        await using var fixture = new Fixture(calendar: true);
        fixture.Configure();
        fixture.Refresh();
        Assert.True(Assert.Single(fixture.Bosses.Alerts).Calendar);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Refresh();
        Assert.True(Assert.Single(fixture.Alerts.NotificationRows).AwaitingConfirmation);
        Assert.Single(fixture.Delivered);
        Assert.Null(fixture.Bosses.View.Boss);
    }

    [Theory]
    [InlineData(false, 600, true, 5)]
    [InlineData(false, 900, false, 0)]
    [InlineData(true, 600, true, 5)]
    [InlineData(true, 900, false, 0)]
    public async Task TenAndFifteenMinuteRemindersAlwaysProduceAnIndependentBannerDespiteLegacyWindows(
        bool until, int lead, bool legacyShow, int legacyWindow)
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: until, duration: 13, lead: lead);
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        fixture.Settings.Current.TimerOverlayShowWorldMap = false;
        fixture.Settings.Current.TimerOverlayShowMenu = false;
        fixture.Settings.Current.TimerOverlayShowInstances = false;
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", new()
        {
            ShowOverlay = legacyShow, AlwaysShowOverlay = false, OverlayLeadMinutes = legacyWindow, PlaySound = true
        }));
        fixture.Observe(fixture.Upcoming(1, lead));
        var notice = Assert.Single(fixture.Alerts.NotificationRows);
        Assert.True(notice.ShowOverlay);
        Assert.True(notice.PlaySound);
        Assert.False(notice.Preview);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(lead), notice.At);
        Assert.Equal(until ? DateTimeOffset.MaxValue : fixture.Clock.GetUtcNow().AddSeconds(13), notice.Expires);
        Assert.Empty(fixture.Alerts.CountdownRows);
        Assert.Empty(fixture.Alerts.TimerOverlayRows);
        fixture.Alerts.Tick();
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task BossPreviewAlwaysHasItsOwnFixedBannerEvenWhenLegacyCountdownDisplayIsDisabled()
    {
        await using var fixture = new Fixture();
        fixture.Configure(duration: 13, lead: 900);
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", new()
        {
            ShowOverlay = false, AlwaysShowOverlay = false, OverlayLeadMinutes = 0
        }));
        fixture.Bosses.PreviewAlert("1110:111001");
        var preview = Assert.Single(fixture.Alerts.NotificationRows);
        Assert.True(preview.Preview);
        Assert.True(preview.ShowOverlay);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(13), preview.Expires);
        Assert.Empty(fixture.Alerts.CountdownRows);
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Single(fixture.Delivered);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("session")]
    [InlineData("map")]
    [InlineData("unknown-list")]
    public async Task AFixedReminderAlreadyAuthorizedKeepsItsOriginalExpiryAcrossLaterSourceChanges(string change)
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13, lead: 600);
        fixture.Observe(fixture.Upcoming(1, 600));
        var original = Assert.Single(fixture.Alerts.NotificationRows);
        var source = Assert.Single(fixture.Bosses.Alerts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        if (change == "gap") fixture.Network.ProcessProgressGap("fixture");
        else if (change == "session") fixture.Network.Stop();
        else if (change == "map") fixture.ObserveMap(1010, new BossSpawn(101001, false, fixture.ServerNow + 7200000));
        else fixture.Observe(fixture.Upcoming(1, 600), new BossSpawn(999999, false, fixture.ServerNow + 600000));
        fixture.Alerts.Tick();
        Assert.Equal(source.Id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.Equal(original, Assert.Single(fixture.Alerts.NotificationRows));
        Assert.Single(fixture.Delivered);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Refresh();
        fixture.Alerts.Tick();
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Single(fixture.Delivered);
    }

    [Fact]
    public async Task AFreshFixedSourceCanFirstDeliverAfterAGapWithoutBeingRecreatedFromCache()
    {
        await using var fixture = new Fixture(deliver: false);
        fixture.Configure(until: false, duration: 13, lead: 600);
        var now = fixture.Clock.GetUtcNow();
        fixture.Observe(fixture.Upcoming(1, 600));
        var sourceId = Assert.Single(fixture.Bosses.Alerts).Id;
        fixture.Network.Stop();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        using var consumer = fixture.NewConsumer();
        var notices = new List<AlertNotice>();
        consumer.NoticeRaised += notices.Add;
        consumer.Tick();
        Assert.Equal(sourceId, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.Equal(now.AddSeconds(13), Assert.Single(consumer.NotificationRows).Expires);
        consumer.Tick();
        Assert.Single(notices);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Refresh();
        consumer.Tick();
        Assert.Empty(consumer.NotificationRows);
        Assert.Single(notices);
    }

    [Fact]
    public async Task SoundAndLegacyWindowEditsDoNotEndOrRestartAnActiveFixedNotification()
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13, lead: 900);
        fixture.Observe(fixture.Upcoming(1, 900));
        var original = Assert.Single(fixture.Alerts.NotificationRows);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", new()
        {
            PlaySound = true, Sound = "beep", ShowOverlay = false, AlwaysShowOverlay = false, OverlayLeadMinutes = 0
        }));
        fixture.Alerts.Tick();
        Assert.Equal(original, Assert.Single(fixture.Alerts.NotificationRows));
        Assert.False(fixture.Alerts.GetBossPresentation("1110:111001").ShowOverlay);
        Assert.Empty(fixture.Alerts.CountdownRows);
        Assert.Single(fixture.Delivered);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(fixture.Alerts.NotificationRows);
    }

    [Fact]
    public async Task FixedWarningsAlreadyQueuedBeforeAGapKeepTheirExistingThreeSlotSchedule()
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13, lead: 600);
        fixture.Observe(Enumerable.Range(1, 4).Select(number => fixture.Upcoming(number, 600)).ToArray());
        Assert.Equal(3, fixture.Delivered.Count);
        fixture.Network.Stop();
        Assert.Equal(3, fixture.Alerts.NotificationRows.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(13));
        fixture.Refresh();
        var fourth = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal("1110:111004", fourth.Key);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(13), fourth.Expires);
        Assert.Single(fixture.Alerts.NotificationRows);
        Assert.Equal(4, fixture.Delivered.Count);
    }

    [Fact]
    public async Task CachedSnapshotsAloneStillCannotCreateEitherFixedWarningOrConfirmedSpawnNotice()
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13, lead: 900);
        fixture.RestoreCached(fixture.Upcoming(1, 600));
        fixture.Refresh();
        fixture.Alerts.Tick();
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        fixture.RestoreCached(new BossSpawn(111001, true, fixture.ServerNow));
        fixture.Refresh();
        fixture.Alerts.Tick();
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Empty(fixture.Delivered);
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("master")]
    [InlineData("reminder")]
    public async Task ExplicitUserCancellationStillEndsFixedNotificationsAfterTheirSourceBecameCached(string change)
    {
        await using var fixture = new Fixture();
        fixture.Configure(until: false, duration: 13, lead: 600);
        fixture.Observe(fixture.Upcoming(1, 600));
        fixture.Network.Stop();
        Assert.Single(fixture.Alerts.NotificationRows);
        if (change == "selected") fixture.Settings.Update(settings => settings.BossAlertIds = []);
        else if (change == "master") fixture.Settings.Update(settings => settings.BossAlertsEnabled = false);
        else Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = false }));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Empty(fixture.Alerts.Active);
        Assert.Single(fixture.Delivered);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-until-spawn-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        public ManualTime Clock { get; }
        public SettingsService Settings { get; }
        public BossRushService Bosses { get; }
        public AlertsService Alerts { get; }
        public NetworkLootService Network { get; }
        public List<AlertNotice> Delivered { get; } = [];
        public TimeSpan ServerOffset { get; set; }
        public long ServerNow => Clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)ServerOffset.TotalMilliseconds;

        public Fixture(bool calendar = false, bool deliver = true)
        {
            Clock = new(calendar ? DateTimeOffset.Parse("2026-10-12T18:59:00Z", System.Globalization.CultureInfo.InvariantCulture)
                : DateTimeOffset.FromUnixTimeMilliseconds(1791257500000));
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), JsonSerializer.Serialize(new
            {
                mapId = 1110,
                bosses = Enumerable.Range(1, 8).Select(number => new
                {
                    spawnId = 111000 + number, npcId = 2400000 + number, en = "Synthetic boss " + number,
                    x = number * 100, y = number * 200,
                    calendarSchedule = calendar && number == 1 ? new AlertSchedule
                    {
                        Kind = AlertScheduleKind.Weekly, TimeZoneId = "Europe/Berlin",
                        Weekdays = [DayOfWeek.Monday], Times = [new(21, 0)]
                    } : null
                })
            }));
            Directory.CreateDirectory(Path.Combine(_folder, "verteron"));
            File.WriteAllText(Path.Combine(_folder, "verteron", "bosses.json"),
                """{"mapId":1010,"bosses":[{"spawnId":101001,"npcId":2400101,"en":"Other map boss","x":50,"y":60}]}""");
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
            Settings.Current.BossAlertsEnabled = true;
            Settings.Current.BossRushEnabled = false;
            Settings.Current.BossAlertIds = [.. Enumerable.Range(1, 8).Select(number => "1110:" + (111000 + number)), "1010:101001"];
            Settings.Current.BossReminderOverrides = [];
            Settings.Current.BossAlertLeadSeconds = 120;
            Settings.Current.BossAlertDurationSeconds = 30;
            Settings.Current.BossAlertUntilSpawn = false;
            Network = _provider.GetRequiredService<NetworkLootService>();
            Bosses = new(_provider.GetRequiredService<ProgressService>(), Settings,
                _provider.GetRequiredService<MapTargetsService>(), Network, Clock, Path.Combine(_folder, "boss-cache.json"));
            Alerts = new(Path.Combine(_folder, "alerts.json"), Clock, runTimer: false, bosses: Bosses, settings: Settings);
            Alerts.NoticeRaised += Delivered.Add;
            if (!deliver) Alerts.Dispose();
        }

        public void Configure(bool until = true, int duration = 7, int lead = 120)
        {
            Settings.Current.BossAlertUntilSpawn = until;
            Settings.Current.BossAlertDurationSeconds = duration;
            Settings.Current.BossAlertLeadSeconds = lead;
        }
        public AlertsService NewConsumer() => new(Path.Combine(_folder, "delayed-alerts.json"), Clock,
            runTimer: false, bosses: Bosses, settings: Settings);
        public void RestoreCached(params BossSpawn[] bosses)
        {
            var state = (BossRushState)typeof(BossRushService).GetField("_state",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Bosses)!;
            state.Restore(new(1110, bosses, ServerNow, TimeSpan.Zero));
        }
        public BossSpawn Upcoming(int number, int seconds) => new(111000 + number, false, ServerNow + seconds * 1000L);
        public void ClockMessage(long serverNow) => Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(serverNow)]);
        public void Refresh() => Settings.Update(_ => { });
        public void Observe(params BossSpawn[] bosses) => ObserveMap(1110, bosses);
        public void ObserveMap(int map, params BossSpawn[] bosses) => EmitMap(map, valid: true, bosses);
        public void Reject(params BossSpawn[] bosses) => EmitMap(1110, valid: false, bosses);
        private void EmitMap(int map, bool valid, BossSpawn[] bosses)
        {
            ClockMessage(ServerNow);
            List<byte> message = [1, 0x91, 0, 0, .. BitConverter.GetBytes(map)];
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
            if (valid) Assert.NotNull(BossSpawnMessage.TryParse(message.ToArray()));
            else Assert.Null(BossSpawnMessage.TryParse(message.ToArray()));
            Bosses.OnMessage("fixture", message.ToArray());

            void Varint(int value)
            {
                var remaining = (uint)value;
                while (remaining >= 128) { message.Add((byte)(remaining | 128)); remaining >>= 7; }
                message.Add((byte)remaining);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Alerts.Dispose();
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class ManualTime(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _stamp += (long)elapsed.TotalMilliseconds; }
        public void ShiftWall(TimeSpan elapsed) => _now += elapsed;
    }
}
