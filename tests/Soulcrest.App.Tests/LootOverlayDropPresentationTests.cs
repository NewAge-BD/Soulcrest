using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootOverlayDropPresentationTests
{
    [Fact]
    public void CommonGearCombinesQuantitiesWithoutMergingWhiteMaterialsOrChangingSourceRows()
    {
        LootOverlayDrop[] original = [new("Item:610530001", "Refining Stone", 3),
            new("Item:110160001", "Worn Greatsword", 2)
            {
                IconPath = "gear.png",
            },
            new("Item:210230006", "Spectral Greaves", 1), new("Item:110160008", "Training Greatsword", 1)];
        var result = LootOverlayDropPresentation.GroupCommonGear(original);
        Assert.Equal(3, result.Count); Assert.Equal("Item:210230006", result[0].Key);
        Assert.Equal(UiText.T("Gewöhnliche Ausrüstung"), result[1].Name); Assert.Equal(3, result[1].Count);
        Assert.Equal("gear.png", result[1].IconPath); Assert.Equal(LootItemRarity.Common, result[1].Rarity);
        Assert.Equal("Item:610530001", result[2].Key);
        Assert.Equal(4, original.Length); Assert.Equal("Worn Greatsword", original[1].Name);
    }


    [Fact]
    public void OverflowRetainsTheIndividualRowsAndStillOrdersThemRatherThanLosingQuantities()
    {
        LootOverlayDrop[] rows = [new("unknown", "Unknown", long.MaxValue),
            new("Item:110160008", "Second", 1), new("Item:110160001", "First", long.MaxValue),
            new("Item:110150003", "Rare first", 2), new("Item:110150004", "Rare second", 3)];
        var result = LootOverlayDropPresentation.GroupCommonGear(rows);
        Assert.Equal(new[] { LootOverlayDropPresentation.RareGearKey, "Item:110160001", "Item:110160008", "unknown" },
            result.Select(row => row.Key));
        Assert.Equal(new[] { 5L, long.MaxValue, 1L, long.MaxValue }, result.Select(row => row.Count));
        Assert.DoesNotContain(result, row => row.Key == LootOverlayDropPresentation.CommonGearKey);
        Assert.Equal(5, rows.Length); Assert.Equal("unknown", rows[0].Key);
    }

    [Fact]
    public void ThreeGearGradesCombineSeparatelyAfterClassificationAndMaterialsRemainExact()
    {
        LootOverlayDrop[] rows = [
            new("Item:110160001", "Common first", 1), new("Item:110150003", "Rare first", 2),
            new("Item:110140002", "Legend first", 1), new("Item:610530001", "Common material", 8),
            new("Item:610530005", "Rare material", 10), new("Item:610530008", "Legend material", 4),
            new("Item:110160008", "Common second", 2), new("Item:110150004", "Rare second", 4),
            new("Item:110140003", "Legend second", 2), new("Item:210230006", "Unique gear", 1),
            new("SoulCrystal:2", "Crystal", 100), new("not-an-item", "Legend gear", 50)];
        var result = LootOverlayDropPresentation.GroupCommonGear(rows);
        Assert.Equal(new[] { "Item:210230006", "Item:610530008", LootOverlayDropPresentation.LegendGearKey,
            "Item:610530005", LootOverlayDropPresentation.RareGearKey, "Item:610530001",
            LootOverlayDropPresentation.CommonGearKey, "SoulCrystal:2", "not-an-item" }, result.Select(row => row.Key));
        Assert.Equal(new[] { 1L, 4L, 3L, 10L, 6L, 8L, 3L, 100L, 50L }, result.Select(row => row.Count));
        Assert.Equal(new[] { LootItemRarity.Legend, LootItemRarity.Rare, LootItemRarity.Common },
            result.Where(row => row.Key.EndsWith("Gear", StringComparison.Ordinal)).Select(row => row.Rarity));
        Assert.Equal(12, rows.Length); Assert.Equal("Item:110160001", rows[0].Key);
        Assert.Equal(result, LootOverlayDropPresentation.GroupCommonGear(Enumerable.Reverse(rows).ToArray()));
    }

    [Fact]
    public void WithoutGearRowsStillUseRarityQuantityNameAndOrdinalIdOrder()
    {
        LootOverlayDrop[] rows = [new("unknown-b", "Same", 2), new("unknown-z", "Alpha", 2),
            new("Item:610530001", "Common material", 500), new("Item:610530008", "Legend material", 1),
            new("unknown-a", "Same", 2), new("unknown-c", "Zulu", 5),
            new("Item:610530005", "Rare material", 900)];
        var result = LootOverlayDropPresentation.GroupCommonGear(rows);
        Assert.Equal(new[] { "Item:610530008", "Item:610530005", "Item:610530001", "unknown-c", "unknown-z", "unknown-a", "unknown-b" },
            result.Select(row => row.Key));
        Assert.Equal(rows.OrderBy(row => row.Key), result.OrderBy(row => row.Key));
    }

    [Fact]
    public void AllExistingRarityGradesRetainTheirOrderAndUnsupportedVisualGradesStayAtTheEnd()
    {
        var rows = Enum.GetValues<LootItemRarity>().Select(rarity =>
            new LootOverlayDrop("synthetic-" + rarity, rarity.ToString(), 1) { Rarity = rarity })
            .Append(new("unsupported", "ZZ unsupported", 1) { Rarity = (LootItemRarity)999 }).ToArray();
        var result = LootOverlayDropPresentation.GroupCommonGear(rows);
        Assert.Equal(Enum.GetValues<LootItemRarity>().OrderByDescending(rarity => (int)rarity),
            result.Take(rows.Length - 1).Select(row => row.Rarity));
        Assert.Equal("unsupported", result[^1].Key);
    }

    [Fact]
    public void ItemTypeAndCatalogGradeControlGroupingRatherThanTheNameOrSuppliedVisualGrade()
    {
        LootOverlayDrop[] rows = [new("unknown", "Rare Gear", 3) { Rarity = LootItemRarity.Rare },
            new("Item:110150003", "Not a gear drop", 2, LootOverlayDropKind.SoulCrystal),
            new("Item:610530005", "Rare Gear", 4),
            new("Item:210230006", "Unique Gear", 2),
            new("Item:110150004", "Catalog rare gear", 1) { Rarity = LootItemRarity.Legend }];
        var result = LootOverlayDropPresentation.GroupCommonGear(rows);
        var gear = Assert.Single(result, row => row.Key == LootOverlayDropPresentation.RareGearKey);
        Assert.Equal(LootItemRarity.Rare, gear.Rarity); Assert.Equal(1, gear.Count);
        foreach (var row in rows.Take(4)) Assert.Contains(row, result);
        Assert.Equal(5, result.Count);
    }

    [Theory]
    [InlineData("de", "Gewöhnliche Ausrüstung", "Seltene Ausrüstung", "Legendäre Ausrüstung")]
    [InlineData("en", "Common Gear", "Rare Gear", "Legend Gear")]
    public void GearSummaryLabelsFollowTheUiLanguage(string language, string common, string rare, string legend)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var result = LootOverlayDropPresentation.GroupCommonGear([
                new("Item:110160001", "Common", 1), new("Item:110150003", "Rare", 1), new("Item:110140002", "Legend", 1)]);
            Assert.Equal(new[] { legend, rare, common }, result.Select(row => row.Name));
        }
        finally { UiText.Language = previous; }
    }

}
