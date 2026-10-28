namespace Soulcrest.App.Services;

/// <summary>
/// User request 2026-10-28: measures a session by its activity moments so that the list can hide
/// a single gathered node or one or two kills. Pure presentation; nothing is deleted or rewritten.
/// </summary>
public static class LootTrackerSessionSize
{
    public const int DefaultMaxActivities = 3;
    public const int MinMaxActivities = 1;
    public const int MaxMaxActivities = 10;
    /// <summary>Kina, drop and points of one kill arrive within moments and form a single activity.</summary>
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(3);

    public static int NormalizeMaxActivities(int activities) => Math.Clamp(activities, MinMaxActivities, MaxMaxActivities);

    /// <summary>
    /// Timeline sessions count own income, drop, gathering and combat facts grouped by the monotonic replay clock.
    /// Legacy aggregates count known monsters and gathering attempts; entirely unknown counters stay unknown.
    /// </summary>
    public static long? Activities(LootTrackerSession session)
    {
        if (session.TimelineVersion > 0)
        {
            var moments = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents)
                .Where(row => row.Kind is not (LootTrackerTimelineEventKind.Availability or LootTrackerTimelineEventKind.PointsStageReached))
                .Select(row => row.Elapsed)
                .Concat((session.CombatEvents.IsDefault ? [] : session.CombatEvents).Select(row => row.Elapsed))
                .Order().ToArray();
            long count = 0;
            for (var i = 0; i < moments.Length; i++)
                if (i == 0 || moments[i] - moments[i - 1] > ActivityWindow) count++;
            return count;
        }
        if (session.MonsterCount is null && session.HarvestCount is null && session.FailedHarvestCount is null) return null;
        return Math.Max(0, session.MonsterCount ?? 0) + Math.Max(0, session.HarvestCount ?? 0) + Math.Max(0, session.FailedHarvestCount ?? 0);
    }

    /// <summary>A live session always starts small and is never hidden; an unknown size is never guessed small.</summary>
    public static bool IsSmall(LootTrackerSession session, int maxActivities) =>
        session.Status != LootTrackerSessionStatus.Live && Activities(session) is { } count && count <= NormalizeMaxActivities(maxActivities);
}
