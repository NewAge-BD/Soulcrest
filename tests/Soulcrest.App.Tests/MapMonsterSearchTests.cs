using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MapMonsterSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedSpawnsAreGroupedAcrossAllMapsBeforeTheFortyNameLimit()
    {
        using var fixture = new IndexFixture();
        fixture.Write("altgard", Enumerable.Repeat(Marker("Mob", "Monster", "pet"), 70)
            .Concat(Enumerable.Range(0, 45).Select(i => Marker($"Mob {i:00}", null, null))));
        fixture.Write("verteron", [Marker("Mob", "Monster", "pet"), Marker("Mob", "Monster", "pet")]);
        var search = fixture.Search(new PetCatalog([Pet("pet")]));
        var matches = search.Search("  mOB  ", "en");
        Assert.Equal(40, matches.Count);
        var repeated = Assert.Single(matches, match => match.En == "Mob");
        Assert.Same(repeated, matches[0]); // exact names precede partial matches
        Assert.Equal(72, repeated.SpawnCount);
        Assert.Equal(new[] { "altgard", "verteron" }, repeated.Maps);
        Assert.Equal(new[] { "pet" }, repeated.PetIds);
        Assert.Equal(new[] { "Mob" }, repeated.Names);
        Assert.Equal(matches.Count, matches.Select(match => match.DisplayName("en")).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(search.Search(" m ", "en"));
    }

    [Fact]
    public void BothLanguagesAreSearchableAndIdenticalLocalizedNamesRetainEveryConcreteMonsterName()
    {
        using var fixture = new IndexFixture();
        fixture.Write("altgard", [Marker("Twin East", "Zwillingsmonster", "first")]);
        fixture.Write("verteron", [Marker("Twin West", "Zwillingsmonster", "second")]);
        var search = fixture.Search(new PetCatalog([Pet("first"), Pet("second")]));
        var german = Assert.Single(search.Search("ZWILLING", "de"));
        Assert.Equal("Zwillingsmonster", german.DisplayName("de"));
        Assert.Equal(new[] { "Twin East", "Twin West" }, german.Names);
        Assert.Equal(new[] { "first", "second" }, german.PetIds);
        Assert.Equal(new[] { "altgard", "verteron" }, german.Maps);
        Assert.Equal(2, german.SpawnCount);
        Assert.Equal(2, search.Search("Zwilling", "en").Count); // the search language does not restrict lookup
        Assert.Single(search.Search("Twin", "de"));
        Assert.Equal(new[] { "Twin East", "Twin West" }, search.Search("Twin", "en").Select(match => match.DisplayName("en")));
    }

    [Fact]
    public void OnlyExplicitPetIdsResolveSoulsAndLegacyIdsUseTheCanonicalIdentity()
    {
        using var fixture = new IndexFixture();
        fixture.Write("altgard", [Marker("Mapped Monster", "Belegtes Monster", "legacy", icon: 0),
            Marker("Mapped Monster", "Belegtes Monster", "canonical"),
            Marker("Unknown Monster", null, "missing"), Marker("Canonical Pet", null, null)]);
        var catalog = new PetCatalog([Pet("canonical") with { En = "Canonical Pet" }],
            new Dictionary<string, string> { ["legacy"] = "canonical" });
        var search = fixture.Search(catalog);
        Assert.Equal(new[] { "canonical" }, Assert.Single(search.Search("Mapped", "en")).PetIds);
        Assert.Empty(Assert.Single(search.Search("Unknown", "en")).PetIds);
        Assert.Null(Assert.Single(search.Search("Unknown", "en")).Icon); // a generic category icon is not the monster portrait
        Assert.Empty(Assert.Single(search.Search("Canonical Pet", "en")).PetIds); // equal names cannot prove a soul drop
        Assert.Equal("/mapdata/icons/monster.png", Assert.Single(search.Search("Mapped", "en")).Icon);
    }

    [Fact]
    public void FarmMapsIncludeOtherSourcedVariantsButExcludeZeroSpawnsAndUnrelatedMaps()
    {
        using var fixture = new IndexFixture();
        fixture.Write("altgard", [Marker("Forest Monster", null, "pet")]);
        fixture.Write("verteron", [Marker("Forest Monster", null, "pet")]);
        var pet = Pet("pet") with { Spawns = new() { ["abyss"] = 2, ["unused"] = 0 }, Monsters = new() { ["verteron"] = 7 } };
        var search = fixture.Search(new PetCatalog([pet]));
        var match = Assert.Single(search.Search("Forest", "en"));
        Assert.Equal(new[] { "altgard", "verteron" }, match.Maps);
        Assert.Equal(new[] { "altgard", "verteron", "abyss" }, search.FarmMaps(match).Select(map => map.Id));
        Assert.Equal(new[] { "altgard" }, search.FarmMaps(match with { Maps = ["altgard"], PetIds = [] }).Select(map => map.Id));
    }

    [Theory]
    [InlineData("fresh", "verteron")]
    [InlineData("stopped", "altgard")]
    [InlineData("lost", "altgard")]
    [InlineData("paused", "altgard")]
    [InlineData("stale-position", "altgard")]
    [InlineData("stale-map", "altgard")]
    [InlineData("world-map", "altgard")]
    [InlineData("instance", "altgard")]
    [InlineData("escape", "altgard")]
    [InlineData("different-map", "altgard")]
    [InlineData("invalid-coordinate", "altgard")]
    public void PreferredMapUsesOnlyFreshActivePlayerEvidenceOtherwiseTheShownMap(string state, string expected)
    {
        var at = state == "stale-position" ? Now.AddSeconds(-6) : Now;
        var tracking = new ExplorationTrackingSnapshot(state != "stopped", state != "lost", state == "paused",
            state == "world-map", state == "instance", state == "escape",
            new PlayerPosition(state == "different-map" ? "abyss" : "verteron", state == "invalid-coordinate" ? double.NaN : 100, 200, at),
            new DetectedMapContext("verteron", state == "world-map", state == "stale-map" ? Now.AddSeconds(-6) : Now));
        var before = tracking;
        var match = new MonsterSearchMatch("Monster", "Monster", null, null, [], ["altgard", "verteron"], 4);
        Assert.Equal(expected, MapMonsterSearch.PreferredMap(match, "altgard", tracking, Now));
        Assert.Equal(before, tracking);
    }

    [Fact]
    public void UnsupportedPlayerAndShownMapsFallBackToTheFirstActualMonsterMap()
    {
        var tracking = new ExplorationTrackingSnapshot(true, true, false, false, false, false,
            new PlayerPosition("abyss", 100, 200, Now), new DetectedMapContext("abyss", false, Now));
        var match = new MonsterSearchMatch("Monster", "Monster", null, null, [], ["altgard", "verteron"], 4);
        Assert.Equal("altgard", MapMonsterSearch.PreferredMap(match, "abyss", tracking, Now));
    }

    private static PetDefinition Pet(string id) => new() { Id = id, En = id, Genus = "fera" };
    private static object?[] Marker(string en, string? de, string? pet, int icon = -1) => [0, 100, 200, en, de, icon, pet];

    private sealed class IndexFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-monster-index-" + Guid.NewGuid().ToString("N"));
        private readonly MapInfo[] _maps = [new("altgard", "Altgard", "Asmodian"), new("verteron", "Verteron", "Elyos"),
            new("abyss", "Abyss", "Abyss"), new("unused", "Unused", "Other")];
        public void Write(string map, IEnumerable<object?[]> markers)
        {
            Directory.CreateDirectory(Path.Combine(_directory, map));
            var data = JsonSerializer.Serialize(new { categories = new[] { new { group = "Monsters", name = "Normal", icon = 0 } },
                icons = new[] { "/mapdata/icons/monster.png" }, markers = markers.ToArray() });
            File.WriteAllText(Path.Combine(_directory, map, "data.js"), "window.SoulcrestMaps[\"" + map + "\"] = " + data + ";");
        }
        public MapMonsterSearch Search(PetCatalog catalog) => new(_directory, _maps, catalog);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
