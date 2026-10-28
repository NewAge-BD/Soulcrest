using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerSessionProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OriginIsFilteredBeforeTheSameItemIdIsAggregatedAndTheSharedClockNeverChanges()
    {
        var parent = Example();
        var mixed = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Mixed);
        var pve = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve);
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        Assert.Equal(9, Assert.Single(mixed.Loot).Quantity);
        Assert.Equal(2, Assert.Single(pve.Loot).Quantity);
        Assert.Equal(7, Assert.Single(life.Loot).Quantity);
        Assert.All(new[] { mixed, pve, life }, row =>
        {
            Assert.Equal(parent.Id, row.Id); Assert.Equal(parent.Kind, row.Kind);
            Assert.Equal(parent.StartedAt, row.StartedAt); Assert.Equal(parent.Elapsed, row.Elapsed);
            Assert.Equal(parent.RouteSegments.ToArray(), row.RouteSegments.ToArray());
        });
        Assert.Equal(2, life.HarvestCount); Assert.Equal(1, life.FailedHarvestCount);
        Assert.Equal(2, LootTrackerPresentation.PerHour(life.HarvestCount, life.Elapsed));
        Assert.Null(life.KinaUnbound); Assert.Equal(1000, pve.KinaUnbound);
        Assert.Equal(9, Assert.Single(parent.Loot).Quantity);
        Assert.Equal("Item:610530005", Assert.Single(mixed.Loot).Id);
    }

    [Fact]
    public void AbyssContextIncludesItsLifeskillFactsButNeverBecomesOutsidePve()
    {
        var parent = Example(abyss: true);
        var abyss = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Abyss);
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        var pve = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve);
        Assert.Equal(9, abyss.ItemCount); Assert.Equal(20, abyss.AbyssPoints);
        Assert.Equal(10, abyss.RankPointsGained); Assert.Equal(3, abyss.RankPointsLost);
        Assert.Single(abyss.CombatEvents); Assert.Equal(2, abyss.GatherVisits.Length);
        Assert.True(LootTrackerSessionProjection.ShowsHarvest(abyss));
        Assert.Equal(7, life.ItemCount); Assert.Equal(2, life.HarvestCount);
        Assert.Empty(life.CombatEvents); Assert.Null(life.AbyssPoints); Assert.Null(life.RankPointsGained);
        Assert.Empty(pve.Loot); Assert.Empty(pve.CombatEvents); Assert.Empty(pve.GatherVisits);
        Assert.Null(pve.KinaUnbound); Assert.Null(pve.AbyssPoints);
        Assert.All(abyss.RouteSegments, segment => Assert.Contains(segment, pve.RouteSegments));
    }

    [Theory]
    [InlineData(LootTrackerViewMode.Mixed)]
    [InlineData(LootTrackerViewMode.Pve)]
    public void OpenWorldPointIncomeUsesTheSelectedFactsAndTheEntireSharedSessionClock(LootTrackerViewMode mode)
    {
        var parent = OpenWorldPointsExample();
        var projected = LootTrackerSessionProjection.Project(parent, mode);
        Assert.True(LootTrackerSessionProjection.ShowsPoints(projected));
        Assert.False(LootTrackerSessionProjection.ShowsAbyss(projected));
        Assert.Equal(20, projected.AbyssPoints);
        Assert.Equal(10, projected.RankPointsGained); Assert.Equal(3, projected.RankPointsLost);
        Assert.Equal(7, LootTrackerPresentation.NetRankPoints(projected));
        Assert.Equal(20, LootTrackerPresentation.PerHour(projected.AbyssPoints, projected.Elapsed));
        Assert.Equal(parent.Elapsed, projected.Elapsed); Assert.Equal(parent.Id, projected.Id);
        Assert.Equal(parent.RouteSegments.ToArray(), projected.RouteSegments.ToArray());
        Assert.All(projected.TimelineEvents.Where(row => row.Kind is LootTrackerTimelineEventKind.AbyssPoints or LootTrackerTimelineEventKind.RankPoints),
            row => Assert.Equal(LootTrackerActivityOrigin.Pve, row.Origin));
        Assert.Equal(mode == LootTrackerViewMode.Mixed ? 9 : 2, Assert.Single(projected.Loot).Quantity);
        foreach (var excluded in new[] { LootTrackerViewMode.Lifeskill, LootTrackerViewMode.Abyss })
        {
            var other = LootTrackerSessionProjection.Project(parent, excluded);
            Assert.False(LootTrackerSessionProjection.ShowsPoints(other));
            Assert.Null(other.AbyssPoints); Assert.Null(other.RankPointsGained); Assert.Null(other.RankPointsLost);
            Assert.DoesNotContain(other.TimelineEvents, row => row.Kind is LootTrackerTimelineEventKind.AbyssPoints or LootTrackerTimelineEventKind.RankPoints);
            Assert.Equal(parent.Elapsed, other.Elapsed);
        }
    }

    [Fact]
    public void OpenWorldPointPresentationPreservesUnknownChannelsAndDistinguishesConfirmedZeroFromNoPoints()
    {
        var parent = Example() with
        {
            RankPointsGained = 10,
            TimelineEvents = Example().TimelineEvents.Add(new("rank", LootTrackerTimelineEventKind.RankPoints,
                At.AddMinutes(42), TimeSpan.FromMinutes(42)) { Origin = LootTrackerActivityOrigin.Pve, Amount = 10 })
        };
        var projected = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve);
        Assert.True(LootTrackerSessionProjection.ShowsPoints(projected));
        Assert.Null(projected.AbyssPoints); Assert.Null(projected.RankPointsLost);
        Assert.Null(LootTrackerPresentation.NetRankPoints(projected)); Assert.Equal(10, projected.RankPointsGained);
        var zero = LootTrackerSessionProjection.Project(Example() with { AbyssPoints = 0 }, LootTrackerViewMode.Mixed);
        Assert.True(LootTrackerSessionProjection.ShowsPoints(zero)); Assert.Equal(0, zero.AbyssPoints);
        Assert.False(LootTrackerSessionProjection.ShowsPoints(LootTrackerSessionProjection.Project(Example(), LootTrackerViewMode.Mixed)));
        Assert.False(LootTrackerSessionProjection.ShowsPoints(LootTrackerSessionProjection.Project(Example(), LootTrackerViewMode.Lifeskill)));
    }

    [Fact]
    public void AvailabilityAndUnknownLegacyFailureCountsDoNotBecomeZeros()
    {
        var parent = Example() with { FailedHarvestCount = null, KinaBound = null };
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        Assert.Null(life.FailedHarvestCount);
        Assert.Null(LootTrackerTimeline.Build(life, LootTimelineValueMode.Cumulative).Single(row => row.Key == "failure").ValueAt(parent.Elapsed));
        Assert.Null(LootTrackerPresentation.PerHour(life.HarvestCount, TimeSpan.Zero));
        var pve = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve);
        Assert.Null(pve.KinaBound);
    }

    [Fact]
    public void LegacyAbyssItemTotalsAreNeverReclassifiedAsHarvestsByAnOdyleItemId()
    {
        var parent = Example(true) with
        {
            UnifiedVersion = 0, TimelineVersion = 0, TimelineEvents = [],
            Loot = [new("Item:610610013", "Synthetic material", LootTrackerLootKind.Item, 9)],
            HarvestCount = null, FailedHarvestCount = null
        };
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        Assert.True(life.ProjectionLimited); Assert.Empty(life.Loot);
        Assert.Null(life.ItemCount); Assert.Null(life.HarvestCount); Assert.Null(life.FailedHarvestCount);
        Assert.Equal(9, Assert.Single(LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Mixed).Loot).Quantity);
        Assert.Equal(9, Assert.Single(parent.Loot).Quantity);
    }

    [Theory]
    [InlineData(LootTrackerSessionKind.Grind, LootTrackerViewMode.Pve)]
    [InlineData(LootTrackerSessionKind.Lifeskill, LootTrackerViewMode.Lifeskill)]
    public void LegacyPureSessionsKeepTheirActualAggregateWithoutConstructingANewSession(LootTrackerSessionKind kind, LootTrackerViewMode view)
    {
        var parent = Example() with { Kind = kind, UnifiedVersion = 0, TimelineVersion = 0, TimelineEvents = [], FailedHarvestCount = null };
        var projected = LootTrackerSessionProjection.Project(parent, view);
        Assert.Equal(parent.Id, projected.Id); Assert.Equal(parent.Loot.ToArray(), projected.Loot.ToArray());
        Assert.Null(projected.FailedHarvestCount); Assert.Empty(projected.TimelineEvents);
    }

    [Fact]
    public void ResourceExportUsesEveryFrozenNodeAndNeverTheWalkedPathOrCombatMarkers()
    {
        var parent = Example(true);
        var resource = LootTrackerSessionProjection.ResourceRoute(parent);
        Assert.Equal(LootTrackerSessionKind.Lifeskill, resource.Kind);
        Assert.Empty(resource.RouteSegments); Assert.Empty(resource.CombatEvents);
        var part = Assert.Single(LootTrackerRouteSaveService.Preview(resource));
        Assert.Equal(new[] { 200d, 400d }, part.Stops.Select(row => row.X));
        Assert.Equal(2, part.Stops.Count); Assert.DoesNotContain(part.Stops, row => row.X == 1000);
        var replay = LootTrackerSessionReplay.Build(LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill), TimeSpan.FromMinutes(25));
        Assert.Single(replay.Session.GatherVisits);
        Assert.Equal(2, Assert.Single(LootTrackerRouteSaveService.Preview(LootTrackerSessionProjection.ResourceRoute(parent))).Stops.Count);
    }

    [Fact]
    public void FilteringKeepsPlayheadAndFullSessionStatisticsSeparate()
    {
        var parent = Example(); var playback = new LootTrackerReplayPlayback();
        playback.UpdateSession(parent.Id, parent.Elapsed, false); playback.Seek(TimeSpan.FromMinutes(25));
        foreach (var mode in Enum.GetValues<LootTrackerViewMode>())
        {
            var projected = LootTrackerSessionProjection.Project(parent, mode);
            playback.UpdateSession(projected.Id, projected.Elapsed, false);
            Assert.Equal(TimeSpan.FromMinutes(25), playback.Elapsed);
            var replay = LootTrackerSessionReplay.Build(projected, playback.Elapsed);
            Assert.Equal(parent.Elapsed, replay.Session.Elapsed);
        }
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        Assert.Equal(2, LootTrackerStatistics.Resources(life).Sum(row => row.Successes));
        Assert.Equal(2, life.HarvestCount);
        Assert.Equal(1, LootTrackerTimeline.Build(life, LootTimelineValueMode.Cumulative).Single(row => row.Key == "success").ValueAt(playback.Elapsed));
    }

    [Fact]
    public void UnknownOriginsCannotPopulateASelectedSourceAndOverflowCannotInventAZero()
    {
        var parent = Example() with { TimelineEvents = [Reward("unknown", LootTrackerTimelineEventKind.Drop, 1, 8, null)] };
        var pve = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve);
        Assert.Empty(pve.Loot); Assert.True(pve.ProjectionLimited);
        var overflow = LootTrackerSessionProjection.Project(parent with { TimelineEvents =
            [Reward("a", LootTrackerTimelineEventKind.Drop, 1, long.MaxValue, LootTrackerActivityOrigin.Pve),
                Reward("b", LootTrackerTimelineEventKind.Drop, 2, 1, LootTrackerActivityOrigin.Pve)] }, LootTrackerViewMode.Pve);
        Assert.True(overflow.ProjectionLimited); Assert.Null(overflow.ItemCount);
    }

    [Fact]
    public void GatheringMarkersFollowSelectedFactsAndDoNotClassifyAllAbyssResourcesAsOdyle()
    {
        var parent = Example(true);
        var mixed = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Mixed);
        Assert.Equal(2, LootTrackerSessionMarkers.All(mixed).Count(row => row.Kind == LootTrackerSessionMarkerKind.Gathering));
        Assert.DoesNotContain(LootTrackerSessionMarkers.All(mixed), row => row.Kind == LootTrackerSessionMarkerKind.Odyle);
        var life = LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Lifeskill);
        Assert.All(LootTrackerSessionMarkers.All(life), row => Assert.Equal(LootTrackerSessionMarkerKind.Gathering, row.Kind));
        Assert.Empty(LootTrackerSessionMarkers.All(LootTrackerSessionProjection.Project(parent, LootTrackerViewMode.Pve)));
        var facts = parent.TimelineEvents.Select(row => row.Id == "harvest-a" ? row with
        { Loot = [new("Item:610610013", "Synthetic resource reward", LootTrackerLootKind.Item, 3)] } : row).ToImmutableArray();
        var odyle = LootTrackerSessionMarkers.All(LootTrackerSessionProjection.Project(parent with { TimelineEvents = facts }, LootTrackerViewMode.Abyss));
        Assert.Single(odyle, row => row.Kind == LootTrackerSessionMarkerKind.Odyle);
        Assert.Single(odyle, row => row.Kind == LootTrackerSessionMarkerKind.Gathering);
    }

    [Fact]
    public void NewEnumValuesAppendWithoutChangingOldKindsAndProjectionMetadataIsNeverStored()
    {
        Assert.Equal(0, (int)LootTrackerSessionKind.Grind); Assert.Equal(3, (int)LootTrackerSessionKind.Abyss);
        Assert.Equal(4, (int)LootTrackerSessionKind.Mixed);
        var projected = LootTrackerSessionProjection.Project(Example(), LootTrackerViewMode.Lifeskill);
        var json = JsonSerializer.Serialize(projected);
        Assert.DoesNotContain("ProjectionViewMode", json); Assert.DoesNotContain("ProjectionLimited", json);
        Assert.Contains("\"UnifiedVersion\":1", json); Assert.Contains("\"Origin\":1", json);
    }

    internal static LootTrackerSession OpenWorldPointsExample()
    {
        var residence = Example(abyss: true);
        return residence with
        {
            Kind = LootTrackerSessionKind.Mixed, CombatEvents = [], LastPresenceAt = null,
            TimelineEvents = residence.TimelineEvents.Select(row => row.Origin == LootTrackerActivityOrigin.Abyss
                ? row with { Origin = LootTrackerActivityOrigin.Pve } : row).ToImmutableArray()
        };
    }

    internal static LootTrackerSession Example(bool abyss = false)
    {
        LootTrackerGatherVisit Visit(string id, double x, int minute) => new(id, 0, "example-map", "Synthetic area", x, 200, x, 200,
            At.AddMinutes(minute), true, "Synthetic resource", 8192, 7001, "node-" + id);
        var first = Visit("visit-a", 200, 20); var second = Visit("visit-b", 400, 30);
        var origin = abyss ? LootTrackerActivityOrigin.Abyss : LootTrackerActivityOrigin.Pve;
        var facts = new List<LootTrackerTimelineEvent>
        {
            Reward("drop", LootTrackerTimelineEventKind.Drop, 10, 2, origin),
            Reward("harvest-a", LootTrackerTimelineEventKind.HarvestSuccess, 20, 3, LootTrackerActivityOrigin.Lifeskill) with { GatherVisit = first, GatherableId = 7001 },
            Reward("harvest-b", LootTrackerTimelineEventKind.HarvestSuccess, 30, 4, LootTrackerActivityOrigin.Lifeskill) with { GatherVisit = second, GatherableId = 7001 },
            new("failed", LootTrackerTimelineEventKind.HarvestFailure, At.AddMinutes(31), TimeSpan.FromMinutes(31)) { Origin = LootTrackerActivityOrigin.Lifeskill, GatherableId = 7001 },
            new("kina", LootTrackerTimelineEventKind.KinaUnbound, At.AddMinutes(40), TimeSpan.FromMinutes(40)) { Origin = origin, Amount = 1000 }
        };
        if (abyss)
        {
            facts.Add(new("ap", LootTrackerTimelineEventKind.AbyssPoints, At.AddMinutes(41), TimeSpan.FromMinutes(41)) { Origin = origin, Amount = 20 });
            facts.Add(new("rp-up", LootTrackerTimelineEventKind.RankPoints, At.AddMinutes(42), TimeSpan.FromMinutes(42)) { Origin = origin, Amount = 10 });
            facts.Add(new("rp-down", LootTrackerTimelineEventKind.RankPoints, At.AddMinutes(43), TimeSpan.FromMinutes(43)) { Origin = origin, Amount = -3 });
        }
        return new()
        {
            Id = "11111111111111111111111111111111", Kind = abyss ? LootTrackerSessionKind.Abyss : LootTrackerSessionKind.Mixed,
            UnifiedVersion = 1, TimelineVersion = 1, StartedAt = At, EndedAt = At.AddHours(1), LastLootAt = At.AddMinutes(43),
            Status = LootTrackerSessionStatus.Completed, Elapsed = TimeSpan.FromHours(1), Location = "Synthetic area",
            Character = new("22222222222222222222222222222222", "Example character"),
            KinaUnbound = 1000, ItemCount = 9, SoulCount = 0, HarvestCount = 2, FailedHarvestCount = 1,
            AbyssPoints = abyss ? 20 : null, RankPointsGained = abyss ? 10 : null, RankPointsLost = abyss ? 3 : null,
            Loot = [new("Item:610530005", "Synthetic material", LootTrackerLootKind.Item, 9)],
            TimelineEvents = facts.ToImmutableArray(), GatherVisits = [first, second],
            FarmRoutePoints = LifeskillFarmRouteBuilder.Build([first, second]),
            RouteSegments = [new("example-map", "Synthetic area", [new(1000, 1000, At, TimeSpan.Zero), new(1100, 1100, At.AddMinutes(50), TimeSpan.FromMinutes(50))])],
            CombatEvents = abyss ? [new("combat", LootTrackerCombatKind.Kill, At.AddMinutes(45), TimeSpan.FromMinutes(45),
                new("example-map", "Synthetic area", 1000, 1000, At.AddMinutes(45))) { Origin = LootTrackerActivityOrigin.Abyss }] : []
        };
    }

    private static LootTrackerTimelineEvent Reward(string id, LootTrackerTimelineEventKind kind, int minute, long quantity, LootTrackerActivityOrigin? origin) =>
        new(id, kind, At.AddMinutes(minute), TimeSpan.FromMinutes(minute))
        { Origin = origin, Loot = [new(kind == LootTrackerTimelineEventKind.Drop ? "Item:610530005" : "item:610530005", "Synthetic material", LootTrackerLootKind.Item, quantity)] };
}
