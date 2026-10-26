using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerMapFocusTests
{
    [Fact]
    public void RouteFocusIsOneShotAndDoesNotConsumeOtherNavigationRequests()
    {
        var ui = new UiState();
        var boss = new MapTarget("boss", "altgard", 10, 20, "Synthetic boss", "Boss", null);
        var place = new ExplorationPlace("place", "altgard", "dungeon", 2, "Synthetic place", null, 20, 30);
        ui.ShowBossOnMap(boss);
        ui.ShowExplorationOnMap(place);
        ui.ShowPetOnMap("synthetic-pet");
        var changes = 0;
        ui.Changed += () => changes++;

        Assert.True(ui.ShowLootRouteOnMap(Session));
        Assert.Equal("map", ui.Tab);
        Assert.Equal(1, changes);
        var focus = Assert.IsType<LootTrackerMapFocus>(ui.TakePendingLootRouteFocus());
        Assert.Equal("session", focus.SessionId);
        Assert.Equal("altgard", focus.MapId);
        Assert.Null(ui.TakePendingLootRouteFocus());
        Assert.Equal(boss, ui.TakePendingBossFocus());
        Assert.Equal(place, ui.TakePendingExplorationFocus());
        Assert.Equal("synthetic-pet", ui.TakePendingPetFocus());
    }

    [Fact]
    public void InvalidOrUnobservedRouteDoesNotNavigateOrReplaceAPendingInspection()
    {
        var ui = new UiState();
        Assert.True(ui.ShowLootRouteOnMap(Session));
        ui.Navigate("loot");
        Assert.False(ui.ShowLootRouteOnMap(new()));
        Assert.False(ui.ShowLootRouteOnMap(Session, "unknown-map"));
        Assert.Equal("loot", ui.Tab);
        Assert.Equal("session", ui.TakePendingLootRouteFocus()?.SessionId);
    }

    [Fact]
    public void MapSelectionAndInvalidPositionsPreserveEverySegmentBoundary()
    {
        var session = Session with
        {
            RouteSegments =
            [
                new("altgard", "Altgard", [new(10, 20), new(double.NaN, 20), new(30, 40)]),
                new("verteron", "Verteron", [new(50, 60), new(70, 80)]),
                new("altgard", "Altgard", [new(100, 200), new(300, 400)]),
            ],
        };
        var focus = Assert.IsType<LootTrackerMapFocus>(LootTrackerMapFocus.Create(session));
        Assert.Equal(3, focus.Segments.Length);
        Assert.Equal(new[] { 1, 1, 2 }, focus.Segments.Select(segment => segment.Points.Length));
        Assert.All(focus.Segments, segment => Assert.Equal("altgard", segment.MapId));
        Assert.Equal(30, focus.Segments[1].Points[0].X);
        var other = Assert.IsType<LootTrackerMapFocus>(LootTrackerMapFocus.Create(session, "verteron"));
        Assert.Equal("verteron", other.MapId);
        Assert.Equal(50, Assert.Single(other.Segments).Points[0].X);
    }

    [Fact]
    public void LifeskillInspectionUsesItsSavedFarmStopsInsteadOfTheLegacyWalk()
    {
        var session = Session with
        {
            Kind = LootTrackerSessionKind.Lifeskill, FarmRouteVersion = 1,
            FarmRoutePoints =
            [
                new("stop-1", 0, "verteron", "Verteron", 10, 10, "Sapphire", 1, 0, 1),
                new("stop-2", 0, "verteron", "Verteron", 20, 20, "Sapphire", 1, 1, 0),
                new("stop-3", 1, "verteron", "Verteron", 30, 30, "Sapphire", 1, 1, 0),
            ],
        };
        var focus = Assert.IsType<LootTrackerMapFocus>(LootTrackerMapFocus.Create(session));
        Assert.Equal("verteron", focus.MapId);
        Assert.Equal(new[] { 2, 1 }, focus.Segments.Select(segment => segment.Points.Length));
        Assert.Equal(new[] { 10d, 20d, 30d }, focus.Segments.SelectMany(segment => segment.Points).Select(point => point.X));
    }

    private static LootTrackerSession Session => new()
    {
        Id = "session", Kind = LootTrackerSessionKind.Grind, Location = "Altgard",
        RouteSegments = [new("altgard", "Altgard", [new(100, 200), new(300, 400)])],
    };
}
