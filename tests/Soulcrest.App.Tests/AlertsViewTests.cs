using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AlertsViewTests
{
    [Theory]
    [InlineData("de", "Events & Alerts", "Eigener Alert", "Event-Vorlagen")]
    [InlineData("en", "Events & alerts", "Custom alert", "Event templates")]
    public async Task EmptyTabOffersTemplatesAndSharedBossSettingsWithoutStartingCapture(string language, string title, string create, string templates)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        Assert.Contains(title, html);
        Assert.Contains(create, html);
        Assert.Contains(templates, html);
        Assert.Contains("Shugo Festival", html);
        Assert.Contains("Abyss Siege", html);
        Assert.Contains("Europe/Berlin", html);
        Assert.Contains("data-boss-alerts-master", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-boss-alerts-master"));
        Assert.DoesNotContain("boss-alert-card", html);
        Assert.DoesNotContain("id=\"alerts-boss-settings\"", html);
        Assert.True(html.IndexOf("data-boss-alerts-master", StringComparison.Ordinal) > html.IndexOf("data-boss-list", StringComparison.Ordinal));
        foreach (var id in new[] { "alerts", "notifications", "event-timeline" })
            Assert.Contains($"data-overlay-link=\"{id}\"", html);
        Assert.DoesNotContain("id=\"timer-overlay-settings\"", html);
        Assert.DoesNotContain("id=\"event-timeline-visible\"", html);
        Assert.Contains("data-boss-strip=", html);
        Assert.DoesNotContain("Zeitraum auch direkt im Overlay", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "empty");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConfirmedAbyssTemplateIsReviewedBeforeSavingAndKeepsBerlinWeeklySchedule(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var editor = await fixture.Invoke("AddPreset", "abyss");
        Assert.Empty(fixture.Alerts.Items);
        Assert.DoesNotContain("id=\"alert-anchor\"", editor);
        Assert.Contains("alerts-weekdays", editor);
        Assert.Contains("id=\"alert-times\" type=\"text\" placeholder=\"09:00, 21:00\" value=\"21:00\"", editor);
        Assert.DoesNotContain("id=\"alert-confirm\"", editor);
        await fixture.Export(editor, "editor");
        var saved = await fixture.Invoke("Save");
        var alert = Assert.Single(fixture.Alerts.Items);
        Assert.True(alert.Enabled);
        Assert.Equal(AlertScheduleKind.Weekly, alert.Schedule.Kind);
        Assert.Equal("Europe/Berlin", alert.Schedule.TimeZoneId);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday }, alert.Schedule.Weekdays);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero), Assert.Single(fixture.Alerts.Upcoming).At);
        Assert.Contains("21:00 · Europe/Berlin", saved);
        Assert.Contains(language == "de" ? "Lokal:" : "Local:", saved);
        Assert.DoesNotContain("id=\"alert-editor\"", saved);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(saved, "saved");
        Assert.True(fixture.Alerts.Save(AlertsService.FromPreset("shugo", "Europe/Berlin") with { AlwaysShowOverlay = true, PlaySound = true }));
        await fixture.Export(await fixture.Render(), "overview");
    }

    [Theory]
    [InlineData("de", "Bitte mindestens eine Uhrzeit eintragen.")]
    [InlineData("en", "Enter at least one time.")]
    public async Task CustomScheduleEditorStillValidatesRequiredTimes(string language, string error)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        await fixture.Invoke("Create");
        fixture.Set("_kind", AlertScheduleKind.Daily);
        fixture.Set("_times", "");
        var rejected = await fixture.Invoke("Save");
        Assert.Empty(fixture.Alerts.Items);
        Assert.Contains(error, rejected);
        fixture.Set("_times", "21:00");
        await fixture.Invoke("Save");
        Assert.Equal(new TimeOnly(21, 0), Assert.Single(Assert.Single(fixture.Alerts.Items).Schedule.Times));
    }

    [Theory]
    [InlineData("de", "Timer-Leiste einstellen", "Ton")]
    [InlineData("en", "Configure timer strip", "Sound")]
    public async Task SelectedBossHasIndependentPresentationAndDoesNotCreateAScheduledCopy(string language, string countdown, string sound)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Alerts.SetBossPresentation("1110:111001", new() { AlwaysShowOverlay = true, PlaySound = true, Sound = "beep" });
        var html = await fixture.Render();
        var bossCard = System.Text.RegularExpressions.Regex.Match(html, "<article[^>]+data-boss=\"1110:111001\"[\\s\\S]*?</article>").Value;
        Assert.NotEmpty(bossCard);
        Assert.Contains("data-advance-warnings", bossCard);
        Assert.Contains(countdown, bossCard);
        Assert.Contains(sound, bossCard);
        Assert.Contains("Synthetic boss", bossCard);
        Assert.True(fixture.Alerts.GetBossPresentation("1110:111001").AlwaysShowOverlay);
        Assert.DoesNotContain(language == "de" ? "Countdown im Overlay" : "Overlay countdown", bossCard);
        Assert.Contains("data-sound=\"standard\"", bossCard);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "boss");
    }

    [Theory]
    [InlineData("de", "2026-03-29T02:30", "ungültig oder doppeldeutig")]
    [InlineData("de", "2026-10-25T02:30", "ungültig oder doppeldeutig")]
    [InlineData("en", "2026-03-29T02:30", "invalid or ambiguous")]
    [InlineData("en", "2026-10-25T02:30", "invalid or ambiguous")]
    public async Task OnceEditorExplainsBothInvalidAndAmbiguousBerlinClockChangeTimes(string language, string localTime, string reason)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        await fixture.Invoke("Create");
        fixture.Set("_kind", AlertScheduleKind.Once);
        fixture.Set("_zone", "Europe/Berlin");
        fixture.Set("_atLocal", localTime);
        var html = await fixture.Invoke("Save");
        Assert.Empty(fixture.Alerts.Items);
        Assert.Contains(reason, html);
        Assert.Contains("role=\"alert\"", html);
    }


    [Theory]
    [InlineData("de", "Event-Zeitachse", "Portal offen")]
    [InlineData("en", "Event timeline", "Portal open")]
    public async Task CalendarAppearsImmediatelyWithResetTimesAndActivePortalWithoutCreatingReminders(string language, string timeline, string portal)
    {
        await using var fixture = new Fixture(language);
        fixture.Clock.UtcNow = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);
        var html = await fixture.Render();
        Assert.Contains(timeline, html);
        Assert.Contains(portal, html);
        Assert.Contains("12 h", html);
        Assert.Contains("24 h", html);
        Assert.Contains("48 h", html);
        Assert.Contains("workspace-reset-grid", html);
        Assert.DoesNotContain("aria-controls=\"alerts-event-details\"", html);
        Assert.Contains("data-event-details=\"shugo\"", html);
        Assert.Contains("event-timeline-portal", html);
        Assert.DoesNotContain(language == "de" ? "Overlay-Widget" : "Overlay widget", html);
        Assert.Contains("data-overlay-link=\"alerts\"", html);
        Assert.Contains("09:00", html); // 07:00 UTC daily reset shown in Berlin daylight time.
        var calendar = html[..html.IndexOf("<section class=\"event-boss-live", StringComparison.Ordinal)];
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(calendar, "data-event-options=").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(calendar, "class=\"event-reset(?: [^\"]*)?\"").Count);
        // The event calendar stays free of schedule badges; the separate boss overview keeps its selection badge.
        Assert.DoesNotContain("event-schedule-badge", calendar);
        Assert.Contains("data-boss=\"1110:111001\"", html); // The catalogue stays configurable before capture or reminder selection.
        Assert.Contains(language == "de" ? "Warte auf Boss-Timer" : "Waiting for boss timers", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.OverlayRows);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "active");
    }

    [Theory]
    [InlineData("de", "invasion")]
    [InlineData("en", "invasion")]
    [InlineData("de", "rift")]
    [InlineData("en", "rift")]
    [InlineData("de", "abyss")]
    [InlineData("en", "abyss")]
    public async Task EventSoundOptionsAreVisibleWhileReminderIsOffAndPersistForLaterActivation(string language, string id)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var html = await fixture.Invoke("SelectEvent", id);
        static string Card(string markup, string eventId)
        {
            var at = markup.IndexOf("data-event-details=\"" + eventId + "\"", StringComparison.Ordinal);
            return markup[at..markup.IndexOf("</section>", at, StringComparison.Ordinal)];
        }
        var card = Card(html, id);
        Assert.Contains("data-sound=\"off\"", card);
        Assert.Contains("data-sound=\"standard\"", card);
        Assert.Contains(language == "de" ? "MP3 hinzufügen" : "Add MP3", card);
        Assert.Contains(language == "de" ? "Ton wird bei aktivierter Erinnerung abgespielt." : "Sound plays when the reminder is enabled.", card);
        Assert.Empty(fixture.Alerts.Items);
        var sound = fixture.Alerts.GetEventReminder(id) with { PlaySound = true, Sound = "custom", SoundFile = fixture.SoundFile };
        html = await fixture.Invoke("SaveEventSound", sound);
        var saved = Assert.Single(fixture.Alerts.Items);
        Assert.False(saved.Enabled);
        Assert.True(saved.PlaySound);
        Assert.Equal(fixture.SoundFile, saved.SoundFile);
        Assert.Contains(language == "de" ? "Signalton gespeichert." : "Sound saved.", html);
        Assert.Contains("aria-pressed=\"true\" data-sound=\"custom\"", Card(html, id));
        await fixture.Invoke("SaveEventReminder", fixture.Alerts.GetEventReminder(id) with { Enabled = true, LeadMinutesList = [5] });
        saved = Assert.Single(fixture.Alerts.Items);
        Assert.True(saved.Enabled);
        Assert.True(saved.PlaySound);
        Assert.Equal(fixture.SoundFile, saved.SoundFile);
        await fixture.Invoke("SaveEventSound", saved with { PlaySound = false });
        Assert.True(Assert.Single(fixture.Alerts.Items).Enabled);
        Assert.False(Assert.Single(fixture.Alerts.Items).PlaySound);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "sound-" + id);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task DefaultDashboardKeepsAllReminderControlsWithoutWorkspaceOptIn(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.RenderDashboard();
        Assert.Contains("event-reset-options", html);
        Assert.DoesNotContain("workspace-event-select", html);
        Assert.DoesNotContain(language == "de" ? "Overlay-Widget" : "Overlay widget", html);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(html, "data-overlay-link=\"alerts\"").Count);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SelectingEventAndResetOnlyChangesDetailsAndPreservesIndependentPreferences(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Alerts.Save(fixture.Alerts.GetEventReminder("shugo") with { Enabled = false, PlaySound = true, Sound = "custom", SoundFile = fixture.SoundFile });
        fixture.Alerts.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true });
        var html = await fixture.Render();
        Assert.Contains("alerts-workspace-grid", html);
        Assert.Contains("data-event-details=\"shugo\"", html);
        Assert.Contains("aria-pressed=\"true\" data-sound=\"custom\"", html);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "data-event-details=").Count);
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items);
        var reset = await fixture.Invoke("SelectEvent", "daily-reset");
        Assert.Contains("data-event-details=\"daily-reset\"", reset);
        Assert.Contains("id=\"alerts-event-details\"", reset);
        Assert.Contains("aria-controls=\"alerts-event-details\"", reset);
        Assert.Contains("data-event-details=\"shugo\"", reset);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(reset, "data-event-details=").Count);
        Assert.Contains("data-sound=\"standard\"", reset);
        Assert.Contains(language == "de" ? "MP3 hinzufügen" : "Add MP3", reset);
        await fixture.Export(reset, "reset-details");
        await fixture.Invoke("SelectEvent", "not-a-catalog-event");
        Assert.Equal("daily-reset", fixture.Field("_selectedEventId"));
        var restored = await fixture.Invoke("SelectEvent", "shugo");
        Assert.Contains("aria-pressed=\"true\" data-sound=\"custom\"", restored);
        Assert.True(fixture.Alerts.TimerOverlayChoices.Single(choice => choice.Key == "event:catalog:shugo").Selected);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items));
        Assert.False(Assert.Single(fixture.Alerts.Items).Enabled);
        Assert.True(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(restored, "selected-details");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task QuickReminderAndTimerStripAreIndependentAndNeverDuplicateCustomCards(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var enabled = await fixture.Invoke("SaveEventReminder", fixture.Alerts.GetEventReminder("shugo") with { Enabled = true, LeadMinutes = 10, LeadMinutesList = [10] });
        var reminder = Assert.Single(fixture.Alerts.Items);
        Assert.Equal("catalog:shugo", reminder.Id);
        Assert.True(reminder.Enabled);
        Assert.Equal(10, reminder.LeadMinutes);
        Assert.DoesNotContain("data-alert=\"catalog:shugo\"", enabled);
        Assert.DoesNotContain(fixture.Alerts.EventTimers, timer => timer.Presentation.ShowOverlay);
        await fixture.RenderTimerSettings();
        await fixture.InvokeTimer("SelectTimer", "event:catalog:shugo", true);
        Assert.Contains("event:catalog:shugo", fixture.Settings.Current.TimerOverlayTimerIds!);
        Assert.False(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
        Assert.True(Assert.Single(fixture.Alerts.Items).Enabled);
        await fixture.Invoke("SaveEventReminder", fixture.Alerts.GetEventReminder("shugo") with { Enabled = false });
        Assert.False(Assert.Single(fixture.Alerts.Items).Enabled);
        Assert.False(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
        Assert.Equal(4, fixture.Alerts.TimerOverlayRows.Count);
        Assert.Contains(fixture.Alerts.TimerOverlayRows, row => row.Key == "event:catalog:shugo");
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(await fixture.Render(), "widget");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EventReminderEditorKeepsCalendarScheduleReadOnlyWhileCustomEditorRemainsFlexible(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var editor = await fixture.Invoke("EditEventReminder", "abyss");
        Assert.Contains("event-editor-schedule", editor);
        Assert.DoesNotContain("id=\"alert-kind\"", editor);
        Assert.DoesNotContain("id=\"alert-times\"", editor);
        Assert.Contains("21:00", editor);
        Assert.DoesNotContain("Countdown im Overlay", editor);
        Assert.DoesNotContain("Overlay countdown", editor);
        Assert.Contains(language == "de" ? "Speichere oder schließe den Editor" : "Save or close the editor", editor);
        var selectionButtons = System.Text.RegularExpressions.Regex.Matches(editor, "<button[^>]+class=\"workspace-reset-select\"[^>]*>");
        Assert.Equal(2, selectionButtons.Count);
        Assert.DoesNotContain("aria-controls=\"alerts-event-details\"", editor);
        Assert.All(selectionButtons.Cast<System.Text.RegularExpressions.Match>(), match => Assert.Contains("disabled", match.Value));
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(editor, "data-event-details=").Count);
        await fixture.Invoke("SelectEvent", "shugo");
        Assert.Equal("abyss", fixture.Field("_selectedEventId"));
        await fixture.Invoke("SaveEventReminder", fixture.Alerts.GetEventReminder("abyss") with { Enabled = true, LeadMinutesList = [5] });
        Assert.Empty(fixture.Alerts.Items); // Queued actions from the previous render cannot replace the open editor.
        Assert.False(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "abyss").Presentation.ShowOverlay);
        fixture.Set("_enabled", true);
        fixture.Set("_warningMinutes", new[] { 9 });
        await fixture.Invoke("Save");
        var reminder = Assert.Single(fixture.Alerts.Items);
        Assert.Equal("catalog:abyss", reminder.Id);
        Assert.Equal(AlertScheduleKind.Weekly, reminder.Schedule.Kind);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday }, reminder.Schedule.Weekdays);
        Assert.Equal(new[] { 9 }, reminder.WarningMinutes);
        await fixture.Export(editor, "event-editor");
        var custom = await fixture.Invoke("Create");
        Assert.Contains("id=\"alert-kind\"", custom);
    }

    [Theory]
    [InlineData("de", "Erfasster Timer")]
    [InlineData("en", "Captured timer")]
    public async Task SelectedBossCardsUseCapturedTimerAndRemainOutsideCalendar(string language, string captured)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        fixture.Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
        fixture.Bosses.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
            0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 61000), 0, 0, 0]);
        Assert.Single(fixture.Bosses.Schedule);
        var html = await fixture.Render();
        Assert.Contains("data-boss=\"1110:111001\"", html);
        Assert.Contains("00:01:01", html);
        Assert.Contains(captured, html);
        Assert.DoesNotContain("data-event=\"1110:111001\"", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "captured-boss");
    }


    [Theory]
    [InlineData("de", false)]
    [InlineData("en", false)]
    [InlineData("de", true)]
    [InlineData("en", true)]
    public async Task CustomAndCatalogRemindersOfferMp3SelectionWithoutOpeningDialogOrPlayingAudio(string language, bool catalog)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        if (catalog) await fixture.Invoke("EditEventReminder", "shugo");
        else await fixture.Invoke("Create");
        fixture.Set("_playSound", true);
        fixture.Set("_enabled", true);
        var editor = await fixture.Invoke("SetSound", new AlertSoundPicker.Selection("custom", fixture.SoundFile));
        Assert.Contains("data-sound=\"custom\"", editor);
        Assert.Contains("custom-tone.mp3", editor);
        Assert.Contains(language == "de" ? "Ton testen" : "Test sound", editor);
        Assert.DoesNotContain(fixture.SoundFile, editor); // Show the filename; keep the local path out of the markup.
        await fixture.Export(editor, catalog ? "event-mp3" : "custom-mp3");
        await fixture.Invoke("Save");
        var reminder = Assert.Single(fixture.Alerts.Items);
        Assert.Equal("custom", reminder.Sound);
        Assert.Equal(fixture.SoundFile, reminder.SoundFile);
        Assert.True(reminder.PlaySound);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SelectedBossOffersItsOwnMp3FileAndTestButtonWithoutPlayback(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        Assert.True(fixture.Alerts.SetBossPresentation("1110:111001", new() { PlaySound = true, Sound = "custom", SoundFile = fixture.SoundFile }));
        var html = await fixture.Render();
        Assert.Contains("data-sound=\"custom\"", html);
        Assert.Contains("custom-tone.mp3", html);
        Assert.Contains(language == "de" ? "MP3 hinzufügen" : "Add MP3", html);
        Assert.Contains(language == "de" ? "Ton testen" : "Test sound", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "boss-mp3");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MapShowsSixEventsAndOnlySelectedBossesAndRefreshesCountdowns(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(5);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        var html = await fixture.RenderMap();
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(html, "data-timer=\"event:").Count);
        Assert.Contains("data-timer=\"boss:1110:111001\"", html);
        Assert.Contains("Synthetic boss", html);
        Assert.Contains("00:55:00", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.OverlayRows);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export("<aside class=\"sidebar mapside mapside-right\" style=\"width:300px;max-width:100%;margin:20px\">" + html + "</aside>", "map-timers");
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        fixture.Alerts.Tick();
        await fixture.Flush();
        Assert.Contains("00:54:59", await fixture.MapMarkup());
    }

    [Fact]
    public async Task SoundButtonsAddAReusableMp3WithoutPlaybackAndAllowOff()
    {
        await using var fixture = new Fixture("en");
        var selections = new List<AlertSoundPicker.Selection>();
        var html = await fixture.RenderPicker(selections.Add);
        Assert.Contains("data-sound=\"standard\"", html);
        Assert.Contains("data-sound=\"off\"", html);
        Assert.Contains("aria-pressed=\"true\" data-sound=\"standard\"", html);
        Assert.Contains("aria-pressed=\"false\" data-sound=\"off\"", html);
        await fixture.InvokePicker("Choose");
        Assert.Equal(new AlertSoundPicker.Selection("custom", fixture.SoundFile), Assert.Single(selections));
        Assert.Contains(fixture.SoundFile, fixture.Alerts.SoundFiles);
        Assert.Equal(0, fixture.SoundPlaybacks);
        html = await fixture.RenderPicker(selections.Add);
        Assert.Contains("custom-tone", html);
        Assert.DoesNotContain(fixture.SoundFile, html);
        await fixture.InvokePicker("Select", new AlertSoundPicker.Selection("standard", null, false));
        Assert.False(selections[^1].Enabled);
        Assert.Equal(0, fixture.SoundPlaybacks);
    }

    [Theory]
    [InlineData("aion2-stylized")]
    [InlineData("aion2-neutral")]
    [InlineData("obsidian")]
    [InlineData("aurora")]
    [InlineData("runic")]
    [InlineData("soulcrest-legacy")]
    [InlineData("aion2")]
    public async Task TimerDomPreviewUsesEverySelectedNativeThemeTokenWithoutChangingItsSelection(string themeId)
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.OverlayTheme = themeId;
        fixture.Settings.Current.TimerOverlayTimerIds = ["event:catalog:shugo", "event:catalog:rift"];
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var theme = OverlayThemeDefinition.Resolve(themeId);
        static string Css(System.Drawing.Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        var html = await fixture.RenderTimerSettings();
        Assert.Contains("overlay-theme-" + theme.Id, html);
        Assert.Contains("--timer-ink:" + (theme.IsLegacy ? "#edede6" : Css(theme.Primary)), html);
        Assert.Contains("--timer-active:" + (theme.IsLegacy ? "#f4d78e" : Css(theme.Gold)), html);
        Assert.Contains("--timer-muted:" + (theme.IsLegacy ? "#abb4b8" : Css(theme.Secondary)), html);
        Assert.Contains("--timer-portrait-border:" + (theme.IsLegacy ? "#d3c297bb" : Css(theme.Gold) + "d2"), html);
        Assert.Contains("--timer-outline:" + (theme.IsLegacy ? "#10171c" : Css(theme.Surface) + "f0"), html);
        Assert.Contains("--timer-shadow:" + (theme.IsLegacy ? "#000c" : Css(theme.Surface) + "aa"), html);
        Assert.DoesNotContain("timer-overlay-background", html);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TimerStripPreviewUsesOnlyRealSelectedRowsAndTheSharedCountdownFormat(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.TimerOverlayTimerIds = ["event:catalog:shugo", "event:catalog:rift", "event:catalog:invasion", "event:catalog:abyss"];
        fixture.Settings.Current.TimerOverlayLeadMinutes = 0;
        var html = await fixture.RenderTimerSettings();
        Assert.Equal(4, fixture.Alerts.TimerOverlayRows.Count);
        foreach (var row in fixture.Alerts.TimerOverlayRows)
        {
            Assert.Contains("data-timer-preview=\"" + row.Key + "\"", html);
            Assert.Contains(TimerOverlayPresentation.Countdown(row, fixture.Alerts.Now, true), html);
        }
        foreach (var asset in new[] { "shugo-festival", "spacetime-rift", "dimensional-invasion", "artifact-siege" })
            Assert.Contains("icons/events/" + asset + ".png", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "timer-strip");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ResetAndCustomTimersShowTheirOwnLocalImagesWithoutABackgroundControl(string language)
    {
        await using var fixture = new Fixture(language);
        Assert.True(fixture.Alerts.Save(AlertsService.NewAlert() with { Id = "synthetic-icon", Title = "Synthetic custom timer", Enabled = true,
            Schedule = new() { Kind = AlertScheduleKind.Hourly, Minute = 15, TimeZoneId = "UTC" } }));
        fixture.Settings.Current.TimerOverlayTimerIds = ["event:catalog:daily-reset", "event:catalog:weekly-reset", "event:synthetic-icon"];
        var html = await fixture.RenderTimerSettings();
        Assert.Equal(3, fixture.Alerts.TimerOverlayRows.Count);
        foreach (var asset in new[] { "daily-reset", "weekly-reset", "custom-timer" })
            Assert.Contains("icons/events/" + asset + ".png", html);
        Assert.DoesNotContain("timer-strip-fallback", html);
        Assert.DoesNotContain("timer-overlay-background", html);
        Assert.DoesNotContain("with-background", html);
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "reset-custom-icons");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CapturedBossUsesItsLocalPortraitAndBrokenImagesFallBackInPreviewAndSelection(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001"];
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        fixture.Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
        fixture.Bosses.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
            0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 61000), 0, 0, 0]);
        var row = Assert.Single(fixture.Alerts.TimerOverlayRows);
        Assert.Equal("map:icons/bosses/synthetic-boss.png", row.IconId);
        var html = await fixture.RenderTimerSettings();
        const string portrait = "https://mapdata.soulcrest/icons/bosses/synthetic-boss.png";
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, System.Text.RegularExpressions.Regex.Escape(portrait)).Count);
        Assert.Contains("data-timer-preview=\"boss:1110:111001\"", html);
        Assert.Contains("data-timer-choice=\"boss:1110:111001\"", html);
        html = await fixture.InvokeTimer("IconFailed", row.IconId!);
        Assert.DoesNotContain(portrait, html);
        Assert.Contains(TimerOverlayIcons.BrowserSource("boss"), html);
        Assert.Equal(new[] { "boss:1110:111001" }, fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "boss-icon-fallback");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TimerStripSelectionPreservesEveryLegacySelectionAndDoesNotChangeDeliveryPreferences(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Alerts.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true });
        fixture.Alerts.Save(AlertsService.NewAlert() with { Id = "synthetic-custom", Title = "Synthetic reminder", Enabled = true,
            ShowOverlay = true, PlaySound = true, Schedule = new() { Kind = AlertScheduleKind.Hourly, Minute = 15, TimeZoneId = "UTC" } });
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Alerts.SetBossPresentation("1110:111001", new() { ShowOverlay = false, PlaySound = true });
        var reminders = System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items);
        var boss = fixture.Alerts.GetBossPresentation("1110:111001");
        var original = fixture.Alerts.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.Key).ToArray();
        Assert.Contains("event:catalog:shugo", original);
        Assert.Null(fixture.Settings.Current.TimerOverlayTimerIds);
        await fixture.RenderTimerSettings();
        await fixture.InvokeTimer("SelectTimer", "event:catalog:rift", true);
        Assert.Equal(original.Append("event:catalog:rift").Distinct().Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        await fixture.InvokeTimer("SelectTimer", "event:catalog:shugo", false);
        Assert.DoesNotContain("event:catalog:shugo", fixture.Settings.Current.TimerOverlayTimerIds!);
        Assert.Equal(reminders, System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items));
        Assert.Equal(boss, fixture.Alerts.GetBossPresentation("1110:111001"));
        Assert.Equal(new[] { "1110:111001" }, fixture.Settings.Current.BossAlertIds);
        Assert.True(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "shugo").Presentation.ShowOverlay);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(0, fixture.SoundPlaybacks);
    }

    [Fact]
    public async Task TimerStripControlsPersistIndependentlyOfBannerAndTimelineDimensions()
    {
        await using var fixture = new Fixture("en");
        var settings = fixture.Settings.Current;
        var beforeBanner = (settings.AlertNotificationsX, settings.AlertNotificationsY, settings.AlertNotificationsWidth, settings.AlertNotificationsScale);
        var beforeTimeline = (settings.EventTimelineOverlayX, settings.EventTimelineOverlayY, settings.EventTimelineOverlayWidth, settings.EventTimelineOverlayScale, settings.EventTimelineHours);
        await fixture.RenderTimerSettings();
        await fixture.InvokeTimer("SetScale", new ChangeEventArgs { Value = "140" });
        await fixture.InvokeTimer("SetGap", new ChangeEventArgs { Value = "31" });
        await fixture.InvokeTimer("SetSeconds", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetEnabled", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetWorldMap", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetMenu", new ChangeEventArgs { Value = true });
        var html = await fixture.InvokeTimer("SetInstances", new ChangeEventArgs { Value = true });
        Assert.Contains("id=\"timer-overlay-worldmap\"", html);
        Assert.DoesNotContain("id=\"timer-overlay-lead\"", html);
        Assert.DoesNotContain("id=\"timer-overlay-window\"", html);
        Assert.Equal((1.4, 31, false, false), (settings.AlertsOverlayScale, settings.TimerOverlayGap, settings.TimerOverlayShowSeconds, settings.AlertsOverlayEnabled));
        Assert.Equal((false, true, true), (settings.TimerOverlayShowWorldMap, settings.TimerOverlayShowMenu, settings.TimerOverlayShowInstances));
        Assert.Equal(0, settings.TimerOverlayLeadMinutes);
        Assert.Equal(beforeBanner, (settings.AlertNotificationsX, settings.AlertNotificationsY, settings.AlertNotificationsWidth, settings.AlertNotificationsScale));
        Assert.Equal(beforeTimeline, (settings.EventTimelineOverlayX, settings.EventTimelineOverlayY, settings.EventTimelineOverlayWidth, settings.EventTimelineOverlayScale, settings.EventTimelineHours));
        var saved = new SettingsService().Current;
        Assert.Equal((1.4, 31, false), (saved.AlertsOverlayScale, saved.TimerOverlayGap, saved.TimerOverlayShowSeconds));
        Assert.Equal((false, true, true), (saved.TimerOverlayShowWorldMap, saved.TimerOverlayShowMenu, saved.TimerOverlayShowInstances));
        await fixture.InvokeTimer("SetWorldMap", new ChangeEventArgs { Value = true });
        await fixture.InvokeTimer("SetMenu", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetInstances", new ChangeEventArgs { Value = false });
        Assert.Equal((true, false, false), (settings.TimerOverlayShowWorldMap, settings.TimerOverlayShowMenu, settings.TimerOverlayShowInstances));
        Assert.Empty(fixture.Alerts.Items);
    }

    [Fact]
    public async Task TimerStripRejectsQueuedChangesWhileBusyAndIgnoresUnknownKeysOrMalformedNumbers()
    {
        await using var fixture = new Fixture("en");
        await fixture.RenderTimerSettings(busy: true);
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        await fixture.InvokeTimer("SelectTimer", "event:catalog:shugo", true);
        await fixture.InvokeTimer("SetGap", new ChangeEventArgs { Value = "48" });
        await fixture.InvokeTimer("SetSeconds", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetWorldMap", new ChangeEventArgs { Value = false });
        await fixture.InvokeTimer("SetMenu", new ChangeEventArgs { Value = true });
        await fixture.InvokeTimer("SetInstances", new ChangeEventArgs { Value = true });
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        await fixture.RenderTimerSettings();
        await fixture.InvokeTimer("SelectTimer", "event:catalog:unknown", true);
        await fixture.InvokeTimer("SetScale", new ChangeEventArgs { Value = "invalid" });
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
    }

    [Theory]
    [InlineData("de", "Timer-Leiste einstellen")]
    [InlineData("en", "Configure timer strip")]
    public async Task CapturedBossListIsCompactSearchableAndLinksToStripSettingsWithoutSelectingReminders(string language, string label)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        fixture.Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
        fixture.Bosses.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
            0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 61000), 0, 0, 0]);
        var html = await fixture.Render();
        Assert.Contains("event-boss-live boss-timers-compact", html);
        Assert.Contains("boss-timer-search", html);
        Assert.Contains("data-boss-options=\"1110:111001\"", html);
        Assert.Contains("00:01:01", html);
        Assert.Contains(label, html);
        Assert.Contains(language == "de" ? "Alle bekannten Bosse" : "All known bosses", html);
        Assert.Contains("data-overlay-link=\"alerts\"", html);
        Assert.Contains("data-boss-strip=", html);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Settings.Current.TimerOverlayTimerIds!);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "boss-list");
        // Native details markup is opened only in the optional static illustration; callback behaviour is covered in LiveBossTimersTests.
        var expanded = new System.Text.RegularExpressions.Regex("(<details class=\"boss-timer-options\"[^>]*)(>)")
            .Replace(html, "$1 open$2", 1);
        await fixture.Export(expanded, "boss-list-expanded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerSettingsSelectionKeepsUnavailableExplicitIdsAndAnExplicitEmptySelection(bool empty)
    {
        await using var fixture = new Fixture("en");
        var original = empty ? Array.Empty<string>() : new[] { "boss:missing-fixture", "event:unknown-fixture" };
        fixture.Settings.Current.TimerOverlayTimerIds = original;
        fixture.Alerts.SetEventPresentation("rift", new() { ShowOverlay = true, AlwaysShowOverlay = true });
        await fixture.RenderTimerSettings();
        await fixture.InvokeTimer("SelectTimer", "event:catalog:shugo", true);
        Assert.Equal(original.Append("event:catalog:shugo").Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        await fixture.InvokeTimer("SelectTimer", "event:catalog:shugo", false);
        Assert.Equal(original.Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.NotNull(new SettingsService().Current.TimerOverlayTimerIds);
        Assert.Equal(original.Order(StringComparer.Ordinal), new SettingsService().Current.TimerOverlayTimerIds);
        Assert.True(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "rift").Presentation.ShowOverlay);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Alerts.Items);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EventPreviewRequiresAnExplicitActionAndDoesNotChangeRemindersOrStripSelection(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.TimerOverlayTimerIds = ["event:catalog:rift"];
        var notices = new List<AlertNotice>();
        fixture.Alerts.NoticeRaised += notices.Add;
        await fixture.Render();
        await fixture.Invoke("SelectEvent", "shugo");
        await fixture.Invoke("SelectEvent", "rift");
        await fixture.Invoke("PreviewEventReminder", "shugo"); // No saved reminder yet.
        Assert.Empty(notices);
        Assert.Empty(fixture.Alerts.Items);
        var reminder = fixture.Alerts.GetEventReminder("shugo") with { Enabled = false, ShowOverlay = true, PlaySound = true };
        await fixture.Invoke("SaveEventReminder", reminder);
        Assert.Empty(notices);
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items);
        var html = await fixture.Invoke("PreviewEventReminder", "shugo");
        var notice = Assert.Single(notices);
        Assert.True(notice.Preview);
        Assert.True(notice.ShowOverlay);
        Assert.True(notice.PlaySound);
        Assert.Contains(language == "de" ? "Test für" : "Triggered a test for", html);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items));
        Assert.Equal(new[] { "event:catalog:rift" }, fixture.Settings.Current.TimerOverlayTimerIds);
        fixture.Set("_busy", true);
        await fixture.Invoke("PreviewEventReminder", "shugo");
        fixture.Set("_busy", false);
        await fixture.Invoke("EditEventReminder", "shugo");
        await fixture.Invoke("PreviewEventReminder", "shugo");
        Assert.Single(notices);
        Assert.Equal(0, fixture.SoundPlaybacks);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CalendarAndCustomWarningListsRenderAndPersistWithoutEnablingTimerStrip(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        await fixture.Invoke("SaveEventReminder", fixture.Alerts.GetEventReminder("shugo") with
            { Enabled = true, LeadMinutesList = [15, 10, 5, 7], PlaySound = true });
        await fixture.Invoke("SelectEvent", "shugo");
        var custom = AlertsService.NewAlert() with { Title = language == "de" ? "Gruppenstart" : "Group start",
            LeadMinutesList = [15, 5, 3], Schedule = new() { Kind = AlertScheduleKind.Hourly }, NotifyAtStart = true };
        Assert.True(fixture.Alerts.Save(custom));
        var html = await fixture.Render();
        Assert.Equal(new[] { 15, 10, 7, 5 }, fixture.Alerts.GetEventReminder("shugo").WarningMinutes);
        Assert.Contains("data-warning-value=\"900\"", html); // boss seconds retain their own units
        Assert.Contains("data-warning-value=\"15\" class=\"selected\"", html);
        Assert.Contains(language == "de" ? "7 Min. vorher" : "7 min before", html);
        Assert.False(fixture.Tracker.Running);
        await fixture.Export(html, "multiple-warnings");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-alert-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly string _language;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private AlertsView? _view;
        private TimerOverlaySettings? _timerSettings;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _timerRoot;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public AlertsService Alerts => _provider.GetRequiredService<AlertsService>();
        public TrackerService Tracker => _provider.GetRequiredService<TrackerService>();
        public BossRushService Bosses => _provider.GetRequiredService<BossRushService>();
        public ManualTime Clock { get; } = new();
        public int SoundPlaybacks { get; private set; }
        private AlertSoundPicker? _picker;
        public string SoundFile => Path.Combine(_folder, "custom-tone.mp3");

        public Fixture(string language)
        {
            _language = language;
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """{"mapId":1110,"bosses":[{"spawnId":111001,"npcId":2400017,"en":"Synthetic boss","de":null,"x":100,"y":200,"icon":"icons/bosses/synthetic-boss.png"}]}""");
            Directory.CreateDirectory(Path.Combine(_folder, "icons", "bosses"));
            using (var icon = new System.Drawing.Bitmap(2, 2))
            {
                icon.SetPixel(0, 0, System.Drawing.Color.Gold);
                icon.Save(Path.Combine(_folder, "icons", "bosses", "synthetic-boss.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var clock = Clock;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton(new AlertSoundPlayer(() => throw new InvalidOperationException("No playback expected"), _ => SoundPlaybacks++, () => Task.FromResult(new AlertSoundFileChoice(SoundFile))));
            services.AddSingleton<UiState>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(sp => new BossRushService(sp.GetRequiredService<ProgressService>(), sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<MapTargetsService>(), sp.GetRequiredService<NetworkLootService>(), clock, Path.Combine(_folder, "boss-timers.json")));
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), clock, runTimer: false, bosses: sp.GetRequiredService<BossRushService>(), settings: sp.GetRequiredService<SettingsService>()));
            _provider = services.BuildServiceProvider();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.BossRushEnabled = Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossAlertSound = false;
            Settings.Current.TimerOverlayTimerIds = null;
            Settings.Current.TimerOverlayGap = 18;
            Settings.Current.TimerOverlayShowSeconds = true;
            Settings.Current.TimerOverlayLeadMinutes = 0;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<string> RenderTimerSettings(bool busy = false)
        {
            _timerRoot = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureTimerOverlaySettings>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaptureTimerOverlaySettings.Captured)] = (Action<TimerOverlaySettings>)(component => _timerSettings = component),
                [nameof(TimerOverlaySettings.Busy)] = busy
            })));
            return WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_timerRoot.ToHtmlString));
        }
        public async Task<string> InvokeTimer(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(TimerOverlaySettings).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_timerSettings, args);
                if (result is Task task) await task;
                await ((CaptureTimerOverlaySettings)_timerSettings!).RefreshNow();
            });
            return WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_timerRoot.ToHtmlString));
        }

        public async Task<string> RenderDashboard()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<EventTimersDashboard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EventTimersDashboard.Timers)] = Alerts.EventTimers,
                [nameof(EventTimersDashboard.Now)] = Clock.GetUtcNow()
            })));
            return await Markup();
        }
        public async Task<string> RenderMap()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<MapTimers>());
            return await Markup();
        }
        public Task<string> MapMarkup() => Markup();
        public Task Flush() => _renderer.Dispatcher.InvokeAsync(() => { });
        public async Task<string> RenderPicker(Action<AlertSoundPicker.Selection> selection)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureAlertSoundPicker>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaptureAlertSoundPicker.Captured)] = (Action<AlertSoundPicker>)(picker => _picker = picker),
                [nameof(AlertSoundPicker.Changed)] = EventCallback.Factory.Create<AlertSoundPicker.Selection>(this, selection)
            })));
            return await Markup();
        }
        public Task InvokePicker(string method, params object[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AlertSoundPicker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_picker, args);
            if (result is Task task) await task;
        });

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureAlertsView>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CaptureAlertsView.Captured)] = (Action<AlertsView>)(view => _view = view) })));
            return await Markup();
        }
        public async Task<string> Invoke(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(AlertsView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, args);
                if (result is Task task) await task;
                await ((CaptureAlertsView)_view!).RefreshNow();
            });
            return await Markup();
        }
        public object? Field(string field) => typeof(AlertsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view);
        public void Set(string field, object value) => typeof(AlertsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, value);
        private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_ALERTS_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "alerts-preview-viewports.json"), """{"viewports":[{"width":1600,"height":900},{"width":1280,"height":720}],"languages":["de","en"],"pattern":"alerts-*-{language}.html","note":"Static HtmlRenderer output; no app, capture, playback or network startup."}""");
            await File.WriteAllTextAsync(Path.Combine(output, $"alerts-{state}-{_language}.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/boss-timers.css\"><link rel=\"stylesheet\" href=\"/css/alerts-workspace.css\"></head><body>" + html + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => UtcNow.UtcTicks;
    }
}

public sealed class CaptureAlertsView : AlertsView
{
    [Parameter] public Action<AlertsView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}

public sealed class CaptureAlertSoundPicker : AlertSoundPicker
{
    [Parameter] public Action<AlertSoundPicker>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
}
public sealed class CaptureTimerOverlaySettings : TimerOverlaySettings
{
    [Parameter] public Action<TimerOverlaySettings>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
