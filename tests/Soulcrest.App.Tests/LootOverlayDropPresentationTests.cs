using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

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
        Assert.Equal(3, result.Count); Assert.Equal("Item:610530001", result[0].Key);
        Assert.Equal("Common Gear", result[1].Name); Assert.Equal(3, result[1].Count);
        Assert.Equal("gear.png", result[1].IconPath); Assert.Equal(LootItemRarity.Common, result[1].Rarity);
        Assert.Equal("Item:210230006", result[2].Key);
        Assert.Equal(4, original.Length); Assert.Equal("Worn Greatsword", original[1].Name);
    }


    [Fact]
    public void OverflowRetainsTheIndividualRowsRatherThanLosingQuantities()
    {
        LootOverlayDrop[] rows = [new("Item:110160001", "First", long.MaxValue), new("Item:110160008", "Second", 1)];
        Assert.Same(rows, LootOverlayDropPresentation.GroupCommonGear(rows));
    }
}
