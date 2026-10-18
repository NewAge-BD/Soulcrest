using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class TimerOverlayPresentationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);
    private readonly string _language = UiText.Language;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-strip-" + Guid.NewGuid().ToString("N"));
    public TimerOverlayPresentationTests() { Directory.CreateDirectory(_folder); UiText.Language = "en"; }

    [Fact]
    public void NewSettingsKeepLegacyNullDistinctFromAnExplicitEmptySelection()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        TimerOverlayPresentation.NormalizeSettings(defaults);
        Assert.Null(defaults.TimerOverlayTimerIds);
        Assert.Equal(18, defaults.TimerOverlayGap);
        Assert.True(defaults.TimerOverlayShowSeconds);
        Assert.Equal(0, defaults.TimerOverlayLeadMinutes);
        defaults.TimerOverlayTimerIds = [];
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(defaults))!;
        TimerOverlayPresentation.NormalizeSettings(restored);
        Assert.NotNull(restored.TimerOverlayTimerIds);
        Assert.Empty(restored.TimerOverlayTimerIds!);
    }

    [Fact]
    public void FormerBackgroundPreferenceIsIgnoredWithoutChangingTimerSelection()
    {
        var restored = JsonSerializer.Deserialize<AppSettings>("""{"TimerOverlayBackground":true,"TimerOverlayTimerIds":["event:catalog:shugo"],"AlertsOverlayScale":1.25}""")!;
        Assert.Equal(new[] { "event:catalog:shugo" }, restored.TimerOverlayTimerIds);
        Assert.Equal(1.25, restored.AlertsOverlayScale);
        Assert.DoesNotContain("TimerOverlayBackground", JsonSerializer.Serialize(restored));
    }

    [Theory]
    [InlineData(-1, -5, 4, 0)]
    [InlineData(18, 15, 18, 15)]
    [InlineData(int.MaxValue, int.MaxValue, 48, 10080)]
    public void SettingsNormalizeSpacingAndWindowWithoutChangingOtherOverlays(int gap, int lead, int expectedGap, int expectedLead)
    {
        var settings = new AppSettings { TimerOverlayGap = gap, TimerOverlayLeadMinutes = lead,
            TimerOverlayTimerIds = ["event:catalog:shugo", "", " ", null!, "event:catalog:shugo", "boss:1:2", "BOSS:1:2"],
            AlertsOverlayScale = 1.4, AlertNotificationsWidth = 577, EventTimelineOverlayWidth = 911 };
        TimerOverlayPresentation.NormalizeSettings(settings);
        Assert.Equal(expectedGap, settings.TimerOverlayGap);
        Assert.Equal(expectedLead, settings.TimerOverlayLeadMinutes);
        Assert.Equal(new[] { "event:catalog:shugo", "boss:1:2", "BOSS:1:2" }, settings.TimerOverlayTimerIds);
        Assert.Equal(1.4, settings.AlertsOverlayScale);
        Assert.Equal(577, settings.AlertNotificationsWidth);
        Assert.Equal(911, settings.EventTimelineOverlayWidth);
    }

    [Fact]
    public void FreshStripDefaultsToFourEventsWithoutEnablingOrPersistingAnyReminder()
    {
        var path = Path.Combine(_folder, "alerts.json");
        using var service = new AlertsService(path, new FixedTime(Now));
        Assert.Equal(6, service.TimerOverlayChoices.Count);
        Assert.Equal(new[] { "shugo", "rift", "invasion", "abyss" },
            service.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.IconId));
        Assert.All(service.TimerOverlayChoices, choice => Assert.True(choice.Available));
        Assert.Equal(4, service.TimerOverlayRows.Count);
        Assert.Empty(service.CountdownRows);
        Assert.Empty(service.Items);
        Assert.Empty(service.NotificationRows);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExistingEventAndCustomSelectionIsInheritedWithoutAddingTheFreshDefaults()
    {
        var path = Path.Combine(_folder, "alerts.json");
        using var service = new AlertsService(path, new FixedTime(Now));
        Assert.True(service.SetEventPresentation("weekly-reset", new() { ShowOverlay = true }));
        Assert.True(service.Save(Custom("mine") with { ShowOverlay = true }));
        Assert.True(service.Save(Custom("sound-only") with { ShowOverlay = false, PlaySound = true }));
        var before = File.ReadAllBytes(path);
        Assert.Equal(new[] { "event:catalog:weekly-reset", "event:mine" },
            service.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.Key));
        Assert.Equal(new[] { "event:catalog:weekly-reset", "event:mine" }, service.TimerOverlayRows.Select(row => row.Key));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.All(service.EventTimers, timer => Assert.False(timer.Reminder?.Enabled ?? false));
    }

    [Fact]
    public void ExplicitEmptyAndUnknownSelectionsStayEmptyAndNeverEnableLegacyDefaults()
    {
        var candidates = Events(Now);
        Assert.Empty(TimerOverlayPresentation.Rows(candidates, [], Now, 0));
        Assert.Empty(TimerOverlayPresentation.Rows(candidates, ["unknown", "event:shugo"], Now, 0));
        Assert.All(TimerOverlayPresentation.Choices(candidates, []), choice => Assert.False(choice.Selected));
        Assert.Null(TimerOverlayPresentation.NormalizeIds(null));
        Assert.Empty(TimerOverlayPresentation.NormalizeIds([])!);
    }

    [Theory]
    [InlineData("2026-10-12T17:59:59.999Z", "2026-10-12T18:00:00Z", false, "Starts in", "00:00:01")]
    [InlineData("2026-10-12T18:00:00Z", "2026-10-12T18:10:00Z", true, "Portal closes in", "00:10:00")]
    [InlineData("2026-10-12T18:05:00Z", "2026-10-12T18:10:00Z", true, "Portal closes in", "00:05:00")]
    [InlineData("2026-10-12T18:09:59.999Z", "2026-10-12T18:10:00Z", true, "Portal closes in", "00:00:01")]
    [InlineData("2026-10-12T18:10:00Z", "2026-10-12T21:00:00Z", false, "Starts in", "02:50:00")]
    [InlineData("2026-10-12T21:00:00Z", "2026-10-12T21:10:00Z", true, "Portal closes in", "00:10:00")]
    public void SelectedRiftUsesTheSharedOpeningAndNextRiftTargetsWithoutAnEnabledReminder(string instant, string target,
        bool open, string label, string counter)
    {
        var now = DateTimeOffset.Parse(instant, global::System.Globalization.CultureInfo.InvariantCulture);
        var candidates = Events(now);
        var row = Assert.Single(TimerOverlayPresentation.Rows(candidates, ["event:catalog:rift"], now, 0));
        Assert.Equal(open, row.Started);
        Assert.Equal(DateTimeOffset.Parse(target, global::System.Globalization.CultureInfo.InvariantCulture), row.At);
        Assert.Equal(label, row.CountdownLabel);
        Assert.Equal("rift", row.IconId);
        Assert.Equal(open ? "Spacetime Rift Open" : "Spacetime Rift", row.Title);
        Assert.Equal(open ? "Spacetime Rift Open" : null, row.InlineLabel);
        Assert.Equal(counter, TimerOverlayPresentation.Countdown(row, now));
        Assert.Null(Event("rift", now).Reminder);
        Assert.False(Event("rift", now).Presentation.ShowOverlay);
        if (!open && row.At - now > TimeSpan.FromMinutes(1))
            Assert.Empty(TimerOverlayPresentation.Rows(candidates, [row.Key], now, 1));
    }

    [Fact]
    public void GlobalLeadStartsAtTheExactBoundaryWhilePermanentModeKeepsFutureTimers()
    {
        TimerOverlayCandidate[] candidates = [Candidate("far", Now.AddMinutes(6)), Candidate("edge", Now.AddMinutes(5)),
            Candidate("near", Now.AddSeconds(1)), Candidate("active", Now.AddHours(1), started: true, label: "Ends in")];
        var ids = candidates.Select(candidate => candidate.Key).ToArray();
        Assert.Equal(new[] { "active", "edge", "near" }, TimerOverlayPresentation.Rows(candidates, ids, Now, 5).Select(row => row.Key));
        Assert.Equal(new[] { "active", "edge", "far", "near" }, TimerOverlayPresentation.Rows(candidates, ids, Now, 0).Select(row => row.Key));
    }

    [Theory]
    [InlineData(false, false, 60, false, "00:01:00")]
    [InlineData(true, false, 60, true, "00:01:00")]
    [InlineData(false, false, 0, true, "?")]
    [InlineData(true, true, 0, true, "?")]
    [InlineData(false, true, 0, false, "Now")]
    public void BossProjectionNeverPromotesCachedOrElapsedEvidenceToAConfirmedSpawn(bool cached, bool spawned,
        int remaining, bool provisional, string counter)
    {
        var choice = Boss();
        var candidate = TimerOverlayPresentation.FromBoss(choice,
            new(choice, spawned, TimeSpan.FromSeconds(remaining), TimeSpan.FromHours(2), cached), false, "Synthetic boss", Now);
        var row = Assert.Single(TimerOverlayPresentation.Rows([candidate], [candidate.Key], Now, 0));
        Assert.Equal(provisional, row.Provisional);
        Assert.Equal(counter, TimerOverlayPresentation.Countdown(row, Now));
        Assert.Equal("boss", row.IconId);
    }

    [Fact]
    public void UncapturedSelectedBossRemainsAChoiceWithoutAnInventedCountdown()
    {
        var candidate = TimerOverlayPresentation.FromBoss(Boss(), null, true, "Synthetic boss", Now);
        var choice = Assert.Single(TimerOverlayPresentation.Choices([candidate], null));
        Assert.True(choice.Selected);
        Assert.False(choice.Available);
        Assert.Equal("boss", choice.IconId);
        Assert.Empty(TimerOverlayPresentation.Rows([candidate], null, Now, 0));
    }

    [Fact]
    public void BossArtworkIsSharedByChoicesAndRowsWithoutChangingTimerOrSourceEvidence()
    {
        var source = Boss() with { Boss = Boss().Boss with { Icon = @"icons\portraits\synthetic-boss.png" } };
        var timer = new BossRushEntry(source, false, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2), Cached: true);
        var candidate = TimerOverlayPresentation.FromBoss(source, timer, true, "Synthetic boss", Now);
        var choice = Assert.Single(TimerOverlayPresentation.Choices([candidate], null));
        var row = Assert.Single(TimerOverlayPresentation.Rows([candidate], null, Now, 0));
        Assert.Equal("map:icons/portraits/synthetic-boss.png", choice.IconId);
        Assert.Equal(choice.IconId, row.IconId);
        Assert.True(row.Provisional);
        Assert.Equal(Now.AddMinutes(5), row.At);
        Assert.Equal("00:05:00", TimerOverlayPresentation.Countdown(row, Now));
        var uncaptured = TimerOverlayPresentation.FromBoss(source, null, true, "Synthetic boss", Now);
        Assert.Equal(choice.IconId, Assert.Single(TimerOverlayPresentation.Choices([uncaptured], null)).IconId);
        Assert.Empty(TimerOverlayPresentation.Rows([uncaptured], null, Now, 0));
        Assert.Equal(@"icons\portraits\synthetic-boss.png", source.Boss.Icon);
        Assert.True(timer.Cached);
        Assert.False(timer.Spawned);
    }

    [Fact]
    public void ResetAndCustomTimersKeepTheirOwnIconTypesAndUnchangedScheduling()
    {
        foreach (var definition in EventCatalog.All)
        {
            var timer = Event(definition.Id, Now);
            var schedule = JsonSerializer.Serialize(timer.Definition.Schedule);
            var candidate = TimerOverlayPresentation.FromEvent(definition, timer);
            Assert.Equal(definition.Id, candidate.IconId);
            Assert.Equal(definition.Id, candidate.Row!.IconId);
            Assert.Equal(schedule, JsonSerializer.Serialize(timer.Definition.Schedule));
            Assert.Null(timer.Reminder);
            Assert.False(timer.Presentation.ShowOverlay);
        }
        var alert = Custom("custom-icon") with { ShowOverlay = false, PlaySound = true };
        var expected = AlertSchedules.Next(alert.Schedule, Now);
        var custom = TimerOverlayPresentation.FromCustom(alert, Now);
        Assert.Equal("custom", custom.IconId);
        Assert.Equal("custom", custom.Row!.IconId);
        Assert.Equal(expected, custom.Row.At);
        Assert.False(alert.ShowOverlay);
        Assert.True(alert.PlaySound);
    }

    [Fact]
    public void ExplicitChoiceOverridesLegacyOverlayFlagsWithoutChangingTheirSources()
    {
        var timer = Event("shugo", Now);
        Assert.False(timer.Presentation.ShowOverlay);
        Assert.Null(timer.Reminder);
        var alert = Custom("silent") with { ShowOverlay = false, PlaySound = true };
        TimerOverlayCandidate[] candidates = [TimerOverlayPresentation.FromEvent(timer.Definition, timer),
            TimerOverlayPresentation.FromCustom(alert, Now)];
        var rows = TimerOverlayPresentation.Rows(candidates, candidates.Select(candidate => candidate.Key), Now, 0);
        Assert.Equal(2, rows.Count);
        Assert.False(timer.Presentation.ShowOverlay);
        Assert.False(alert.ShowOverlay);
        Assert.True(alert.PlaySound);
    }

    [Fact]
    public void DisabledInvalidAndExpiredCustomRulesRemainUnavailableEvenWhenExplicitlySelected()
    {
        var disabled = Custom("disabled") with { Enabled = false };
        var invalid = Custom("invalid") with { Schedule = new() { TimeZoneId = "not-a-zone" } };
        var expired = Custom("expired") with { Schedule = new() { Kind = AlertScheduleKind.Once, At = Now.AddSeconds(-1) } };
        var candidates = new[] { disabled, invalid, expired }.Select(alert => TimerOverlayPresentation.FromCustom(alert, Now)).ToArray();
        var keys = candidates.Select(candidate => candidate.Key).ToArray();
        Assert.All(TimerOverlayPresentation.Choices(candidates, keys), choice =>
        { Assert.True(choice.Selected); Assert.False(choice.Available); });
        Assert.Empty(TimerOverlayPresentation.Rows(candidates, keys, Now, 0));
    }

    [Theory]
    [InlineData(1, true, "00:00:01")]
    [InlineData(1001, true, "00:00:02")]
    [InlineData(59999, true, "00:01:00")]
    [InlineData(1, false, "00:01")]
    [InlineData(60001, false, "00:02")]
    [InlineData(3599999, false, "01:00")]
    public void CountdownRoundsUpInsteadOfDisplayingZeroBeforeTheTarget(int milliseconds, bool seconds, string expected) =>
        Assert.Equal(expected, TimerOverlayPresentation.Countdown(new("fixture", "Fixture", "", "#ffffff",
            Now.AddMilliseconds(milliseconds), false), Now, seconds));

    [Theory]
    [InlineData("de", true, "2T 03:04:05")]
    [InlineData("en", true, "2d 03:04:05")]
    [InlineData("de", false, "2T 03:05")]
    [InlineData("en", false, "2d 03:05")]
    public void LongCountdownsUseCompactLocalizedDays(string language, bool seconds, string expected)
    {
        UiText.Language = language;
        var row = new AlertDisplayRow("fixture", "Fixture", "", "#ffffff", Now.AddDays(2).AddHours(3).AddMinutes(4).AddMilliseconds(4001), false);
        Assert.Equal(expected, TimerOverlayPresentation.Countdown(row, Now, seconds));
        row = row with { At = Now.AddDays(100).AddHours(3) };
        Assert.Equal(UiText.F("{0} T.", 100), TimerOverlayPresentation.Countdown(row, Now, seconds));
    }

    private static TimerOverlayCandidate Candidate(string key, DateTimeOffset at, bool started = false, string? label = null) =>
        new(key, key, null, false, new(key, key, "", "#ffffff", at, started, CountdownLabel: label));
    private static EventAlert Custom(string id) => new() { Id = id, Title = "Synthetic custom timer", Schedule = new() { TimeZoneId = "UTC" } };
    private static BossChoice Boss() => new(1110, "altgard", new BossPlace(111001, 42, "Synthetic boss", null, 10, 20));
    private static EventTimerSnapshot Event(string id, DateTimeOffset now)
    {
        var definition = EventCatalog.Find(id)!;
        return new(definition, EventWindows.At(definition.Schedule, now, TimeSpan.FromMinutes(definition.DurationMinutes),
            TimeSpan.FromMinutes(definition.PortalMinutes))!, null, new());
    }
    private static TimerOverlayCandidate[] Events(DateTimeOffset now) => EventCatalog.All
        .Select(definition => TimerOverlayPresentation.FromEvent(definition, Event(definition.Id, now))).ToArray();
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose() { UiText.Language = _language; Directory.Delete(_folder, recursive: true); }
}
