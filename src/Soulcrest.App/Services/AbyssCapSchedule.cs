namespace Soulcrest.App.Services;

/// <summary>User decision 2026-10-26: cumulative per-character cap; spending never restores budget.</summary>
public static class AbyssCapSchedule
{
    public static DateTimeOffset Anchor { get; } = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    public const long AnchorCap = 2_000_000;
    public const long WeeklyIncrease = 500_000;

    /// <summary>No earlier seasonal cap is inferred from the current user-confirmed anchor.</summary>
    public static long? CapAt(DateTimeOffset at) => at < Anchor ? null
        : checked(AnchorCap + (at.UtcTicks - Anchor.UtcTicks) / TimeSpan.TicksPerDay / 7 * WeeklyIncrease);

    public static DateTimeOffset NextReset(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        var next = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 7, 0, 0, TimeSpan.Zero);
        next = next.AddDays(((int)DayOfWeek.Wednesday - (int)utc.DayOfWeek + 7) % 7);
        return next <= utc ? next.AddDays(7) : next;
    }
}

/// <summary>Cached values remain visible but only separately marked current values are confirmed.</summary>
public sealed record AbyssProgressSnapshot(string? ProfileId, long? AbyssPointBalance,
    long? FarmedAbyssPoints, long? RankPoints, long? Cap, DateTimeOffset? ObservedAt, bool Confirmed)
{
    /// <summary>Transient own binding only. Never persisted with character values.</summary>
    public long? BindingGeneration { get; init; }
    public bool AbyssPointBalanceConfirmed { get; init; }
    public bool FarmedAbyssPointsConfirmed { get; init; }
    public bool RankPointsConfirmed { get; init; }
    public DateTimeOffset? RankPointsObservedAt { get; init; }
    public long RankObservationSequence { get; init; }
    public bool RankTrackingActive { get; init; }
    public long RankContinuityRevision { get; init; }
    public DateTimeOffset? NextResetAt { get; init; }
    public long? Remaining => Cap is { } cap && FarmedAbyssPoints is { } farmed ? Math.Max(0, cap - farmed) : null;
    public static AbyssProgressSnapshot Empty { get; } = new(null, null, null, null, null, null, false);
}
