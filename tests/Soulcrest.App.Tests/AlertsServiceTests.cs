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
public sealed class AlertsServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private string StorePath => Path.Combine(_folder, "alerts.json");
    public AlertsServiceTests() => Directory.CreateDirectory(_folder);
    private AlertsService Create() => new(StorePath, _time);
    private static EventAlert Rule(string id = "fixture") => new()
    { Id = id, Title = "Fixture event", Schedule = new() { TimeZoneId = "Europe/Berlin" }, PlaySound = true };

    [Fact]
    public void FreshStoreHasNoAutomaticallyEnabledPresetsOrWrites()
    {
        using var service = Create();
        Assert.Empty(service.Items);
        Assert.Empty(service.Upcoming);
        Assert.Empty(service.Active);
        Assert.False(File.Exists(StorePath));
        Assert.Equal(7, AlertsService.Presets.Count);
    }

    [Fact]
    public void LeadAndStartAreIndependentAndOnlyRaisedOnce()
    {
        using var service = Create();
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        Assert.True(service.Save(Rule()));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        var lead = Assert.Single(notices);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), lead.At);
        Assert.True(lead.PlaySound);
        service.Tick();
        Assert.Single(notices);
        _time.Advance(TimeSpan.FromMinutes(5));
        service.Tick();
        Assert.Equal(2, notices.Count);
        Assert.NotEqual(lead.Key, notices[1].Key);
        Assert.Single(service.Active);
        Assert.True(Assert.Single(service.OverlayRows).Started);
    }

    [Fact]
    public void ZeroLeadDoesNotDuplicateTheStartNotification()
    {
        using var service = Create();
        var count = 0;
        service.NoticeRaised += _ => count++;
        Assert.True(service.Save(Rule() with { LeadMinutes = 0 }));
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        service.Tick();
        service.Tick();
        Assert.Equal(1, count);
    }

    [Fact]
    public void NotifyAtStartCanBeDisabledWithoutDisablingLead()
    {
        using var service = Create();
        var count = 0;
        service.NoticeRaised += _ => count++;
        Assert.True(service.Save(Rule() with { NotifyAtStart = false }));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        _time.Advance(TimeSpan.FromMinutes(5));
        service.Tick();
        Assert.Equal(1, count);
        Assert.Empty(service.Active);
    }

    [Fact]
    public void StandbyDoesNotFloodMissedNotifications()
    {
        using var service = Create();
        var count = 0;
        service.NoticeRaised += _ => count++;
        Assert.True(service.Save(Rule()));
        _time.Advance(TimeSpan.FromHours(48) + TimeSpan.FromMinutes(6));
        service.Tick();
        Assert.Equal(0, count);
        Assert.Empty(service.Active);
    }

    [Fact]
    public void StandbyAllowsOnlyTheLastThirtySeconds()
    {
        using var service = Create();
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        Assert.True(service.Save(Rule()));
        _time.Advance(TimeSpan.FromHours(48) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(21));
        service.Tick();
        var notice = Assert.Single(notices);
        Assert.Equal(_time.GetUtcNow().AddSeconds(-20), notice.At);
    }

    [Fact]
    public void RestartStartsNowInsteadOfReplayingOfflineAlerts()
    {
        using (var service = Create()) Assert.True(service.Save(Rule()));
        _time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(6));
        using var restarted = Create();
        var count = 0;
        restarted.NoticeRaised += _ => count++;
        restarted.Tick();
        Assert.Equal(0, count);
        Assert.Single(restarted.Items);
    }

    [Fact]
    public void NewlySavedOrEnabledRuleDoesNotReplayAnEarlierBoundary()
    {
        using var service = Create();
        var count = 0;
        service.NoticeRaised += _ => count++;
        _time.Advance(TimeSpan.FromSeconds(2)); // The 18:55 lead was before this rule existed.
        Assert.True(service.Save(Rule()));
        service.Tick();
        Assert.Equal(0, count);
        service.SetEnabled("fixture", false);
        _time.Advance(TimeSpan.FromMinutes(5)); // The 19:00 start was while disabled.
        service.SetEnabled("fixture", true);
        service.Tick();
        Assert.Equal(0, count);
    }

    [Fact]
    public void ReceiptSurvivesRestartAndClockMovingBackAndForth()
    {
        var start = _time.GetUtcNow();
        using (var service = Create())
        {
            Assert.True(service.Save(Rule()));
            _time.Advance(TimeSpan.FromSeconds(1));
            service.Tick();
            Assert.Single(service.Active);
        }
        _time.Set(start);
        using var restarted = Create();
        var count = 0;
        restarted.NoticeRaised += _ => count++;
        _time.Advance(TimeSpan.FromSeconds(1));
        restarted.Tick();
        Assert.Equal(0, count);
        _time.Set(start);
        restarted.Tick();
        _time.Advance(TimeSpan.FromSeconds(1));
        restarted.Tick();
        Assert.Equal(0, count);
        _time.Advance(TimeSpan.FromMinutes(5));
        restarted.Tick();
        Assert.Equal(1, count);
    }

    [Fact]
    public void ClockRollbackRemovesNoticesFromTheFuture()
    {
        using var service = Create();
        Assert.True(service.Save(Rule()));
        var original = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Single(service.Active);
        _time.Set(original);
        service.Tick();
        Assert.Empty(service.Active);
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Empty(service.Active);
    }

    [Fact]
    public void TicksAndReadsDoNotRewriteUnchangedStorage()
    {
        using var service = Create();
        Assert.True(service.Save(Rule()));
        var oldTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StorePath, oldTime);
        var before = File.ReadAllText(StorePath);
        service.Tick();
        _ = service.Items;
        _ = service.Upcoming;
        _ = service.Active;
        _ = service.OverlayRows;
        service.Tick();
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(StorePath));
    }

    [Fact]
    public void PreviewDoesNotConsumeRealReceiptOrWriteStorage()
    {
        using var service = Create();
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        Assert.True(service.Save(Rule()));
        var before = File.ReadAllText(StorePath);
        service.Preview("fixture");
        Assert.True(Assert.Single(notices).Preview);
        Assert.Equal(before, File.ReadAllText(StorePath));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Equal(2, notices.Count);
        Assert.False(notices[1].Preview);
        Assert.Single(service.Active);
    }

    [Fact]
    public void DisableAndDeleteImmediatelyRemoveActiveNoticesAndOverlay()
    {
        using var service = Create();
        Assert.True(service.Save(Rule()));
        service.Preview("fixture");
        Assert.Single(service.OverlayRows);
        service.SetEnabled("fixture", false);
        Assert.False(Assert.Single(service.Items).Enabled);
        Assert.Empty(service.Active);
        Assert.Empty(service.OverlayRows);
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Empty(service.Active);
        service.SetEnabled("fixture", true);
        service.Preview("fixture");
        Assert.True(service.Remove("fixture"));
        Assert.Empty(service.Items);
        Assert.Empty(service.Active);
        Assert.False(service.Remove("missing"));
    }

    [Fact]
    public void IncomingAndOutgoingArraysCannotMutateStoredRules()
    {
        using var service = Create();
        var times = new[] { new TimeOnly(21, 0) };
        Assert.True(service.Save(Rule() with { Schedule = new() { Kind = AlertScheduleKind.Daily, Times = times } }));
        times[0] = new TimeOnly(8, 0);
        var snapshot = Assert.Single(service.Items);
        Assert.Equal(new TimeOnly(21, 0), snapshot.Schedule.Times[0]);
        snapshot.Schedule.Times[0] = new TimeOnly(9, 0);
        Assert.Equal(new TimeOnly(21, 0), Assert.Single(service.Items).Schedule.Times[0]);
    }

    [Fact]
    public void InvalidOrUnconfirmedEnabledRulesCannotReplaceValidSettings()
    {
        using var service = Create();
        var original = Rule();
        Assert.True(service.Save(original));
        var invalid = new[]
        {
            original with { Title = " " }, original with { Schedule = null! },
            original with { Schedule = new() { TimeZoneId = "invalid" } },
            original with { DurationSeconds = 0 }, original with { LeadMinutes = -1 },
            original with { Sound = "unknown" }, original with { Color = "broken" },
            original with { SourceUrl = "javascript:alert(1)" },
            original with { NeedsConfirmation = true, ScheduleConfirmed = false }
        };
        foreach (var alert in invalid)
        {
            Assert.False(service.Save(alert));
            Assert.NotNull(service.Status);
            Assert.Equal(original, Assert.Single(service.Items));
        }
        Assert.True(service.Save(original with { NeedsConfirmation = true, Enabled = false }));
        service.SetEnabled(original.Id, true);
        Assert.False(Assert.Single(service.Items).Enabled);
    }

    [Fact]
    public void LegacyBuiltInSoundLoadsAsTheNewStandardWithoutDisablingTheReminder()
    {
        JsonFile.Save(StorePath, new { Version = 1, Items = new[] { Rule() with { Sound = "asterisk" } },
            BossPresentation = new Dictionary<string, BossAlertPresentation> { ["synthetic-boss"] = new() { Sound = "beep", PlaySound = true } } });
        using var service = Create();
        var alert = Assert.Single(service.Items);
        Assert.True(alert.Enabled);
        Assert.True(alert.PlaySound);
        Assert.Equal("standard", alert.Sound);
        Assert.Equal("standard", service.GetBossPresentation("synthetic-boss").Sound);
        Assert.True(service.GetBossPresentation("synthetic-boss").PlaySound);
    }

    [Fact]
    public void FailedLibrarySaveDoesNotKeepAnUnsavedSelection()
    {
        using var service = new AlertsService(_folder, _time); // A directory cannot be replaced by the store file.
        Assert.False(service.AddSoundFile(Path.Combine(_folder, "gentle.mp3")));
        Assert.Empty(service.SoundFiles);
        Assert.NotNull(service.Status);
    }

    [Fact]
    public void Mp3LibraryPersistsDeduplicatesAndSharesExistingEventAndBossFiles()
    {
        var first = Path.Combine(_folder, "gentle.mp3");
        var second = Path.Combine(_folder, "boss.mp3");
        using (var service = Create())
        {
            Assert.True(service.AddSoundFile(first));
            Assert.True(service.AddSoundFile(first.ToUpperInvariant()));
            Assert.False(service.AddSoundFile("https://example.invalid/alert.mp3"));
            Assert.False(service.AddSoundFile(Path.Combine(_folder, "wrong.wav")));
            Assert.True(service.Save(Rule() with { Sound = "custom", SoundFile = second }));
            Assert.True(service.SetBossPresentation("synthetic-boss", new() { Sound = "custom", SoundFile = first }));
            Assert.Equal(2, service.SoundFiles.Count);
        }
        using var reloaded = Create();
        Assert.Equal(new[] { first, second }, reloaded.SoundFiles);
        Assert.True(reloaded.Save(Rule() with { Sound = "standard" }));
        Assert.Contains(first, reloaded.SoundFiles);
        Assert.Contains(second, reloaded.SoundFiles);
    }

    [Theory]
    [InlineData(18, 59, 59, 19, 0)]
    [InlineData(19, 0, 0, 20, 0)]
    [InlineData(19, 5, 0, 20, 0)]
    public void FestivalOnlyCountsDownToNextStart(int hour, int minute, int second, int nextHour, int nextMinute)
    {
        _time.Set(new(2026, 10, 12, hour, minute, second, TimeSpan.Zero));
        using var service = Create();
        var festival = Assert.Single(service.EventTimers, timer => timer.Definition.Id == "shugo");
        Assert.False(festival.Window.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, nextHour, nextMinute, 0, TimeSpan.Zero), festival.Window.StartsAt);
        Assert.Equal(festival.Window.StartsAt, festival.Window.EndsAt);
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true }));
        var row = Assert.Single(service.OverlayRows);
        Assert.Equal(UiText.T("Startet in"), row.CountdownLabel);
        Assert.False(row.Started);
    }

    [Fact]
    public void PresetsMatchCommunityCalendarWithoutBossSchedules()
    {
        var siege = AlertsService.FromPreset("abyss", "Europe/Berlin");
        Assert.Equal(AlertScheduleKind.Weekly, siege.Schedule.Kind);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday }, siege.Schedule.Weekdays);
        Assert.Equal(new TimeOnly(21, 0), Assert.Single(siege.Schedule.Times));
        Assert.False(siege.NeedsConfirmation);
        Assert.Equal(EventCatalog.SourceUrl, siege.SourceUrl);
        Assert.Equal(0, AlertsService.FromPreset("shugo", "Europe/Berlin").Schedule.Minute);
        Assert.Equal(8, AlertsService.FromPreset("rift", "Europe/Berlin").Schedule.Times.Length);
        Assert.Equal(30, AlertsService.FromPreset("invasion", "Europe/Berlin").Schedule.Minute);
        var daily = AlertsService.FromPreset("daily-reset", "Europe/Berlin");
        Assert.Equal("UTC", daily.Schedule.TimeZoneId);
        Assert.Equal(new TimeOnly(7, 0), Assert.Single(daily.Schedule.Times));
        var weekly = AlertsService.FromPreset("weekly-reset", "Europe/Berlin");
        Assert.Equal("UTC", weekly.Schedule.TimeZoneId);
        Assert.Equal(DayOfWeek.Wednesday, Assert.Single(weekly.Schedule.Weekdays));
        Assert.All(EventCatalog.All, item => Assert.Equal(EventCatalog.SourceUrl, item.SourceUrl));
        Assert.Equal(new[] { "shugo", "rift", "invasion", "abyss", "daily-reset", "weekly-reset" }, EventCatalog.All.Select(item => item.Id));
    }

    [Fact]
    public void WindowOverlayAndSoundCanUseDifferentAdvanceTimes()
    {
        using var service = Create();
        Assert.True(service.Save(Rule() with { OverlayLeadMinutes = 1 }));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        var notice = Assert.Single(service.Active);
        Assert.True(notice.PlaySound);
        Assert.False(notice.ShowOverlay);
        Assert.Empty(service.OverlayRows);
        _time.Advance(TimeSpan.FromMinutes(4));
        service.Tick();
        Assert.Single(service.OverlayRows);
    }

    [Fact]
    public void PermanentOverlayAndPreviewIgnoreWindowButNeverDuplicateRows()
    {
        using var service = Create();
        Assert.True(service.Save(Rule() with { AlwaysShowOverlay = true, OverlayLeadMinutes = 0 }));
        Assert.Single(service.OverlayRows);
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.True(Assert.Single(service.Active).ShowOverlay);
        Assert.Single(service.OverlayRows);
        Assert.True(service.Save(Rule() with { OverlayLeadMinutes = 0 }));
        Assert.Empty(service.OverlayRows);
        service.Preview("fixture");
        Assert.Single(service.OverlayRows);
    }

    [Fact]
    public void SoundOnlyAlertsHaveNoOverlayRows()
    {
        using var service = Create();
        Assert.True(service.Save(Rule() with { ShowOverlay = false, PlaySound = true, AlwaysShowOverlay = true }));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.True(Assert.Single(service.Active).PlaySound);
        Assert.Empty(service.OverlayRows);
    }

    [Fact]
    public void SimultaneousEventsAreBoundedAndRetainDistinctDeliveryKeys()
    {
        using var service = Create();
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        for (var index = 0; index < 128; index++) Assert.True(service.Save(Rule("event-" + index)));
        Assert.False(service.Save(Rule("too-many")));
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        Assert.Equal(128, notices.Count);
        Assert.Equal(128, notices.DistinctBy(notice => notice.Key).Count());
        Assert.Equal(128, service.Active.Count);
        Assert.Equal(128, service.OverlayRows.Count); // Native panel limits visible rows and reports the remainder.
    }

    [Fact]
    public void ReceiptLedgerIsBoundedWithoutLosingNewReceipt()
    {
        JsonFile.Save(StorePath, new
        {
            Version = 1, Items = new[] { Rule() },
            Receipts = Enumerable.Range(0, 4096).Select(index => new { Key = "old:" + index, At = _time.GetUtcNow() }).ToArray()
        });
        using var service = Create();
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        using var saved = JsonDocument.Parse(File.ReadAllText(StorePath));
        var receipts = saved.RootElement.GetProperty("Receipts");
        Assert.Equal(4096, receipts.GetArrayLength());
        Assert.StartsWith("event:fixture:", receipts[4095].GetProperty("Key").GetString());
    }

    [Fact]
    public void FailedAtomicSaveKeepsInMemoryRulesAndReportsFailure()
    {
        using var service = Create();
        Assert.True(service.Save(Rule()));
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        Assert.False(service.Save(Rule() with { Title = "Changed" }));
        Assert.Equal("Fixture event", Assert.Single(service.Items).Title);
        Assert.NotNull(service.Status);
    }

    [Fact]
    public void UnsupportedVersionIsPreservedInsteadOfOverwritten()
    {
        File.WriteAllText(StorePath, """{"Version": 500, "Items": []}""");
        var before = File.ReadAllText(StorePath);
        using var service = Create();
        Assert.NotNull(service.Status);
        Assert.False(service.Save(Rule()));
        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    [Fact]
    public void NullListsAndInvalidLoadedRulesStaySafeAndDisabled()
    {
        File.WriteAllText(StorePath, """
            {"Version":1,"Items":[null,{"Id":"broken","Title":"Broken","Schedule":null,"Enabled":true}],"Receipts":null,"BossPresentation":null}
            """);
        using var service = Create();
        Assert.False(Assert.Single(service.Items).Enabled);
        Assert.Empty(service.Upcoming);
        Assert.Empty(service.OverlayRows);
        service.Tick();
        Assert.Empty(service.Active);
    }

    [Fact]
    public void CorruptJsonIsBackedUpAndDoesNotCrashTheTab()
    {
        File.WriteAllText(StorePath, "broken-json");
        using var service = Create();
        Assert.Empty(service.Items);
        Assert.Single(Directory.GetFiles(_folder, "alerts.json.defekt-*"));
        Assert.True(service.Save(Rule()));
    }

    [Fact]
    public void BossPresentationOverridesGlobalSoundAndSurvivesRestart()
    {
        using (var service = Create())
        {
            Assert.False(service.GetBossPresentation("1110:111001").PlaySound);
            Assert.True(service.SetBossPresentation("1110:111001", new() { PlaySound = true, AlwaysShowOverlay = true, Sound = "beep" }));
        }
        using var restarted = Create();
        var presentation = restarted.GetBossPresentation("1110:111001");
        Assert.True(presentation.PlaySound);
        Assert.True(presentation.AlwaysShowOverlay);
        Assert.Equal("standard", presentation.Sound);
        Assert.False(restarted.SetBossPresentation("1110:111001", new() { Sound = "invalid" }));
        Assert.Equal(presentation, restarted.GetBossPresentation("1110:111001"));
    }

    [Fact]
    public void DeliverySubscriberFailureDoesNotPreventOtherSubscribers()
    {
        using var service = Create();
        var count = 0;
        service.NoticeRaised += _ => throw new InvalidOperationException("fixture");
        service.NoticeRaised += _ => count++;
        Assert.True(service.Save(Rule()));
        service.Preview("fixture");
        Assert.Equal(1, count);
    }

    [Fact]
    public void DisposePreventsFurtherTicksAndMutations()
    {
        var service = Create();
        Assert.True(service.Save(Rule()));
        var count = 0;
        service.NoticeRaised += _ => count++;
        service.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        service.Preview("fixture");
        Assert.False(service.Save(Rule("new")));
        Assert.False(service.Remove("fixture"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExistingBossPolicyProvidesSingleWarningAndConfirmedSpawn()
    {
        await using var fixture = new BossFixture(_folder, _time);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        fixture.Upcoming(10);
        var warning = Assert.Single(notices);
        Assert.False(warning.Preview);
        Assert.Equal("boss:1110:111001", Assert.Single(service.OverlayRows).Key);
        service.Tick();
        Assert.Single(notices);
        _time.Advance(TimeSpan.FromSeconds(10));
        service.Tick();
        Assert.Single(notices); // An expired countdown alone does not prove a spawn.
        Assert.True(Assert.Single(service.OverlayRows).Provisional);
        Assert.False(Assert.Single(service.OverlayRows).Started);
        fixture.Spawned();
        Assert.Equal(2, notices.Count);
        Assert.True(Assert.Single(service.OverlayRows).Started);
        fixture.Settings.Update(settings => settings.BossAlertIds = []);
        Assert.Empty(service.OverlayRows);
    }

    [Fact]
    public async Task DisablingOneBossReminderRemovesItsBannerButKeepsTheTimerAndSelection()
    {
        await using var fixture = new BossFixture(_folder, _time);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        Assert.True(service.SetBossPresentation("1110:111001", new() { AlwaysShowOverlay = true }));
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        fixture.Upcoming(10);
        Assert.Single(service.NotificationRows);
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = false, LeadSeconds = 120 }));
        Assert.Empty(service.NotificationRows);
        Assert.Empty(service.Active);
        Assert.Single(service.CountdownRows);
        Assert.Equal(new[] { "1110:111001" }, fixture.Settings.Current.BossAlertIds);
        fixture.Spawned();
        Assert.Single(notices);
        // The explicit test action remains available even with the personal reminder switched off.
        fixture.Bosses.PreviewAlert("1110:111001");
        Assert.True(Assert.Single(service.NotificationRows).Preview);
    }

    [Fact]
    public async Task CachedBossRowsStaySilentAndConfirmedRowsDoNotExpireWithListAge()
    {
        var cache = Path.Combine(_folder, "boss-cache.json");
        Assert.True(new BossTimerCache(cache, _time).Save(new(1110,
            [new BossSpawn(111001, false, _time.GetUtcNow().AddSeconds(10).ToUnixTimeMilliseconds())],
            _time.GetUtcNow().ToUnixTimeMilliseconds(), TimeSpan.Zero)));
        await using var fixture = new BossFixture(_folder, _time, cache);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        Assert.True(service.SetBossPresentation("1110:111001", new() { AlwaysShowOverlay = true, PlaySound = true }));
        var count = 0;
        service.NoticeRaised += _ => count++;
        service.Tick();
        Assert.True(Assert.Single(service.OverlayRows).Provisional);
        _time.Advance(TimeSpan.FromSeconds(20));
        service.Tick();
        Assert.Equal(0, count);
        Assert.False(Assert.Single(service.OverlayRows).Started);
        fixture.Upcoming(600);
        _time.Advance(TimeSpan.FromSeconds(16));
        service.Tick();
        Assert.False(Assert.Single(service.OverlayRows).Provisional);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SourceGapInvalidatesCountdownAuthorityButKeepsAnAlreadyDeliveredFixedReminder()
    {
        await using var fixture = new BossFixture(_folder, _time);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        Assert.True(service.SetBossPresentation("1110:111001", new() { AlwaysShowOverlay = true, PlaySound = true }));
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        fixture.Upcoming(600);
        _time.Advance(TimeSpan.FromMinutes(9));
        // Refresh the actual service without injecting a replacement boss list.
        fixture.Settings.Update(settings => settings.BossRushEnabled = false);
        service.Tick();
        Assert.Single(notices);
        Assert.True(notices[0].PlaySound);
        var row = Assert.Single(service.CountdownRows);
        Assert.False(row.Provisional);
        Assert.False(row.Started);
        fixture.Network.ProcessProgressGap("other-stream");
        Assert.False(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Network.ProcessProgressGap("fixture");
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        Assert.True(Assert.Single(service.CountdownRows).Provisional);
        Assert.Single(fixture.Bosses.Alerts);
        Assert.Single(service.NotificationRows);
        Assert.Single(service.Active);
        fixture.ClockMessage();
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Upcoming(60);
        Assert.False(Assert.Single(fixture.Bosses.Schedule).Cached);
        Assert.Single(notices); // The same cycle's lead is never delivered twice.
        fixture.Network.ProcessProgressGap("*");
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Upcoming(60);
        _time.Advance(TimeSpan.FromMinutes(1));
        service.Tick();
        Assert.Single(notices);
        Assert.True(Assert.Single(service.CountdownRows).Provisional);
        Assert.False(Assert.Single(service.CountdownRows).Started);
        fixture.Spawned();
        Assert.Equal(2, notices.Count);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(Assert.Single(service.CountdownRows).Provisional);
        Assert.True(Assert.Single(service.CountdownRows).Started);
    }

    [Fact]
    public async Task BossCycleReceiptSurvivesRestartButAllowsConfirmedSpawn()
    {
        var cycle = _time.GetUtcNow().AddSeconds(10);
        await using (var first = new BossFixture(_folder, _time))
        using (var service = new AlertsService(StorePath, _time, bosses: first.Bosses, settings: first.Settings))
        {
            first.Upcoming(10);
            Assert.Single(service.Active);
        }
        _time.Advance(TimeSpan.FromSeconds(1));
        await using var restartedBosses = new BossFixture(_folder, _time);
        using var restarted = new AlertsService(StorePath, _time, bosses: restartedBosses.Bosses, settings: restartedBosses.Settings);
        List<AlertNotice> notices = [];
        restarted.NoticeRaised += notices.Add;
        restartedBosses.Upcoming(9); // Same server cycle, new local notice id and delivery time.
        Assert.Empty(notices);
        Assert.Empty(restarted.Active);
        _time.Advance(cycle - _time.GetUtcNow());
        restartedBosses.Spawned();
        var spawned = Assert.Single(notices);
        Assert.EndsWith(":spawn", spawned.Key);
        Assert.True(Assert.Single(restarted.OverlayRows).Started);
        restarted.Tick();
        Assert.Single(notices);
    }

    [Fact]
    public async Task BossPreviewWorksWithoutLiveScheduleAndDoesNotWriteReceipts()
    {
        await using var fixture = new BossFixture(_folder, _time);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        fixture.Bosses.PreviewAlert();
        Assert.True(Assert.Single(notices).Preview);
        Assert.Single(service.OverlayRows);
        Assert.False(File.Exists(StorePath));
        service.Tick();
        Assert.Single(notices);
    }

    [Fact]
    public async Task BossReminderBannerIsIndependentOfItsLegacyCountdownWindow()
    {
        await using var fixture = new BossFixture(_folder, _time);
        fixture.Settings.Current.BossAlertLeadSeconds = 3600;
        fixture.Settings.Current.BossAlertSound = true;
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        Assert.True(service.SetBossPresentation("1110:111001", new() { OverlayLeadMinutes = 1, PlaySound = true }));
        fixture.Upcoming(600);
        var warning = Assert.Single(service.Active);
        Assert.True(warning.ShowOverlay);
        Assert.True(warning.PlaySound);
        Assert.Single(service.NotificationRows);
        Assert.Single(service.OverlayRows);
        Assert.Empty(service.CountdownRows);
    }

    [Fact]
    public void CatalogTimersExistImmediatelyWithoutEnablingRemindersOrWriting()
    {
        using var service = Create();
        Assert.Equal(6, service.EventTimers.Count);
        Assert.All(service.EventTimers, timer =>
        {
            Assert.Null(timer.Reminder);
            Assert.False(timer.Presentation.ShowOverlay);
            Assert.Null(AlertSchedules.Error(timer.Definition.Schedule));
        });
        Assert.False(service.GetEventReminder("shugo").Enabled);
        Assert.Equal("catalog:shugo", service.GetEventReminder("shugo").Id);
        Assert.Empty(service.Items);
        Assert.Empty(service.OverlayRows);
        _time.Advance(TimeSpan.FromHours(3));
        service.Tick();
        Assert.Empty(service.Active);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WidgetCanRunWithNoReminderAndNoNotification()
    {
        using var service = Create();
        var raised = 0;
        service.NoticeRaised += _ => raised++;
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true }));
        var row = Assert.Single(service.OverlayRows);
        Assert.Equal("event:catalog:shugo", row.Key);
        Assert.Equal(UiText.T("Startet in"), row.CountdownLabel);
        Assert.Empty(service.Items);
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        service.Tick();
        row = Assert.Single(service.OverlayRows);
        Assert.False(row.Started);
        Assert.Equal(UiText.T("Startet in"), row.CountdownLabel);
        Assert.Equal(_time.GetUtcNow().AddHours(1), row.At);
        Assert.Equal(0, raised);
        Assert.Empty(service.Active);
    }

    [Fact]
    public void RiftWidgetCountsDownPortalThenEventAndHidesOutsideWindow()
    {
        _time.Set(DateTimeOffset.Parse("2026-10-12T18:05:00Z", global::System.Globalization.CultureInfo.InvariantCulture));
        using var service = Create();
        Assert.True(service.SetEventPresentation("rift", new() { ShowOverlay = true, OverlayLeadMinutes = 5 }));
        var row = Assert.Single(service.OverlayRows);
        Assert.True(row.Started);
        Assert.Equal(UiText.T("Portal schließt in"), row.CountdownLabel);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), row.At);
        _time.Advance(TimeSpan.FromMinutes(5));
        row = Assert.Single(service.OverlayRows);
        Assert.Equal(UiText.T("Endet in"), row.CountdownLabel);
        Assert.Equal(_time.GetUtcNow().AddMinutes(50), row.At);
        _time.Advance(TimeSpan.FromMinutes(50));
        Assert.Empty(service.OverlayRows);
        Assert.Equal(EventWindowPhase.Upcoming, service.EventTimers.Single(timer => timer.Definition.Id == "rift").Window.Phase);
    }

    [Fact]
    public void WidgetWindowBeginsAtExactLeadBoundary()
    {
        using var service = Create();
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true, OverlayLeadMinutes = 1 }));
        Assert.Empty(service.OverlayRows);
        _time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));
        Assert.Single(service.OverlayRows);
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(service.OverlayRows);
    }

    [Fact]
    public void ReminderCanNotifyWithoutEnablingWidgetAndDoesNotLoseItsLeadBanner()
    {
        using var service = Create();
        var reminder = service.GetEventReminder("shugo") with { Enabled = true, LeadMinutes = 5,
            OverlayLeadMinutes = 0, PlaySound = false, ShowOverlay = true };
        Assert.True(service.Save(reminder));
        Assert.Empty(service.OverlayRows); // No generic catalog countdown without an enabled widget.
        _time.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        var notice = Assert.Single(service.Active);
        Assert.True(notice.ShowOverlay);
        Assert.False(notice.PlaySound);
        Assert.Single(service.OverlayRows); // The optional reminder banner is independent of the widget.
        _time.Advance(TimeSpan.FromSeconds(reminder.DurationSeconds));
        service.Tick();
        Assert.Empty(service.OverlayRows);
        Assert.False(service.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
    }

    [Fact]
    public void WidgetAndReminderNeverDuplicateTheCatalogEventOrReplaceItsLivePhase()
    {
        _time.Set(DateTimeOffset.Parse("2026-10-12T18:05:00Z", global::System.Globalization.CultureInfo.InvariantCulture));
        using var service = Create();
        Assert.True(service.SetEventPresentation("rift", new() { ShowOverlay = true, AlwaysShowOverlay = true }));
        Assert.True(service.Save(service.GetEventReminder("rift") with { Enabled = true, AlwaysShowOverlay = true }));
        service.Preview("catalog:rift");
        var row = Assert.Single(service.OverlayRows);
        Assert.Equal(UiText.T("Portal schließt in"), row.CountdownLabel);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), row.At);
        service.SetEventReminderEnabled("rift", false);
        Assert.Single(service.OverlayRows);
        Assert.True(service.SetEventPresentation("rift", new() { ShowOverlay = false }));
        Assert.Empty(service.OverlayRows);
    }

    [Fact]
    public void EventPresentationPersistsIndependentlyAcrossOtherSavesAndRestarts()
    {
        using (var service = Create())
        {
            Assert.True(service.SetEventPresentation("invasion", new() { ShowOverlay = true, AlwaysShowOverlay = true, OverlayLeadMinutes = 3 }));
            Assert.True(service.Save(Rule()));
            _time.Advance(TimeSpan.FromSeconds(1));
            service.Tick();
        }
        using var restarted = Create();
        var presentation = restarted.EventTimers.Single(timer => timer.Definition.Id == "invasion").Presentation;
        Assert.True(presentation.ShowOverlay);
        Assert.True(presentation.AlwaysShowOverlay);
        Assert.Equal(3, presentation.OverlayLeadMinutes);
        Assert.Equal("fixture", Assert.Single(restarted.Items).Id);
        Assert.Contains(restarted.OverlayRows, row => row.Key == "event:catalog:invasion");
    }

    [Fact]
    public void InvalidEventPresentationCannotReplaceSavedPreferences()
    {
        using var service = Create();
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true }));
        var before = File.ReadAllText(StorePath);
        Assert.False(service.SetEventPresentation("shugo", new() { OverlayLeadMinutes = -1 }));
        Assert.False(service.SetEventPresentation("unknown", new()));
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.True(service.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
    }

    [Fact]
    public void CatalogSchedulesStayFixedWhileOwnSchedulesRemainUntouched()
    {
        using var service = Create();
        var oldSiege = Rule("own-siege") with { Schedule = new() { Kind = AlertScheduleKind.EveryNDays,
            AnchorDate = new(2026, 10, 12), EveryDays = 2, Times = [new(21, 0)] } };
        Assert.True(service.Save(oldSiege));
        Assert.True(service.Save(service.GetEventReminder("abyss") with { Enabled = true, Schedule = oldSiege.Schedule }));
        var own = service.Items.Single(item => item.Id == "own-siege");
        Assert.Equal(AlertScheduleKind.EveryNDays, own.Schedule.Kind);
        Assert.Equal(2, own.Schedule.EveryDays);
        var catalog = service.Items.Single(item => item.Id == "catalog:abyss");
        Assert.Equal(AlertScheduleKind.Weekly, catalog.Schedule.Kind);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday }, catalog.Schedule.Weekdays);
        Assert.Equal(JsonSerializer.Serialize(own), JsonSerializer.Serialize(service.Items.Single(item => item.Id == "own-siege")));
    }

    [Fact]
    public void LegacyStoreLoadsWithoutMigrationWritesAndCatalogRulesNormalizeInMemory()
    {
        var own = Rule();
        var oldCatalog = Rule("catalog:abyss") with { Schedule = new() { Kind = AlertScheduleKind.Daily, Times = [new(1, 0)] } };
        JsonFile.Save(StorePath, new { Version = 1, Items = new[] { own, oldCatalog } });
        var before = File.ReadAllText(StorePath);
        using var service = Create();
        Assert.Equal(6, service.EventTimers.Count);
        Assert.Equal(AlertScheduleKind.Weekly, service.GetEventReminder("abyss").Schedule.Kind);
        Assert.Empty(service.GetEventReminder("abyss").SourceLabel);
        Assert.Equal(EventCatalog.SourceUrl, service.GetEventReminder("abyss").SourceUrl);
        Assert.Equal(JsonSerializer.Serialize(own), JsonSerializer.Serialize(service.Items.Single(item => item.Id == own.Id)));
        service.Tick();
        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    [Fact]
    public void UnknownCatalogReminderIsDisabledOnLoadAndCannotBeNewlySaved()
    {
        JsonFile.Save(StorePath, new { Version = 1, Items = new[] { Rule("catalog:unknown") } });
        using var service = Create();
        Assert.False(Assert.Single(service.Items).Enabled);
        Assert.False(service.Save(Rule("catalog:unknown")));
        Assert.Throws<ArgumentException>(() => service.GetEventReminder("unknown"));
    }

    [Fact]
    public void CallerCannotMutateSharedCatalogSchedulesOrReminderRules()
    {
        using var service = Create();
        var timer = service.EventTimers.Single(item => item.Definition.Id == "rift");
        timer.Definition.Schedule.Times[0] = new(1, 0);
        Assert.Equal(new TimeOnly(2, 0), service.EventTimers.Single(item => item.Definition.Id == "rift").Definition.Schedule.Times[0]);
        Assert.True(service.SetEventReminderEnabled("rift", true));
        var reminder = service.GetEventReminder("rift");
        reminder.Schedule.Times[0] = new(1, 0);
        Assert.Equal(new TimeOnly(2, 0), service.GetEventReminder("rift").Schedule.Times[0]);
    }

    [Fact]
    public void ReadingAndTickingVisibleWidgetsDoesNotRewriteStorage()
    {
        using var service = Create();
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true }));
        var before = File.ReadAllText(StorePath);
        var oldTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StorePath, oldTime);
        _ = service.EventTimers;
        _ = service.OverlayRows;
        _time.Advance(TimeSpan.FromMinutes(8));
        service.Tick();
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(StorePath));
    }

    [Fact]
    public void CustomMp3PersistsAndReachesPreviewLeadAndStartNotices()
    {
        var file = Path.Combine(_folder, "event-tone.mp3");
        using (var service = Create())
        {
            List<AlertNotice> notices = [];
            service.NoticeRaised += notices.Add;
            Assert.True(service.Save(Rule() with { Sound = "custom", SoundFile = file }));
            service.Preview("fixture");
            _time.Advance(TimeSpan.FromSeconds(1));
            service.Tick();
            _time.Advance(TimeSpan.FromMinutes(5));
            service.Tick();
            Assert.Equal(3, notices.Count);
            Assert.All(notices, notice => { Assert.Equal("custom", notice.Sound); Assert.Equal(file, notice.SoundFile); });
        }
        using var restarted = Create();
        var saved = Assert.Single(restarted.Items);
        Assert.Equal("custom", saved.Sound);
        Assert.Equal(file, saved.SoundFile);
        Assert.True(saved.Enabled);
        Assert.False(File.Exists(file)); // Temporary absence does not disable the overlay reminder.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tone.mp3")]
    [InlineData("https://example.invalid/tone.mp3")]
    [InlineData(@"\\server\share\tone.mp3")]
    [InlineData(@"C:\tone.wav")]
    [InlineData(@"C:\tone:stream.mp3")]
    [InlineData(@"\\?\C:\tone.mp3")]
    public void CustomMp3RequiresALocalAbsoluteMp3Path(string? path)
    {
        Assert.False(AlertsService.ValidSoundFile(path));
        using var service = Create();
        Assert.False(service.Save(Rule() with { Sound = "custom", SoundFile = path }));
        Assert.Empty(service.Items);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void CustomMp3ValidationKeepsExistingSavedAlert()
    {
        using var service = Create();
        var file = Path.Combine(_folder, "selected.MP3");
        Assert.True(AlertsService.ValidSoundFile(file));
        Assert.True(service.Save(Rule() with { Sound = "custom", SoundFile = file }));
        var before = File.ReadAllText(StorePath);
        Assert.False(service.Save(Rule() with { Sound = "custom", SoundFile = null }));
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(file, Assert.Single(service.Items).SoundFile);
    }

    [Fact]
    public async Task BossCustomMp3IsIndependentAndReachesPreviewAndCapturedWarnings()
    {
        await using var fixture = new BossFixture(_folder, _time);
        using var service = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        var file = Path.Combine(_folder, "boss-tone.mp3");
        Assert.True(service.SetBossPresentation("1110:111001", new() { PlaySound = true, Sound = "custom", SoundFile = file }));
        List<AlertNotice> notices = [];
        service.NoticeRaised += notices.Add;
        fixture.Bosses.PreviewAlert();
        fixture.Upcoming(30);
        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice => { Assert.True(notice.PlaySound); Assert.Equal("custom", notice.Sound); Assert.Equal(file, notice.SoundFile); });
        using var restarted = new AlertsService(StorePath, _time, bosses: fixture.Bosses, settings: fixture.Settings);
        Assert.Equal(file, restarted.GetBossPresentation("1110:111001").SoundFile);
        Assert.False(service.SetBossPresentation("1110:111001", new() { Sound = "custom", SoundFile = "invalid.mp3" }));
        Assert.Equal(file, service.GetBossPresentation("1110:111001").SoundFile);
    }

    [Fact]
    public void NotificationBannersAreSeparateFromCountdownHud()
    {
        using var service = Create();
        Assert.True(service.Save(Rule() with { OverlayLeadMinutes = 0 }));
        service.Preview("fixture");
        Assert.Single(service.Active);
        Assert.Single(service.NotificationRows);
        Assert.Empty(service.CountdownRows);
        Assert.Single(service.OverlayRows); // Compatibility view still exposes the transient banner.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(service.NotificationRows);
        Assert.Empty(service.CountdownRows);
    }

    [Fact]
    public void CatalogCountdownAndTransientBannerKeepSeparateLifetimes()
    {
        using var service = Create();
        Assert.True(service.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true }));
        Assert.True(service.SetEventReminderEnabled("shugo", true));
        service.Preview("catalog:shugo");
        Assert.Single(service.NotificationRows);
        Assert.Single(service.CountdownRows);
        Assert.Single(service.OverlayRows);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(service.NotificationRows);
        Assert.Single(service.CountdownRows);
        Assert.Single(service.OverlayRows);
    }

    [Fact]
    public void SoundOnlyNoticeDoesNotBecomeBannerOrCountdown()
    {
        using var service = Create();
        Assert.True(service.Save(Rule() with { ShowOverlay = false, PlaySound = true }));
        service.Preview("fixture");
        Assert.Single(service.Active);
        Assert.Empty(service.NotificationRows);
        Assert.Empty(service.CountdownRows);
    }

    [Fact]
    public void NotificationReadHidesFutureNoticesBeforeTheNextTick()
    {
        using var service = Create();
        Assert.True(service.Save(Rule()));
        service.Preview("fixture");
        Assert.Single(service.NotificationRows);
        _time.Set(_time.GetUtcNow().AddSeconds(-1));
        Assert.Empty(service.NotificationRows);
        Assert.Empty(service.Active);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-12T18:54:59Z", global::System.Globalization.CultureInfo.InvariantCulture);
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span) { _now += span; _stamp += Math.Max(0, span.Ticks); }
        public void Set(DateTimeOffset now) { _stamp += Math.Max(0, (now - _now).Ticks); _now = now; }
    }

    private sealed class BossFixture : IAsyncDisposable
    {
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly ManualTime _time;
        public SettingsService Settings { get; }
        public BossRushService Bosses { get; }
        public NetworkLootService Network => _provider.GetRequiredService<NetworkLootService>();
        public void ClockMessage() => Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(_time.GetUtcNow().ToUnixTimeMilliseconds())]);
        public BossFixture(string folder, ManualTime time, string? cache = null)
        {
            _time = time;
            var map = Path.Combine(folder, "map");
            Directory.CreateDirectory(Path.Combine(map, "altgard"));
            File.WriteAllText(Path.Combine(map, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
            File.WriteAllText(Path.Combine(map, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[{"spawnId":111001,"npcId":2400017,"en":"Fixture boss","x":100,"y":200}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", map);
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
            Settings.Current.BossAlertIds = ["1110:111001"];
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.BossAlertDurationSeconds = 30;
            Bosses = new(_provider.GetRequiredService<ProgressService>(), Settings,
                _provider.GetRequiredService<MapTargetsService>(), _provider.GetRequiredService<NetworkLootService>(),
                time, cache ?? Path.Combine(folder, "boss-cache.json"));
        }
        public void Upcoming(int seconds)
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            Bosses.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + seconds * 1000L), 0, 0, 0]);
        }
        public void Spawned()
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            Bosses.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                1, 0x99, 0xe3, 6, .. new byte[12], 1, .. BitConverter.GetBytes(now), 0, 0, 0]);
        }
        public async ValueTask DisposeAsync()
        {
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _settingsBackup);
            _ = new SettingsService();
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
