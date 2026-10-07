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
}
