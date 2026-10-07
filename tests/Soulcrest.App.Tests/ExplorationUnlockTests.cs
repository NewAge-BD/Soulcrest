using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Sealed dungeons that open only after others (user information 2026-10-07).</summary>
public sealed class ExplorationUnlockTests
{
    private static ExplorationPlace Dungeon(string map, string en) => new($"{map}|dungeon|{en}", map, "dungeon", 0, en, null);

    private static readonly ExplorationPlace[] Places =
    [
        Dungeon("verteron", "Distorted Cave Entrance"),
        Dungeon("verteron", "Fissure Cave Entrance"),
        Dungeon("verteron", "Rift Cave Entrance"),
        Dungeon("verteron", "Altar of Hope Entrance"),
        Dungeon("verteron", "Ruins of the Ancient City of Ru"),
        Dungeon("altgard", "Lost Ruin Entrance"),
        Dungeon("altgard", "Twisted Pit Entrance"),
        Dungeon("altgard", "Rift Fissure Entrance"),
        Dungeon("altgard", "Ruins of the Ancient City of Ru"),
        new("altgard|stronghold|Fort", "altgard", "stronghold", 0, "Fort", null),
    ];

    private static string[] Needs(string id) =>
        [.. ExplorationService.Prerequisites(Places.Single(p => p.Id == id), Places).Select(p => p.En).Order()];

    [Fact]
    public void ChainsOpenOneAfterTheOther()
    {
        Assert.Empty(Needs("verteron|dungeon|Distorted Cave Entrance"));
        Assert.Equal(["Distorted Cave Entrance"], Needs("verteron|dungeon|Fissure Cave Entrance"));
        Assert.Equal(["Distorted Cave Entrance", "Fissure Cave Entrance"], Needs("verteron|dungeon|Rift Cave Entrance"));
        Assert.Equal(["Lost Ruin Entrance", "Twisted Pit Entrance"], Needs("altgard|dungeon|Rift Fissure Entrance"));
        Assert.Empty(Needs("verteron|dungeon|Altar of Hope Entrance"));
        Assert.Empty(Needs("altgard|stronghold|Fort"));
    }

    [Fact]
    public void TheAncientCityNeedsEveryOtherSealedDungeonOfItsMap()
    {
        Assert.Equal(["Altar of Hope Entrance", "Distorted Cave Entrance", "Fissure Cave Entrance", "Rift Cave Entrance"],
            Needs("verteron|dungeon|Ruins of the Ancient City of Ru"));
        Assert.Equal(["Lost Ruin Entrance", "Rift Fissure Entrance", "Twisted Pit Entrance"],
            Needs("altgard|dungeon|Ruins of the Ancient City of Ru"));
    }

    [Fact]
    public void DoneDungeonsUnlockTheNext()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-unlock-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new ExplorationService(path, Places);
            var rift = Places.Single(p => p.En == "Rift Cave Entrance");
            Assert.Equal(2, service.MissingFor(rift, service.Completed).Count);
            service.SetDone(service.ActiveId, ["verteron|dungeon|Distorted Cave Entrance", "verteron|dungeon|Fissure Cave Entrance"]);
            Assert.Empty(service.MissingFor(rift, service.Completed));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheNamesExistInTheMapData()
    {
        // The chain names must match the map data, or the rules would silently do nothing.
        var mapdata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "imports", "generated", "mapdata"));
        if (!Directory.Exists(mapdata))
            return;
        foreach (var (map, names) in new[] { ("verteron", new[] { "Distorted Cave Entrance", "Fissure Cave Entrance", "Rift Cave Entrance" }),
                     ("altgard", new[] { "Lost Ruin Entrance", "Twisted Pit Entrance", "Rift Fissure Entrance" }) })
        {
            var data = File.ReadAllText(Path.Combine(mapdata, map, "data.js"));
            foreach (var name in names.Append("Ruins of the Ancient City of Ru"))
                Assert.Contains($"\"{name}\"", data);
        }
    }
}
