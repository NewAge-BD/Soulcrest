using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

public sealed record EventTimerDefinition(string Id, string Title, string Description, string Symbol,
    string Color, AlertSchedule Schedule, int DurationMinutes, int PortalMinutes, string SourceUrl);
public sealed record EventTimerPresentation
{
    public bool ShowOverlay { get; init; }
    public bool AlwaysShowOverlay { get; init; }
    public int OverlayLeadMinutes { get; init; } = 15;
}
public sealed record EventTimerSnapshot(EventTimerDefinition Definition, EventWindow Window,
    EventAlert? Reminder, EventTimerPresentation Presentation);

/// <summary>Offline community calendar. Bosses deliberately remain outside this catalog.</summary>
public static class EventCatalog
{
    public const string SourceUrl = "";
    public const string ReminderPrefix = "catalog:";
    public static string ReminderId(string id) => ReminderPrefix + id;
    public static IReadOnlyList<EventTimerDefinition> All =>
    [
        new("shugo", "Shugo Festival", "Start zu jeder vollen Stunde.", "✦", "#fbbf24",
            new() { TimeZoneId = "Europe/Berlin" }, 0, 0, SourceUrl),
        new("rift", "Spacetime Rift", "Alle drei Stunden · Portal 10 Minuten offen · Event 60 Minuten.", "◇", "#a78bfa",
            new() { Kind = AlertScheduleKind.Daily, TimeZoneId = "Europe/Berlin",
                Times = [new(2, 0), new(5, 0), new(8, 0), new(11, 0), new(14, 0), new(17, 0), new(20, 0), new(23, 0)] }, 60, 10, SourceUrl),
        new("invasion", "Dimensional Invasion", "Stündlich um :30 · 15 Minuten aktiv.", "⚔", "#fb923c",
            new() { TimeZoneId = "Europe/Berlin", Minute = 30 }, 15, 0, SourceUrl),
        // User decision 2026-10-12: use the community EU weekdays; existing own alerts remain unchanged.
        new("abyss", "Abyss Siege", "Montag, Donnerstag und Samstag · 21:00 Europe/Berlin.", "♜", "#ef4444",
            new() { Kind = AlertScheduleKind.Weekly, TimeZoneId = "Europe/Berlin",
                Weekdays = [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday], Times = [new(21, 0)] }, 30, 0, SourceUrl),
        new("daily-reset", "Täglicher Reset", "Täglich um 07:00 UTC.", "↻", "#5eead4",
            new() { Kind = AlertScheduleKind.Daily, TimeZoneId = "UTC", Times = [new(7, 0)] }, 0, 0, SourceUrl),
        new("weekly-reset", "Wöchentlicher Reset", "Mittwochs um 07:00 UTC.", "↺", "#60a5fa",
            new() { Kind = AlertScheduleKind.Weekly, TimeZoneId = "UTC", Times = [new(7, 0)], Weekdays = [DayOfWeek.Wednesday] }, 0, 0, SourceUrl)
    ];

    public static EventTimerDefinition? Find(string id) => All.FirstOrDefault(item => item.Id == id);
    public static EventAlert Reminder(EventTimerDefinition definition) => new()
    {
        Id = ReminderId(definition.Id), Title = UiText.T(definition.Title), Schedule = definition.Schedule,
        Enabled = false, NotifyAtStart = false, LeadMinutes = 5, OverlayLeadMinutes = 0,
        Color = definition.Color, SourceLabel = "", SourceUrl = definition.SourceUrl
    };
}
