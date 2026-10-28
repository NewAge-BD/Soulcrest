using Soulcrest.Core.Instances;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>User decision 2026-10-28: public builds classify worlds from numeric kinds without area names.</summary>
public sealed class PublicMapKindTests
{
    private static InstanceCatalog Table() => InstanceCatalog.FromJson(File.ReadAllText(
        Path.Combine(TestPrerequisites.RepositoryRoot, "data", "instances", "map-kinds.json")));

    [Fact]
    public void PublicTableClassifiesOpenWorldInstancesAndAbyssWithoutNames()
    {
        var table = Table();
        Assert.True(table.HasData);
        Assert.False(table.HasLocationNames);
        Assert.Equal(InstanceMapKind.OpenWorld, table.FindMap(1110)?.Kind);
        Assert.Equal("altgard", table.FindMap(1110)?.SoulcrestMapId);
        Assert.Equal(InstanceMapKind.Instance, table.FindMap(20)?.Kind);
        Assert.Equal("Abyss", table.FindMap(20)?.Category);
        Assert.All(table.Maps.Values, map => Assert.Equal(new InstanceNames(null, null), map.Names));
        Assert.Contains(table.Dungeons.Values, dungeon => dungeon.InstanceLayer == true);
    }

    [Fact]
    public void EmbeddedCatalogAlwaysClassifiesWorldsAndOnlyThePrivateCatalogNamesThem()
    {
        var embedded = InstanceCatalog.Default;
        Assert.True(embedded.HasData);
        Assert.Equal(InstanceMapKind.OpenWorld, embedded.FindMap(1110)?.Kind);
        var privateCatalog = File.Exists(Path.Combine(TestPrerequisites.RepositoryRoot, "data", "instances", "catalog.json"));
        Assert.Equal(privateCatalog, embedded.HasLocationNames);
    }

    [RequiresFact(TestRequirement.PrivateInstanceCatalog)]
    public void PublicTableAgreesWithEveryClassifiedPrivateDefinition()
    {
        var table = Table();
        var full = InstanceCatalog.Default;
        foreach (var map in full.Maps.Values)
        {
            var row = table.FindMap(map.Id);
            if (map.Kind == InstanceMapKind.Unknown) { Assert.Null(row); continue; }
            Assert.Equal((map.Kind, map.Category, map.SoulcrestMapId), (row!.Kind, row.Category, row.SoulcrestMapId));
        }
        foreach (var dungeon in table.Dungeons.Values)
        {
            var source = full.FindDungeon(dungeon.Id)!;
            Assert.Equal((source.Category, source.Mode, source.InstanceLayer, source.Active), (dungeon.Category, dungeon.Mode, dungeon.InstanceLayer, dungeon.Active));
            Assert.All(dungeon.MapIds, id => Assert.Contains(id, source.MapIds));
        }
    }
}
