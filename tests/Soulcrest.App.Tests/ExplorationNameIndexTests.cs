using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationCatalogFactAttribute : FactAttribute
{
    public ExplorationCatalogFactAttribute()
    {
        if (ExplorationNameIndexTests.CurrentGeneratedCatalogRoot() is null)
            Skip = "Requires the current generated map-data package (imports/generated/mapdata).";
    }
}

public sealed class ExplorationCatalogTheoryAttribute : TheoryAttribute
{
    public ExplorationCatalogTheoryAttribute()
    {
        if (ExplorationNameIndexTests.CurrentGeneratedCatalogRoot() is null)
            Skip = "Requires the current generated map-data package (imports/generated/mapdata).";
    }
}

public sealed class ExplorationNameIndexTests
{
    private static readonly Lazy<IReadOnlyList<ExplorationPlace>> Catalog = new(LoadCurrentGeneratedCatalog);
    private static ExplorationPlace Place(string id, string en, string? de = null, string kind = "dungeon", string map = "altgard") =>
        new(id, map, kind, 0, en, de);
    private static string[] Ids(IEnumerable<ExplorationPlace> places) => places.Select(place => place.Id).Order().ToArray();

    [ExplorationCatalogFact]
    public void EveryActualEnglishAndGermanSourceNameIsIndexedWithoutCreatingShortenedReferences()
    {
        var places = Catalog.Value;
        Assert.NotEmpty(places);
        var all = new ExplorationNameIndex(places);
        Assert.Equal(places.Sum(place => 1 + (string.IsNullOrWhiteSpace(place.De) ? 0 : 1)), all.References.Count);
        Assert.All(all.References, reference =>
        {
            var place = Assert.Single(places, place => place.Id == reference.PlaceId);
            Assert.Equal(reference.Language == "de" ? place.De : place.En, reference.Name);
            Assert.Equal(ExplorationService.Normalize(reference.Name), reference.NormalizedName);
        });
        foreach (var scope in places.GroupBy(place => (place.Map, place.Kind)))
        {
            var names = new ExplorationNameIndex(scope);
            foreach (var reference in names.References)
            {
                var expected = names.References.Where(other => other.NormalizedName == reference.NormalizedName)
                    .Select(other => other.PlaceId).Distinct().Order();
                Assert.Equal(expected, Ids(names.Resolve(reference.Name)));
                var result = names.ResolveObservation(reference.Name);
                Assert.False(result.Conflict);
                Assert.Equal(result.Candidates.Count == 1 ? result.Candidates[0] : null, names.Match(reference.Name));
            }
        }
    }

    [ExplorationCatalogFact]
    public void ArbitraryLongTruncationsOfEveryActualReferenceKeepTheirOwnIdentityAmongTheCandidates()
    {
        foreach (var scope in Catalog.Value.GroupBy(place => (place.Map, place.Kind)))
        {
            var names = new ExplorationNameIndex(scope);
            var minimum = scope.Key.Kind == "kibelisk" ? 8 : 12;
            foreach (var reference in names.References.Where(reference => reference.NormalizedName.Length > minimum))
            {
                var length = Math.Max(minimum, reference.NormalizedName.Length / 2);
                var prefix = reference.NormalizedName[..length];
                var result = names.ResolveObservation(prefix + "…unrelated OCR annotation");
                Assert.False(result.Conflict);
                Assert.Contains(result.Candidates, place => place.Id == reference.PlaceId);
                Assert.DoesNotContain(names.References, source => source.Name.Contains("unrelated OCR annotation", StringComparison.Ordinal));
            }
        }
    }

