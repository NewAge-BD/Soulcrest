using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LiveBossTimersTests
{
    [Theory]
    [InlineData("de", "Warte auf Boss-Timer", "Boss-Erinnerungen global pausiert")]
    [InlineData("en", "Waiting for boss timers", "Boss reminders are globally paused")]
    public async Task SelectedUncapturedBossHasCardAndPausedReminderWithoutStartingCapture(string language, string waiting, string paused)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        var html = await fixture.Render();
        Assert.Contains("data-boss=\"1110:111001\"", html);
        Assert.DoesNotContain("data-boss=\"1110:111002\"", html);
        Assert.Contains(waiting, html);
        Assert.Contains(paused, html);
        Assert.Contains("event-quick-reminders", html);
        Assert.DoesNotContain("boss-timers-compact", html);
        Assert.DoesNotContain("boss-timer-options", html);
        Assert.DoesNotContain("00:00:00", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("de", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ReminderControlsAlwaysDescribeNotificationsAndSoundChangesPreserveLegacyPresentationAndStripSelection(string language, bool explicitStrip)
    {
        await using var fixture = new Fixture(language, isolateTracker: true);
        const string key = "1110:111001", otherKey = "1110:111002";
        fixture.Settings.Current.BossAlertIds = [key, otherKey];
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertDurationSeconds = 15;
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        fixture.Settings.Current.TimerOverlayTimerIds = explicitStrip ? ["event:catalog:shugo"] : null;
        fixture.Settings.Current.TimerOverlayLeadMinutes = 15;
        fixture.Settings.Current.BossReminderOverrides[key] = new() { Enabled = true, LeadSeconds = 600 };
        var legacy = new BossAlertPresentation
            { ShowOverlay = false, AlwaysShowOverlay = false, OverlayLeadMinutes = 0, PlaySound = false };
        var other = new BossAlertPresentation { ShowOverlay = true, AlwaysShowOverlay = false, OverlayLeadMinutes = 1, PlaySound = true };
        Assert.True(fixture.Alerts.SetBossPresentation(key, legacy));
        Assert.True(fixture.Alerts.SetBossPresentation(otherKey, other));
        await fixture.CaptureRows(1110,
            new Soulcrest.Core.Network.BossSpawn(111001, false, fixture.Clock.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds()),
            new Soulcrest.Core.Network.BossSpawn(111002, true, fixture.Clock.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));
        var reminder = fixture.Bosses.GetReminder(key);
        var settings = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var strip = fixture.Alerts.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.Key).Order(StringComparer.Ordinal).ToArray();
        var html = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        var card = TimerCards(html)[key];
        Assert.Contains(language == "de" ? "Vorwarnzeit" : "Advance warning", card);
        Assert.Contains(language == "de" ? "Signalton" : "Sound", card);
        Assert.Contains(language == "de" ? ">Testen</button>" : ">Test</button>", card);
        Assert.Contains(language == "de" ? "Jede aktivierte Boss-Erinnerung erscheint als Overlay-Benachrichtigung" : "Every enabled boss reminder appears as an overlay notification", html);
        Assert.Contains(language == "de" ? "In Timer-Leiste anzeigen" : "Show in Timeline Strip", card);
        Assert.DoesNotContain("Bannerfenster", html);
        Assert.DoesNotContain("Banner window", html);
        Assert.DoesNotContain("Banner ab", html);
        Assert.DoesNotContain("Show banner from", html);
        Assert.DoesNotContain(">Banner</label>", card);
        Assert.DoesNotContain("<select", card);
        Assert.Equal(legacy, fixture.Alerts.GetBossPresentation(key));
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));

        // A sound callback from the old render merges only sound fields into the current presentation.
        var current = legacy with { AlwaysShowOverlay = true, OverlayLeadMinutes = 2 };
        Assert.True(fixture.Alerts.SetBossPresentation(key, current));
        await fixture.Invoke("SetSound", key, new AlertSoundPicker.Selection("standard", null));
        Assert.Equal(current with { PlaySound = true }, fixture.Alerts.GetBossPresentation(key));
        await fixture.Invoke("SetSound", key, new AlertSoundPicker.Selection("standard", null, false));
        Assert.Equal(current, fixture.Alerts.GetBossPresentation(key));
        Assert.Equal(other, fixture.Alerts.GetBossPresentation(otherKey));
        Assert.Equal(reminder, fixture.Bosses.GetReminder(key));
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(strip, fixture.Alerts.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.Key).Order(StringComparer.Ordinal).ToArray());
        if (explicitStrip) Assert.Equal(new[] { "event:catalog:shugo" }, fixture.Settings.Current.TimerOverlayTimerIds);
        else Assert.Null(fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.False(fixture.Tracker.Running);
        Assert.Null(fixture.Tracker.Network);

        if (explicitStrip)
        {
            await fixture.Invoke("ToggleOptions", key);
            var expanded = await fixture.Invoke("ToggleAdvanced", key);
            await fixture.Export(expanded, "boss-reminder-expanded-fixed");
            var whole = await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "until-spawn" });
            await fixture.Export(whole, "boss-reminder-expanded-whole");
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TurningOneReminderOffKeepsItsTimerAndOtherBossSettingsVisible(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossAlertLeadSeconds = 60;
        fixture.Capture(false, 610_000);
        await fixture.Render();
        var enabled = await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = true, LeadSeconds = 300 });
        Assert.Contains(language == "de" ? "Boss-Erinnerung gespeichert." : "Boss reminder saved.", enabled);
        Assert.Equal(300, fixture.Bosses.GetReminder("1110:111001").LeadSeconds);
        Assert.Equal(60, fixture.Bosses.GetReminder("1110:111002").LeadSeconds);
        var disabled = await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        Assert.Contains("data-boss=\"1110:111001\"", disabled);
        Assert.Contains("00:10:10", disabled);
        Assert.False(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.True(fixture.Bosses.GetReminder("1110:111002").Enabled);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(60, fixture.Settings.Current.BossAlertLeadSeconds);
        Assert.Equal(new[] { "1110:111001", "1110:111002" }, fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de", "Gespeichert · unbestätigt", "Unbestätigt")]
    [InlineData("en", "Saved · unconfirmed", "Unconfirmed")]
    public async Task ExpiredAndCachedDataDoNotBecomeConfirmedSpawns(string language, string provisional, string unconfirmed)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Capture(false, 1_000);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(17);
        var expired = await fixture.Render();
        Assert.DoesNotContain(provisional, expired);
        Assert.Contains(language == "de" ? "Warte auf Bestätigung" : "Waiting for confirmation", expired);
        Assert.DoesNotContain(">" + (language == "de" ? "Gespawnt" : "Spawned") + "</strong>", expired);
        fixture.Capture(true, -1_000);
        fixture.Network.Stop();
        var cached = await fixture.Render();
        Assert.Contains(provisional, cached);
        Assert.Contains(">" + unconfirmed + "</strong>", cached);
        Assert.DoesNotContain(">" + (language == "de" ? "Gespawnt" : "Spawned") + "</strong>", cached);
        Assert.Empty(fixture.Alerts.NotificationRows);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConfirmedTimerAndSpawnRemainConfirmedAfterMinutesWithoutAnotherList(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Capture(false, 610_000);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var countdown = await fixture.Render();
        Assert.Contains("00:08:10", countdown);
        Assert.DoesNotContain(language == "de" ? "Gespeichert · unbestätigt" : "Saved · unconfirmed", countdown);
        fixture.Capture(true, -1_000);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var spawned = await fixture.Render();
        Assert.Contains(">" + (language == "de" ? "Gespawnt" : "Spawned") + "</strong>", spawned);
        Assert.DoesNotContain(language == "de" ? "Gespeichert · unbestätigt" : "Saved · unconfirmed", spawned);
    }

    [Fact]
    public async Task BusyAndDeselectedCardsRejectQueuedMutationsAndPreview()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossAlertsEnabled = true;
        await fixture.Render(busy: true);
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        await fixture.Invoke("SavePresentation", "1110:111001", new BossAlertPresentation { ShowOverlay = false });
        await fixture.Invoke("Preview", "1110:111001");
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.True(fixture.Alerts.GetBossPresentation("1110:111001").ShowOverlay);
        Assert.Empty(fixture.Bosses.Alerts);
        await fixture.View.SetBusy(false);
        fixture.Settings.Current.BossAlertIds = [];
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        await fixture.Invoke("Preview", "1110:111001");
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.Empty(fixture.Bosses.Alerts);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BossRushVariantKeepsUnselectedCapturedTimersAndSharesExplicitSelection(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Capture(false, 610_000);
        var html = await fixture.Render(includeAll: true);
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"false\"", html);
        Assert.Contains("00:10:10", html);
        Assert.Contains(language == "de" ? "Für Erinnerungen auswählen" : "Select for reminders", html);
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false });
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled); // An old click cannot implicitly select a boss.
        var selected = await fixture.Invoke("SelectChoice", "1110:111001", true);
        Assert.Equal("1110:111001", Assert.Single(fixture.Settings.Current.BossAlertIds));
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"true\"", selected);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        var removed = await fixture.Invoke("SelectChoice", "1110:111001", false);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"false\"", removed);
        Assert.Contains("00:10:10", removed);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CompactRowsCollapseAllPersonalControlsAndRetainTheirActions(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Capture(false, 610_000);
        var html = await fixture.Render(includeAll: true, compact: true);
        Assert.Contains("event-boss-live boss-timers-compact", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"boss-timer-overview\"").Count);
        Assert.Contains("00:10:10", html);
        Assert.Contains("Altgard", html);
        Assert.DoesNotContain("class=\"event-time-range\"", html);
        var options = Regex.Matches(html, "<details class=\"boss-timer-options\"[^>]*>");
        Assert.Equal(2, options.Count);
        Assert.All(options.Cast<Match>(), option => Assert.DoesNotContain("open", option.Value));
        Assert.Contains("alert-sound-picker", html);
        Assert.Contains(language == "de" ? "MP3 hinzufügen" : "Add MP3", html);
        Assert.Contains("event-quick-reminders", html);
        Assert.Contains("boss-timer-select", html);
        Assert.Contains("boss-timer-advanced", html);
        var expanded = await fixture.Invoke("ToggleOptions", "1110:111001");
        Assert.Single(Regex.Matches(expanded, "<details class=\"boss-timer-options\"[^>]* open(?:[ >])").Cast<Match>());
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        Assert.False(fixture.Bosses.GetReminder("1110:111001").Enabled);
        await fixture.Invoke("SavePresentation", "1110:111001", new BossAlertPresentation { ShowOverlay = false, PlaySound = false });
        Assert.False(fixture.Alerts.GetBossPresentation("1110:111001").ShowOverlay);
        Assert.False(fixture.Alerts.GetBossPresentation("1110:111001").PlaySound);
        await fixture.Invoke("Preview", "1110:111001");
        Assert.Equal("1110:111001", Assert.Single(fixture.Bosses.Alerts).Key);
        var collapsed = await fixture.Invoke("ToggleOptions", "1110:111001");
        Assert.All(Regex.Matches(collapsed, "<details class=\"boss-timer-options\"[^>]*>").Cast<Match>(), option => Assert.DoesNotContain("open", option.Value));
        Assert.False(fixture.Tracker.Running);
    }

    [Fact]
    public async Task CardPreviewTargetsItsOwnBossEvenWhenItsReminderIsOff()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossAlertsEnabled = true;
        Assert.True(fixture.Bosses.SetReminder("1110:111002", new() { Enabled = false, LeadSeconds = 300 }));
        await fixture.Render();
        await fixture.Invoke("Preview", "1110:111002");
        Assert.Equal("1110:111002", Assert.Single(fixture.Bosses.Alerts).Key);
        Assert.False(fixture.Bosses.GetReminder("1110:111002").Enabled);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de", "In Timer-Leiste anzeigen", "Einstellungen")]
    [InlineData("en", "Show in Timeline Strip", "Settings")]
    public async Task CatalogueListShowsUncapturedBossesAndSearchesLocalizedNamesAndMapsWithoutChangingSelections(string language, string stripLabel, string settingsLabel)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        fixture.Capture(false, 610_000);
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        Assert.Equal(3, Regex.Matches(html, "data-boss=\"").Count);
        Assert.Contains("data-boss=\"1010:101001\"", html); // Unselected and never captured, still configurable.
        Assert.Contains(language == "de" ? "Alle bekannten Bosse" : "All known bosses", html);
        Assert.Contains(language == "de" ? "Warte auf Boss-Timer" : "Waiting for boss timers", html);
        Assert.DoesNotContain("00:00:00", html);
        Assert.Contains(stripLabel, html);
        Assert.Contains(">" + settingsLabel + "</span>", html);
        var stripInputs = Regex.Matches(html, "<input[^>]+data-boss-strip=[^>]*>");
        Assert.Equal(3, stripInputs.Count);
        Assert.All(stripInputs.Cast<Match>(), input => Assert.DoesNotContain("disabled", input.Value));
        await fixture.Export(html, "boss-list");
        var filtered = await fixture.Invoke("Search", new ChangeEventArgs { Value = language == "de" ? "prüf-boss EINS" : "BOSS ONE" });
        Assert.Contains("data-boss=\"1110:111001\"", filtered);
        Assert.DoesNotContain("data-boss=\"1110:111002\"", filtered);
        Assert.DoesNotContain("data-boss=\"1010:101001\"", filtered);
        await fixture.Export(filtered, "boss-list-filtered");
        var map = await fixture.Invoke("Search", new ChangeEventArgs { Value = "VERTERON" });
        Assert.Contains("data-boss=\"1010:101001\"", map);
        Assert.DoesNotContain("data-boss=\"1110:111001\"", map);
        var empty = await fixture.Invoke("Search", new ChangeEventArgs { Value = "No matching fixture" });
        Assert.DoesNotContain("data-boss=\"", empty);
        Assert.Contains(language == "de" ? "Keine passenden Bosse" : "No matching bosses", empty);
        await fixture.Export(empty, "boss-list-empty");
        var cleared = await fixture.Invoke("ClearSearch");
        Assert.Equal(3, Regex.Matches(cleared, "data-boss=\"").Count);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task StripToggleMaterializesUnfilteredLegacySelectionAndDoesNotSelectOrEnableReminders(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111002"];
        fixture.Settings.Current.TimerOverlayTimerIds = null;
        fixture.Alerts.SetEventPresentation("shugo", new() { ShowOverlay = true, AlwaysShowOverlay = true });
        fixture.Alerts.SetBossPresentation("1110:111002", new() { ShowOverlay = true, PlaySound = true });
        var reminder = fixture.Bosses.GetReminder("1110:111002");
        var presentation = fixture.Alerts.GetBossPresentation("1110:111002");
        var timeline = (fixture.Settings.Current.EventTimelineOverlayEnabled, fixture.Settings.Current.EventTimelineHours,
            fixture.Settings.Current.EventTimelineOverlayScale);
        var original = fixture.Alerts.TimerOverlayChoices.Where(choice => choice.Selected).Select(choice => choice.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("event:catalog:shugo", original);
        await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        await fixture.Invoke("Search", new ChangeEventArgs { Value = language == "de" ? "Eins" : "one" });
        await fixture.Invoke("ToggleOptions", "1110:111001");
        var added = await fixture.Invoke("SelectStrip", "1110:111001", true);
        Assert.Contains(language == "de" ? "Timer-Leiste gespeichert." : "Timer strip saved.", added);
        Assert.Equal(original.Append("boss:1110:111001").Distinct().Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.Equal(fixture.Settings.Current.TimerOverlayTimerIds, new SettingsService().Current.TimerOverlayTimerIds);
        Assert.Equal(new[] { "1110:111002" }, fixture.Settings.Current.BossAlertIds);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(reminder, fixture.Bosses.GetReminder("1110:111002"));
        Assert.Equal(presentation, fixture.Alerts.GetBossPresentation("1110:111002"));
        Assert.Equal(timeline, (fixture.Settings.Current.EventTimelineOverlayEnabled, fixture.Settings.Current.EventTimelineHours,
            fixture.Settings.Current.EventTimelineOverlayScale));
        await fixture.Invoke("SelectStrip", "1110:111001", false);
        Assert.Equal(original, fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStripSelectionPreservesUnknownIdsAndKeepsAnExplicitEmptyArrayEmpty(bool empty)
    {
        await using var fixture = new Fixture("en");
        var original = empty ? Array.Empty<string>() : new[] { "boss:unavailable-fixture", "event:catalog:shugo", "event:missing-fixture" };
        fixture.Settings.Current.TimerOverlayTimerIds = original;
        fixture.Alerts.SetEventPresentation("rift", new() { ShowOverlay = true, AlwaysShowOverlay = true });
        await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        await fixture.Invoke("Search", new ChangeEventArgs { Value = "one" });
        await fixture.Invoke("SelectStrip", "1110:111001", true);
        Assert.Equal(original.Append("boss:1110:111001").Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        await fixture.Invoke("SelectStrip", "1110:111001", false);
        Assert.Equal(original.Order(StringComparer.Ordinal), fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.NotNull(new SettingsService().Current.TimerOverlayTimerIds);
        Assert.Equal(original.Order(StringComparer.Ordinal), new SettingsService().Current.TimerOverlayTimerIds);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.True(fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "rift").Presentation.ShowOverlay);
    }

    [Fact]
    public async Task StripControlsRejectBusyAndUnknownCallbacksButRemainIndependentOfReminderSelection()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        var html = await fixture.Render(busy: true, includeAll: true, compact: true, catalogue: true, strip: true);
        Assert.All(Regex.Matches(html, "<input[^>]+data-boss-strip=[^>]*>").Cast<Match>(), input => Assert.Contains("disabled", input.Value));
        await fixture.Invoke("SelectStrip", "1110:111001", true);
        Assert.Empty(fixture.Settings.Current.TimerOverlayTimerIds!);
        await fixture.View.SetBusy(false);
        await fixture.Invoke("SelectStrip", "missing-fixture", true);
        Assert.Empty(fixture.Settings.Current.TimerOverlayTimerIds!);
        await fixture.Invoke("SelectStrip", "1110:111001", true);
        Assert.Equal(new[] { "boss:1110:111001" }, fixture.Settings.Current.TimerOverlayTimerIds);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Empty(fixture.Bosses.Alerts);
    }

    [Fact]
    public async Task ExplicitUncapturedStripBossRemainsVisibleWithoutExpandingTheBossRushCatalogueMode()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111002"];
        var html = await fixture.Render(includeAll: true, compact: true, strip: true);
        Assert.Contains("data-boss=\"1110:111002\"", html);
        Assert.DoesNotContain("data-boss=\"1010:101001\"", html);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData("en", "de")]
    public async Task AbyssGroupSearchCombinesWithLocalizedMapAndBossNamesWithoutChangingPreferences(string uiLanguage, string nameLanguage)
    {
        await using var fixture = new Fixture(uiLanguage, abyssMaps: true);
        fixture.Settings.Current.NameLanguage = nameLanguage;
        fixture.Settings.Current.BossAlertIds = ["20:2001", "1010:101001"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:23:2301", "event:catalog:shugo", "boss:unavailable-fixture"];
        fixture.Settings.Current.BossReminderOverrides["20:2001"] = new BossReminderPreference { Enabled = false, LeadSeconds = 900 };
        var settingsBefore = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var reminderBefore = fixture.Bosses.GetReminder("20:2001");
        var presentationBefore = fixture.Alerts.GetBossPresentation("20:2001");
        var all = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        Assert.Equal(4, Regex.Matches(all, "data-boss=\"").Count);

        foreach (var query in new[] { "abyss", "aBySs" })
        {
            var matches = await fixture.Invoke("Search", new ChangeEventArgs { Value = query });
            Assert.Equal(3, Regex.Matches(matches, "data-boss=\"").Count);
            Assert.Contains("data-boss=\"20:2001\"", matches);
            Assert.Contains("data-boss=\"22:2201\"", matches);
            Assert.Contains("data-boss=\"23:2301\"", matches);
            Assert.DoesNotContain("data-boss=\"1010:101001\"", matches);
        }

        var upper = await fixture.Invoke("Search", new ChangeEventArgs { Value = uiLanguage == "de" ? "abyss oberes" : "abyss upper" });
        Assert.Single(Regex.Matches(upper, "data-boss=\"").Cast<Match>());
        Assert.Contains("data-boss=\"23:2301\"", upper);
        var middle = await fixture.Invoke("Search", new ChangeEventArgs { Value = nameLanguage == "de" ? "Prüf-Wächter Mitte" : "middle sentinel" });
        Assert.Single(Regex.Matches(middle, "data-boss=\"").Cast<Match>());
        Assert.Contains("data-boss=\"22:2201\"", middle);
        var lower = await fixture.Invoke("Search", new ChangeEventArgs { Value = uiLanguage == "de" ? "Untere" : "Lower" });
        Assert.Single(Regex.Matches(lower, "data-boss=\"").Cast<Match>());
        Assert.Contains("data-boss=\"20:2001\"", lower);
        var verteron = await fixture.Invoke("Search", new ChangeEventArgs { Value = "VERTERON" });
        Assert.Single(Regex.Matches(verteron, "data-boss=\"").Cast<Match>());
        Assert.Contains("data-boss=\"1010:101001\"", verteron);
        var cleared = await fixture.Invoke("ClearSearch");
        Assert.Equal(4, Regex.Matches(cleared, "data-boss=\"").Count);

        Assert.Equal(settingsBefore, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(reminderBefore, fixture.Bosses.GetReminder("20:2001"));
        Assert.Equal(presentationBefore, fixture.Alerts.GetBossPresentation("20:2001"));
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AlreadyRenderedCompactCatalogueAndBossRushReceiveAllMapTimersFromTheirSharedSingleton(string language)
    {
        await using var fixture = new Fixture(language, liveUpdates: true);
        var catalogueBefore = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        var rushBefore = await fixture.RenderBossRush();
        Assert.Equal(56, TimerCards(catalogueBefore).Count);
        Assert.All(TimerCards(catalogueBefore).Values, card => Assert.Contains("boss-timer-card missing", card));
        Assert.Equal(56, TimerCards(rushBefore).Count);
        Assert.All(TimerCards(rushBefore).Values, card => Assert.Contains("boss-timer-card missing", card));
        Assert.Empty(fixture.Bosses.Schedule);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.False(fixture.Settings.Current.BossRushEnabled);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);

        // Delivery happens after both roots subscribed, on a capture-like worker thread. Read the
        // existing roots afterwards: no new RenderComponentAsync or forced StateHasChanged can mask a lost event.
        await fixture.CaptureMap(1110, 111001, 24, 900_000);
        var expected = Enumerable.Range(111001, 24).ToDictionary(id => $"1110:{id}", _ => "00:15:00", StringComparer.Ordinal);
        await AssertBothRoots(expected);
        Assert.Equal(24, fixture.Bosses.Schedule.Count);
        Assert.All(fixture.Bosses.Schedule, row => Assert.False(row.Cached));

        fixture.Clock.UtcNow += TimeSpan.FromSeconds(10);
        await fixture.CaptureMap(1010, 101001, 24, 1_200_000);
        foreach (var key in expected.Keys.ToArray()) expected[key] = "00:14:50";
        foreach (var id in Enumerable.Range(101001, 24)) expected.Add($"1010:{id}", "00:20:00");
        await AssertBothRoots(expected);
        Assert.Equal(48, fixture.Bosses.Schedule.Count);
        Assert.All(fixture.Bosses.Schedule.Where(row => row.Place.Map == "altgard"), row => Assert.True(row.Cached));

        fixture.Clock.UtcNow += TimeSpan.FromSeconds(10);
        await fixture.CaptureMap(20, 2001, 8, 1_800_000);
        foreach (var key in expected.Keys.Where(key => key.StartsWith("1110:", StringComparison.Ordinal)).ToArray()) expected[key] = "00:14:40";
        foreach (var key in expected.Keys.Where(key => key.StartsWith("1010:", StringComparison.Ordinal)).ToArray()) expected[key] = "00:19:50";
        foreach (var id in Enumerable.Range(2001, 8)) expected.Add($"20:{id}", "00:30:00");
        await AssertBothRoots(expected);
        Assert.Equal(56, fixture.Bosses.Schedule.Count);
        Assert.Equal(48, fixture.Bosses.Schedule.Count(row => row.Cached));
        Assert.All(fixture.Bosses.Schedule.Where(row => row.Place.Map == "abyss-reshanta-a"), row => Assert.False(row.Cached));
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.False(fixture.Settings.Current.BossRushEnabled);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.False(fixture.Tracker.Running);
        Assert.Empty(fixture.Alerts.NotificationRows);

        async Task AssertBothRoots(IReadOnlyDictionary<string, string> counters)
        {
            AssertTimerCounters(await fixture.CurrentMarkup(), counters);
            AssertTimerCounters(await fixture.BossRushMarkup(), counters);
        }
    }

    [Theory]
    [InlineData("de", "Warte auf Boss-Timer")]
    [InlineData("en", "Waiting for boss timers")]
    public async Task LowerReshantaZeroTimesKeepTheRealTimerAndRemoveStaleCountdownsInBothOpenTabs(string language, string waiting)
    {
        await using var fixture = new Fixture(language, liveUpdates: true);
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = Enumerable.Range(2001, 8).Select(id => $"20:{id}").ToArray();
        fixture.Settings.Current.TimerOverlayTimerIds = Enumerable.Range(2001, 8).Select(id => $"boss:20:{id}").ToArray();
        await fixture.Render(includeAll: true, compact: true, catalogue: true);
        await fixture.RenderBossRush();
        await fixture.CaptureMap(20, 2001, 8, 600_000);
        Assert.Equal(8, fixture.Bosses.Schedule.Count);
        // Observed Lower list shape: one real time, seven wire-zero times. Replaces prior known times.
        await fixture.CaptureMap(20, 2001, 8, 600_000, Enumerable.Range(2002, 7).ToHashSet());
        var timer = Assert.Single(fixture.Bosses.Schedule);
        Assert.Equal("20:2001", timer.Place.Key);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Remaining);
        Assert.False(timer.Spawned);
        Assert.False(timer.Cached);
        Assert.Equal(2001, fixture.Bosses.View.Boss!.SpawnId);
        Assert.Equal(2001, Assert.Single(fixture.Bosses.OverlaySpawns).Place.Boss.SpawnId);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        var catalogue = TimerCards(await fixture.CurrentMarkup());
        var rush = TimerCards(await fixture.BossRushMarkup());
        Assert.Contains("00:10:00", catalogue["20:2001"]);
        Assert.Equal(56, rush.Count);
        Assert.Contains("00:10:00", rush["20:2001"]);
        foreach (var id in Enumerable.Range(2002, 7))
        {
            Assert.Contains(waiting, catalogue[$"20:{id}"]);
            Assert.Contains("boss-timer-card missing", catalogue[$"20:{id}"]);
            Assert.Contains($"<strong>{waiting}</strong>", catalogue[$"20:{id}"]);
            Assert.Contains($"<strong>{waiting}</strong>", rush[$"20:{id}"]);
        }
        Assert.Equal("boss:20:2001", Assert.Single(fixture.Alerts.TimerOverlayRows).Key);
        var mapRows = MapTimerPresentation.Create([], fixture.Bosses.Schedule, fixture.Bosses.Catalogue,
            fixture.Settings.Current.BossAlertIds, fixture.Clock.GetUtcNow(), language);
        Assert.Equal("00:10:00", mapRows.Single(row => row.Key == "boss:20:2001").Counter);
        Assert.All(mapRows.Where(row => row.Key != "boss:20:2001"), row =>
        {
            Assert.Null(row.At);
            Assert.Equal("—", row.Counter);
            Assert.Equal(waiting, row.State);
        });
        await fixture.CaptureMap(20, 2001, 8, 600_000, Enumerable.Range(2001, 8).ToHashSet());
        Assert.Empty(fixture.Bosses.Schedule);
        Assert.Empty(fixture.Alerts.TimerOverlayRows);
        Assert.Null(fixture.Bosses.View.Boss);
        Assert.Empty(fixture.Bosses.OverlaySpawns);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.All(TimerCards(await fixture.BossRushMarkup()).Values, card => Assert.Contains(waiting, card));
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de", "Fester Spielzeitplan", "Spielkalender · Europe/Berlin")]
    [InlineData("en", "Fixed game schedule", "Game calendar · Europe/Berlin")]
    public async Task CalendarFallbackFillsAllSevenLowerTimersInBothTabsMapAndStripWithoutConfirmingThem(string language, string phase, string source)
    {
        await using var fixture = new Fixture(language, liveUpdates: true, calendars: true);
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-10-18T13:56:01Z");
        fixture.Settings.Current.BossAlertIds = Enumerable.Range(2001, 8).Select(id => $"20:{id}").ToArray();
        fixture.Settings.Current.TimerOverlayTimerIds = Enumerable.Range(2001, 8).Select(id => $"boss:20:{id}").ToArray();
        await fixture.Render(includeAll: true, compact: true, catalogue: true);
        await fixture.RenderBossRush();
        Assert.Equal(7, fixture.Bosses.Schedule.Count);
        Assert.All(fixture.Bosses.Schedule, row => Assert.True(row.Calendar));
        await fixture.CaptureMap(20, 2001, 8, (long)new TimeSpan(9, 34, 40).TotalMilliseconds,
            Enumerable.Range(2002, 7).ToHashSet());
        var schedule = fixture.Bosses.Schedule;
        Assert.Equal(8, schedule.Count);
        Assert.Equal(7, schedule.Count(row => row.Calendar));
        Assert.False(schedule.Single(row => row.Place.Key == "20:2001").Calendar);
        var catalogue = TimerCards(await fixture.CurrentMarkup());
        var rush = TimerCards(await fixture.BossRushMarkup());
        Assert.Equal(56, rush.Count);
        foreach (var id in Enumerable.Range(2002, 7))
        {
            var expected = id == 2002 ? "00:03:59" : id <= 2005 ? "05:03:59" : "29:33:59";
            Assert.Contains(expected, catalogue[$"20:{id}"]);
            Assert.Contains(expected, rush[$"20:{id}"]);
            Assert.Contains(phase, catalogue[$"20:{id}"]);
            Assert.Contains(source, catalogue[$"20:{id}"]);
            Assert.DoesNotContain(language == "de" ? "Letzter Stand vor" : "Last update", catalogue[$"20:{id}"]);
        }
        Assert.Contains("09:34:40", catalogue["20:2001"]);
        Assert.Equal(8, fixture.Alerts.TimerOverlayRows.Count);
        var map = MapTimerPresentation.Create([], schedule, fixture.Bosses.Catalogue,
            fixture.Settings.Current.BossAlertIds, fixture.Clock.GetUtcNow(), language);
        Assert.Equal(8, map.Count);
        Assert.All(map.Where(row => row.Key != "boss:20:2001"), row => Assert.Equal(phase, row.State));
        var cache = new BossTimerCache(fixture.CachePath, fixture.Clock).Load();
        Assert.NotNull(cache);
        Assert.Equal(7, cache.Bosses.Count(row => row.SpawnUnixMs is null));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        Assert.False(fixture.Tracker.Running);
    }

    [Fact]
    public async Task CalendarWarningsDeduplicateTheirExactDeadlineWithoutConfirmingSpawnsOrWritingNetworkCache()
    {
        await using var fixture = new Fixture("en", liveUpdates: true, calendars: true);
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-10-18T13:59:10Z");
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["20:2002"];
        fixture.Settings.Current.BossAlertLeadSeconds = 60;
        fixture.Settings.Current.BossAlertDurationSeconds = 60;
        Assert.Equal(7, fixture.Bosses.Schedule.Count);
        fixture.Settings.Update(_ => { });
        var warning = Assert.Single(fixture.Bosses.Alerts);
        Assert.True(warning.Calendar);
        Assert.False(warning.Spawned);
        Assert.Equal("20:2002", warning.Key);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T14:00:00Z").ToUnixTimeMilliseconds(), warning.SpawnUnixMs);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(20);
        fixture.Settings.Update(_ => { });
        Assert.Equal(warning.Id, Assert.Single(fixture.Bosses.Alerts).Id);
        fixture.Network.Stop(); // A network reset cannot erase an independent calendar warning.
        Assert.Equal(warning.Id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-10-18T14:00:00Z");
        fixture.Settings.Update(_ => { });
        Assert.All(fixture.Bosses.Schedule, row => Assert.False(row.Spawned));
        Assert.Equal(TimeSpan.FromHours(3), fixture.Bosses.Schedule.Single(row => row.Place.Key == "20:2002").Remaining);
        Assert.DoesNotContain(fixture.Bosses.Alerts, row => row.Spawned);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BothBossListsPutConfirmedSpawnsBeforeFutureNetworkCacheAndCalendarTimesThenWaiting(string language)
    {
        await using var fixture = new Fixture(language, liveUpdates: true, calendars: true);
        fixture.Clock.UtcNow = DateTimeOffset.Parse("2026-10-18T13:56:01Z", global::System.Globalization.CultureInfo.InvariantCulture);
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002", "1110:111003", "1110:111004",
            "1010:101001", "1010:101002", "1010:101003", "1010:101004", "1010:101005"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111002", "boss:1010:101001"];
        var settingsBefore = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await fixture.CaptureRows(1110, new(111001, true, now - 60_000), new(111002, false, now + 300_000),
            new(111003, false, now - 60_000));
        await fixture.CaptureRows(1010, new(101001, true, now - 120_000), new(101002, false, now + 600_000),
            new(101003, false, now + 60_000), new(101004, false, now - 1_000), new(101005, false, null));
        var scheduleBefore = System.Text.Json.JsonSerializer.Serialize(fixture.Bosses.Schedule);
        var catalogue = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        var rush = await fixture.RenderBossRush();
        string[] first = ["1010:101001", "1010:101003", "20:2002", "1110:111002", "1010:101002",
            "20:2003", "20:2004", "20:2005", "20:2006", "20:2007", "20:2008"];
        string[] waiting = ["1010:101004", "1010:101005", "1110:111001", "1110:111003", "1110:111004"];
        Assert.Equal(first, TimerOrder(catalogue).Take(first.Length));
        Assert.Equal(TimerOrder(catalogue), TimerOrder(rush));
        Assert.Equal(first, TimerOrder(rush).Take(first.Length));
        var preview = Regex.Match(rush, @"<section\b[^>]*\bdata-boss-list[\s\S]*?</section>");
        Assert.True(preview.Success);
        await fixture.Export(preview.Value, "boss-list-sorted");
        Assert.All(waiting, key => Assert.True(Array.IndexOf(TimerOrder(catalogue), key) >= first.Length));
        var cards = TimerCards(catalogue);
        Assert.Contains(language == "de" ? "Unbestätigt" : "Unconfirmed", cards["1110:111001"]);
        Assert.Contains(language == "de" ? "Warte auf Bestätigung" : "Waiting for confirmation", cards["1010:101004"]);
        Assert.Contains(language == "de" ? "Warte auf Boss-Timer" : "Waiting for boss timers", cards["1010:101005"]);
        Assert.Contains(language == "de" ? "Fester Spielzeitplan" : "Fixed game schedule", cards["20:2002"]);
        var filtered = await fixture.Invoke("Search", new ChangeEventArgs { Value = "VERTERON" });
        Assert.Equal(new[] { "1010:101001", "1010:101003", "1010:101002" }, TimerOrder(filtered).Take(3));
        Assert.All(TimerOrder(filtered), key => Assert.StartsWith("1010:", key));
        Assert.Equal(scheduleBefore, System.Text.Json.JsonSerializer.Serialize(fixture.Bosses.Schedule));
        Assert.Equal(settingsBefore, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        Assert.False(fixture.Tracker.Running);
    }

    [Fact]
    public async Task EqualCountdownsAndNamesUseMapAndSpawnIdsInsteadOfCatalogueInsertionOrder()
    {
        await using var fixture = new Fixture("en", liveUpdates: true, duplicateBossNames: true);
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await fixture.CaptureRows(1110, new(111002, false, now + 300_000), new(111001, false, now + 300_000));
        await fixture.CaptureRows(1010, new Soulcrest.Core.Network.BossSpawn(101001, false, now + 300_000));
        var html = await fixture.Render(includeAll: true, compact: true, catalogue: true);
        Assert.Equal(new[] { "1010:101001", "1110:111001", "1110:111002" }, TimerOrder(html).Take(3));
        await fixture.RenderBossRush();
        Assert.Equal(TimerOrder(html), TimerOrder(await fixture.BossRushMarkup()));
        Assert.Equal(TimerOrder(html), TimerOrder(await fixture.Invoke("ClearSearch")));
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExpiredCountdownMovesBehindFutureTimersWhileItsExpandedOptionsStayOpen(string language)
    {
        await using var fixture = new Fixture(language);
        var now = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await fixture.CaptureRows(1110, new(111001, false, now + 1_000), new(111002, false, now + 60_000));
        var html = await fixture.Render(includeAll: true, compact: true);
        Assert.Equal(new[] { "1110:111001", "1110:111002" }, TimerOrder(html));
        await fixture.Invoke("ToggleOptions", "1110:111001");
        fixture.Clock.UtcNow += TimeSpan.FromMilliseconds(1_001);
        fixture.Settings.Update(_ => { });
        html = await fixture.CurrentMarkup();
        Assert.Equal(new[] { "1110:111002", "1110:111001" }, TimerOrder(html));
        var expired = TimerCards(html)["1110:111001"];
        Assert.Contains(language == "de" ? "Warte auf Bestätigung" : "Waiting for confirmation", expired);
        Assert.Matches("<details[^>]*data-boss-options=\"1110:111001\"[^>]*\\bopen(?:[ =>])", expired);
        Assert.Equal(TimeSpan.Zero, fixture.Bosses.Schedule.Single(row => row.Place.Key == "1110:111001").Remaining);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task GlobalDurationIsAboveSearchAndPersistsWithoutChangingAnyBossPreferenceOrOtherSetting(string language)
    {
        await using var fixture = new Fixture(language, isolateTracker: true);
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["event:catalog:shugo"];
        fixture.Settings.Current.BossAlertDurationSeconds = 10;
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        fixture.Settings.Current.LootTrackingEnabled = false;
        fixture.Settings.Current.BossReminderOverrides["1110:111001"] = new() { Enabled = false, LeadSeconds = 147 };
        fixture.Settings.Current.BossReminderOverrides["1110:111002"] = new() { Enabled = true, LeadSeconds = 900 };
        var otherSettings = SettingsWithoutBossDuration(fixture.Settings.Current);
        var html = await fixture.Render(includeAll: true, compact: true, catalogue: true, searchable: true, strip: true);
        Assert.Single(Regex.Matches(html, "data-boss-duration-global").Cast<Match>());
        Assert.True(html.IndexOf("data-boss-duration-global", StringComparison.Ordinal)
            < html.IndexOf("class=\"boss-timer-search-row\"", StringComparison.Ordinal));
        Assert.All(TimerCards(html).Values, card => Assert.DoesNotContain("data-boss-duration", card));
        var fixedHtml = await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = "17" });
        Assert.Equal(17, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.False(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Contains(language == "de" ? "Feste Dauer (Sekunden)" : "Fixed duration (seconds)", fixedHtml);
        Assert.DoesNotContain(language == "de" ? "Bestätigter Spawn & Test (Sekunden)" : "Confirmed spawn & test (seconds)", fixedHtml);
        Assert.Contains(language == "de" ? "Gilt für alle Boss-Erinnerungen." : "Applies to all boss reminders.", fixedHtml);
        Assert.Equal(17, new SettingsService().Current.BossAlertDurationSeconds);
        await fixture.Export(fixedHtml, "boss-duration-global-fixed");
        var untilHtml = await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "until-spawn" });
        Assert.True(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Equal(17, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.Contains(language == "de" ? "Gesamtes Vorwarnfenster" : "Full advance-warning window", untilHtml);
        Assert.Contains(language == "de" ? "Bestätigter Spawn & Test (Sekunden)" : "Confirmed spawn & test (seconds)", untilHtml);
        Assert.DoesNotContain(language == "de" ? "Feste Dauer (Sekunden)" : "Fixed duration (seconds)", untilHtml);
        Assert.Contains(language == "de" ? "ab der eingestellten Vorwarnzeit" : "from the configured advance warning", untilHtml);
        Assert.Contains(language == "de" ? "auch wenn der Countdown 0 erreicht" : "even when the countdown reaches 0", untilHtml);
        Assert.Single(Regex.Matches(untilHtml, "data-boss-duration-seconds").Cast<Match>());
        Assert.True(new SettingsService().Current.BossAlertUntilSpawn);
        await fixture.Export(untilHtml, "boss-duration-global-whole");
        await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = "31" });
        Assert.True(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Equal(31, fixture.Settings.Current.BossAlertDurationSeconds); // Confirmation and preview duration remains editable.
        await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "fixed" });
        Assert.False(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Equal(31, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.Equal(otherSettings, SettingsWithoutBossDuration(fixture.Settings.Current));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Null(fixture.Tracker.Network);
        Assert.False(fixture.Tracker.Running);
    }

    [Fact]
    public async Task GlobalDurationRejectsBusyAndMalformedChangesButDoesNotRequireSelectedBosses()
    {
        await using var fixture = new Fixture("en", isolateTracker: true);
        fixture.Settings.Current.BossAlertIds = [];
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        fixture.Settings.Current.BossAlertDurationSeconds = 10;
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        fixture.Settings.Current.LootTrackingEnabled = false;
        var html = await fixture.Render(busy: true, includeAll: true, compact: true, catalogue: true, searchable: true);
        Assert.All(Regex.Matches(html, "<(?:input|select)[^>]*data-boss-duration-(?:mode|seconds)[^>]*>").Cast<Match>(),
            control => Assert.Contains("disabled", control.Value));
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = "42" });
        await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "until-spawn" });
        await fixture.Invoke("ToggleAlerts", new ChangeEventArgs { Value = true });
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        await fixture.View.SetBusy(false);
        foreach (var invalid in new[] { "2", "61", "invalid" })
            await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = invalid });
        await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "invalid" });
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = "42" });
        await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "until-spawn" });
        Assert.Equal(42, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.True(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Settings.Current.BossReminderOverrides);
        Assert.Empty(fixture.Settings.Current.TimerOverlayTimerIds!);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.False(fixture.Settings.Current.LootTrackingEnabled);
        Assert.Null(fixture.Tracker.Network);
    }

    [Fact]
    public async Task OlderBossLeadAndEnabledCallbacksCannotChangeTheGlobalDuration()
    {
        await using var fixture = new Fixture("en");
        const string key = "1110:111001";
        fixture.Settings.Current.BossAlertIds = [key];
        fixture.Settings.Current.BossAlertDurationSeconds = 10;
        fixture.Settings.Current.BossAlertUntilSpawn = false;
        var earlierRender = fixture.Bosses.GetReminder(key);
        await fixture.Render(includeAll: true, compact: true);
        await fixture.Invoke("SetDuration", new ChangeEventArgs { Value = "17" });
        await fixture.Invoke("SetDurationMode", new ChangeEventArgs { Value = "until-spawn" });
        await fixture.Invoke("SetEnabled", key, false);
        await fixture.Invoke("SetLead", key, earlierRender, new ChangeEventArgs { Value = "901" });
        Assert.False(fixture.Bosses.GetReminder(key).Enabled);
        Assert.Equal(901, fixture.Bosses.GetReminder(key).LeadSeconds);
        await fixture.Invoke("SetQuickLead", key, 300);
        Assert.True(fixture.Bosses.GetReminder(key).Enabled);
        Assert.Equal(300, fixture.Bosses.GetReminder(key).LeadSeconds);
        Assert.True(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Equal(17, fixture.Settings.Current.BossAlertDurationSeconds);
        await fixture.Invoke("SetEnabled", key, false);
        Assert.True(fixture.Settings.Current.BossAlertUntilSpawn);
        Assert.Equal(17, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.Empty(fixture.Bosses.Alerts);
    }

    private static string SettingsWithoutBossDuration(AppSettings settings)
    {
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        values.Remove(nameof(AppSettings.BossAlertDurationSeconds));
        values.Remove(nameof(AppSettings.BossAlertUntilSpawn));
        return System.Text.Json.JsonSerializer.Serialize(values);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task HeaderMasterPreservesExplicitTrackingRequestWithoutOpeningACaptureSourceInTheFixture(string language)
    {
        await using var fixture = new Fixture(language, isolateTracker: true);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.TimerOverlayTimerIds = [];
        fixture.Settings.Current.LootTrackingEnabled = false;
        var globalDuration = fixture.Settings.Current.BossAlertDurationSeconds;
        var html = await fixture.Render(includeAll: true, compact: true, catalogue: true);
        Assert.Single(Regex.Matches(html, "data-boss-alerts-master").Cast<Match>());
        var id = Regex.Match(html, "id=\"(boss-alerts-toggle-[^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(id);
        Assert.Contains("for=\"" + id + "\"", html);
        Assert.Contains(language == "de" ? "Alerts für ausgewählte Bosse" : "Alerts for selected bosses", html);
        Assert.Null(fixture.Tracker.Network); // No interface source or capture coordinator is present.
        await fixture.Invoke("ToggleAlerts", new ChangeEventArgs { Value = true });
        Assert.True(fixture.Settings.Current.BossAlertsEnabled);
        Assert.True(fixture.Settings.Current.LootTrackingEnabled);
        Assert.False(fixture.Tracker.Running);
        await fixture.Invoke("ToggleAlerts", new ChangeEventArgs { Value = false });
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.True(fixture.Settings.Current.LootTrackingEnabled); // Pausing reminders does not stop requested loot tracking.
        Assert.Equal(globalDuration, fixture.Settings.Current.BossAlertDurationSeconds);
        Assert.Equal(new[] { "1110:111001" }, fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Settings.Current.TimerOverlayTimerIds!);
        Assert.Empty(fixture.Settings.Current.BossReminderOverrides);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.False(new SettingsService().Current.BossAlertsEnabled);
    }

    private static string[] TimerOrder(string html) => Regex.Matches(html, "<article\\b[^>]*\\bdata-boss=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();

    private static Dictionary<string, string> TimerCards(string html) => Regex.Matches(html,
            "<article\\b[^>]*\\bdata-boss=\"([^\"]+)\"[^>]*>[\\s\\S]*?</article>")
        .Cast<Match>().ToDictionary(match => match.Groups[1].Value, match => match.Value, StringComparer.Ordinal);

    private static void AssertTimerCounters(string html, IReadOnlyDictionary<string, string> expected)
    {
        var cards = TimerCards(html);
        Assert.Equal(56, cards.Count);
        foreach (var (key, counter) in expected)
        {
            Assert.True(cards.ContainsKey(key), $"Missing captured timer {key}.");
            var card = cards[key];
            Assert.DoesNotContain("boss-timer-card missing", card);
            var rendered = Regex.Match(card, "class=\"event-countdown[^\"]*\"[^>]*>[\\s\\S]*?<strong>([^<]*)</strong>");
            Assert.True(rendered.Success, $"Missing countdown for {key}.");
            Assert.Equal(counter, rendered.Groups[1].Value);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-timer-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root, _bossRoot;
        public CaptureLiveBossTimers View { get; private set; } = null!;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public AlertsService Alerts => _provider.GetRequiredService<AlertsService>();
        public TrackerService Tracker => _provider.GetRequiredService<TrackerService>();
        public NetworkLootService Network => _provider.GetRequiredService<NetworkLootService>();
        public BossRushService Bosses => _provider.GetRequiredService<BossRushService>();
        public ManualTime Clock { get; } = new();
        public MapTargetsService Targets => _provider.GetRequiredService<MapTargetsService>();
        public string CachePath => Path.Combine(_folder, "boss-timers.json");

        public Fixture(string language, bool abyssMaps = false, bool liveUpdates = false, bool calendars = false, bool duplicateBossNames = false, bool isolateTracker = false)
        {
            _language = language;
            if (liveUpdates)
            {
                var maps = new[]
                {
                    new { id = "altgard", label = "Altgard", group = "Asmodier", mapId = 1110, firstSpawn = 111001, count = 24 },
                    new { id = "verteron", label = "Verteron", group = "Elyos", mapId = 1010, firstSpawn = 101001, count = 24 },
                    new { id = "abyss-reshanta-a", label = "Chaotische Untere Ebene von Reshanta", group = "Abyss", mapId = 20, firstSpawn = 2001, count = 8 }
                };
                Directory.CreateDirectory(_folder);
                File.WriteAllText(Path.Combine(_folder, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    maps = maps.Select(map => new { map.id, map.label, map.group })
                }));
                foreach (var map in maps)
                {
                    Directory.CreateDirectory(Path.Combine(_folder, map.id));
                    File.WriteAllText(Path.Combine(_folder, map.id, "bosses.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        mapId = map.mapId,
                        bosses = Enumerable.Range(map.firstSpawn, map.count).Select(id => new
                        {
                            spawnId = id, npcId = 9_000_000 + id,
                            en = duplicateBossNames ? "Repeated synthetic boss" : $"Synthetic boss {id}",
                            de = duplicateBossNames ? "Gleichnamiger Prüf-Boss" : $"Prüf-Boss {id}",
                            x = id, y = 200, icon = "icons/bosses/synthetic-boss.png",
                            calendarSchedule = calendars && map.mapId == 20 ? CalendarFor(id) : null
                        })
                    }));
                }
            }
            else if (abyssMaps)
            {
                var maps = new[]
                {
                    new { id = "abyss-reshanta-a", label = "Chaotische Untere Ebene von Reshanta", group = "Abyss", mapId = 20, spawnId = 2001, en = "Synthetic lower sentry", de = "Prüf-Wächter Unten" },
                    new { id = "chaotic-middle-reshanta", label = "Chaotische Mittlere Ebene von Reshanta", group = "Abyss", mapId = 22, spawnId = 2201, en = "Synthetic middle sentinel", de = "Prüf-Wächter Mitte" },
                    new { id = "chaotic-upper-reshanta", label = "Chaotisches Oberes Reshanta", group = "Abyss", mapId = 23, spawnId = 2301, en = "Synthetic upper guardian", de = "Prüf-Wächter Oben" },
                    new { id = "verteron", label = "Verteron", group = "Elyos", mapId = 1010, spawnId = 101001, en = "Synthetic land guardian", de = "Prüf-Wächter Land" }
                };
                Directory.CreateDirectory(_folder);
                File.WriteAllText(Path.Combine(_folder, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    maps = maps.Select(map => new { map.id, map.label, map.group })
                }));
                foreach (var map in maps)
                {
                    Directory.CreateDirectory(Path.Combine(_folder, map.id));
                    File.WriteAllText(Path.Combine(_folder, map.id, "bosses.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        mapId = map.mapId,
                        bosses = new[] { new { spawnId = map.spawnId, npcId = 9000000 + map.mapId, map.en, map.de, x = 100, y = 200, icon = "icons/bosses/synthetic-boss.png" } }
                    }));
                }
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
                File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
                File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                    {"mapId":1110,"bosses":[
                    {"spawnId":111001,"npcId":2400017,"en":"Synthetic boss one","de":"Prüf-Boss Eins","x":100,"y":200,"icon":"icons/bosses/synthetic-boss.png"},
                    {"spawnId":111002,"npcId":2400074,"en":"Synthetic boss two","de":"Prüf-Boss Zwei","x":300,"y":400,"icon":"icons/bosses/synthetic-boss.png"}]}
                    """);
                Directory.CreateDirectory(Path.Combine(_folder, "verteron"));
                File.WriteAllText(Path.Combine(_folder, "verteron", "bosses.json"), """{"mapId":1010,"bosses":[{"spawnId":101001,"npcId":2400088,"en":"Synthetic boss three","de":"Prüf-Boss Drei","x":500,"y":600,"icon":"icons/bosses/synthetic-boss.png"}]}""");
            }
            Directory.CreateDirectory(Path.Combine(_folder, "icons", "bosses"));
            using (var icon = new System.Drawing.Bitmap(2, 2))
            {
                icon.SetPixel(0, 0, System.Drawing.Color.Gold);
                icon.Save(Path.Combine(_folder, "icons", "bosses", "synthetic-boss.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            if (isolateTracker) services.AddSingleton(sp => new TrackerService(sp.GetRequiredService<SettingsService>(), network: null));
            else services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton<UiState>();
            services.AddSingleton(sp => new BossRushService(sp.GetRequiredService<ProgressService>(), sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<MapTargetsService>(), sp.GetRequiredService<NetworkLootService>(), Clock, Path.Combine(_folder, "boss-timers.json")));
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), Clock, runTimer: false, bosses: sp.GetRequiredService<BossRushService>(), settings: sp.GetRequiredService<SettingsService>()));
            _provider = services.BuildServiceProvider();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.BossRushEnabled = Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossReminderOverrides = new(StringComparer.Ordinal);
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.BossAlertSound = false;
            Settings.Current.TimerOverlayTimerIds = null;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        private static Soulcrest.Core.Alerts.AlertSchedule? CalendarFor(int id) => id switch
        {
            2002 => new() { Kind = Soulcrest.Core.Alerts.AlertScheduleKind.Weekly,
                Times = Enumerable.Range(0, 8).Select(i => new TimeOnly(1 + i * 3, 0)).ToArray(),
                Weekdays = Enum.GetValues<DayOfWeek>() },
            >= 2003 and <= 2005 => new() { Kind = Soulcrest.Core.Alerts.AlertScheduleKind.Weekly,
                Times = [new(21, 0)], Weekdays = [DayOfWeek.Friday, DayOfWeek.Sunday] },
            >= 2006 and <= 2008 => new() { Kind = Soulcrest.Core.Alerts.AlertScheduleKind.Weekly,
                Times = [new(21, 30)], Weekdays = [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday] },
            _ => null
        };

        public void Capture(bool spawned, long delta)
        {
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            byte[] message = spawned
                ? [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                    1, 0x99, 0xe3, 6, .. new byte[12], 1, .. BitConverter.GetBytes(now + delta), 0, 0, 0]
                : [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                    0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + delta), 0, 0, 0];
            var parsed = Soulcrest.Core.Network.BossSpawnMessage.TryParse(message);
            Assert.NotNull(parsed);
            Assert.Equal(spawned, Assert.Single(parsed.Bosses).Spawned);
            Bosses.OnMessage("fixture", message);
            Assert.Equal(spawned, Assert.Single(Bosses.Schedule).Spawned);
        }

        public Task CaptureRows(int mapId, params Soulcrest.Core.Network.BossSpawn[] bosses) => Task.Run(() =>
        {
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
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
                message.AddRange(BitConverter.GetBytes(boss.SpawnUnixMs ?? 0L));
            }
            message.AddRange([0, 0, 0]);
            Assert.NotNull(Soulcrest.Core.Network.BossSpawnMessage.TryParse(message.ToArray()));
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            Bosses.OnMessage("fixture", message.ToArray());

            void Varint(int value)
            {
                var remaining = (uint)value;
                while (remaining >= 128) { message.Add((byte)(remaining | 128)); remaining >>= 7; }
                message.Add((byte)remaining);
            }
        });

        public Task CaptureMap(int mapId, int firstSpawn, int count, long delta, IReadOnlySet<int>? withoutTime = null) => Task.Run(() =>
        {
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            List<byte> message = [1, 0x91, 0, 0, .. BitConverter.GetBytes(mapId)];
            Varint(count);
            for (var index = 0; index < count; index++)
            {
                message.Add(0); // No spawn is inferred from these future timestamps.
                Varint(firstSpawn + index);
                if (index % 8 == 0) message.Add(0); // Carried packed booleans, one byte per eight rows.
                message.AddRange(BitConverter.GetBytes(withoutTime?.Contains(firstSpawn + index) == true ? 0L : now + delta));
            }
            message.AddRange([0, 0, 0]);
            var parsed = Soulcrest.Core.Network.BossSpawnMessage.TryParse(message.ToArray());
            Assert.NotNull(parsed);
            Assert.Equal(count, parsed.Bosses.Count);
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            Bosses.OnMessage("fixture", message.ToArray());

            void Varint(int value)
            {
                var remaining = (uint)value;
                while (remaining >= 128) { message.Add((byte)(remaining | 128)); remaining >>= 7; }
                message.Add((byte)remaining);
            }
        });

        public async Task<string> RenderBossRush()
        {
            _bossRoot = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>());
            return await BossRushMarkup();
        }

        public Task<string> CurrentMarkup() => Markup();
        public async Task<string> BossRushMarkup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_bossRoot.ToHtmlString));

        public async Task<string> Render(bool busy = false, bool includeAll = false, bool compact = false,
            bool catalogue = false, bool searchable = false, bool strip = false)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureLiveBossTimers>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaptureLiveBossTimers.Captured)] = (Action<CaptureLiveBossTimers>)(view => View = view),
                [nameof(LiveBossTimers.Busy)] = busy, [nameof(LiveBossTimers.IncludeAllCaptured)] = includeAll, [nameof(LiveBossTimers.Compact)] = compact,
                [nameof(LiveBossTimers.IncludeCatalogue)] = catalogue, [nameof(LiveBossTimers.Searchable)] = searchable,
                [nameof(LiveBossTimers.ShowTimerStripSelection)] = strip
            })));
            return await Markup();
        }
        public async Task<string> Invoke(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(LiveBossTimers).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, args);
                if (result is Task task) await task;
                await View.RefreshNow();
            });
            return await Markup();
        }
        private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_ALERTS_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"{state}-{_language}.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/boss-timers.css\"><link rel=\"stylesheet\" href=\"/css/alerts-workspace.css\"></head><body><div class=\"page alerts-page alerts-workspace\" style=\"max-width:1100px;margin:0 auto\">" + html + "</div></body></html>");
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

public sealed class CaptureLiveBossTimers : LiveBossTimers
{
    [Parameter] public Action<CaptureLiveBossTimers>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
    public Task SetBusy(bool busy) => InvokeAsync(() => { Busy = busy; StateHasChanged(); });
}
