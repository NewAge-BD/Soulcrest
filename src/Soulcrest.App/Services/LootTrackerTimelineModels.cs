using System.Collections.Immutable;

namespace Soulcrest.App.Services;

public enum LootTrackerTimelineEventKind
{
    AbyssPoints, RankPoints, KinaBound, KinaUnbound, Drop, HarvestSuccess, HarvestFailure,
    PointsStageReached, Availability
}

public enum LootTrackerTimelineMetric { AbyssPoints, RankPoints, KinaBound, KinaUnbound }

/// <summary>
/// One immutable local fact in a session transaction. Elapsed is the monotonic replay clock;
/// At retains the original observation time. A points-stage marker never books point income.
/// Anonymous values only: no opponent names, transport identities or native entity/object keys.
/// </summary>
public sealed record LootTrackerTimelineEvent(string Id, LootTrackerTimelineEventKind Kind,
    DateTimeOffset At, TimeSpan Elapsed)
{
    public LootTrackerActivityOrigin? Origin { get; init; }
    public long? Amount { get; init; }
    public ImmutableArray<LootTrackerLoot> Loot { get; init; } = [];
    /// <summary>Fresh visual evidence captured with a new drop; absent historical placements stay unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LootTrackerCombatPosition? Position { get; init; }
    public LootTrackerGatherVisit? GatherVisit { get; init; }
    /// <summary>Static resource template, including attempts without a visual placement.</summary>
    public int? GatherableId { get; init; }
    public string? FromStageId { get; init; }
    public string? ToStageId { get; init; }
    public LootTrackerTimelineMetric? Metric { get; init; }
    /// <summary>An explicit authority baseline/change; unavailable is never a confirmed zero.</summary>
    public bool? Available { get; init; }
}

internal static class LootTrackerTimelineFacts
{
    internal static LootTrackerTimelineEvent Create(LootTrackerTimelineEventKind kind,
        DateTimeOffset at, TimeSpan elapsed) => new(Guid.NewGuid().ToString("N"), kind, at, elapsed);

    internal static string? ClassId(string? value) => value is "Gladiator" or "Templar" or "Ranger"
        or "Assassin" or "Elementalist" or "Sorcerer" or "Cleric" or "Chanter" ? value : null;

    internal static LootTrackerSession Availability(LootTrackerSession session, LootTrackerTimelineMetric metric,
        bool available, DateTimeOffset at, TimeSpan elapsed)
    {
        var last = session.TimelineEvents.LastOrDefault(row => row.Kind == LootTrackerTimelineEventKind.Availability
            && row.Metric == metric);
        if (last?.Available == available) return session;
        return session with { TimelineEvents = session.TimelineEvents.Add(Create(LootTrackerTimelineEventKind.Availability,
            at, elapsed) with { Metric = metric, Available = available }) };
    }
}
