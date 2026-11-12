using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetDungeonMapCatalogTests
{
    [Fact]
    public void EmbeddedCatalogueKeepsAllProvenMapsPetsImagesAndPlacementsIndependentOfWorldMaps()
    {
        var maps = PetDungeonMapCatalog.All;
        Assert.Equal(46, maps.Count);
        Assert.Equal(65, maps.SelectMany(map => map.Spawns).Select(spawn => spawn.PetNumber).Distinct().Count());
        Assert.Equal(15, maps.Select(map => map.Image).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3341, maps.Sum(map => map.Spawns.Count));
        Assert.Equal(maps.Count, maps.Select(map => map.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(PetAcquisitionCatalog.All.Keys.Order(),
            maps.SelectMany(map => map.Spawns).Select(spawn => spawn.PetNumber).Distinct().Order());
        foreach (var map in maps)
        {
            Assert.StartsWith("dungeon-maps/", map.Image, StringComparison.Ordinal);
            Assert.NotEmpty(map.SourceRefs);
            foreach (var spawn in map.Spawns)
            {
                Assert.InRange(spawn.X, 0, map.Width);
                Assert.InRange(spawn.Y, 0, map.Height);
                Assert.NotEmpty(spawn.SourceRefs);
                Assert.Contains(PetAcquisitionCatalog.Find(spawn.PetNumber)!.Sources,
                    source => source.MapIds.Contains(map.MapId)
                        && source.Npcs.Any(npc => npc.Id == spawn.NpcId && npc.MapIds.Contains(map.MapId)));
            }
        }
        Assert.Empty(PetDungeonMapCatalog.ForPet(-1));
        Assert.Equal(maps.Where(map => map.Spawns.Any(spawn => spawn.PetNumber == 1093)), PetDungeonMapCatalog.ForPet(1093));
    }

    [Fact]
    public void EmbeddedJsonIsStrictRuntimeProjectionAndAllNestedListsAreReadOnly()
    {
        using var stream = typeof(PetDungeonMapCatalog).Assembly.GetManifestResourceStream("Soulcrest.PetDungeonMaps.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(["schemaVersion", "maps"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(46, PetDungeonMapCatalog.FromJson(document.RootElement.GetRawText()).Count);
        var maps = PetDungeonMapCatalog.All;
        var map = maps[0];
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PetDungeonMap>>(maps).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PetDungeonSpawn>>(map.Spawns).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(map.SourceRefs).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(map.Spawns[0].SourceRefs).Clear());
    }

    [Fact]
    public void ExactImagePixelsAndNamesSurviveWithoutInventedTranslations()
    {
        var file = Fixture();
        var map = Assert.Single(PetDungeonMapCatalog.FromJson(file.ToJsonString()));
        Assert.Equal((200, 100), (map.Width, map.Height));
        Assert.Equal((42.25, 63.5), (map.Spawns[0].X, map.Spawns[0].Y));
        Assert.Equal("Beispieldungeon", map.DisplayName("de"));
        Assert.Equal("Example dungeon", map.DisplayName("en"));
        Assert.Equal("Beispielvariante", map.VariantDisplayName("de"));
        Map(file)["de"] = null;
        Map(file)["variantDe"] = null;
        map = Assert.Single(PetDungeonMapCatalog.FromJson(file.ToJsonString()));
        Assert.Equal("Example dungeon", map.DisplayName("de"));
        Assert.Equal("Example variant", map.VariantDisplayName("de"));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    public void MissingOrNullCoordinatesNeverBecomeZero(string coordinate)
    {
        var file = Fixture();
        Spawn(file).AsObject().Remove(coordinate);
        Invalid(file);
        file = Fixture();
        Spawn(file)[coordinate] = null;
        Invalid(file);
    }

    [Theory]
    [InlineData(-0.01, 10)]
    [InlineData(200.01, 10)]
    [InlineData(10, -0.01)]
    [InlineData(10, 100.01)]
    public void OutOfImageCoordinatesFailClosed(double x, double y)
    {
        var file = Fixture();
        Spawn(file)["x"] = x;
        Spawn(file)["y"] = y;
        Invalid(file);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void NonFiniteCoordinatesAreRejected(string value)
    {
        var json = Fixture().ToJsonString().Replace("\"x\":42.25", "\"x\":" + value, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => PetDungeonMapCatalog.FromJson(json));
    }

    [Fact]
    public void TheImageBoundaryIsARealPositionAndIsNotDiscardedAsMissing()
    {
        var file = Fixture();
        Spawn(file)["x"] = 0;
        Spawn(file)["y"] = 100;
        var spawn = Assert.Single(Assert.Single(PetDungeonMapCatalog.FromJson(file.ToJsonString())).Spawns);
        Assert.Equal((0d, 100d), (spawn.X, spawn.Y));
    }

    [Theory]
    [InlineData("petNumber", 1002)]
    [InlineData("petNumber", int.MaxValue)]
    [InlineData("npcId", 0)]
    [InlineData("npcId", 2300103)]
    [InlineData("npcId", 2310102)]
    [InlineData("mapId", 20)]
    [InlineData("mapId", 600002)]
    public void PetNpcAndDungeonMapMustHaveTheExactExistingSoulSourceBinding(string field, int value)
    {
        var file = Fixture();
        (field == "mapId" ? Map(file) : Spawn(file))[field] = value;
        Invalid(file);
    }

    [Fact]
    public void RepeatedMapIdsSpawnIdsAndDuplicateSourcePositionsAreRejected()
    {
        var file = Fixture();
        file["maps"]!.AsArray().Add(Map(file).DeepClone());
        Invalid(file);
        file = Fixture();
        Map(file)["spawns"]!.AsArray().Add(Spawn(file).DeepClone());
        Invalid(file);
        file = Fixture();
        var duplicate = Spawn(file).DeepClone();
        duplicate["id"] = "different-id-same-source-position";
        Map(file)["spawns"]!.AsArray().Add(duplicate);
        Invalid(file);
    }

    [Fact]
    public void MultipleProvenPositionsOfTheSameNpcArePreserved()
    {
        var file = Fixture();
        var second = Spawn(file).DeepClone();
        second["id"] = "second-proven-position";
        second["x"] = 150;
        Map(file)["spawns"]!.AsArray().Add(second);
        Assert.Equal(2, Assert.Single(PetDungeonMapCatalog.FromJson(file.ToJsonString())).Spawns.Count);
    }

    [Theory]
    [InlineData("../map.png")]
    [InlineData("dungeon-maps/../map.png")]
    [InlineData("dungeon-maps/a/b.png")]
    [InlineData("https://example.test/map.png")]
    [InlineData("C:/map.png")]
    [InlineData("dungeon-maps/map.jpg")]
    [InlineData("dungeon-maps/Map.png")]
    public void ImagesMustBeSafeLocalDungeonPackagePaths(string image)
    {
        var file = Fixture();
        Map(file)["image"] = image;
        Invalid(file);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ImageHashesMustBeCompleteLowercaseSha256(string hash)
    {
        var file = Fixture();
        Map(file)["imageSha256"] = hash;
        Invalid(file);
    }

    [Theory]
    [InlineData("imageSha256")]
    [InlineData("width")]
    [InlineData("height")]
    public void VariantsSharingAnImageMustAgreeOnHashAndDimensions(string changed)
    {
        var file = Fixture();
        var second = Map(file).DeepClone();
        second["id"] = "second-variant";
        second[changed] = changed == "imageSha256" ? JsonValue.Create(new string('b', 64)) : JsonValue.Create(80);
        file["maps"]!.AsArray().Add(second);
        Invalid(file);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("spawn")]
    public void EveryMapAndPositionRequiresUniquePublicHttpsReferences(string level)
    {
        foreach (var references in new JsonNode?[] { null, new JsonArray(), new JsonArray("file:///private/source"),
            new JsonArray("https://user:secret@example.test/source"), new JsonArray("https://example.test/source", "https://example.test/source") })
        {
            var file = Fixture();
            (level == "map" ? Map(file) : Spawn(file))["sourceRefs"] = references;
            Invalid(file);
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("map")]
    [InlineData("spawn")]
    public void PrivateOrUnknownMetadataCannotEnterTheRuntimeProjection(string level)
    {
        var file = Fixture();
        (level == "root" ? file : level == "map" ? Map(file) : Spawn(file))["privateSourcePath"] = "private evidence";
        Invalid(file);
    }

    [Theory]
    [InlineData("schemaVersion", 2)]
    [InlineData("width", 0)]
    [InlineData("height", 16385)]
    public void UnsupportedSchemaAndInvalidImageDimensionsFailClosed(string field, int value)
    {
        var file = Fixture();
        (field == "schemaVersion" ? file : Map(file))[field] = value;
        Invalid(file);
    }

    private static JsonNode Fixture() => JsonNode.Parse("""
        {"schemaVersion":1,"maps":[{"id":"example-map","mapId":600001,"en":"Example dungeon","de":"Beispieldungeon",
        "variantEn":"Example variant","variantDe":"Beispielvariante","image":"dungeon-maps/example.webp",
        "imageSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","width":200,"height":100,
        "spawns":[{"id":"example-position","npcId":2300102,"petNumber":1068,"x":42.25,"y":63.5,
        "sourceRefs":["https://example.test/position"]}],"sourceRefs":["https://example.test/map"]}]}
        """)!;
    private static JsonNode Map(JsonNode file) => file["maps"]![0]!;
    private static JsonNode Spawn(JsonNode file) => Map(file)["spawns"]![0]!;
    private static void Invalid(JsonNode file) => Assert.Throws<InvalidDataException>(() => PetDungeonMapCatalog.FromJson(file.ToJsonString()));
}
