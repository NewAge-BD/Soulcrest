using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class EventTimerDisplayTests : IDisposable
{
    private readonly string _language = UiText.Language;
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("de", "2026-10-12T17:59:59.999Z", "2026-10-12T18:00:00Z", false)]
    [InlineData("en", "2026-10-12T17:59:59.999Z", "2026-10-12T18:00:00Z", false)]
    [InlineData("de", "2026-10-12T18:00:00Z", "2026-10-12T18:10:00Z", true)]
    [InlineData("en", "2026-10-12T18:00:00Z", "2026-10-12T18:10:00Z", true)]
    [InlineData("de", "2026-10-12T18:09:59.999Z", "2026-10-12T18:10:00Z", true)]
    [InlineData("en", "2026-10-12T18:09:59.999Z", "2026-10-12T18:10:00Z", true)]
    [InlineData("de", "2026-10-12T18:10:00Z", "2026-10-12T21:00:00Z", false)]
    [InlineData("en", "2026-10-12T18:10:00Z", "2026-10-12T21:00:00Z", false)]
    [InlineData("de", "2026-10-12T21:00:00Z", "2026-10-12T21:10:00Z", true)]
    [InlineData("en", "2026-10-12T21:00:00Z", "2026-10-12T21:10:00Z", true)]
    public void RiftDisplayChangesExactlyAtPortalBoundariesAndSharesLocalizedTitleWithMapAndStrip(
        string language, string instant, string target, bool open)
    {
        UiText.Language = language;
        var now = At(instant);
        var timer = Snapshot("rift", now);
        var original = JsonSerializer.Serialize(timer);
        var display = EventTimerDisplay.From(timer);
        Assert.Equal(open, display.IsActive);
        Assert.Equal(open, display.Window.IsActive);
        Assert.Equal(open ? EventWindowPhase.PortalOpen : EventWindowPhase.Upcoming, display.Window.Phase);
        Assert.Equal(At(target), display.CountdownAt);
        Assert.Equal(display.Window.StartsAt.AddMinutes(10), display.Window.EndsAt);
        Assert.Equal(display.Window.EndsAt, display.Window.PortalEndsAt);
        Assert.Equal(open ? language == "de" ? "Spacetime Rift offen" : "Spacetime Rift Open" : "Spacetime Rift", display.Title);
        Assert.Equal(open ? language == "de" ? "Portal offen" : "Portal open" : language == "de" ? "Als Nächstes" : "Coming up", display.PhaseLabel);
        Assert.Equal(open ? language == "de" ? "Portal schließt in" : "Portal closes in" : language == "de" ? "Startet in" : "Starts in", display.CountdownLabel);
        var strip = TimerOverlayPresentation.FromEvent(timer.Definition, timer).Row!;
        var map = Assert.Single(MapTimerPresentation.Create([timer], [], [], [], now, language));
        Assert.Equal(display.Title, strip.Title);
        Assert.Equal(display.Title, map.Title);
        Assert.Equal(display.CountdownAt, strip.At);
        Assert.Equal(display.CountdownAt, map.At);
        Assert.Equal(display.CountdownLabel, strip.CountdownLabel);
        Assert.Equal(display.CountdownLabel, map.State);
        Assert.Equal(open ? display.Title : null, strip.InlineLabel);
        Assert.Equal(original, JsonSerializer.Serialize(timer));
    }

    [Theory]
    [InlineData("2026-10-25T00:09:59.999Z", "2026-10-25T00:10:00Z", true)]
    [InlineData("2026-10-25T00:10:00Z", "2026-10-25T04:00:00Z", false)]
    [InlineData("2026-10-25T01:05:00Z", "2026-10-25T04:00:00Z", false)]
    [InlineData("2026-03-29T00:59:59Z", "2026-03-29T03:00:00Z", false)]
    [InlineData("2026-03-29T03:00:00Z", "2026-03-29T03:10:00Z", true)]
    public void RiftDisplayUsesTheExistingCalendarAcrossBerlinDstWithoutInventingARepeatedOpening(
        string instant, string target, bool open)
    {
        var now = At(instant);
        var timer = Snapshot("rift", now);
        var display = EventTimerDisplay.From(timer);
        Assert.Equal(open, display.IsActive);
        Assert.Equal(At(target), display.CountdownAt);
        Assert.Equal(display.Window.StartsAt.AddMinutes(10), display.Window.EndsAt);
        Assert.Equal(timer.Window.NextAt, AlertSchedules.Next(timer.Definition.Schedule, now));
    }

    [Fact]
    public void DisplayProjectionLeavesSixtyMinuteCalendarAndReminderOccurrencesUnchanged()
    {
        var now = At("2026-10-12T18:10:00Z");
        var timer = Snapshot("rift", now);
        var reminder = EventCatalog.Reminder(timer.Definition) with
        {
            Enabled = true, LeadMinutes = 7, NotifyAtStart = true, PlaySound = true, ShowOverlay = false
        };
        timer = timer with { Reminder = reminder, Presentation = new() { ShowOverlay = true, AlwaysShowOverlay = true } };
        var original = JsonSerializer.Serialize(timer);
        var expectedNext = AlertSchedules.Next(reminder.Schedule, now);
        var display = EventTimerDisplay.From(timer);
        Assert.False(display.IsActive);
        Assert.Equal(EventWindowPhase.Active, timer.Window.Phase);
        Assert.Equal(At("2026-10-12T18:00:00Z"), timer.Window.StartsAt);
        Assert.Equal(At("2026-10-12T19:00:00Z"), timer.Window.EndsAt);
        Assert.Equal(At("2026-10-12T21:00:00Z"), display.Window.StartsAt);
        Assert.Equal(60, timer.Definition.DurationMinutes);
        Assert.Equal(10, timer.Definition.PortalMinutes);
        Assert.Equal(8, timer.Definition.Schedule.Times.Length);
        Assert.Same(reminder, timer.Reminder);
        Assert.Same(timer.Definition.Schedule, reminder.Schedule);
        Assert.Equal(expectedNext, AlertSchedules.Next(reminder.Schedule, now));
        Assert.Equal(original, JsonSerializer.Serialize(timer));
    }

    [Theory]
    [InlineData("invasion", "2026-10-12T18:35:00Z", "2026-10-12T18:45:00Z", true)]
    [InlineData("abyss", "2026-10-12T19:05:00Z", "2026-10-12T19:30:00Z", true)]
    [InlineData("shugo", "2026-10-12T18:05:00Z", "2026-10-12T19:00:00Z", false)]
    [InlineData("daily-reset", "2026-10-12T18:05:00Z", "2026-10-13T07:00:00Z", false)]
    [InlineData("weekly-reset", "2026-10-12T18:05:00Z", "2026-10-14T07:00:00Z", false)]
    public void OtherEventsKeepTheirExistingWindowTitleAndTarget(string id, string instant, string target, bool active)
    {
        var timer = Snapshot(id, At(instant));
        var display = EventTimerDisplay.From(timer);
        Assert.Same(timer.Window, display.Window);
        Assert.Equal(UiText.T(timer.Definition.Title), display.Title);
        Assert.Equal(At(target), display.CountdownAt);
        Assert.Equal(active, display.IsActive);
        Assert.Null(TimerOverlayPresentation.FromEvent(timer.Definition, timer).Row!.InlineLabel);
    }

    private static EventTimerSnapshot Snapshot(string id, DateTimeOffset now)
    {
        var definition = EventCatalog.Find(id)!;
        var window = EventWindows.At(definition.Schedule, now, TimeSpan.FromMinutes(definition.DurationMinutes),
            TimeSpan.FromMinutes(definition.PortalMinutes));
        return new(definition, Assert.IsType<EventWindow>(window), null, new());
    }

    public void Dispose() => UiText.Language = _language;
}