    [ExplorationCatalogTheory]
    [InlineData("Verlassenes Gefängnis der...Wlu s Brücke")]
    [InlineData("Verlassenes Gefängnis der...Mu@s Brücke")]
    [InlineData("Verlassenes Gefängnis der… 785 m")]
    [InlineData("Verlassenes Gefängnis der.. arbitrary extra text...")]
    public void TheReportedArchonJailObservationUsesTheCatalogAndOneEditInflection(string observation)
    {
        var scope = Catalog.Value.Where(place => place.Map == "altgard" && place.Kind == "dungeon").ToArray();
        var expected = Assert.Single(scope, place => place.En == "Abandoned Archon Jail Entrance");
        Assert.Equal("Eingang zum Verlassenen Gefängnis der Archonen", expected.De);
        var names = new ExplorationNameIndex(scope);
        var result = names.ResolveObservation(observation);
        Assert.False(result.Conflict);
        Assert.Equal(expected.Id, Assert.Single(result.Candidates).Id);
        Assert.Equal(expected.Id, names.Match(observation)?.Id);
    }

    [Theory]
    [InlineData("Provisorium der Klingenle... 785 m")]
    [InlineData("Provisorium der Klingenle…785m")]
    [InlineData("Provisorium der Klingenle..foreign map label")]
    public void OnlyTheInitialNameBeforeTheFirstExplicitEllipsisIsUsed(string observation)
    {
        var expected = Place("a", "Synthetic stronghold fixture", "Provisorium der Klingenlegion", "stronghold");
        var names = new ExplorationNameIndex([expected]);
        Assert.Equal(expected, names.Match(observation));
        Assert.Single(names.References, reference => reference.Language == "de");
    }

    [Theory]
    [InlineData("Synthetic Northern Ridge 785 m")]
    [InlineData("Synthetic Northern Ridge. 785 m")]
    [InlineData("Synthetic Northern Ridge foreign label")]
    [InlineData("Unrelated map label: Synthetic Northern Ridge Beacon")]
    public void AnArbitrarySuffixOrPrefixCannotBeDiscardedWithoutAnExplicitEllipsis(string observation)
    {
        var names = new ExplorationNameIndex([Place("a", "Synthetic Northern Ridge Beacon")]);
        Assert.Empty(names.Resolve(observation));
        Assert.Null(names.Match(observation));
    }

    [Fact]
    public void ACompleteNameHasPriorityButAnExplicitCutKeepsLongerContinuations()
    {
        var shortName = Place("short", "Safe Haven", kind: "kibelisk");
        var longName = Place("long", "Safe Haven Cliff", kind: "kibelisk");
        var names = new ExplorationNameIndex([shortName, longName]);
        Assert.Equal(shortName, names.Match("Safe Haven"));
        Assert.Equal(["long", "short"], Ids(names.Resolve("Safe Haven…")));
        Assert.Equal(["long", "short"], Ids(names.Resolve("Safe Haven", truncated: true)));
        Assert.Null(names.Match("Safe Haven…"));
    }

    [Fact]
    public void EquivalentLanguagesAndRepeatedReferencesDoNotDuplicateAnIdentity()
    {
        var place = Place("a", "Synthetic Café", "Synthetic Cafe");
        var names = new ExplorationNameIndex([place, place]);
        Assert.Equal(place, Assert.Single(names.Resolve("SYNTHETIC CAFÉ")));
        Assert.Equal(2, names.References.Count);
    }

    [ExplorationCatalogTheory]
    [InlineData("abyss-reshanta-a", "Siel's Left Wing", 2)]
    [InlineData("abyss-reshanta-a", "Siels Linkschwinge", 2)]
    [InlineData("abyss-reshanta-a", "River of Rotten Sap", 2)]
    [InlineData("verteron", "Cantas Valley Mushroom Tree Peak", 2)]
    public void ActualDuplicateKibeliskNamesRemainSeparateIdentities(string map, string name, int expected)
    {
        var names = new ExplorationNameIndex(Catalog.Value.Where(place => place.Map == map && place.Kind == "kibelisk"));
        Assert.Equal(expected, names.Resolve(name).Count);
        Assert.Null(names.Match(name));
    }

