using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class TransferCraftingComboTargetTests
{
    [Fact]
    public void SelectedComboRequiresRepeatedTransfersAndOnlyOneOwnedPrecursorIsFree()
    {
        var catalog = Catalog(.25m);
        var request = Request() with
        { OwnedItems = new Dictionary<int, long> { [2] = 1, [5] = 1 }.ToImmutableDictionary() };
        var result = new TransferCraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        // Geometric(.25): E=4 root attempts; one precursor owned, remaining 3 cost 20 each.
        Assert.InRange(result.ExpectedCost!.Value, 138.5m, 141.5m);
        Assert.Equal(340m, result.P90Cost); // q90=9; 9*20 root + 8*20 precursor.
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts, 3.96m, 4.04m);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts, 2.96m, 3.04m);
        Assert.Equal(1m, result.Byproducts.Single(row => row.ItemId == 5).ExpectedQuantity);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity, 2.96m, 3.04m);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId is 1 or 2 or 5);
        Assert.Null(result.ExpectedProfit); Assert.Null(result.ExpectedFees); Assert.Null(result.TargetOutput);
        Assert.All(result.Byproducts, row => Assert.Null(row.NetValue));
    }

    [Fact]
    public void BaseTargetRetainsGuaranteedBaseOrBetterUpgradeButUnreachableComboHasNoBudget()
    {
        var request = Request() with { RecipeId = 100, TargetItemId = 1 };
        var basic = new TransferCraftingCalculator(Catalog(.25m)).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, basic.Status);
        Assert.Equal(40m, basic.ExpectedCost);
        var impossible = new TransferCraftingCalculator(Catalog(0)).Calculate(Request());
        Assert.Equal(CraftingPlanStatus.Unknown, impossible.Status);
        Assert.Equal("TargetUnreachable", Assert.Single(impossible.Issues).Code);
        Assert.Null(impossible.ExpectedCost); Assert.Null(impossible.P90Cost); Assert.Empty(impossible.Steps);
    }

    private static CraftingCatalog Catalog(decimal activation) => new("synthetic",
        Enumerable.Range(1, 6).Select(id => new CraftingItem
        { Id = id, NameEn = "Synthetic " + id, NameDe = "Synthetisch " + id, IsEquipment = id is 1 or 2 or 5, IsTradable = true }),
        [new() { Id = 100, Profession = "Blacksmithing", Subcategory = "Greatsword", OutputItemId = 1, OutputQuantity = 1,
            IsTransferCrafting = true, RequiredProficiency = 0, CraftingFee = 0, TransferItemIds = [2],
            Ingredients = [new() { ItemId = 2, Quantity = 1 }, new() { ItemId = 3, Quantity = 1 }],
            ComboRecipeId = 101, ComboActivationChance = activation },
        new() { Id = 101, Profession = "Blacksmithing", Subcategory = "Greatsword", OutputItemId = 5, OutputQuantity = 1,
            RequiredProficiency = 0, CraftingFee = 0 },
        new() { Id = 200, Profession = "Blacksmithing", Subcategory = "Greatsword", OutputItemId = 2, OutputQuantity = 1,
            Ingredients = [new() { ItemId = 4, Quantity = 1 }], IsTransferCrafting = false,
            MechanicsQualified = true, CraftingFee = 0, SuccessRates = [1m] }]);
    private static TransferCraftingPlanRequest Request() => new()
    {
        RecipeId = 101, TargetItemId = 5,
        BuyPrices = new Dictionary<int, CraftingPrice>
        { [2] = new(2, 99999, CraftingPriceSource.Manual), [3] = new(3, 20, CraftingPriceSource.Manual),
            [4] = new(4, 20, CraftingPriceSource.Manual) }.ToImmutableDictionary()
    };
}
