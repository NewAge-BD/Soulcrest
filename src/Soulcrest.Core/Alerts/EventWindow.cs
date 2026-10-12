namespace Soulcrest.Core.Alerts;

public enum EventWindowPhase
{
    Upcoming,
    PortalOpen,
    Active
}

/// <summary>The current active occurrence, or the next occurrence when the event is inactive.</summary>
public sealed record EventWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset? PortalEndsAt, DateTimeOffset? NextAt, EventWindowPhase Phase)
{
    public bool IsActive => Phase is EventWindowPhase.PortalOpen or EventWindowPhase.Active;
}

public static class EventWindows
{
    /// <summary>Resolve event and portal phases independently of notification banner duration.</summary>
    public static EventWindow? At(AlertSchedule schedule, DateTimeOffset now, TimeSpan duration, TimeSpan portal = default)
    {
        if (AlertSchedules.Error(schedule) is not null || duration < TimeSpan.Zero || duration > TimeSpan.FromDays(7) ||
            portal < TimeSpan.Zero || portal > duration) return null;

        var next = AlertSchedules.Next(schedule, now);
        DateTimeOffset? started = null;
        if (duration > TimeSpan.Zero)
        {
            // Next is exclusive, so an occurrence ending exactly now is already inactive.
            var cursor = AlertSchedules.Next(schedule, Add(now, -duration));
            for (var count = 0; cursor is { } candidate && candidate <= now && count < 10081; count++)
            {
                started = candidate;
                cursor = AlertSchedules.Next(schedule, candidate);
            }
        }
        if (started is not { } start)
            return next is { } upcoming ? new(upcoming, Add(upcoming, duration),
                portal > TimeSpan.Zero ? Add(upcoming, portal) : null, next, EventWindowPhase.Upcoming) : null;

        var portalEnd = portal > TimeSpan.Zero ? Add(start, portal) : (DateTimeOffset?)null;
        return new(start, Add(start, duration), portalEnd, next,
            portalEnd > now ? EventWindowPhase.PortalOpen : EventWindowPhase.Active);
    }

    /// <summary>Bounded occurrence list, inclusive at from and exclusive at until.</summary>
    public static IReadOnlyList<DateTimeOffset> Starts(AlertSchedule schedule, DateTimeOffset from, DateTimeOffset until, int limit = 128)
    {
        if (until <= from || limit <= 0) return [];
        limit = Math.Min(limit, 4096);
        List<DateTimeOffset> result = [];
        var cursor = from.UtcTicks == 0 ? from : from.AddTicks(-1);
        while (result.Count < limit && AlertSchedules.Next(schedule, cursor) is { } next && next < until)
        {
            result.Add(next);
            cursor = next;
        }
        return result;
    }

    private static DateTimeOffset Add(DateTimeOffset at, TimeSpan span)
    {
        if (span.Ticks > 0 && DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks < span.Ticks) return DateTimeOffset.MaxValue;
        if (span.Ticks < 0 && at.UtcTicks < -span.Ticks) return DateTimeOffset.MinValue;
        return new DateTimeOffset(at.UtcTicks + span.Ticks, TimeSpan.Zero);
    }
}