    [ExplorationCatalogTheory]
    [InlineData("altgard", "Eastern Gribade Highland…", "Eastern Gribade Highland Cliff", "Eastern Gribade Highland Tail Island")]
    [InlineData("verteron", "Garden of the Illusion God…", "Garden of the Illusion God East Observatory", "Garden of the Illusion God West Observatory")]
    [InlineData("verteron", "Garten des Illusionsgottes…", "Garden of the Illusion God East Observatory", "Garden of the Illusion God West Observatory")]
    public void ActualLongSharedPrefixesStayAmbiguousUntilTheDistinguishingTextIsVisible(string map, string observed, string first, string second)
    {
        var scope = Catalog.Value.Where(place => place.Map == map && place.Kind == "kibelisk").ToArray();
        var expected = scope.Where(place => place.En == first || place.En == second).ToArray();
        Assert.Equal(2, expected.Length);
        var names = new ExplorationNameIndex(scope);
        Assert.Equal(Ids(expected), Ids(names.Resolve(observed)));
        Assert.Null(names.Match(observed));
        foreach (var place in expected) Assert.Equal(place.Id, names.Match(place.En)?.Id);
    }

    [Theory]
    [InlineData("Synthetic Northern…Synthetic Southern Sanctuary")]
    [InlineData("Synthetic Northern... Synthetic Southern Sanctuary")]
    public void ASecondCompleteCatalogNameAfterAnEllipsisIsAHardConflict(string observation)
    {
        var north = Place("north", "Synthetic Northern Sanctuary");
        var south = Place("south", "Synthetic Southern Sanctuary");
        var names = new ExplorationNameIndex([north, south]);
        var result = names.ResolveObservation(observation);
        Assert.True(result.Conflict);
        Assert.Equal(["north", "south"], Ids(result.Candidates));
        Assert.Equal(Ids(result.Candidates), Ids(names.Resolve(observation)));
        Assert.Null(names.Match(observation));
    }

    [Fact]
    public void SuffixTextCannotSelectOneMemberOfAnAmbiguousInitialPrefix()
    {
        var first = Place("a", "Synthetic Northern Sanctuary Alpha");
        var second = Place("b", "Synthetic Northern Sanctuary Beta");
        var names = new ExplorationNameIndex([first, second]);
        var result = names.ResolveObservation("Synthetic Northern Sanctuary…" + first.En);
        Assert.False(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match("Synthetic Northern Sanctuary…" + first.En));
    }

