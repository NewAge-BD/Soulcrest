using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementFactionCatalogTests
{
    private const string Entry = "{\"id\":42,\"faction\":\"Elyos\",\"stageIds\":[421,422]}";
    private static string Document(string families = Entry) =>
        "{\"schema\":1,\"metadataBuild\":26425393,\"referenceOnly\":true,\"families\":[" + families + "]}";

    [Fact]
    public void EmbeddedFactionMetadataCoversEveryExactFamilyAndStageInEveryArea()
    {
        var catalog = CombatAchievementFactionCatalog.Default;
        var definitions = AchievementDefinitionCatalog.Default;
        var display = CombatAchievementDisplayCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(definitions.MetadataBuild, catalog.MetadataBuild);
        Assert.Equal(display.MetadataBuild, catalog.MetadataBuild);
        Assert.Equal(6428, catalog.Definitions.Count);
        Assert.Equal(17316, catalog.Definitions.Sum(row => row.StageIds.Count));
        Assert.Equal(794, catalog.Definitions.Count(row => row.Faction == CombatAchievementFaction.All));
        Assert.Equal(2884, catalog.Definitions.Count(row => row.Faction == CombatAchievementFaction.Elyos));
        Assert.Equal(2750, catalog.Definitions.Count(row => row.Faction == CombatAchievementFaction.Asmodian));
        Assert.DoesNotContain(catalog.Definitions, row => row.Faction == CombatAchievementFaction.Unknown);
        Assert.Equal(definitions.Definitions.Select(row => row.Id).Order(), catalog.Definitions.Select(row => row.Id).Order());
        Assert.All(catalog.Definitions, row =>
        {
            Assert.Equal(definitions.Find(row.Id)!.StageIds, row.StageIds);
            Assert.Equal(row.Faction, catalog.FindFamily(row.Id));
            Assert.All(row.StageIds, id => Assert.Equal(row.Faction, catalog.FindStage(id)));
        });
        foreach (var area in Enum.GetValues<AchievementArea>())
        {
            var members = definitions.Definitions.Where(row => row.Area == area).ToArray();
            Assert.NotEmpty(members);
            Assert.All(members, row => Assert.NotEqual(CombatAchievementFaction.Unknown, catalog.FindFamily(row.Id)));
        }
    }

    [Fact]
    public void NativeFactionPairsAndNeutralReferencesUseTheSelectedCanonicalFaction()
    {
        var catalog = CombatAchievementFactionCatalog.Default;
        Assert.Equal(CombatAchievementFaction.Elyos, catalog.FindFamily(301100100));
        Assert.Equal(CombatAchievementFaction.Asmodian, catalog.FindFamily(401100100));
        Assert.True(catalog.IncludesFamily(301100100, Factions.Elyos));
        Assert.False(catalog.IncludesFamily(301100100, Factions.Asmodian));
        Assert.True(catalog.IncludesFamily(401100100, Factions.Asmodian));
        Assert.False(catalog.IncludesFamily(401100100, Factions.Elyos));
        foreach (var row in catalog.Definitions.Where(row => row.Faction == CombatAchievementFaction.All))
        {
            Assert.True(catalog.IncludesFamily(row.Id, Factions.Elyos));
            Assert.True(catalog.IncludesFamily(row.Id, Factions.Asmodian));
            Assert.All(row.StageIds, id =>
            {
                Assert.True(catalog.IncludesStage(id, Factions.Elyos));
                Assert.True(catalog.IncludesStage(id, Factions.Asmodian));
            });
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Elyos")]
    [InlineData("unknown")]
    public void MissingOrUnsupportedSelectionKeepsBothFactionsVisible(string? selectedFaction)
    {
        var catalog = CombatAchievementFactionCatalog.Default;
        Assert.True(catalog.IncludesFamily(301100100, selectedFaction));
        Assert.True(catalog.IncludesFamily(401100100, selectedFaction));
        Assert.All(catalog.Definitions.SelectMany(row => row.StageIds), id => Assert.True(catalog.IncludesStage(id, selectedFaction)));
    }

    [Theory]
    [InlineData(Factions.Elyos)]
    [InlineData(Factions.Asmodian)]
    public void MissingAndExplicitUnknownEntriesRemainVisibleWithoutIdHeuristics(string selectedFaction)
    {
        var catalog = CombatAchievementFactionCatalog.FromJson(Document(
            "{\"id\":300000001,\"faction\":\"Asmodian\",\"stageIds\":[400000001]},"
            + "{\"id\":400000002,\"faction\":\"Elyos\",\"stageIds\":[300000002]},"
            + "{\"id\":43,\"faction\":\"Unknown\",\"stageIds\":[431]}"));
        Assert.Equal(CombatAchievementFaction.Asmodian, catalog.FindFamily(300000001));
        Assert.Equal(CombatAchievementFaction.Elyos, catalog.FindFamily(400000002));
        Assert.Equal(CombatAchievementFaction.Asmodian, catalog.FindStage(400000001));
        Assert.Equal(CombatAchievementFaction.Elyos, catalog.FindStage(300000002));
        Assert.True(catalog.IncludesFamily(43, selectedFaction));
        Assert.True(catalog.IncludesStage(431, selectedFaction));
        foreach (var id in new[] { 0, -1, 300000003, 400000003, int.MaxValue })
        {
            Assert.Equal(CombatAchievementFaction.Unknown, catalog.FindFamily(id));
            Assert.Equal(CombatAchievementFaction.Unknown, catalog.FindStage(id));
            Assert.True(catalog.IncludesFamily(id, selectedFaction));
            Assert.True(catalog.IncludesStage(id, selectedFaction));
        }
    }

    [Fact]
    public void DefinitionListsCannotBeMutatedAfterValidation()
    {
        var catalog = CombatAchievementFactionCatalog.FromJson(Document());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<CombatAchievementFactionDefinition>>(catalog.Definitions).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<int>>(catalog.Definitions[0].StageIds).Clear());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("reference")]
    [InlineData("unknown-value")]
    [InlineData("numeric-value")]
    [InlineData("duplicate-family")]
    [InlineData("duplicate-stage")]
    [InlineData("shared-stage")]
    [InlineData("invalid-family")]
    [InlineData("invalid-stage")]
    [InlineData("empty-stages")]
    [InlineData("null-stages")]
    [InlineData("empty-families")]
    [InlineData("null-family")]
    [InlineData("live-state")]
    [InlineData("private-source")]
    public void InvalidOrOverclaimingMetadataCannotEnterTheLookup(string variant)
    {
        var json = variant switch
        {
            "schema" => Document().Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            "build" => Document().Replace("\"metadataBuild\":26425393", "\"metadataBuild\":0", StringComparison.Ordinal),
            "reference" => Document().Replace("\"referenceOnly\":true", "\"referenceOnly\":false", StringComparison.Ordinal),
            "unknown-value" => Document().Replace("\"Elyos\"", "\"FutureFaction\"", StringComparison.Ordinal),
            "numeric-value" => Document().Replace("\"Elyos\"", "1", StringComparison.Ordinal),
            "duplicate-family" => Document(Entry + "," + Entry),
            "duplicate-stage" => Document().Replace("[421,422]", "[421,421]", StringComparison.Ordinal),
            "shared-stage" => Document(Entry + "," + Entry.Replace("\"id\":42", "\"id\":43", StringComparison.Ordinal)),
            "invalid-family" => Document().Replace("\"id\":42", "\"id\":0", StringComparison.Ordinal),
            "invalid-stage" => Document().Replace("[421,422]", "[0]", StringComparison.Ordinal),
            "empty-stages" => Document().Replace("[421,422]", "[]", StringComparison.Ordinal),
            "null-stages" => Document().Replace("[421,422]", "null", StringComparison.Ordinal),
            "empty-families" => Document(""),
            "null-family" => Document("null"),
            "live-state" => Document().Replace("\"id\":42", "\"completed\":true,\"id\":42", StringComparison.Ordinal),
            "private-source" => Document().Replace("\"id\":42", "\"sourcePath\":\"private\",\"id\":42", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => CombatAchievementFactionCatalog.FromJson(json));
    }
}
