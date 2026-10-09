using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Resources and hidden cubes for the in-game map come from the same data.js as the interactive map.</summary>
public sealed class MapResourceMarkersTests
{
    [Fact]
    public void OnlyResourcesAndHiddenCubesWithTheirKindAndIcon()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-resources-" + Guid.NewGuid() + ".js");
        File.WriteAllText(path, """
            window.SoulcrestMaps = window.SoulcrestMaps || {};
            window.SoulcrestMaps["test"] = {"categories":[
              {"name":"Herb","group":"Resources","icon":0},
              {"name":"Hidden Cube","group":"Collectibles","icon":1},
              {"name":"Empyrean Trace","group":"Collectibles","icon":2},
              {"name":"Kibelisk","group":"Locations","icon":3},
              {"name":"Cogni","group":"Pets","icon":4},
              {"name":"Fera","group":"Monsters","icon":-1}],
             "icons":["icons/herb.png","icons/cube.png","icons/trace.png","icons/kibelisk.png","icons/pet.png","icons/azpha.png"],
             "markers":[[0,10,20,"Azpha",null,5,null],[0,12,22,"Calendula",null,-1,null],[1,30,40,"Cube",null,-1,null],[2,50,60,"Trace",null,-1,null],
                        [3,70,80,"Kibelisk",null,-1,null],[4,90,100,"Klaw",null,4,"klaw"],
                        [5,110,120,"Klaw Scout",null,-1,"klaw"],[5,130,140,"Wolf",null,-1,null]]};
            """);
        try
        {
            var resources = MapPetMarkers.LoadResources(path);
            Assert.Equal(
                [new MapResource("Resources/Herb", 10, 20, "icons/azpha.png", "Azpha"), new MapResource("Resources/Herb", 12, 22, "icons/herb.png", "Calendula"),
                 new MapResource("Collectibles/Hidden Cube", 30, 40, "icons/cube.png")],
                resources);
            Assert.Equal("Resources/Herb/Azpha", resources[0].KindKey); // legend: Herb → Azpha
            Assert.Null(resources[2].KindKey);
            Assert.Equal([new PetSpawn("klaw", 90, 100, "icons/pet.png")], MapPetMarkers.Load(path));
            Assert.Equal([new PetSpawn("klaw", 110, 120, null, new MonsterName("Klaw Scout", null))], MapPetMarkers.LoadSoulMonsters(path)); // soul monster of Klaw
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RightClickedMonsterUsesItsExactPositionAndLocalizedSourceName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-marked-monster-" + Guid.NewGuid());
        var mapId = "marked-monster-" + Guid.NewGuid().ToString("N");
        var map = Path.Combine(directory, mapId);
        Directory.CreateDirectory(map);
        File.WriteAllText(Path.Combine(map, "data.js"), $$"""
            window.SoulcrestMaps["{{mapId}}"] = {"categories":[{"name":"Varian","group":"Pets","icon":-1},{"name":"Varian","group":"Monsters","icon":-1}],"icons":[],
             "markers":[[0,10,20,"Pet A",null,-1,"pet-a",{"en":"Source A (28×)","de":"Quelle A (28×)"}],
                        [1,30,40,"Source B","Quelle B",-1,"pet-a"],
                        [1,50,60,"Source C",null,-1,"pet-a"],
                        [1,70,80,"Unrelated source",null,-1,null]]};
            """);
        try
        {
            Assert.Equal("Quelle A", MapPetMarkers.MonsterAt(directory, mapId, "pet-a", 10, 20)?.In("de"));
            Assert.Equal("Quelle B", MapPetMarkers.MonsterAt(directory, mapId, "pet-a", 30, 40)?.In("de"));
            Assert.Equal("Source B", MapPetMarkers.MonsterAt(directory, mapId, "pet-a", 30, 40)?.In("en"));
            Assert.Equal("Source C", MapPetMarkers.MonsterAt(directory, mapId, "pet-a", 50, 60)?.In("de"));
            Assert.Null(MapPetMarkers.MonsterAt(directory, mapId, "another-pet", 30, 40));
            Assert.Null(MapPetMarkers.MonsterAt(directory, mapId, "pet-a", 70, 80));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GuardsAreTheNpcsNamedGuard()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-guards-" + Guid.NewGuid() + ".js");
        File.WriteAllText(path, """
            window.SoulcrestMaps["test"] = {"categories":[{"name":"npc-other","group":"NPCs","icon":-1},{"name":"Cogni","group":"Monsters","icon":-1}],"icons":[],
             "markers":[[0,10,20,"Guard","Wachmann",-1,null],[0,30,40,"Guard Captain","Wachhauptmann",-1,null],[1,50,60,"Guard","Wachmann",-1,null]]};
            """);
        try
        {
            Assert.Equal([new GuardPost(10, 20)], MapPetMarkers.LoadGuards(path)); // not the captain, not a monster called Guard
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PetSymbolsNameTheMonsterThatDropsTheSoul()
    {
        // Pet "Drana Mutant Brute" comes from the monster "Drana Mutant" (user report 2026-10-07).
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-monster-" + Guid.NewGuid() + ".js");
        File.WriteAllText(path, """
            window.SoulcrestMaps["test"] = {"categories":[{"name":"Varian","group":"Pets","icon":0}],"icons":["icons/pet.png"],
             "markers":[[0,10,20,"Drana Mutant Brute","Drana-Mutantenbrecher",0,"drana-mutant-brute",{"en":"Drana Mutant (28×)","de":"Dranamutant (28×)"}],
                        [0,30,40,"Stone Spirit","Steingeist",0,"stone-spirit",{"en":"Abyss Rock Spirit","de":"Felsgeist (Abgrund)"}],
                        [0,50,60,"Klaw","Klaw",0,"klaw"]]};
            """);
        try
        {
            var spawns = MapPetMarkers.Load(path);
            Assert.Equal(new MonsterName("Drana Mutant", "Dranamutant", 28), spawns[0].Monster);
            Assert.Equal("Felsgeist (Abgrund)", spawns[1].Monster!.In("de")); // only the group size goes, not other brackets
            Assert.Equal("Abyss Rock Spirit", spawns[1].Monster!.In("en"));
            Assert.Null(spawns[2].Monster); // no source monster: the overlay keeps the pet name
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("altgard", "lesser-wind-spirit", "Soft Breeze Spirit")]
    [InlineData("altgard", "superior-wind-spirit", "Whirlwind Spirit")]
    [InlineData("altgard", "drana-mutant-brute", "Drana Mutant")]
    public void LootTrackerNamesTheMonsterToHunt(string map, string petId, string monster)
    {
        // User report 2026-10-07: the game names the monster, not the pet ("Soft Breeze Spirit", not "Lesser Wind Spirit").
        var mapdata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "imports", "generated", "mapdata"));
        if (!File.Exists(Path.Combine(mapdata, map, "data.js")))
            return; // map data package not built here
        Assert.Contains(monster, MapPetMarkers.MonstersOf(mapdata, map, petId).Select(m => m.En));
    }
}
