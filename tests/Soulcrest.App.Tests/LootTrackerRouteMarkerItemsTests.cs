using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerRouteMarkerItemsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Material = "Item:610530001", Unique = "Item:210230006";
    private static LootTrackerLoot Loot(string id, long quantity, string? name = null) => new(id, name ?? id, LootTrackerLootKind.Item, quantity);
    private static LootTrackerGatherVisit Visit(string id, int second, string? node = "node", string map = "map") =>
        new(id, 0, map, map, 90, 190, 100, 200, At.AddSeconds(second), true, "Resource", CatalogNodeId: node);
    private static LootTrackerTimelineEvent Harvest(string id, LootTrackerGatherVisit? visit, int second, params LootTrackerLoot[] items) =>
        new(id, LootTrackerTimelineEventKind.HarvestSuccess, At.AddSeconds(second), TimeSpan.FromSeconds(second))
            { GatherVisit = visit, Loot = items.ToImmutableArray() };
    private static LootTrackerTimelineEvent Drop(string id, int second, LootTrackerCombatPosition? position, params LootTrackerLoot[] items) =>
        new(id, LootTrackerTimelineEventKind.Drop, At.AddSeconds(second), TimeSpan.FromSeconds(second))
            { Position = position, Loot = items.ToImmutableArray() };
    private static LootTrackerSession Session => new()
    {
        Id = "sample-marker-items", Kind = LootTrackerSessionKind.Lifeskill, TimelineVersion = 1,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddSeconds(30), Elapsed = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public void ExactCatalogueRevisitsAggregateEveryRewardWithItsOriginalIconAndRarity()
    {
        var one = Visit("one", 2); var two = Visit("two", 10);
        var session = Session with
        {
            GatherVisits = [one, two],
            TimelineEvents = [Harvest("h1", one, 2, Loot(Material, 2, "Material"), Loot(Unique, 1, "Unique item")),
                Harvest("h2", two, 10, Loot("item:610530001", 3, "Material"), Loot(Material, 4, "Material"))]
        };
        var marker = Assert.Single(LootTrackerSessionMarkers.All(session));
        Assert.Equal(LootTrackerSessionMarkerKind.Gathering, marker.Kind);
        Assert.Equal(new[] { At.AddSeconds(2), At.AddSeconds(10) }, marker.Times);
        Assert.Equal(new[] { Unique, Material }, marker.Items.Select(item => item.Id));
        var material = marker.Items.Single(item => item.Id == Material);
        Assert.Equal(9, material.Quantity); Assert.Equal("Material", material.Name);
        Assert.Equal(MonsterLootIconCatalog.Default.DropAsset(Material), material.Asset); Assert.NotNull(material.Asset);
        var unique = marker.Items.Single(item => item.Id == Unique);
        Assert.Equal(LootItemRarity.Unique, unique.Rarity); Assert.Equal(1, unique.Quantity);
        Assert.Contains("Material ×9", marker.Tooltip); Assert.Contains("Unique item ×1", marker.Tooltip);
        Assert.Equal(2, session.GatherVisits.Length); Assert.Equal(2, session.TimelineEvents.Length);
    }

    [Fact]
    public void ReplayPrefixContainsOnlyReachedRewardsAndRevisits()
    {
        var one = Visit("one", 2); var two = Visit("two", 10);
        var session = Session with { GatherVisits = [one, two], TimelineEvents =
            [Harvest("h1", one, 2, Loot(Material, 2)), Harvest("h2", two, 10, Loot(Material, 7), Loot(Unique, 1))] };
        var early = Assert.Single(LootTrackerSessionMarkers.All(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5)).Session));
        Assert.Single(early.Times); Assert.Equal(2, Assert.Single(early.Items).Quantity);
        Assert.DoesNotContain(Unique, early.Tooltip);
        var end = Assert.Single(LootTrackerSessionMarkers.All(LootTrackerSessionReplay.Build(session, session.Elapsed).Session));
        Assert.Equal(2, end.Times.Length); Assert.Equal(9, end.Items.Single(item => item.Id == Material).Quantity);
        Assert.Contains(end.Items, item => item.Id == Unique);
    }

    [Fact]
    public void EmbeddedConfirmedHarvestVisitIsSufficientWithoutAnAggregateRouteMirror()
    {
        var visit = Visit("only-fact", 3);
        var marker = Assert.Single(LootTrackerSessionMarkers.All(Session with
            { TimelineEvents = [Harvest("h", visit, 3, Loot(Material, 4))] }));
        Assert.Equal((visit.X, visit.Y), (marker.X, marker.Y));
        Assert.Equal(4, Assert.Single(marker.Items).Quantity);
    }

    [Fact]
    public void LegacyLifeskillVisitsNeverBorrowTheWholeSessionLootAggregate()
    {
        var marker = Assert.Single(LootTrackerSessionMarkers.All(Session with
            { TimelineVersion = 0, GatherVisits = [Visit("legacy", 3)], Loot = [Loot(Unique, 999)] }));
        Assert.Equal(LootTrackerSessionMarkerKind.Gathering, marker.Kind); Assert.Empty(marker.Items);
        Assert.DoesNotContain("999", marker.Tooltip);
    }

    [Fact]
    public void EqualCoordinatesAndResourceNamesDoNotMergeUnresolvedVisitsOrDifferentMaps()
    {
        var visits = new[] { Visit("one", 1, null), Visit("two", 2, null), Visit("three", 3, "node", "other-map"), Visit("four", 4) };
        var markers = LootTrackerSessionMarkers.All(Session with { GatherVisits = visits.ToImmutableArray(),
            TimelineEvents = visits.Select((visit, index) => Harvest("h" + index, visit, index + 1, Loot(Material, index + 1))).ToImmutableArray() });
        Assert.Equal(4, markers.Length); Assert.All(markers, marker => Assert.Single(marker.Times));
        Assert.Equal(new long?[] { 1, 2, 3, 4 }, markers.Select(marker => Assert.Single(marker.Items).Quantity));
    }

    [Fact]
    public void HarvestWithoutPlacementAndDropWithoutPlacementCannotBorrowNearbyRouteOrCombatPositions()
    {
        var session = Session with { Kind = LootTrackerSessionKind.Grind,
            RouteSegments = [new("map", "Map", [new(100, 200, At.AddSeconds(3), TimeSpan.FromSeconds(3))])],
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddSeconds(3), TimeSpan.FromSeconds(3), new("map", "Map", 100, 200, At.AddSeconds(3)))],
            TimelineEvents = [Harvest("unplaced-h", null, 3, Loot(Material, 10)), Drop("unplaced-d", 3, null, Loot(Unique, 1))] };
        var markers = LootTrackerSessionMarkers.All(session);
        Assert.Single(markers); Assert.Equal(LootTrackerSessionMarkerKind.Kill, markers[0].Kind); Assert.Empty(markers[0].Items);
    }

    [Fact]
    public void PositionedPveDropsUseOnlyTheirOwnFactsAndGroupExactRecordedLocations()
    {
        var position = new LootTrackerCombatPosition("map", "Map", 100.123456789, 200.987654321, At.AddSeconds(2));
        var session = Session with { Kind = LootTrackerSessionKind.Grind, UnifiedVersion = 1,
            TimelineEvents = [Drop("d1", 2, position, Loot(Material, 2)),
                Drop("d2", 10, position with { ObservedAt = At.AddSeconds(10) }, Loot(Material, 3), Loot(Unique, 1)),
                Drop("unplaced", 10, null, Loot(Unique, 99))] };
        var early = Assert.Single(LootTrackerSessionMarkers.All(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5)).Session));
        Assert.Equal(LootTrackerSessionMarkerKind.Drop, early.Kind); Assert.Equal("drop", early.KindKey);
        Assert.Equal((position.X, position.Y), (early.X, early.Y)); Assert.Equal(2, Assert.Single(early.Items).Quantity);
        var end = Assert.Single(LootTrackerSessionMarkers.All(session));
        Assert.Equal(2, end.Times.Length); Assert.Equal(5, end.Items.Single(item => item.Id == Material).Quantity);
        Assert.Equal(1, end.Items.Single(item => item.Id == Unique).Quantity); Assert.DoesNotContain("99", end.Tooltip);
        Assert.Contains(LootTrackerSessionMarkers.Maps(session), map => map.MapId == "map");
    }

    [Fact]
    public void InvalidDropPositionsStayUnplacedAndCloseCoordinatesRemainSeparate()
    {
        var position = new LootTrackerCombatPosition("map", "Map", 100, 200, At.AddSeconds(2));
        var markers = LootTrackerSessionMarkers.All(Session with { TimelineEvents =
            [Drop("valid", 2, position, Loot(Material, 1)), Drop("close", 2, position with { X = 100.00001 }, Loot(Material, 1)),
                Drop("nan", 2, position with { X = double.NaN }, Loot(Material, 1)),
                Drop("negative", 2, position with { Y = -1 }, Loot(Material, 1)),
                Drop("missing-map", 2, position with { MapId = "" }, Loot(Material, 1))] });
        Assert.Equal(2, markers.Length); Assert.All(markers, marker => Assert.Equal(1, Assert.Single(marker.Items).Quantity));
    }

    [Fact]
    public void KillsExposeQualifiedOpponentClassAndRankButUnknownsAndDeathsStayNeutral()
    {
        var kill = new LootTrackerCombatEvent("kill", LootTrackerCombatKind.Kill, At, TimeSpan.Zero, new("map", "Map", 1, 2, At))
            { OpponentClassId = "Cleric", OpponentAbyssRankGrade = 19 };
        var markers = LootTrackerSessionMarkers.All(Session with { Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [kill, kill with { Id = "unknown", OpponentClassId = "Invented", OpponentAbyssRankGrade = 99 },
                kill with { Id = "death", Kind = LootTrackerCombatKind.Death }] });
        Assert.Equal("Cleric", markers[0].OpponentClassId); Assert.Equal(19, markers[0].OpponentAbyssRankGrade);
        Assert.Null(markers[1].OpponentClassId); Assert.Null(markers[1].OpponentAbyssRankGrade);
        Assert.Null(markers[2].OpponentClassId); Assert.Null(markers[2].OpponentAbyssRankGrade);
    }

    [Fact]
    public void DuplicateFactIdentifiersAreNotCountedTwiceAndUnknownItemsHaveNoGuessedVisual()
    {
        var visit = Visit("one", 2); var fact = Harvest("h", visit, 2, Loot("Item:999999999", 2, "Unknown example"));
        var marker = Assert.Single(LootTrackerSessionMarkers.All(Session with { GatherVisits = [visit], TimelineEvents = [fact, fact] }));
        var item = Assert.Single(marker.Items); Assert.Equal(2, item.Quantity); Assert.Null(item.Asset); Assert.Equal(LootItemRarity.Unknown, item.Rarity);
    }

    [Fact]
    public void UnrepresentableQuantityIsUnknownAndNeverWrappedClampedOrSilentlyDropped()
    {
        var visit = Visit("one", 2);
        var marker = Assert.Single(LootTrackerSessionMarkers.All(Session with
            { TimelineEvents = [Harvest("h", visit, 2, Loot(Material, long.MaxValue), Loot(Material, 1))] }));
        Assert.Null(Assert.Single(marker.Items).Quantity); Assert.Contains("—", marker.Tooltip);
    }
}
