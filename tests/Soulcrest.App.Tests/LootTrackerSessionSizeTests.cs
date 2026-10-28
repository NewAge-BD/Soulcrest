using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerSessionSizeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static LootTrackerTimelineEvent Fact(LootTrackerTimelineEventKind kind, double seconds) =>
        new(Guid.NewGuid().ToString("N"), kind, At.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));

    private static LootTrackerSession Timeline(params LootTrackerTimelineEvent[] facts) => new()
    {
        Id = "timeline", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At, TimelineEvents = [.. facts]
    };

    [Fact]
    public void FactsOfOneKillFormOneActivityAndLaterKillsAddMore()
    {
        var oneKill = Timeline(Fact(LootTrackerTimelineEventKind.KinaUnbound, 0), Fact(LootTrackerTimelineEventKind.Drop, 0.4),
            Fact(LootTrackerTimelineEventKind.AbyssPoints, 2.5));
        Assert.Equal(1, LootTrackerSessionSize.Activities(oneKill));
        var twoKills = oneKill with { TimelineEvents = oneKill.TimelineEvents.Add(Fact(LootTrackerTimelineEventKind.Drop, 12)) };
        Assert.Equal(2, LootTrackerSessionSize.Activities(twoKills));
    }

    [Fact]
    public void GatheringAttemptsCountSeparatelyAndMarkersDoNot()
    {
        var session = Timeline(Fact(LootTrackerTimelineEventKind.Availability, 0), Fact(LootTrackerTimelineEventKind.HarvestSuccess, 5),
            Fact(LootTrackerTimelineEventKind.HarvestFailure, 11), Fact(LootTrackerTimelineEventKind.PointsStageReached, 30));
        Assert.Equal(2, LootTrackerSessionSize.Activities(session));
        Assert.Equal(0, LootTrackerSessionSize.Activities(Timeline(Fact(LootTrackerTimelineEventKind.Availability, 0))));
    }

    [Fact]
    public void OwnCombatResultsCountAsActivities()
    {
        var session = Timeline() with
        {
            Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddSeconds(20), TimeSpan.FromSeconds(20)),
                new("death", LootTrackerCombatKind.Death, At.AddSeconds(90), TimeSpan.FromSeconds(90))]
        };
        Assert.Equal(2, LootTrackerSessionSize.Activities(session));
    }

    [Fact]
    public void LegacySessionsUseKnownCountersAndUnknownSizesAreNeverSmall()
    {
        var legacy = new LootTrackerSession { Id = "legacy", Status = LootTrackerSessionStatus.Completed, StartedAt = At, MonsterCount = 2 };
        Assert.Equal(2, LootTrackerSessionSize.Activities(legacy));
        Assert.True(LootTrackerSessionSize.IsSmall(legacy, 3));
        Assert.Equal(5, LootTrackerSessionSize.Activities(legacy with { MonsterCount = null, HarvestCount = 4, FailedHarvestCount = 1 }));
        var unknown = legacy with { MonsterCount = null };
        Assert.Null(LootTrackerSessionSize.Activities(unknown));
        Assert.False(LootTrackerSessionSize.IsSmall(unknown, 10));
    }

    [Fact]
    public void ThresholdIsInclusiveClampedAndALiveSessionIsNeverSmall()
    {
        var three = Timeline(Fact(LootTrackerTimelineEventKind.Drop, 0), Fact(LootTrackerTimelineEventKind.Drop, 10), Fact(LootTrackerTimelineEventKind.Drop, 20));
        Assert.True(LootTrackerSessionSize.IsSmall(three, 3));
        Assert.False(LootTrackerSessionSize.IsSmall(three, 2));
        Assert.False(LootTrackerSessionSize.IsSmall(three, 0));
        Assert.True(LootTrackerSessionSize.IsSmall(three, 99));
        Assert.False(LootTrackerSessionSize.IsSmall(three with { Status = LootTrackerSessionStatus.Live }, 3));
        Assert.True(LootTrackerSessionSize.IsSmall(three with { Status = LootTrackerSessionStatus.Interrupted }, 3));
        Assert.Equal(1, LootTrackerSessionSize.NormalizeMaxActivities(0));
        Assert.Equal(10, LootTrackerSessionSize.NormalizeMaxActivities(99));
    }
}
