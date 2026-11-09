using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetAcquisitionCatalogTests
{
    private const string PatchUrl = "https://steamcommunity.com/games/3393110/announcements/detail/675136702174461957";

    [Fact]
    public void AdditionalSourcesResolveExactlyTheTwelveExistingPetIdentities()
    {
        Dictionary<int, int> expected = new()
        {
            [1009] = 534660008, [1028] = 534660027, [1043] = 534660044, [1068] = 534660069,
            [1078] = 534660079, [1080] = 534660081, [1082] = 534660083, [1090] = 534660091,
            [1100] = 534660101, [1107] = 534660108, [1167] = 534660132, [1199] = 534660131
        };
        Assert.Equal(expected.Keys.Order(), PetAcquisitionCatalog.All.Keys.Order());
        foreach (var (number, item) in expected)
        {
            var pet = Assert.IsType<PetAcquisitionEntry>(PetAcquisitionCatalog.Find(number));
            Assert.Equal(number, pet.PetNumber);
            Assert.Equal(item, pet.SoulItemId);
            Assert.Equal("2026-11-04", pet.NewSince);
            Assert.NotNull(PetIdTable.Find(number)?.CatalogId);
            Assert.NotEmpty(pet.Sources);
            Assert.Contains(PatchUrl, pet.SourceRefs);
        }
        Assert.Null(PetAcquisitionCatalog.Find((int?)null));
        Assert.Null(PetAcquisitionCatalog.Find(-1));
        Assert.Null(PetAcquisitionCatalog.Find(1002));
    }

    [Fact]
    public void SevenSourceFamiliesPreserveNamesAndLeaveModeAndDropChanceUnknown()
    {
        var sources = PetAcquisitionCatalog.All.Values.SelectMany(pet => pet.Sources).ToArray();
        Assert.Equal(7, sources.Select(source => source.DungeonEn).Distinct().Count());
        Assert.Equal(36, sources.SelectMany(source => source.Npcs).Select(npc => npc.Id).Distinct().Count());
        int[] excluded = [2301052, 2340704, 2351704, 2352704, 2354704, 2355704, 2357704];
        Assert.DoesNotContain(sources.SelectMany(source => source.Npcs), npc => excluded.Contains(npc.Id));
        foreach (var source in sources)
        {
            Assert.Null(source.Mode);
            Assert.Null(source.DropChance);
            Assert.NotEmpty(source.MapIds);
            Assert.NotEmpty(source.Npcs);
            Assert.Equal(source.MapIds.Order(), source.GlobalAvailability.Select(map => map.MapId).Order());
            Assert.All(source.GlobalAvailability, map =>
            {
                Assert.Equal("2026-11-05", map.CheckedAt);
                Assert.NotEmpty(map.SourceRefs);
            });
            Assert.Contains(PatchUrl, source.SourceRefs);
            Assert.Contains(source.SourceRefs, reference => reference.EndsWith("#" + source.Id, StringComparison.Ordinal));
            foreach (var npc in source.Npcs)
            {
                Assert.NotEmpty(npc.MapIds);
                Assert.All(npc.MapIds, map => Assert.Contains(map, source.MapIds));
                Assert.False(string.IsNullOrWhiteSpace(npc.DisplayName("en")));
                Assert.False(string.IsNullOrWhiteSpace(npc.DisplayName("de")));
                Assert.Contains(PatchUrl, npc.SourceRefs);
            }
        }
        var krao = Assert.Single(PetAcquisitionCatalog.Find(1068)!.Sources);
        Assert.Equal("Kraohöhle", krao.DungeonDisplayName("de"));
        Assert.Equal("Krao Cave", krao.DungeonDisplayName("en"));
        Assert.Equal("Verstärktes Kraoversuchsobjekt", krao.Npcs[0].DisplayName("de"));
        Assert.Equal("Enhanced Krao Test Subject", krao.Npcs[0].DisplayName("en"));
        Assert.Equal(PetAcquisitionCategory.Transcendence,
            Assert.Single(PetAcquisitionCatalog.Find(1090)!.Sources).Category);
        Assert.All(sources.Where(source => source.DungeonEn != "Shattered Arkanis"),
            source => Assert.Equal(PetAcquisitionCategory.Expedition, source.Category));
    }

    [Fact]
    public void ReturnedCatalogAndNestedCollectionsCannotBeMutated()
    {
        var all = Assert.IsAssignableFrom<IDictionary<int, PetAcquisitionEntry>>(PetAcquisitionCatalog.All);
        var pet = PetAcquisitionCatalog.Find(1009)!;
        Assert.Throws<NotSupportedException>(() => all.Remove(pet.PetNumber));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PetAcquisitionSource>>(pet.Sources).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(pet.SourceRefs).Clear());
        var source = pet.Sources[0];
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<int>>(source.MapIds)[0] = 1);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PetAcquisitionNpc>>(source.Npcs).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(source.SourceRefs).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<PetAcquisitionMapAvailability>>(source.GlobalAvailability).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(source.GlobalAvailability[0].SourceRefs).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<int>>(source.Npcs[0].MapIds).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(source.Npcs[0].SourceRefs).Clear());
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Hard")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void AModeCannotBeInventedByEditingTheSourceData(string mode)
    {
        var file = Fixture();
        Source(file)["mode"] = mode;
        Assert.Throws<InvalidDataException>(() => PetAcquisitionCatalog.FromJson(file.ToJsonString()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void AProbabilityCannotBeInventedFromLootCoefficients(int chance)
    {
        var file = Fixture();
        Source(file)["dropChance"] = chance;
        Assert.Throws<InvalidDataException>(() => PetAcquisitionCatalog.FromJson(file.ToJsonString()));
    }

    [Fact]
    public void MissingGermanUsesTheOriginalEnglishWithoutInventingATranslation()
    {
        var file = Fixture();
        Source(file)["dungeonDe"] = null;
        Source(file)["npcs"]![0]!["de"] = null;
        var source = Assert.Single(PetAcquisitionCatalog.FromJson(file.ToJsonString())[1009].Sources);
        Assert.Equal("Example dungeon", source.DungeonDisplayName("de"));
        Assert.Equal("Example monster", source.Npcs[0].DisplayName("de"));
    }

    [Fact]
    public void InvalidIdentityDisconnectedMapDuplicateOrExtraFieldFailsClosed()
    {
        var unknownPet = Fixture();
        unknownPet["pets"]![0]!["petNumber"] = int.MaxValue;
        AssertInvalid(unknownPet);
        var disconnected = Fixture();
        Source(disconnected)["npcs"]![0]!["mapIds"]![0] = 2;
        AssertInvalid(disconnected);
        var duplicate = Fixture();
        duplicate["pets"]!.AsArray().Add(duplicate["pets"]![0]!.DeepClone());
        AssertInvalid(duplicate);
        var category = Fixture();
        Source(category)["category"] = "Raid";
        AssertInvalid(category);
        var extra = Fixture();
        extra["unrelatedMetadata"] = "not part of the runtime projection";
        AssertInvalid(extra);
    }

    [Fact]
    public void EmbeddedProjectionContainsOnlyRuntimeFactsAndPublicReferences()
    {
        using var stream = typeof(PetAcquisitionCatalog).Assembly.GetManifestResourceStream("Soulcrest.PetAcquisitionSources.json");
        Assert.NotNull(stream);
        Assert.InRange(stream.Length, 1, 64_000);
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(["schemaVersion", "pets"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(12, document.RootElement.GetProperty("pets").GetArrayLength());
        // Strict deserialization also prevents extra metadata from entering the bundled contract.
        Assert.Equal(12, PetAcquisitionCatalog.FromJson(document.RootElement.GetRawText()).Count);
    }

    [Fact]
    public void FutureOnlySourcesCannotBePromotedToCurrentSourcesByTheirExistingSpawns()
    {
        foreach (var number in new[] { 1009, 1043, 1080 })
        {
            var source = Assert.Single(PetAcquisitionCatalog.Find(number)!.Sources);
            Assert.NotEmpty(source.Npcs);
            Assert.True(source.HasUpcomingVariants);
            Assert.False(source.HasAvailableVariants);
            Assert.False(source.HasUnconfirmedVariants);
            Assert.All(source.GlobalAvailability, map => Assert.Equal(PetGlobalAvailability.Upcoming, map.Status));
        }
    }

    [Fact]
    public void TheSameOriginalNpcNameCanRemainInSeparateCurrentFutureAndUnconfirmedVariants()
    {
        var file = Fixture();
        var source = Source(file);
        source["mapIds"] = JsonNode.Parse("[1,2,3]");
        source["globalAvailability"] = JsonNode.Parse("""
            [{"mapId":1,"status":"available","checkedAt":"2026-11-05","sourceRefs":["https://example.com/open"]},
             {"mapId":2,"status":"upcoming","checkedAt":"2026-11-05","sourceRefs":["https://example.com/future"]},
             {"mapId":3,"status":"unconfirmed","checkedAt":"2026-11-05","sourceRefs":["https://example.com/unknown"]}]
            """);
        for (var id = 2; id <= 3; id++)
        {
            var npc = source["npcs"]![0]!.DeepClone();
            npc["id"] = id;
            npc["mapIds"]![0] = id;
            source["npcs"]!.AsArray().Add(npc);
        }
        var loaded = Assert.Single(PetAcquisitionCatalog.FromJson(file.ToJsonString())[1009].Sources);
        Assert.True(loaded.HasAvailableVariants);
        Assert.True(loaded.HasUpcomingVariants);
        Assert.True(loaded.HasUnconfirmedVariants);
        Assert.Equal(1, Assert.Single(loaded.NpcsWithAvailability(PetGlobalAvailability.Available)).Id);
        Assert.Equal(2, Assert.Single(loaded.NpcsWithAvailability(PetGlobalAvailability.Upcoming)).Id);
        Assert.Equal(3, Assert.Single(loaded.NpcsWithAvailability(PetGlobalAvailability.Unconfirmed)).Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disconnected")]
    [InlineData("duplicate")]
    [InlineData("unknown-state")]
    [InlineData("bad-date")]
    [InlineData("missing-evidence")]
    public void AvailabilityMustBeExplicitCompleteDatedAndSourced(string defect)
    {
        var file = Fixture();
        var source = Source(file);
        var availability = source["globalAvailability"]!.AsArray();
        switch (defect)
        {
            case "missing": source.Remove("globalAvailability"); break;
            case "disconnected": availability[0]!["mapId"] = 2; break;
            case "duplicate": availability.Add(availability[0]!.DeepClone()); break;
            case "unknown-state": availability[0]!["status"] = "open-probably"; break;
            case "bad-date": availability[0]!["checkedAt"] = "2026-11-31"; break;
            case "missing-evidence": availability[0]!["sourceRefs"] = new JsonArray(); break;
        }
        AssertInvalid(file);
    }

    private static void AssertInvalid(JsonNode file) =>
        Assert.Throws<InvalidDataException>(() => PetAcquisitionCatalog.FromJson(file.ToJsonString()));
    private static JsonObject Source(JsonNode file) => file["pets"]![0]!["sources"]![0]!.AsObject();
    private static JsonNode Fixture() => JsonNode.Parse("""
        {"schemaVersion":1,"pets":[{"petNumber":1009,"soulItemId":534660008,"newSince":"2026-11-04",
          "sourceRefs":["https://example.com/pet"],"sources":[{
            "id":"pet-1009-dungeon-example","dungeonEn":"Example dungeon","dungeonDe":"Beispielgebiet",
            "category":"Expedition","mapIds":[1],"mode":null,"dropChance":null,
            "globalAvailability":[{"mapId":1,"status":"unconfirmed","checkedAt":"2026-11-05","sourceRefs":["https://example.com/availability"]}],
            "sourceRefs":["https://example.com/source"],
            "npcs":[{"id":1,"en":"Example monster","de":"Beispielmonster","mapIds":[1],
              "sourceRefs":["https://example.com/monster"]}]}]}]}
        """)!;
}
