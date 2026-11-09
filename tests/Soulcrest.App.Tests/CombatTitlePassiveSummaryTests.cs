using System.Text.Json;
using Soulcrest.App.Components;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatTitlePassiveSummaryTests
{
    [Fact]
    public void TitleOrderUsesYellowBlueGreenWhiteBeforeOtherGradesWithNamesAndIdsAsTieBreakers()
    {
        var catalog = Catalog(Title(1, "Other", []) with { Grade = "Special" },
            Title(2, "Blue", []) with { Grade = "Legend" }, Title(3, "Green", []) with { Grade = "Rare" },
            Title(4, "Same", []) with { Grade = "Common" }, Title(5, "Yellow", []) with { Grade = "Unique" },
            Title(6, "Same", []) with { Grade = "Common" });
        var rows = CombatTitlesPresentation.Rows(catalog, [1, 2, 3, 4, 5, 6], "en");
        Assert.Equal(new[] { 5, 2, 3, 4, 6, 1 }, rows.Select(row => row.Id));
    }

    [Fact]
    public void DistinctTitleIdsStackEqualBonusesAndNeverIncludeEquippedEffects()
    {
        var catalog = Catalog(Title(1, "Same name", [Effect("HPMax", 5)], [Effect("HPMax", 900)]),
            Title(2, "Same name", [Effect("HPMax", 5)], [Effect("HPMax", 900)]));
        var result = CombatTitlesPresentation.OwnedPassives(catalog, [1, 1, 2], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Complete, result.Status);
        var hp = Assert.Single(result.Rows);
        Assert.Equal(10m, hp.Value); Assert.Equal(10m, hp.KnownSubtotal); Assert.Equal(2, hp.TitleCount);
    }

    [Fact]
    public void StatAndUnitAreIndependentGroupingKeysAndExplicitZeroRemainsZero()
    {
        var catalog = Catalog(Title(1, "First", [Effect("Sample", 0), Effect("Sample", 1.5m, "Percent")]),
            Title(2, "Second", [Effect("Sample", 2.5m, "Percent")]));
        var result = CombatTitlesPresentation.OwnedPassives(catalog, [1, 2], true, "en");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0m, result.Rows.Single(row => row.Key.Unit == "Points").Value);
        Assert.Equal(4m, result.Rows.Single(row => row.Key.Unit == "Percent").Value);
    }

    [Fact]
    public void UnknownMetadataAndPartialOwnershipKeepOnlyQualifiedSubtotalsWithoutInventingAnExactTotal()
    {
        var catalog = Catalog(Title(1, "First", [Effect("HPMax", 5)]));
        foreach (var result in new[]
        {
            CombatTitlesPresentation.OwnedPassives(catalog, [1, 999999], true, "en"),
            CombatTitlesPresentation.OwnedPassives(catalog, [1], false, "en")
        })
        {
            Assert.Equal(CombatKnowledgeStatus.Partial, result.Status);
            var hp = Assert.Single(result.Rows);
            Assert.Null(hp.Value); Assert.Equal(5m, hp.KnownSubtotal);
            Assert.Equal(CombatKnowledgeStatus.Partial, hp.Status);
        }
        var unknown = CombatTitlesPresentation.OwnedPassives(catalog, null, false, "en");
        Assert.Equal(CombatKnowledgeStatus.Unknown, unknown.Status); Assert.Empty(unknown.Rows);
    }

    [Fact]
    public void UnknownUnitsAndValuesRemainPartialAndCannotTurnIntoZero()
    {
        var catalog = Catalog(Title(1, "First", [Effect("HPMax", 5), Effect("HPMax", 20, null), Effect("MPMax", null)]));
        var result = CombatTitlesPresentation.OwnedPassives(catalog, [1], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Partial, result.Status);
        Assert.All(result.Rows, row => Assert.Null(row.Value));
        Assert.Null(result.Rows.Single(row => row.Key.Unit is null).KnownSubtotal);
        Assert.Null(result.Rows.Single(row => row.Key.StatId == "MPMax").KnownSubtotal);
        Assert.Equal(5m, result.Rows.Single(row => row.Key == new CombatTitlePassiveKey("HPMax", "Points")).KnownSubtotal);
    }

    [Fact]
    public void EmptyConfirmedInventoryHasNoEffectsButMissingPreviewIsOnlyASetOfCatalogStatTypes()
    {
        var catalog = Catalog(Title(1, "First", [Effect("HPMax", 5)]),
            Title(2, "Second", [Effect("Accuracy", 3)], [Effect("HPMax", 999)]));
        var empty = CombatTitlesPresentation.OwnedPassives(catalog, [], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Complete, empty.Status); Assert.Empty(empty.Rows);
        var preview = CombatTitlesPresentation.MissingPassivePreview(catalog, [1], "en");
        var row = Assert.Single(preview);
        Assert.Equal(new CombatTitlePassiveKey("Accuracy", "Points"), row.Key);
        Assert.Equal(1, row.CatalogVariantCount);
    }

    [Fact]
    public void PassiveStatFilterMatchesOwnedChannelOnlyAcrossAllCatalogVariants()
    {
        var catalog = Catalog(Title(1, "First", [Effect("HPMax", 5)], [Effect("Accuracy", 100)]),
            Title(2, "Second", [Effect("Accuracy", 3)], [Effect("HPMax", 999)]));
        var rows = CombatTitlesPresentation.Rows(catalog, catalog.Titles.Keys, "en", passive: new("HPMax", "Points"));
        Assert.Equal(1, Assert.Single(rows).Id);
    }

    [Fact]
    public void EstimatedMissingTotalsUseOnlyTheExplicitMissingIdsAndKeepUnqualifiedInputsPartial()
    {
        var catalog = Catalog(Title(1, "Owned", [Effect("HPMax", 100)]),
            Title(2, "Missing A", [Effect("HPMax", 20)], [Effect("HPMax", 999)]),
            Title(3, "Missing B", [Effect("HPMax", 30)]));
        var result = CombatTitlesPresentation.EstimatedMissingPassives(catalog, [2, 2, 3], "en");
        Assert.Equal(50m, Assert.Single(result).EstimatedValue);
        Assert.Equal(2, result[0].CatalogVariantCount);
        var unknown = CombatTitlesPresentation.EstimatedMissingPassives(catalog, [2, 999999], "en");
        Assert.Null(Assert.Single(unknown).EstimatedValue);
        Assert.Equal(CombatKnowledgeStatus.Partial, unknown[0].ValueStatus);
        var unknownUnitCatalog = Catalog(Title(2, "Missing", [Effect("HPMax", 20, null)]));
        Assert.Null(Assert.Single(CombatTitlesPresentation.EstimatedMissingPassives(unknownUnitCatalog, [2], "en")).EstimatedValue);
    }

    private static CombatTitleDisplayEffect Effect(string id, decimal? value, string? unit = "Points") => new(id, id, id, value, unit);
    private static CombatTitleDisplayDefinition Title(int id, string name, IReadOnlyList<CombatTitleDisplayEffect>? owned,
        IReadOnlyList<CombatTitleDisplayEffect>? equipped = null) => new(id, name, name, "Attack")
        { Origin = "Quest", Grade = "Common", OwnedEffects = owned, EquippedEffects = equipped ?? [] };
    private static CombatTitleDisplayCatalog Catalog(params CombatTitleDisplayDefinition[] titles) =>
        CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = 26425393, presentationOnly = true,
            doesNotEstablishOwnershipOrEffectApplicability = true, titles
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
