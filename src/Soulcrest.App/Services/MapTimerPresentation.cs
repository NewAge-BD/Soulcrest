using System.Globalization;

namespace Soulcrest.App.Services;

public sealed record MapTimerRow(string Key, string Title, string Symbol, string Color,
    string Counter, string State, string Detail, DateTimeOffset? At, bool Provisional = false);

/// <summary>All offline events and selected captured bosses, independent of reminder/overlay switches.</summary>
public static class MapTimerPresentation
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    public static IReadOnlyList<MapTimerRow> Create(IReadOnlyList<EventTimerSnapshot> events,
        IReadOnlyList<BossRushEntry> captured, IReadOnlyList<BossChoice> catalogue, IEnumerable<string> selected,
        DateTimeOffset now, string language)
    {
        List<MapTimerRow> rows = [];
        foreach (var timer in events)
        {
            var window = timer.Window;
            var portal = window.IsActive && window.PortalEndsAt > now;
            var at = window.IsActive ? portal ? window.PortalEndsAt!.Value : window.EndsAt : window.StartsAt;
            var state = UiText.T(window.IsActive ? portal ? "Portal schließt in" : "Endet in" : "Startet in");
            rows.Add(new("event:" + timer.Definition.Id, UiText.T(timer.Definition.Title), timer.Definition.Symbol,
                timer.Definition.Color, Countdown(at - now), state, state + " · " + Clock(at), at));
        }
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        var timers = captured.ToDictionary(row => row.Place.Key, StringComparer.Ordinal);
        foreach (var choice in catalogue.Where(choice => ids.Contains(choice.Key)))
        {
            var timer = timers.GetValueOrDefault(choice.Key);
            var provisional = timer is null || timer.Cached || timer.Age > TimeSpan.FromSeconds(15);
            var state = UiText.T(timer is null ? "Warte auf Boss-Timer" : provisional ? "Unbestätigt" : timer.Spawned ? "Gespawnt" : timer.Remaining > TimeSpan.Zero ? "Startet in" : "Warte auf Bestätigung");
            DateTimeOffset? at = timer is { Remaining: var remaining } && remaining > TimeSpan.Zero ? now + remaining : null;
            var counter = timer is null ? "—" : timer.Spawned ? UiText.T(provisional ? "Unbestätigt" : "Gespawnt") : timer.Remaining > TimeSpan.Zero ? Countdown(timer.Remaining) : "—";
            rows.Add(new("boss:" + choice.Key, choice.Boss.Name(language), "◆", "#f87171", counter,
                state, state + (at is { } spawn ? " · " + Clock(spawn) : ""), at, provisional));
        }
        return rows.OrderBy(row => row.At ?? DateTimeOffset.MaxValue).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }
    private static string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
    private static string Countdown(TimeSpan remaining) => remaining <= TimeSpan.Zero ? UiText.T("Jetzt")
        : remaining.TotalDays >= 1 ? UiText.F("{0} T. {1:00}:{2:00}:{3:00}", (int)remaining.TotalDays, remaining.Hours, remaining.Minutes, remaining.Seconds)
        : $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
}
