using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerTimelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static LootTrackerTimelineEvent Fact(string id, LootTrackerTimelineEventKind kind, int seconds, long? amount = null) =>
        new(id, kind, At.AddSeconds(seconds), TimeSpan.FromSeconds(seconds)) { Amount = amount };
    private static LootTrackerTimelineEvent Availability(string id, LootTrackerTimelineMetric metric, int seconds, bool available) =>
        Fact(id, LootTrackerTimelineEventKind.Availability, seconds) with { Metric = metric, Available = available };
    private static LootTrackerSession Session => new()
    {
        Id = "timeline-synthetic", Kind = LootTrackerSessionKind.Abyss, TimelineVersion = 1,
        StartedAt = At, Elapsed = TimeSpan.FromMinutes(3), AbyssPoints = 300, RankPointsGained = 200, RankPointsLost = 350,
        TimelineEvents = [Availability("ap-ready", LootTrackerTimelineMetric.AbyssPoints, 10, true),
            Availability("rp-ready", LootTrackerTimelineMetric.RankPoints, 0, true),
            Fact("ap1", LootTrackerTimelineEventKind.AbyssPoints, 20, 100),
            Fact("rp1", LootTrackerTimelineEventKind.RankPoints, 30, 200),
            Fact("rp2", LootTrackerTimelineEventKind.RankPoints, 70, -350),
            Fact("ap2", LootTrackerTimelineEventKind.AbyssPoints, 80, 200)]
    };

    [Fact]
    public void CumulativeSeriesPreservesUnknownBaselineAndIndependentSignedRankIncome()
    {
        var session = Session;
        var series = LootTrackerTimeline.Build(session, LootTimelineValueMode.Cumulative);
        var ap = series.Single(s => s.Key == "ap"); var rp = series.Single(s => s.Key == "rp");
        Assert.Null(ap.ValueAt(TimeSpan.FromSeconds(9)));
        Assert.Equal(0, ap.ValueAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(100, ap.ValueAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(300, ap.ValueAt(session.Elapsed));
        Assert.Equal(-150, rp.ValueAt(session.Elapsed));
        Assert.Equal(-150, rp.Minimum);
        Assert.Empty(session.CombatEvents);
        Assert.Equal(300, session.AbyssPoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenWorldPointsKeepTheirIndependentAvailabilityAndSignedCurvesWithOrWithoutAView(bool projected)
    {
        var parent = Session with
        {
            Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1,
            TimelineEvents = Session.TimelineEvents.Select(row => row with { Origin = LootTrackerActivityOrigin.Pve }).ToImmutableArray()
        };
        var session = projected ? LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve) : parent;
        var series = LootTrackerTimeline.Build(session, LootTimelineValueMode.Cumulative);
        var ap = Assert.Single(series, row => row.Key == "ap"); var rp = Assert.Single(series, row => row.Key == "rp");
        Assert.Null(ap.ValueAt(TimeSpan.FromSeconds(9))); Assert.Equal(0, ap.ValueAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(300, ap.ValueAt(session.Elapsed)); Assert.Equal(-150, rp.ValueAt(session.Elapsed));
        Assert.Equal(-150, rp.Minimum); Assert.Equal(TimeSpan.FromMinutes(3), session.Elapsed);
        var minutes = LootTrackerTimeline.Build(session, LootTimelineValueMode.PerMinute);
        Assert.Equal(100, minutes.Single(row => row.Key == "ap").ValueAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(200, minutes.Single(row => row.Key == "ap").ValueAt(TimeSpan.FromSeconds(80)));
        Assert.Equal(-350, minutes.Single(row => row.Key == "rp").ValueAt(TimeSpan.FromSeconds(80)));
        foreach (var excluded in new[] { LootTrackerViewMode.Lifeskill, LootTrackerViewMode.Abyss })
            Assert.DoesNotContain(LootTrackerTimeline.Build(LootTrackerSessionProjection.Project(parent, excluded), LootTimelineValueMode.Cumulative),
                row => row.Key is "ap" or "rp");
    }

    [Fact]
    public void PerMinuteValuesAreAmountsInEachMinuteAndDoNotReuseCumulativeAmounts()
    {
        var series = LootTrackerTimeline.Build(Session, LootTimelineValueMode.PerMinute);
        Assert.Equal(100, series[0].ValueAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(200, series[0].ValueAt(TimeSpan.FromSeconds(80)));
        Assert.Equal(0, series[0].ValueAt(TimeSpan.FromSeconds(150)));
        Assert.Equal(200, series[1].ValueAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(-350, series[1].ValueAt(TimeSpan.FromSeconds(80)));
    }

    [Fact]
    public void SourceLossSplitsCurveAndDoesNotTreatAnUnqualifiedMinuteAsZero()
    {
        var session = Session with { TimelineEvents = [Availability("off", LootTrackerTimelineMetric.AbyssPoints, 0, false),
            Availability("on", LootTrackerTimelineMetric.AbyssPoints, 60, true),
            Fact("gain", LootTrackerTimelineEventKind.AbyssPoints, 80, 25),
            Availability("lost", LootTrackerTimelineMetric.AbyssPoints, 100, false),
            Availability("back", LootTrackerTimelineMetric.AbyssPoints, 130, true)] };
        var cumulative = LootTrackerTimeline.Build(session, LootTimelineValueMode.Cumulative)[0];
        Assert.Null(cumulative.ValueAt(TimeSpan.FromSeconds(30)));
        Assert.Null(cumulative.ValueAt(TimeSpan.FromSeconds(110)));
        Assert.Equal(25, cumulative.ValueAt(TimeSpan.FromSeconds(140)));
        Assert.Equal(2, cumulative.Paths(session.Elapsed).Count);
        var minutes = LootTrackerTimeline.Build(session, LootTimelineValueMode.PerMinute)[0];
        Assert.Null(minutes.ValueAt(TimeSpan.FromSeconds(30)));
        Assert.Equal(25, minutes.ValueAt(TimeSpan.FromSeconds(80)));
        Assert.Null(minutes.ValueAt(TimeSpan.FromSeconds(110)));
        Assert.Null(minutes.ValueAt(TimeSpan.FromSeconds(125)));
    }

    [Fact]
    public void GatheringRewardIsCountedOnceAndUnplacedFailedAttemptRemainsVisible()
    {
        var session = Session with { Kind = LootTrackerSessionKind.Lifeskill, CombatEvents = [], FailedHarvestCount = 1, HarvestCount = 1, ItemCount = 6,
            TimelineEvents = [Fact("harvest", LootTrackerTimelineEventKind.HarvestSuccess, 20) with
                { Loot = [new("sample-material", "Sample material", LootTrackerLootKind.Item, 4), new("sample-bonus", "Sample bonus", LootTrackerLootKind.Item, 2)] },
                Fact("failed", LootTrackerTimelineEventKind.HarvestFailure, 30)] };
        var series = LootTrackerTimeline.Build(session, LootTimelineValueMode.Cumulative);
        Assert.Equal(6, series.Single(s => s.Key == "items").ValueAt(session.Elapsed));
        Assert.Equal(1, series.Single(s => s.Key == "success").ValueAt(session.Elapsed));
        Assert.Equal(1, series.Single(s => s.Key == "failure").ValueAt(session.Elapsed));
        Assert.Equal(2, LootTrackerTimeline.Events(session).Count);
    }

    [Fact]
    public void OldMinuteBinsRemainExplicitlyAggregatedWithoutInventedCombatOrStageEvents()
    {
        var session = Session with { TimelineVersion = 0, TimelineEvents = [],
            MinuteIncome = [new(0, null, null) { AbyssPoints = 100 }, new(2, null, null) { AbyssPoints = 200 }] };
        var ap = LootTrackerTimeline.Build(session, LootTimelineValueMode.PerMinute)[0];
        Assert.True(ap.Aggregated);
        Assert.Equal(100, ap.ValueAt(TimeSpan.FromSeconds(20)));
        Assert.Null(ap.ValueAt(TimeSpan.FromSeconds(90)));
        Assert.Empty(LootTrackerTimeline.Events(session));
    }

    [Theory]
    [InlineData(50L, null)]
    [InlineData(null, 20L)]
    [InlineData(null, null)]
    public void LegacyRankNetKeepsAnUnknownGainOrLossSourceUnknown(long? gained, long? lost)
    {
        var session = Session with { TimelineVersion = 0, TimelineEvents = [],
            MinuteIncome = [new(0, null, null) { RankPointsGained = gained, RankPointsLost = lost }] };
        foreach (var mode in Enum.GetValues<LootTimelineValueMode>())
            Assert.Null(LootTrackerTimeline.Build(session, mode).Single(series => series.Key == "rp").ValueAt(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void OldUnknownFailureSourceDoesNotBecomeConfirmedZeroFromExactSuccessTimes()
    {
        var session = Session with { Kind = LootTrackerSessionKind.Lifeskill, FailedHarvestCount = null };
        Assert.Null(LootTrackerTimeline.Build(session, LootTimelineValueMode.Cumulative).Single(s => s.Key == "failure").ValueAt(session.Elapsed));
    }

    [Fact]
    public void DenseEventsKeepEveryEventWhenGroupedAndZeroDurationIsFinite()
    {
        var session = Session with { CombatEvents = Enumerable.Range(0, 30).Select(i =>
            new LootTrackerCombatEvent("combat-" + i, LootTrackerCombatKind.Kill, At.AddMilliseconds(i), TimeSpan.FromMilliseconds(i))).ToImmutableArray() };
        var groups = LootTrackerTimeline.Groups(LootTrackerTimeline.Events(session), session.Elapsed);
        Assert.Equal(30, groups.Sum(g => g.Events.Count));
        Assert.Single(groups);
        Assert.Equal(0, LootTrackerTimeline.Fraction(TimeSpan.Zero, TimeSpan.Zero));
        Assert.All(LootTrackerTimeline.Build(session with { Elapsed = TimeSpan.Zero }, LootTimelineValueMode.Cumulative), s =>
            Assert.All(s.Paths(TimeSpan.Zero), path => { Assert.DoesNotContain("NaN", path); Assert.DoesNotContain("Infinity", path); }));
    }
}
