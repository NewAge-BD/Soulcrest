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
            Assert.Equal([new PetSpawn("klaw", 110, 120, null)], MapPetMarkers.LoadSoulMonsters(path)); // soul monster of Klaw
        }
        finally { File.Delete(path); }
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
            Assert.Equal(new MonsterName("Drana Mutant", "Dranamutant"), spawns[0].Monster);
            Assert.Equal("Felsgeist (Abgrund)", spawns[1].Monster!.In("de")); // only the group size goes, not other brackets
            Assert.Equal("Abyss Rock Spirit", spawns[1].Monster!.In("en"));
            Assert.Null(spawns[2].Monster); // no source monster: the overlay keeps the pet name
        }
        finally { File.Delete(path); }
    }
}
