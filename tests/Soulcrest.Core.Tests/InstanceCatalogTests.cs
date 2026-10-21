using Soulcrest.Core.Instances;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class InstanceCatalogTests
{
    [Fact]
    public void ReadsExplicitFactsWithoutInferringMissingModeNamesOrExclusiveDungeon()
    {
        var catalog = InstanceCatalog.FromJson("""
            {"schemaVersion":1,"extraEvidence":{"ignored":true},"maps":[
              {"id":100,"names":{"de":"Welt","en":null},"kind":"openWorld","category":"field","soulcrestMapId":"fixture-map","sourceRefs":["map-proof"]},
              {"id":200,"names":{"de":null,"en":"Shared chamber"},"kind":"instance","category":null,"sourceRefs":["map-proof"]}],
             "dungeons":[
              {"id":201,"mapIds":[200],"names":{"de":null,"en":"Test dungeon"},"category":"repeatable","categoryNames":{"de":"Testkategorie","en":"Test category"},"mode":null,"modeNames":{"de":null,"en":null},"instanceLayer":false,"active":true,"sourceRefs":["dungeon-proof"]},
              {"id":202,"mapIds":[200],"names":{"de":null,"en":null},"category":null,"mode":null,"instanceLayer":null,"active":null,"sourceRefs":["dungeon-proof"]}]}
            """);
        Assert.Equal(InstanceMapKind.OpenWorld, catalog.FindMap(100)?.Kind);
        Assert.Equal("fixture-map", catalog.FindMap(100)?.SoulcrestMapId);
        Assert.Null(catalog.FindMap(100)?.Names.En);
        Assert.Equal(new[] { 201, 202 }, catalog.DungeonsForMap(200).Select(x => x.Id));
        Assert.Equal("Test category", catalog.FindDungeon(201)?.CategoryNames?.En);
        Assert.Null(catalog.FindDungeon(201)?.ModeNames?.De);
        Assert.True(catalog.FindDungeon(201)?.Active);
        Assert.False(catalog.FindDungeon(201)?.InstanceLayer);
        Assert.Null(catalog.FindDungeon(202)?.Mode);
        Assert.Null(catalog.FindDungeon(202)?.Active);
        Assert.Null(catalog.FindMap(999));
        Assert.Empty(catalog.DungeonsForMap(999));
    }

    [Fact]
    public void EmbeddedRealCatalogIsAvailableAndUsesExplicitSupportedMapBridges()
    {
        if (PrivateInstanceCatalog.Missing) return; // needs the private instance catalog
        var catalog = InstanceCatalog.Default;
        Assert.True(catalog.HasData);
        Assert.Equal("altgard", catalog.FindMap(1110)?.SoulcrestMapId);
        Assert.Equal(InstanceMapKind.OpenWorld, catalog.FindMap(1110)?.Kind);
        Assert.Equal("Abyss", catalog.FindMap(20)?.Category);
        Assert.Equal(InstanceMapKind.Instance, catalog.FindMap(20)?.Kind);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"maps":[],"dungeons":[]}""")]
    [InlineData("""{"schemaVersion":1,"maps":null,"dungeons":[]}""")]
    [InlineData("""{"schemaVersion":1,"maps":[{"id":0,"kind":"instance","sourceRefs":[]}],"dungeons":[]}""")]
    [InlineData("""{"schemaVersion":1,"maps":[{"id":1,"kind":"guess","sourceRefs":[]}],"dungeons":[]}""")]
    [InlineData("""{"schemaVersion":1,"maps":[{"id":1,"kind":"unknown","sourceRefs":[]},{"id":1,"kind":"instance","sourceRefs":[]}],"dungeons":[]}""")]
    public void RejectsAmbiguousDefinitionsAndUnknownContract(string json) =>
        Assert.Throws<InvalidDataException>(() => InstanceCatalog.FromJson(json));

    [Fact]
    public void UnresolvedAndDuplicateMapReferencesAreNotUsableJoins()
    {
        foreach (var ids in new[] { "[2]", "[1,1]", "[-1]" })
            Assert.Throws<InvalidDataException>(() => InstanceCatalog.FromJson("""
                {"schemaVersion":1,"maps":[{"id":1,"kind":"unknown","sourceRefs":[]}],
                "dungeons":[{"id":10,"mapIds":MAPS,"sourceRefs":[]}]}
                """.Replace("MAPS", ids, StringComparison.Ordinal)));
    }

    [Fact]
    public void EmptyOrUnmappedDefinitionsDoNotInventClassification()
    {
        Assert.False(InstanceCatalog.Empty.HasData);
        var catalog = InstanceCatalog.FromJson("""
            {"schemaVersion":1,"maps":[{"id":1,"kind":"unknown","sourceRefs":[]}],
            "dungeons":[{"id":10,"mapIds":[],"sourceRefs":[]}]}
            """);
        Assert.Equal(InstanceMapKind.Unknown, catalog.FindMap(1)?.Kind);
        Assert.Null(catalog.FindMap(1)?.Category);
        Assert.Empty(catalog.DungeonsForMap(1));
    }
}
