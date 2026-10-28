using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task AnOwnApGainStartsAnOrdinaryMixedSessionWithoutAbyssTrackingOrInventedLoot()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.AbyssTrackingEnabled = false;
        f.Source.Points(14, 1); f.Source.Points(14, 1); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, session.Kind); Assert.Equal(LootTrackerSessionMode.Normal, session.Mode);
        Assert.Equal(14, session.AbyssPoints); Assert.Equal(0, session.ItemCount); Assert.Equal(0, session.SoulCount);
        Assert.Null(session.KinaUnbound); Assert.Null(session.LastPresenceAt); Assert.Empty(session.Loot);
        Assert.Null(session.RankPointsGained); Assert.Null(session.RankPointsLost);
        var fact = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.AbyssPoints);
        Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin); Assert.Equal(14, fact.Amount);
        Assert.Equal(14, Assert.Single(session.MinuteIncome).AbyssPoints);
        Assert.Single(f.Starts); Assert.Empty(f.Accepted); Assert.Empty(session.CombatEvents);
        Assert.True(session.TimelineEvents.Last(row => row.Metric == LootTrackerTimelineMetric.AbyssPoints).Available);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(14, recovered.AbyssPoints); Assert.Equal(session.Id, recovered.Id);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(-585)]
    public async Task AnOwnSignedRankChangeStartsAMixedSessionAndCountsOnce(long delta)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.AbyssTrackingEnabled = false;
        f.Source.Rank(delta, 1); f.Source.Rank(delta, 1); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, session.Kind); Assert.Null(session.AbyssPoints);
        Assert.Equal(delta > 0 ? delta : 0, session.RankPointsGained);
        Assert.Equal(delta < 0 ? -delta : 0, session.RankPointsLost);
        Assert.Equal(delta, LootTrackerPresentation.NetRankPoints(session));
        var fact = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.RankPoints);
        Assert.Equal(delta, fact.Amount); Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin);
        Assert.Single(f.Starts); Assert.Empty(f.Accepted); Assert.Empty(session.CombatEvents);
    }

    [Fact]
    public async Task OpenWorldPointsHarvestAndDropsShareOneSessionClockAndIndependentEchoGuards()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(30)); f.Source.Points(14, 1); f.Source.Rank(20, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(30)); f.Source.Drop(7, 3, 1); f.Source.Rank(-50, 2); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(30)); f.Source.Points(7, 2); await f.Ready();
        f.Source.Points(7, 2); f.Source.Rank(-50, 2); f.Source.Drop(7, 3, 1);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(TimeSpan.FromSeconds(90), session.Elapsed);
        Assert.Equal(21, session.AbyssPoints); Assert.Equal(-30, LootTrackerPresentation.NetRankPoints(session));
        Assert.Equal(5, session.ItemCount); Assert.Equal(5, Assert.Single(session.Loot).Quantity);
        Assert.Equal(1, session.HarvestCount); Assert.Equal(2, f.Accepted.Count); Assert.Single(f.Starts);
        Assert.Equal(2, session.TimelineEvents.Count(row => row.Kind == LootTrackerTimelineEventKind.AbyssPoints));
        Assert.Equal(2, session.TimelineEvents.Count(row => row.Kind == LootTrackerTimelineEventKind.RankPoints));
        Assert.All(session.TimelineEvents.Where(row => row.Kind is LootTrackerTimelineEventKind.AbyssPoints
            or LootTrackerTimelineEventKind.RankPoints), row => Assert.Equal(LootTrackerActivityOrigin.Pve, row.Origin));
        f.Clock.Advance(TimeSpan.FromSeconds(119)); await f.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(f.Service.Snapshot.Sessions).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status); Assert.Equal(TimeSpan.FromSeconds(90), completed.Elapsed);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task OutsidePointTrackingRespectsItsOwnToggleAndConsumesDisabledEchoes(bool masterToggle, bool rank)
    {
        await using var f = new Fixture(); await f.Ready();
        if (masterToggle) f.Settings.Current.LootTrackingEnabled = false;
        else f.Settings.Current.MonsterLootTrackingEnabled = false;
        if (rank) f.Source.Rank(10, 1); else f.Source.Points(10, 1);
        await f.Ready(); Assert.Empty(f.Service.Snapshot.Sessions);
        f.Settings.Current.LootTrackingEnabled = f.Settings.Current.MonsterLootTrackingEnabled = true;
        f.Settings.Current.AbyssTrackingEnabled = false;
        if (rank) f.Source.Rank(10, 1); else f.Source.Points(10, 1);
        await f.Ready(); Assert.Empty(f.Service.Snapshot.Sessions);
        if (rank) f.Source.Rank(3, 2); else f.Source.Points(3, 2);
        await f.Ready(); var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(3, rank ? LootTrackerPresentation.NetRankPoints(current) : current.AbyssPoints);
        Assert.Single(f.Starts);
    }

    [Fact]
    public async Task OutsidePointAvailabilityChangesOnlyWithAnActualActivityAndBaselinesNeverExtendItsTimeout()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(15_000); f.Clock.Advance(TimeSpan.FromMinutes(3)); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Starts); Assert.Empty(f.Journal);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 1)); await f.Ready();
        var original = Assert.Single(f.Service.Snapshot.Sessions); var writes = f.Journal.Count;
        f.Clock.Advance(TimeSpan.FromSeconds(100)); f.Source.ClearAbsolute(); await f.Ready();
        Assert.Equal(writes, f.Journal.Count); Assert.Equal(original.LastLootAt, Assert.Single(f.Service.Snapshot.Sessions).LastLootAt);
        Assert.Equal(original.TimelineEvents.Length, Assert.Single(f.Service.Snapshot.Sessions).TimelineEvents.Length);
        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status); Assert.Equal(TimeSpan.Zero, completed.Elapsed);
        Assert.Null(completed.AbyssPoints); Assert.Null(LootTrackerPresentation.NetRankPoints(completed));
        Assert.DoesNotContain(completed.TimelineEvents, row => row.Kind is LootTrackerTimelineEventKind.PointsStageReached
            or LootTrackerTimelineEventKind.AbyssPoints or LootTrackerTimelineEventKind.RankPoints);
        Assert.All(completed.TimelineEvents, row => Assert.True(row.At <= completed.LastLootAt));
        f.Source.Drop(7, 1, 1); await f.Ready();
        var next = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.Null(next.AbyssPoints); Assert.Null(next.RankPointsGained); Assert.Null(next.RankPointsLost);
        Assert.DoesNotContain(next.TimelineEvents, row => row.Metric is LootTrackerTimelineMetric.AbyssPoints or LootTrackerTimelineMetric.RankPoints);
    }

    [Fact]
    public async Task APointGapAndFreshAbsoluteBaselineCannotRecoverUnobservedOutsideIncome()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Points(14, 1); f.Source.Rank(20, 1); await f.Ready();
        f.Source.ClearAbsolute(); await f.Ready();
        f.Source.Points(500, 2); f.Source.Rank(500, 2); await f.Ready();
        f.Source.Absolute(100_000); await f.Ready();
        var baseline = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(14, baseline.AbyssPoints); Assert.Equal(20, LootTrackerPresentation.NetRankPoints(baseline));
        f.Source.Points(3, 3); f.Source.Rank(-7, 3); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(17, current.AbyssPoints); Assert.Equal(13, LootTrackerPresentation.NetRankPoints(current));
        Assert.DoesNotContain(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.PointsStageReached);
    }

    [Fact]
    public async Task AnActivatedOutsidePointChannelUpdatesAvailabilityAtActivitiesWithoutActivatingTheOtherChannel()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Points(14, 1); await f.Ready();
        var writes = f.Journal.Count;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.ClearAbsolute(); await f.Ready();
        Assert.Equal(writes, f.Journal.Count);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        var unavailable = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(14, unavailable.AbyssPoints); Assert.Null(unavailable.RankPointsGained); Assert.Null(unavailable.RankPointsLost);
        Assert.False(unavailable.TimelineEvents.Last(row => row.Metric == LootTrackerTimelineMetric.AbyssPoints).Available);
        Assert.DoesNotContain(unavailable.TimelineEvents, row => row.Metric == LootTrackerTimelineMetric.RankPoints);
        writes = f.Journal.Count;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Absolute(100_000); await f.Ready();
        Assert.Equal(writes, f.Journal.Count);
        f.Source.Harvest(2, new NetworkGatheringItem(7, 1)); await f.Ready();
        var confirmed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(14, confirmed.AbyssPoints); Assert.Null(confirmed.RankPointsGained); Assert.Null(confirmed.RankPointsLost);
        Assert.True(confirmed.TimelineEvents.Last(row => row.Metric == LootTrackerTimelineMetric.AbyssPoints).Available);
        Assert.Single(confirmed.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.AbyssPoints);
        Assert.DoesNotContain(confirmed.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.PointsStageReached);
        Assert.All(confirmed.TimelineEvents, row => Assert.True(row.At <= confirmed.LastLootAt));
    }

    [Fact]
    public async Task DailyKinaLimitDoesNotSplitOutsidePointsButTheSevenUtcFarmResetDoes()
    {
        await using var f = new Fixture(start: new(2026, 10, 28, 6, 59, 0, TimeSpan.Zero)); await f.Ready();
        f.Source.Points(10, 1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Source.Daily(1_000_000); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(30)); f.Source.Rank(3, 1); await f.Ready();
        var capped = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, capped.Id); Assert.Equal(LootTrackerSessionMode.LimitedKina, capped.Mode);
        Assert.Equal(10, capped.AbyssPoints); Assert.Equal(3, LootTrackerPresentation.NetRankPoints(capped));
        Assert.Equal(1_000_000, f.Service.DailyMonsterKinaSnapshot.Total);
        f.Clock.Advance(TimeSpan.FromSeconds(31)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status); Assert.Equal(TimeSpan.FromSeconds(30), completed.Elapsed);
        f.Source.Points(4, 2); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(id, current.Id); Assert.Equal(new DateOnly(2026, 10, 28), current.FarmDay);
        Assert.Equal(LootTrackerSessionMode.Normal, current.Mode); Assert.Equal(4, current.AbyssPoints);
        Assert.False(f.Service.OverlayState.DailyConfirmed);
    }

    [Theory]
    [InlineData("source-stopped")] [InlineData("source-rejected")] [InlineData("source-unqualified")]
    [InlineData("source-foreign")] [InlineData("source-future")] [InlineData("fact-future")]
    [InlineData("world-stale")] [InlineData("world-future")] [InlineData("binding")]
    [InlineData("other-instance")] [InlineData("unbound")] [InlineData("gain-future")]
    [InlineData("gain-default")] [InlineData("gain-before-own")] [InlineData("gain-foreign")]
    [InlineData("zero-sequence")]
    public async Task OutsidePointGainsStillRequireTheCurrentQualifiedOwnWorldAndPointSource(string invalid)
    {
        await using var f = new Fixture(); await f.Ready();
        var absolute = f.Source.AbyssSnapshot; var own = f.Source.OwnCharacter!;
        var at = f.Clock.GetUtcNow(); var sequence = 1L;
        switch (invalid)
        {
            case "source-stopped": f.Source.Running = false; break;
            case "source-rejected": absolute = absolute with { FormatRejected = true }; break;
            case "source-unqualified": absolute = absolute with { AbyssPointGainsQualified = false, RankPointChangesQualified = false }; break;
            case "source-foreign": absolute = absolute with { Character = own with { } }; break;
            case "source-future": absolute = absolute with { At = at.AddSeconds(1) }; break;
            case "fact-future": absolute = absolute with { AbyssPointBalanceObservedAt = at.AddSeconds(1), RankPointsObservedAt = at.AddSeconds(1) }; break;
            case "world-stale": f.WorldStatus = WorldContextStatus.Stale; break;
            case "world-future": f.WorldOffset = TimeSpan.FromSeconds(1); break;
            case "binding": f.BindingOffset = 1; break;
            case "other-instance": f.WorldKind = WorldContextKind.Instance; f.Category = "Party"; break;
            case "unbound": f.Bound = false; break;
            case "gain-future": at = at.AddSeconds(1); break;
            case "gain-default": at = default; break;
            case "gain-before-own": at = at.AddTicks(-1); break;
            case "gain-foreign": own = own with { }; break;
            case "zero-sequence": sequence = 0; break;
        }
        f.Source.SetAbsolute(absolute, notify: false);
        f.Source.Points(10, sequence, at, own); f.Source.Rank(-10, sequence, at, own); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Journal); Assert.Empty(f.Starts);
    }

    [Fact]
    public async Task OpenWorldCombatNeverCreatesASessionOrAcquiresThePointIncomeOfAnOutcome()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Combat(NetworkAbyssCombatKind.Kill, 1); f.Source.Combat(NetworkAbyssCombatKind.Death, 2); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Points(10, 1); await f.Ready();
        f.Source.Combat(NetworkAbyssCombatKind.Kill, 3); f.Source.Combat(NetworkAbyssCombatKind.Death, 4); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(10, current.AbyssPoints); Assert.Empty(current.CombatEvents);
        Assert.DoesNotContain(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.RankPoints);
    }
}
