using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class QuestMonsterTests
{
    private static QuestMonsterCatalog Catalog() => new([
        new("route", 2, "altgard", 100, 200, "2102610", 1, [
            new("altgard", 110, 220, "2400580", "Black Claw Guard Troopers Searcher", "Sucher", 6, "icons/monster.png"),
            new("altgard", 120, 230, "2400580", "Black Claw Guard Troopers Searcher", "Sucher", 6, "icons/monster.png"),
            new("altgard", 130, 240, "2400557", "Totem", null, 1, "icons/monster.png")])]);
    private static MapTarget Current() => new("current", "altgard", 100, 200, "Quest", "Waypoint", null,
        RouteId: "route", Leveling: true, StopIndex: 2);

    [Fact]
    public void ActiveStepShowsEveryMatchingSpawnInWhiteWithMonsterSymbolAndRequiredCount()
    {
        var result = Catalog().Resolve(Current(), "en");
        Assert.Equal(3, result.Count);
        Assert.All(result, t =>
        {
            Assert.True(t.QuestMonster); Assert.False(t.Leveling); Assert.Null(t.RouteId);
            Assert.Equal("#ffffff", t.Color); Assert.Equal("icons/monster.png", t.Icon);
            Assert.Null(t.After); Assert.Null(t.StopIndex); Assert.Null(t.ExplorationId);
        });
        Assert.Equal(2, result.Count(t => t.Name.Contains("Searcher · ×6", StringComparison.Ordinal)));
        Assert.Contains(result, t => t.Name == "Totem · ×1");
        Assert.Contains(Catalog().Resolve(Current(), "de"), t => t.Name == "Sucher · ×6");
    }

    [Fact]
    public void CompletionNavigationCancellationAndEditedStationsCannotShowStaleSpawns()
    {
        var catalog = Catalog();
        Assert.Empty(catalog.Resolve(null, "en"));
        Assert.Empty(catalog.Resolve(Current() with { Completed = true }, "en"));
        Assert.Empty(catalog.Resolve(Current() with { StopIndex = 3 }, "en"));
        Assert.Empty(catalog.Resolve(Current() with { X = 105 }, "en"));
        Assert.Empty(catalog.Resolve(Current() with { MapId = "verteron" }, "en"));
        Assert.Empty(catalog.Resolve(Current() with { RouteId = "other-character-route" }, "en"));
        Assert.Empty(catalog.Resolve(Current() with { Leveling = false }, "en"));
        Assert.Equal(3, catalog.Resolve(Current(), "en").Count); // returning to the step restores its hints
    }

    [Fact]
    public void KillStepBranchesFromAcceptanceWhileTheMainRouteBypassesItUntilTurnIn()
    {
        var catalog = new QuestMonsterCatalog([new("route", 6, "altgard", 300, 200, "quest", 1,
            [new("altgard", 310, 210, "monster", "Searcher", null, 6, "monster.png")], 5, 7)]);
        var accept = Current() with { Id = "accept", X = 100, Y = 100, StopIndex = 5, Completed = true };
        var kill = Current() with { Id = "kill", X = 300, Y = 200, StopIndex = 6, After = "accept" };
        var turnIn = Current() with { Id = "return", X = 105, Y = 100, StopIndex = 7, After = "kill" };
        MapTarget[] all = [accept, kill, turnIn];
        var main = catalog.MainRouteTargets(all, [accept, kill, turnIn with { After = null }]);
        Assert.Equal(2, main.Count);
        Assert.Equal("accept", main[1].After); Assert.Equal(100, main[1].BranchX);
        var hints = catalog.ResolveRoute(all, "en");
        var branch = Assert.Single(hints, t => t.MonsterBranch);
        Assert.Equal("accept", branch.After); Assert.Equal("#ffffff", branch.Color);
        Assert.Equal(100, branch.BranchX); Assert.Equal("monster.png", branch.Icon);
        all[1] = kill with { Completed = true };
        Assert.Equal(2, catalog.ResolveRoute(all, "en").Count); // reaching monster point 6 keeps hints
        all[1] = kill;
        var arrival = catalog.ArrivalTargets(all, new("altgard", 310, 210, DateTimeOffset.UtcNow));
        var killArrival = Assert.Single(arrival, t => t.Id == "kill");
        Assert.Equal(310, killArrival.X); Assert.Equal(210, killArrival.Y);
        Assert.Equal(300, kill.X); // display/arrival projections never rewrite stored progress
        all[1] = kill with { Completed = true };
        all[2] = turnIn with { Completed = true };
        Assert.Empty(catalog.ResolveRoute(all, "en")); // completing point 7 removes the branch
        all[0] = accept with { Completed = false }; all[2] = turnIn;
        Assert.Empty(catalog.ResolveRoute(all, "en")); // not accepted yet / rewound
    }

    [Fact]
    public void LiveOfflineCatalogIncludesTheExactClawGuardObjective()
    {
        var data = AppPaths.FindMapData();
        Assert.NotNull(data);
        var stations = JsonFile.Load<List<QuestMonsterCatalog.Station>>(Path.Combine(data, "leveling-objectives.json"));
        var station = Assert.Single(stations, s => s.QuestId == "2102610");
        Assert.Equal(11, station.AcceptIndex); Assert.Equal(12, station.StopIndex); Assert.Equal(13, station.TurnInIndex);
        var searchers = station.Spawns.Where(s => s.NpcId == "2400580").ToArray();
        Assert.Equal(16, searchers.Length);
        Assert.All(searchers, s => { Assert.Equal(6, s.Count); Assert.True(File.Exists(Path.Combine(data, s.Icon))); });
        Assert.All(station.Spawns, s => Assert.Equal("altgard", s.MapId));
        Assert.Contains(station.Spawns, s => s.NpcId == "2400582" && s.Count == 6);
        Assert.Contains(station.Spawns, s => s.NpcId == "2400557" && s.Count == 1);
    }
}