    [Fact]
    public void AConflictRetainsAllInitialCandidatesAndTheForeignFullName()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Northern Sanctuary Alpha"), Place("b", "Synthetic Northern Sanctuary Beta"),
            Place("c", "Synthetic Southern Sanctuary")]);
        var result = names.ResolveObservation("Synthetic Northern Sanctuary…Synthetic Southern Sanctuary");
        Assert.True(result.Conflict);
        Assert.Equal(["a", "b", "c"], Ids(result.Candidates));
    }

    [Fact]
    public void UnknownInitialTextCannotBeRescuedByAKnownNameAfterAnEllipsis()
    {
        var names = new ExplorationNameIndex([Place("a", "Synthetic Northern Sanctuary")]);
        var result = names.ResolveObservation("Unknown region…Synthetic Northern Sanctuary");
        Assert.True(result.Conflict);
        Assert.Null(names.Match("Unknown region…Synthetic Northern Sanctuary"));
    }

    [Theory]
    [InlineData("Synthetix Monument Alphx", true)]
    [InlineData("Synthetix Monumxnt Alphx", false)]
    public void CompleteNameFuzzyStillRequiresNinetyPercent(string observation, bool accepted)
    {
        var place = Place("a", "Synthetic Monument Alpha");
        var names = new ExplorationNameIndex([place]);
        Assert.Equal(accepted ? place : null, names.Match(observation));
    }

    [Fact]
    public void FullFuzzyContendersWithinEightPercentagePointsRemainCandidates()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Monument Alpha"), Place("b", "Synthetic Monument Alphaa")]);
        const string observation = "Synthetic Monument Alphx";
        var result = names.ResolveObservation(observation);
        Assert.False(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match(observation));
    }

    [Fact]
    public void MissingEllipsisPermitsOnlyAnExactLongPrefix()
    {
        var place = Place("a", "Synthetic Northern Ridge Beacon");
        var names = new ExplorationNameIndex([place]);
        Assert.Equal(place, names.Match("Synthetic Northern Ridge"));
        Assert.Empty(names.Resolve("Synthelic Northern Ridge"));
        Assert.Equal(place, names.Match("Synthelic Northern Ridge…"));
        Assert.Empty(names.Resolve("Synthetic N…"));
        Assert.Empty(names.Resolve("Synthelix Northern Ridge…")); // Two substitutions.
    }

    [Fact]
    public void ExactImplicitPrefixesAlsoPreserveEveryPlausibleIdentity()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Northern Ridge Beacon"), Place("b", "Synthetic Northern Ridge Camp")]);
        Assert.Equal(["a", "b"], Ids(names.Resolve("Synthetic Northern Ridge")));
        Assert.Null(names.Match("Synthetic Northern Ridge"));
    }

    [Fact]
    public void KibelisksKeepEightCharacterExactPrefixesAndOneEditWholeNames()
    {
        var place = Place("a", "Synthetic Northern Ridge Beacon", kind: "kibelisk");
        var names = new ExplorationNameIndex([place]);
        Assert.Equal(place, Assert.Single(names.Resolve("Synthetic")));
        Assert.Equal(place, Assert.Single(names.Resolve("Synthetic", truncated: true)));
        Assert.Empty(names.Resolve("Synthet", truncated: true)); // Seven normalized characters.
        Assert.Equal(place, Assert.Single(names.Resolve("Synthelic Northern Ridge Beacon")));
        Assert.Empty(names.Resolve("Synthelic Northern Ridge", truncated: true));
        var lake = Place("lake", "Idun's Lake", kind: "kibelisk");
        Assert.Equal(lake, new ExplorationNameIndex([lake]).Match("'dun's Lake"));
    }

    [Fact]
    public void DifferentKibeliskFullFuzzyNamesCannotBecomeAGroup()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Monument Alpha", kind: "kibelisk"),
            Place("b", "Synthetic Monument Alphaa", kind: "kibelisk")]);
        var result = names.ResolveObservation("Synthetic Monument Alphx");
        Assert.True(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match("Synthetic Monument Alphx"));
    }

    [Fact]
    public void DifferentKibeliskOneEditNamesCannotBecomeAGroup()
    {
        var names = new ExplorationNameIndex([
            Place("a", "FixtureA", kind: "kibelisk"), Place("b", "FixtureB", kind: "kibelisk")]);
        var result = names.ResolveObservation("FixtureC"); // Seven of eight letters: below the 90% fuzzy path.
        Assert.True(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match("FixtureC"));
    }

    [Theory]
    [InlineData("Synthetic Monument Alpha", "Synthetic Monument Alphaa", "Synthetic Monument Alph")]
    [InlineData("FixtureA1", "FixtureA2", "FixtureA")]
    public void AnExactKibeliskPrefixGroupMayRecoverOnlyWhenItCoversEveryFuzzyContender(string first, string second, string observation)
    {
        var names = new ExplorationNameIndex([
            Place("a", first, kind: "kibelisk"), Place("b", second, kind: "kibelisk")]);
        var result = names.ResolveObservation(observation);
        Assert.False(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match(observation));
    }

    [Fact]
    public void OneExactKibeliskPrefixCannotRemoveAnotherPlausibleFuzzyIdentity()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Monument Alpha", kind: "kibelisk"),
            Place("b", "Synthetic Monument Blpha", kind: "kibelisk")]);
        var result = names.ResolveObservation("Synthetic Monument Alph");
        Assert.True(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
        Assert.Null(names.Match("Synthetic Monument Alph"));
    }

    [Fact]
    public void OneEditOfTheSameGenuineKibeliskNameKeepsTheExistingDuplicateGroup()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Idun's Lake", kind: "kibelisk"), Place("b", "Idun's Lake", kind: "kibelisk")]);
        var result = names.ResolveObservation("'dun's Lake");
        Assert.False(result.Conflict);
        Assert.Equal(["a", "b"], Ids(result.Candidates));
    }

    [Fact]
    public void KibeliskShortNameRulesCannotBroadenAMixedCategoryScope()
    {
        var names = new ExplorationNameIndex([
            Place("a", "Synthetic Northern Ridge Beacon", kind: "kibelisk"),
            Place("b", "Unrelated Dungeon Entrance")]);
        Assert.Empty(names.Resolve("Synthetic", truncated: true));
    }

    [Theory]
    [InlineData("Lost Ruins", "Lost Ruin Entrance")]
    [InlineData("Fafnite Storage Room", "Fafnite Storage Entrance")]
    public void EvidencedWholeListAliasesStayScopedAndAreNotSourceReferences(string observation, string canonical)
    {
        var place = Place("a", canonical);
        var names = new ExplorationNameIndex([place]);
        Assert.Equal(place, names.Match(observation));
        Assert.Equal(canonical, Assert.Single(names.References).Name);
        Assert.DoesNotContain(names.References, reference => reference.Name == observation);
        Assert.Null(new ExplorationNameIndex([place with { Map = "verteron" }]).Match(observation));
        Assert.Null(new ExplorationNameIndex([place with { Kind = "stronghold" }]).Match(observation));
    }

    [Fact]
    public void ConstructorCopiesReferencesAndCandidateEnumeration()
    {
        var place = Place("a", "Synthetic Northern Sanctuary");
        var source = new List<ExplorationPlace> { place };
        var names = new ExplorationNameIndex(source);
        source.Clear();
        Assert.Equal(place, names.Match(place.En));
        Assert.Single(names.References);
    }

    [Theory]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("short…")]
    [InlineData("Unknown unrelated text")]
    public void EmptyShortAndUnknownObservationsCannotSelectAnIdentity(string observation)
    {
        var names = new ExplorationNameIndex([Place("a", "Synthetic Northern Sanctuary")]);
        Assert.Empty(names.Resolve(observation));
        Assert.Null(names.Match(observation));
        Assert.Empty(new ExplorationNameIndex([]).Resolve(observation));
    }

    internal static string? CurrentGeneratedCatalogRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(root, "manifest.json"))) return root;
            directory = directory.Parent;
        }
        return null;
    }

    private static IReadOnlyList<ExplorationPlace> LoadCurrentGeneratedCatalog()
    {
        var root = CurrentGeneratedCatalogRoot() ?? throw new InvalidOperationException("Current generated map-data package is missing.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var places = new List<ExplorationPlace>();
        foreach (var map in manifest.RootElement.GetProperty("maps").EnumerateArray())
        {
            var mapId = map.GetProperty("id").GetString()!;
            var path = Path.Combine(root, mapId, "data.js");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            var start = text.IndexOf("] = {", StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            Assert.True(start >= 0 && end > start, $"Invalid current generated catalog: {mapId}");
            using var data = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
            var categories = data.RootElement.GetProperty("categories").EnumerateArray()
                .Select(category => category.GetProperty("name").GetString()).ToArray();
            var markerIndex = 0;
            foreach (var marker in data.RootElement.GetProperty("markers").EnumerateArray())
            {
                var kind = categories[marker[0].GetInt32()] switch
                {
                    "Sealed Dungeon" => "dungeon", "Stronghold" => "stronghold", "Kibelisk" => "kibelisk", _ => null
                };
                if (kind is not null && marker[3].GetString() is { Length: > 0 } en)
                    places.Add(new($"{mapId}|{kind}|{en}", mapId, kind, markerIndex, en, marker[4].GetString(),
                        marker[1].GetDouble(), marker[2].GetDouble()));
                markerIndex++;
            }
        }
        return ExplorationService.UniqueKibelisks(places).DistinctBy(place => place.Id).ToArray();
    }
}
