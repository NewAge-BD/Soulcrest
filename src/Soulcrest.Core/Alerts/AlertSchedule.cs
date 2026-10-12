namespace Soulcrest.Core.Alerts;

public enum AlertScheduleKind
{
    Hourly,
    Daily,
    Weekly,
    EveryNDays,
    Interval,
    Once
}

/// <summary>A local calendar rule or an absolute elapsed-time rule, independent of the computer's zone.</summary>
public sealed record AlertSchedule
{
    public AlertScheduleKind Kind { get; init; }
    public string TimeZoneId { get; init; } = "Europe/Berlin";
    public int Minute { get; init; }
    public TimeOnly[] Times { get; init; } = [];
    public DayOfWeek[] Weekdays { get; init; } = [];
    public DateOnly? AnchorDate { get; init; }
    public int EveryDays { get; init; } = 2;
    public DateTimeOffset? At { get; init; }
    public int IntervalMinutes { get; init; } = 60;
}

public static class AlertSchedules
{
    // User request 2026-10-12: calendar events follow their explicit zone, including DST.
    // Search occurrences, not elapsed UTC days; even annual rules can skip an invalid local time.
    private const int SearchLimit = 370;

    /// <summary>Returns a short validation code, or null for a valid rule.</summary>
    public static string? Error(AlertSchedule schedule) => Validate(schedule, out _);

    /// <summary>Returns the next instant strictly after the supplied instant, always in UTC.</summary>
    public static DateTimeOffset? Next(AlertSchedule schedule, DateTimeOffset after)
    {
        if (Validate(schedule, out var zone) is not null) return null;
        if (schedule.Kind == AlertScheduleKind.Once)
            return schedule.At > after ? schedule.At!.Value.ToUniversalTime() : null;
        if (schedule.Kind == AlertScheduleKind.Interval)
            return NextInterval(schedule.At!.Value, schedule.IntervalMinutes, after);

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone!).DateTime);
        var step = 1;
        if (schedule.Kind == AlertScheduleKind.EveryNDays)
        {
            step = schedule.EveryDays;
            var anchor = schedule.AnchorDate!.Value.DayNumber;
            var distance = Math.Max(0L, day.DayNumber - (long)anchor);
            var first = anchor + (distance + step - 1) / step * step;
            if (first > DateOnly.MaxValue.DayNumber) return null;
            day = DateOnly.FromDayNumber((int)first);
        }

        var times = schedule.Kind == AlertScheduleKind.Hourly
            ? Enumerable.Range(0, 24).Select(hour => new TimeOnly(hour, schedule.Minute)).ToArray()
            : schedule.Times.Distinct().Order().ToArray();
        DateTimeOffset? best = null;
        var bestDay = 0;
        for (var searched = 0; searched < SearchLimit; searched++)
        {
            // Zone offsets are bounded by 14 hours. Later local dates cannot precede this
            // candidate after two additional days, even across a historical date-line change.
            if (best is not null && day.DayNumber > bestDay + 2) return best;
            if (schedule.Kind != AlertScheduleKind.Weekly || schedule.Weekdays.Contains(day.DayOfWeek))
            {
                foreach (var time in times)
                {
                    var local = day.ToDateTime(time, DateTimeKind.Unspecified);
                    foreach (var instant in Instants(local, zone!, schedule.Kind == AlertScheduleKind.Hourly))
                    {
                        if (instant <= after || best is not null && instant >= best) continue;
                        best = instant;
                        bestDay = day.DayNumber;
                    }
                }
            }
            if (step > DateOnly.MaxValue.DayNumber - day.DayNumber) return best;
            day = day.AddDays(step);
        }
        return best;
    }

    private static string? Validate(AlertSchedule? schedule, out TimeZoneInfo? zone)
    {
        zone = null;
        if (schedule is null) return "schedule";
        if (!Enum.IsDefined(schedule.Kind)) return "kind";
        if (!TryZone(schedule.TimeZoneId, out zone)) return "time-zone";
        switch (schedule.Kind)
        {
            case AlertScheduleKind.Hourly:
                return schedule.Minute is < 0 or > 59 ? "minute" : null;
            case AlertScheduleKind.Daily:
                return schedule.Times is not { Length: > 0 } ? "times" : null;
            case AlertScheduleKind.Weekly:
                if (schedule.Times is not { Length: > 0 }) return "times";
                return schedule.Weekdays is not { Length: > 0 } ||
                    schedule.Weekdays.Any(day => !Enum.IsDefined(day)) ? "weekdays" : null;
            case AlertScheduleKind.EveryNDays:
                if (schedule.Times is not { Length: > 0 }) return "times";
                if (schedule.AnchorDate is null) return "anchor-date";
                return schedule.EveryDays <= 0 ? "every-days" : null;
            case AlertScheduleKind.Interval:
                if (schedule.At is null) return "at";
                return schedule.IntervalMinutes <= 0 ? "interval-minutes" : null;
            case AlertScheduleKind.Once:
                return schedule.At is null ? "at" : null;
            default:
                return "kind";
        }
    }

    private static bool TryZone(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone)) return true;
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) &&
                TimeZoneInfo.TryFindSystemTimeZoneById(windows, out zone)) return true;
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) &&
                TimeZoneInfo.TryFindSystemTimeZoneById(iana, out zone);
        }
        catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static DateTimeOffset? NextInterval(DateTimeOffset anchor, int minutes, DateTimeOffset after)
    {
        var start = anchor.UtcTicks;
        if (after.UtcTicks < start) return anchor.ToUniversalTime();
        var step = minutes * TimeSpan.TicksPerMinute;
        var next = (after.UtcTicks - start) / step + 1;
        if (next > (DateTimeOffset.MaxValue.UtcTicks - start) / step) return null;
        return new DateTimeOffset(start + next * step, TimeSpan.Zero);
    }

    private static IEnumerable<DateTimeOffset> Instants(DateTime local, TimeZoneInfo zone, bool repeatHour)
    {
        if (zone.IsInvalidTime(local)) yield break;
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        DateTimeOffset? first = null;
        foreach (var offset in offsets)
        {
            DateTimeOffset instant;
            try { instant = new DateTimeOffset(local, offset).ToUniversalTime(); }
            catch (ArgumentException) { continue; } // Local year 1/9999 can exceed the UTC range.
            if (repeatHour) yield return instant;
            else if (first is null || instant < first) first = instant;
        }
        if (!repeatHour && first is not null) yield return first.Value;
    }
}
