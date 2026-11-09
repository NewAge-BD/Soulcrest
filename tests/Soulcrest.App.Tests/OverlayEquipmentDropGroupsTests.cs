using System.Globalization;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEquipmentDropGroupsTests
{
    [Fact]
    public void ThreeCatalogGradesCombineAtTheirFirstMemberWhileExactRowsAndSourceRemainUnchanged()
    {
        OverlayLootItem[] rows =
        [
            Item(610530001, 8), Item(110160008, 3, "common-second.png"),
            Item(210230006, 1) with { IsRare = true, Rarity = LootItemRarity.Unique },
            Item(110150004, 7, "rare-second.png"), Item(110140003, 2, "blue-second.png"),
            Item(110160001, 2, "common-first.png"), Item(110150003, 3, "rare-first.png"),
            Item(110140002, 1, "blue-first.png"),
        ];
        var original = rows.ToArray();
        var result = OverlayEquipmentDropGroups.Group(rows, "de");
        Assert.Equal(new[] { "Item:610530001", "equipment-group:common", "Item:210230006",
            "equipment-group:rare", "equipment-group:legend" }, result.Select(row => row.Key));
        Assert.Equal(new long[] { 8, 5, 1, 10, 3 }, result.Select(row => row.Quantity));
        Assert.Same(rows[0], result[0]);
        Assert.Same(rows[2], result[2]);
        var groups = result.Where(row => row.IsEquipmentGroup).ToArray();
        Assert.Equal(new[] { "Gewöhnliche Ausrüstung", "Seltene Ausrüstung", "Epische Ausrüstung" }, groups.Select(row => row.Name));
        Assert.Equal(new[] { LootItemRarity.Common, LootItemRarity.Rare, LootItemRarity.Legend }, groups.Select(row => row.Rarity));
        Assert.Equal(new[] { "common-first.png", "rare-first.png", "blue-first.png" }, groups.Select(row => row.IconPath));
        Assert.All(groups, row => Assert.False(row.IsRare));
        Assert.Equal(original, rows);
    }

    [Theory]
    [InlineData(110160001, 110160008, "equipment-group:common")]
    [InlineData(110150003, 110150004, "equipment-group:rare")]
    [InlineData(110140002, 110140003, "equipment-group:legend")]
    public void CheckedQuantitiesCanReachTheExactLongBoundary(int first, int second, string key)
    {
        var result = OverlayEquipmentDropGroups.Group([Item(first, long.MaxValue - 1), Item(second, 1)], "en");
        var group = Assert.Single(result);
        Assert.Equal(key, group.Key);
        Assert.Equal(long.MaxValue, group.Quantity);
        Assert.Equal("9,223,372,036,854,775,807", group.QuantityText);
        Assert.Equal(group.Name + " ×" + group.QuantityText, group.Tooltip);
    }

    [Fact]
    public void OverflowKeepsEveryAffectedExactRowAndItsPositionWhileOtherGradesStillCombine()
    {
        OverlayLootItem[] rows =
        [
            new("unknown", "Unknown", "9", Quantity: 9), Item(110160008, 1), Item(610530001, 8),
            Item(110160001, long.MaxValue), Item(110150004, 3), Item(110150003, 4), Item(110160001, 0),
        ];
        var result = OverlayEquipmentDropGroups.Group(rows, "en");
        Assert.Equal(new[] { "unknown", "Item:110160008", "Item:610530001", "Item:110160001",
            "equipment-group:rare", "Item:110160001" }, result.Select(row => row.Key));
        for (var index = 0; index < 4; index++) Assert.Same(rows[index], result[index]);
        Assert.Same(rows[^1], result[^1]);
        Assert.Equal(7, result[4].Quantity);
        Assert.DoesNotContain(result, row => row.Key == "equipment-group:common");
        Assert.Equal(long.MaxValue, rows[3].Quantity);
    }

    [Fact]
    public void ZeroUnknownNegativeAndNonGearRowsRemainExactRatherThanEnteringAGroup()
    {
        OverlayLootItem[] exact =
        [
            Item(110160001, 0), Item(110160008, 0) with { QuantityText = "—", Tooltip = "Unknown quantity" },
            Item(110150003, -2), Item(999999999, 4) with { Name = "Common Gear", Rarity = LootItemRarity.Common },
            new("SoulCrystal:2", "Rare Gear", "5", Quantity: 5, Rarity: LootItemRarity.Rare),
            new("Item:invalid", "Epic Gear", "6", Quantity: 6, Rarity: LootItemRarity.Legend),
            Item(610530005, 7), Item(210230006, 1),
        ];
        var result = OverlayEquipmentDropGroups.Group(exact.Append(Item(110160001, 2)), "en");
        Assert.Equal(exact.Length + 1, result.Count);
        for (var index = 0; index < exact.Length; index++) Assert.Same(exact[index], result[index]);
        var group = result[^1];
        Assert.True(group.IsEquipmentGroup);
        Assert.Equal("equipment-group:common", group.Key);
        Assert.Equal(2, group.Quantity);
    }

    [Fact]
    public void ClassificationUsesProvenGearAndCatalogRarityRatherThanNamesOrPresentationGrades()
    {
        var source = Item(110150003, 2) with { Name = "Not gear", Rarity = LootItemRarity.Special, IsRare = true };
        var unique = Item(210230006, 1) with { Name = "Rare Gear", Rarity = LootItemRarity.Rare };
        var material = Item(610530008, 8) with { Name = "Epic Gear", Rarity = LootItemRarity.Legend };
        var result = OverlayEquipmentDropGroups.Group([source, unique, material], "en");
        Assert.Equal("equipment-group:rare", result[0].Key);
        Assert.Equal(LootItemRarity.Rare, result[0].Rarity);
        Assert.False(result[0].IsRare);
        Assert.Same(unique, result[1]);
        Assert.Same(material, result[2]);
        Assert.Equal(LootItemRarity.Special, source.Rarity);
    }

    [Theory]
    [InlineData("de", "Gewöhnliche Ausrüstung", "Seltene Ausrüstung", "Epische Ausrüstung", "1.234", "5.678", "1.000.000")]
    [InlineData("en", "Common Gear", "Rare Gear", "Epic Gear", "1,234", "5,678", "1,000,000")]
    [InlineData(null, "Common Gear", "Rare Gear", "Epic Gear", "1,234", "5,678", "1,000,000")]
    public void NamesAndExactAmountsUseOnlyTheRequestedLanguage(string? language, string common, string rare, string blue,
        string commonAmount, string rareAmount, string blueAmount)
    {
        var result = OverlayEquipmentDropGroups.Group(
            [Item(110160001, 1234), Item(110150003, 5678), Item(110140002, 1_000_000)], language);
        Assert.Equal(new[] { common, rare, blue }, result.Select(row => row.Name));
        Assert.Equal(new[] { commonAmount, rareAmount, blueAmount }, result.Select(row => row.QuantityText));
        Assert.All(result, row => Assert.Equal(row.Name + " ×" + row.QuantityText, row.Tooltip));
        Assert.Equal(LootItemRarity.Legend, result[2].Rarity);
    }

    [Fact]
    public void ARealMemberIconIsChosenByOrdinalKeyIndependentlyOfSourceOrder()
    {
        OverlayLootItem[] rows = [Item(110160008, 2, "later.png"), Item(110160001, 3, "first.png")];
        var forward = Assert.Single(OverlayEquipmentDropGroups.Group(rows, "en"));
        var reverse = Assert.Single(OverlayEquipmentDropGroups.Group(Enumerable.Reverse(rows), "en"));
        Assert.Equal("first.png", forward.IconPath);
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void MissingFirstMemberIconUsesTheNextActualAssetAndAllMissingIconsRemainUnknown()
    {
        OverlayLootItem[] rows = [Item(110160001, 2), Item(110160008, 3, "available-member.png")];
        var group = Assert.Single(OverlayEquipmentDropGroups.Group(rows, "en"));
        Assert.Equal("available-member.png", group.IconPath);
        var missing = rows.Select(row => row with { IconPath = null });
        Assert.Null(Assert.Single(OverlayEquipmentDropGroups.Group(missing, "en")).IconPath);
        Assert.Null(rows[0].IconPath);
        Assert.Equal("available-member.png", rows[1].IconPath);
    }

    [Fact]
    public void SourceIsEnumeratedOnceAndTheReturnedListCannotModifyIt()
    {
        var enumerations = 0;
        var sourceRow = Item(110160001, 2);
        IEnumerable<OverlayLootItem> Once()
        {
            if (++enumerations > 1) throw new InvalidOperationException("Source was reread.");
            yield return sourceRow;
        }
        var result = OverlayEquipmentDropGroups.Group(Once(), "en");
        Assert.Single(result);
        Assert.Equal(1, enumerations);
        Assert.True(Assert.IsAssignableFrom<IList<OverlayLootItem>>(result).IsReadOnly);
        Assert.Equal("Item:110160001", sourceRow.Key);
        Assert.False(sourceRow.IsEquipmentGroup);
    }

    private static OverlayLootItem Item(int id, long quantity, string? icon = null) => new(
        "Item:" + id.ToString(CultureInfo.InvariantCulture), "Exact source item " + id.ToString(CultureInfo.InvariantCulture),
        quantity.ToString(CultureInfo.InvariantCulture), icon, Quantity: quantity);
}
