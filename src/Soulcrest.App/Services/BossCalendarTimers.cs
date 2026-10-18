using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

/// <summary>Verified EU calendar rules fill missing times without manufacturing network observations.</summary>
public static class BossCalendarTimers
{
    public static IReadOnlyList<BossRushEntry> Create(IReadOnlyList<BossChoice> catalogue,
        IReadOnlyList<BossRushEntry> captured, DateTimeOffset now)
    {
        var known = captured.Select(row => row.Place.Key).ToHashSet(StringComparer.Ordinal);
        List<BossRushEntry> rows = [];
        foreach (var choice in catalogue)
        {
            if (known.Contains(choice.Key) || choice.Boss.CalendarSchedule is not { Kind: AlertScheduleKind.Weekly } schedule
                || schedule.TimeZoneId != "Europe/Berlin" || AlertSchedules.Next(schedule, now) is not { } next) continue;
            rows.Add(new(choice, false, next - now, TimeSpan.Zero, Calendar: true));
        }
        return rows;
    }
}
