using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

public sealed record EventTimelineOccurrence(string Key, string Title, string Color,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? PortalEndsAt = null,
    bool Provisional = false, bool Spawned = false, string? Icon = null);
public sealed record EventTimelineLane(string Key, string Title, string Color,
    IReadOnlyList<EventTimelineOccurrence> Occurrences, DateTimeOffset? NextAt = null, bool Bosses = false);
public sealed record EventTimelineSnapshot(DateTimeOffset Now, DateTimeOffset Until, int Hours,
    IReadOnlyList<EventTimelineLane> Lanes, int SelectedBossCount, int CapturedBossCount,
    IReadOnlyList<EventTimelineOccurrence> Resets)
{
    // User request 2026-10-12: stable full-hour grid; Now remains the actual current instant.
    public DateTimeOffset From => HourStart(Now);
    internal static DateTimeOffset HourStart(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month,
        at.UtcDateTime.Day, at.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
    public double Fraction(DateTimeOffset at) => Until <= From ? 0 : Math.Clamp((at - From).TotalSeconds / (Until - From).TotalSeconds, 0, 1);
}

/// <summary>One captured boss occurrence, never an invented respawn calendar, beside the offline event calendar.</summary>
public static class EventTimelinePresentation
{
    public static EventTimelineSnapshot Create(IReadOnlyList<EventTimerSnapshot> events,
        IReadOnlyList<BossRushEntry> bosses, IEnumerable<string> selectedBossIds,
        DateTimeOffset now, int hours, string nameLanguage)
    {
        hours = hours is 24 or 48 ? hours : 12;
        var until = Add(EventTimelineSnapshot.HourStart(now), TimeSpan.FromHours(hours));
        List<EventTimelineLane> lanes = [];
        List<EventTimelineOccurrence> resets = [];
        List<EventTimelineOccurrence> siege = [];
        foreach (var timer in events)
        {
            var definition = timer.Definition;
            var duration = TimeSpan.FromMinutes(definition.DurationMinutes);
            var starts = EventWindows.Starts(definition.Schedule, Add(now, -duration), until);
            var occurrences = starts.Where(at => duration == TimeSpan.Zero ? at >= now : Add(at, duration) > now)
                .Select(at => new EventTimelineOccurrence(definition.Id + ":" + at.ToUnixTimeSeconds(),
                    UiText.T(definition.Title), definition.Color, at, Add(at, duration),
                    definition.PortalMinutes > 0 ? Add(at, TimeSpan.FromMinutes(definition.PortalMinutes)) : null))
                .ToArray();
            // User decision 2026-10-12: resets cross all tracks; siege shares the invasion track.
            if (definition.Id is "daily-reset" or "weekly-reset") resets.AddRange(occurrences);
            else if (definition.Id == "abyss") siege.AddRange(occurrences);
            else lanes.Add(new(definition.Id, UiText.T(definition.Title), definition.Color, occurrences, timer.Window.StartsAt));
        }
        var invasionIndex = lanes.FindIndex(lane => lane.Key == "invasion");
        if (invasionIndex >= 0)
        {
            var invasion = lanes[invasionIndex];
            var occurrences = invasion.Occurrences.Concat(siege).OrderBy(row => row.StartsAt).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
            lanes[invasionIndex] = invasion with
            {
                Title = invasion.Title + " · " + UiText.T("Abyss Siege"), Occurrences = occurrences,
                NextAt = occurrences.FirstOrDefault()?.StartsAt
            };
        }

        var selected = selectedBossIds.ToHashSet(StringComparer.Ordinal);
        var captured = bosses.Where(row => selected.Contains(row.Place.Key)).ToArray();
        var bossOccurrences = captured.Select(row =>
        {
            var at = row.Spawned || row.Remaining <= TimeSpan.Zero ? now : Add(now, row.Remaining);
            return new EventTimelineOccurrence(row.Place.Key, row.Place.Boss.Name(nameLanguage), "#f87171", at, at,
                Provisional: row.Cached || (!row.Spawned && row.Remaining <= TimeSpan.Zero),
                Spawned: row.Spawned, Icon: row.Place.Boss.Icon);
        }).Where(row => row.StartsAt < until).OrderBy(row => row.StartsAt).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
        // Reminder Off retains selection and its visible timer. No boss selection means no boss lane.
        if (selected.Count > 0) lanes.Add(new("bosses", UiText.T("Erfasste Bosse"), "#f87171", bossOccurrences,
            bossOccurrences.FirstOrDefault()?.StartsAt, Bosses: true));
        return new(now, until, hours, lanes, selected.Count, captured.Length,
            resets.OrderBy(row => row.StartsAt).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray());
    }

    private static DateTimeOffset Add(DateTimeOffset at, TimeSpan span)
    {
        if (span.Ticks > 0 && DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks < span.Ticks) return DateTimeOffset.MaxValue;
        if (span.Ticks < 0 && at.UtcTicks < -span.Ticks) return DateTimeOffset.MinValue;
        return new DateTimeOffset(at.UtcTicks + span.Ticks, TimeSpan.Zero);
    }
}
