using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CraftingOwnedMaterialTests
{
    [Theory]
    [InlineData(0, 52, 5)]
    [InlineData(3, 22, 2)]
    [InlineData(5, 2, 0)]
    [InlineData(500, 2, 0)]
    public void OwnedMaterialsReduceOnlyOutstandingPurchasesAndNeverCreditSurplus(int owned, int cost, int bought)
    {
        var catalog = Catalog([Recipe(100, 1, [(2, 5)]) with { CraftingFee = 2 }]);
        var result = new CraftingCalculator(catalog).Calculate(Request() with
        { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, owned) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal((decimal)cost, result.ExpectedCost); Assert.Equal((decimal)cost, result.P90Cost);
        Assert.Equal(88m - cost, result.ExpectedProfit);
        Assert.Equal((decimal)bought, result.Shopping.Sum(row => row.ExpectedQuantity));
    }

    [Fact]
    public void FullyOwnedMaterialDoesNotNeedAPurchasePrice()
    {
        var result = new CraftingCalculator(Catalog([Recipe(100, 1, [(2, 5)])])).Calculate(Request() with
        { BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty, OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 5) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(0m, result.ExpectedCost); Assert.Empty(result.Shopping);
    }

    [Fact]
    public void OneStockPoolCoversSharedMaterialsAcrossCraftedIntermediateAndRoot()
    {
        var result = new CraftingCalculator(Catalog([Recipe(100, 1, [(2, 2), (3, 1)]), Recipe(200, 3, [(2, 4)])])).Calculate(Request() with
        {
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 5),
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(3, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(3, 200)
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(10m, result.ExpectedCost);
        var row = Assert.Single(result.Shopping); Assert.Equal(2, row.ItemId); Assert.Equal(1m, row.ExpectedQuantity);
    }

    [Fact]
    public void AnAlreadyOwnedOutputDoesNotReplaceTheRequestedNewCraft()
    {
        var result = new CraftingCalculator(Catalog([Recipe(100, 1, [(2, 5)])])).Calculate(Request() with
        { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(1, 100) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(50m, result.ExpectedCost);
    }

    private static CraftingCatalog Catalog(CraftingRecipe[] recipes) => new("synthetic", Enumerable.Range(1, 3)
        .Select(id => new CraftingItem { Id = id, NameEn = "Example " + id, NameDe = "Beispiel " + id, IsTradable = true }), recipes);
    private static CraftingRecipe Recipe(int id, int output, (int Id, int Quantity)[] inputs) => new()
    {
        Id = id, Profession = "Blacksmithing", Subcategory = "Materials", OutputItemId = output, OutputQuantity = 1,
        Ingredients = inputs.Select(row => new CraftingIngredient { ItemId = row.Id, Quantity = row.Quantity }).ToImmutableArray(),
        SuccessRates = [1m], MechanicsQualified = true, CraftingFee = 0
    };
    private static CraftingPlanRequest Request() => new()
    {
        RecipeId = 100, Samples = 100,
        BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(2, new(2, 10, CraftingPriceSource.Manual)),
        SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(1, new(1, 100, CraftingPriceSource.Manual))
    };
}
