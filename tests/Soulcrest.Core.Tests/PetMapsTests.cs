using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetMapsTests
{
    private static PetDefinition Pet(Dictionary<string, int> spawns, Dictionary<string, int> monsters) =>
        new() { Id = "p", En = "P", Genus = "fera", Spawns = spawns, Monsters = monsters };

    [Fact]
    public void ExclusiveMeansSpawnsAndSourceMonstersOnlyOnThatMap()
    {
        Assert.True(Pet(new() { ["altgard"] = 3 }, []).IsExclusiveTo("altgard"));
        Assert.False(Pet(new() { ["altgard"] = 3 }, new() { ["verteron"] = 1 }).IsExclusiveTo("altgard"));
        Assert.False(Pet(new() { ["altgard"] = 3 }, []).IsExclusiveTo("verteron"));
        Assert.True(Pet(new() { ["altgard"] = 3, ["verteron"] = 0 }, []).IsExclusiveTo("altgard"));
        Assert.False(Pet([], []).IsExclusiveTo("altgard"));
    }
}
