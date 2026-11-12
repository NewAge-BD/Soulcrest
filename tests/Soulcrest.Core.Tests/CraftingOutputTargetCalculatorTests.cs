using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CraftingOutputTargetCalculatorTests
{
    [Fact]
    public void ExactComboTargetUsesThreeIndependentRollsAndTheWholeGeometricBudget()
    {
        var catalog = Catalog([Recipe(100, 1, (2, 1)) with
            { SuccessRates = [.5m], ComboActivationChance = .5m, ComboRecipeId = 101 },
            Recipe(101, 3) with { SuccessRates = [.5m] }]);
        var request = Request() with { BuyPrices = Prices((2, 10)), SellPrices = Prices((3, 100), (1, 20)) };
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        // Geometric(p=.5*.5*.5): E[N]=8; q90=ceil(log(.1)/log(.875))=18.
        Assert.InRange(result.ExpectedCost!.Value, 79.1m, 80.9m);
        Assert.Equal(180m, result.P90Cost);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts, 7.91m, 8.09m);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 101).ExpectedAttempts, 1.97m, 2.03m);
        Assert.Equal(new CraftingOutputResult(3, 1, 88), result.TargetOutput);
        Assert.Equal(12m, result.ExpectedFees);
        Assert.Equal(88m - result.ExpectedCost, result.ExpectedProfit);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity, 1.97m, 2.03m);
        // Optional base sales remain separate from the profit of the selected exact target.
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 1).NetValue!.Value, 33m, 37m);
        var repeat = new CraftingOutputTargetCalculator(catalog).Calculate(request);
        Assert.Equal(result.ExpectedCost, repeat.ExpectedCost);
        Assert.Equal(result.P90Cost, repeat.P90Cost);
        Assert.Equal(result.Shopping.ToArray(), repeat.Shopping.ToArray());
    }

    [Fact]
    public void SameItemDoubleComboBasketUsesActualOutputsAndKeepsTheSurplus()
    {
        var catalog = Catalog([Recipe(100, 1, (2, 1)) with { ComboRecipeId = 101, ComboActivationChance = .5m },
            Recipe(101, 3) with { ComboRecipeId = 102, ComboActivationChance = .5m },
            Recipe(102, 3) with { OutputQuantity = 2 }]);
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(Request() with
        { TargetOutputQuantity = 2, BuyPrices = Prices((2, 10)) });
        // One-item and two-item baskets occur equally after activation: E[N(2)]=1+.5=1.5
        // successful COMBO baskets, each requiring two starting attempts on average.
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, 29.6m, 30.4m);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 3).ExpectedQuantity, .245m, .255m);
        Assert.Equal(2m, result.TargetOutput!.ExpectedQuantity);
    }

    [Fact]
    public void PredecessorMustBeItsExactComboIdAndSharedOwnedStockIsFinite()
    {
        var catalog = Catalog([Recipe(100, 1, (5, 1), (2, 1)) with { ComboRecipeId = 101, ComboActivationChance = 1 },
            Recipe(101, 3), Recipe(200, 4, (2, 2)) with { ComboRecipeId = 201, ComboActivationChance = .5m },
            Recipe(201, 5)]);
        var request = Request() with
        {
            TargetOutputQuantity = 2,
            OwnedItems = new Dictionary<int, long> { [2] = 1, [3] = 10, [4] = 100 }.ToImmutableDictionary(),
            Acquisition = new Dictionary<int, CraftingAcquisition> { [5] = CraftingAcquisition.Craft }.ToImmutableDictionary(),
            RecipeChoices = new Dictionary<int, int> { [5] = 201 }.ToImmutableDictionary(), BuyPrices = Prices((2, 10))
        };
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        // Two exact precursor COMBOs: expected 4 precursor attempts, 8 raw + 2 final raw - 1 owned.
        Assert.InRange(result.ExpectedCost!.Value, 89.2m, 90.8m);
        Assert.Equal(2m, result.TargetOutput!.ExpectedQuantity);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts, 3.96m, 4.04m);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId is 3 or 4 or 5);
        Assert.DoesNotContain(result.Byproducts, row => row.ItemId == 3);
        var invalid = new CraftingOutputTargetCalculator(catalog).Calculate(request with
        { RecipeChoices = new Dictionary<int, int> { [5] = 200 }.ToImmutableDictionary() });
        Assert.Equal(CraftingPlanStatus.Invalid, invalid.Status);
        Assert.Contains(invalid.Issues, row => row.Code == "CraftRouteInvalid");
        Assert.Null(invalid.ExpectedCost);
    }

    [Fact]
    public void MissingBaseSalePriceDoesNotBlockTargetAndNegativeProfitIsKept()
    {
        var catalog = Catalog([Recipe(100, 1, (2, 1)) with { ComboRecipeId = 101, ComboActivationChance = .5m }, Recipe(101, 3)]);
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(Request() with { BuyPrices = Prices((2, 100)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.True(result.ExpectedProfit < 0);
        Assert.Null(result.Byproducts.Single(row => row.ItemId == 1).NetValue);
    }

    [Theory]
    [InlineData(true, "TargetPriceMissing")]
    [InlineData(false, "TargetNotTradable")]
    [InlineData(null, "TargetTradeabilityUnknown")]
    public void MissingTargetSaleKeepsCompletedAcquisitionCostsWithoutInventingRevenue(bool? tradable, string issue)
    {
        var initial = Catalog([Recipe(100, 1, (2, 1)) with { ComboRecipeId = 101, ComboActivationChance = 1 }, Recipe(101, 3)]);
        var catalog = new CraftingCatalog("synthetic", initial.Items.Select(item => item.Id == 3 ? item with { IsTradable = tradable } : item), initial.Recipes);
        var request = Request() with { SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty, BuyPrices = Prices((2, 10)) };
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status); Assert.Equal(issue, Assert.Single(result.Issues).Code);
        Assert.Equal(100_000, result.Samples); Assert.Equal(10m, result.ExpectedCost); Assert.Equal(10m, result.P90Cost);
        Assert.Equal(new CraftingOutputResult(3, 1, null), result.TargetOutput);
        Assert.Null(result.ExpectedProfit); Assert.Null(result.ExpectedFees); Assert.Single(result.Shopping);
        var covered = new CraftingOutputTargetCalculator(catalog).Calculate(request with
        { OwnedItems = new Dictionary<int, long> { [2] = 1 }.ToImmutableDictionary() });
        Assert.Equal(0m, covered.ExpectedCost); Assert.Empty(covered.Shopping); Assert.Equal(100_000, covered.Samples);
    }

    [Theory]
    [InlineData(.5, 60, 110)]
    [InlineData(1, 10, 10)]
    public void APrecursorsComboCanCompleteTheOuterTargetBeforeUnnecessaryRootMaterialsAreBought(decimal activation, decimal expected, decimal p90)
    {
        var catalog = Catalog([Recipe(100, 1, (4, 1), (2, 1)),
            Recipe(200, 2, (6, 1)) with { ComboRecipeId = 201, ComboActivationChance = activation }, Recipe(201, 1)]);
        var request = Request() with
        {
            RecipeId = 100, TargetItemId = 1, SellPrices = Prices((1, 100)), BuyPrices = Prices((4, 100), (6, 10)),
            Acquisition = new Dictionary<int, CraftingAcquisition> { [2] = CraftingAcquisition.Craft }.ToImmutableDictionary(),
            RecipeChoices = new Dictionary<int, int> { [2] = 200 }.ToImmutableDictionary()
        };
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, expected - .5m, expected + .5m);
        Assert.Equal(p90, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts);
        if (activation < 1)
        {
            Assert.InRange(result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts, .495m, .505m);
            Assert.InRange(result.Shopping.Single(row => row.ItemId == 4).ExpectedQuantity, .495m, .505m);
        }
        else
        {
            Assert.DoesNotContain(result.Steps, row => row.RecipeId == 100);
            Assert.DoesNotContain(result.Shopping, row => row.ItemId == 4);
        }
        Assert.Empty(result.Byproducts);
    }

    [Theory]
    [InlineData(.5, 61, 111)]
    [InlineData(1, 11, 11)]
    public void ADeeperComboCanCompleteAPendingIntermediateAndSkipItsUnnecessaryRecipe(decimal activation, decimal expected, decimal p90)
    {
        var catalog = Catalog([Recipe(100, 1, (3, 1), (7, 1)), Recipe(200, 3, (4, 1), (2, 1)),
            Recipe(300, 2, (6, 1)) with { ComboRecipeId = 301, ComboActivationChance = activation }, Recipe(301, 3)]);
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(Request() with
        {
            RecipeId = 100, TargetItemId = 1, SellPrices = Prices((1, 100)), BuyPrices = Prices((4, 100), (6, 10), (7, 1)),
            Acquisition = new Dictionary<int, CraftingAcquisition> { [2] = CraftingAcquisition.Craft, [3] = CraftingAcquisition.Craft }.ToImmutableDictionary(),
            RecipeChoices = new Dictionary<int, int> { [2] = 300, [3] = 200 }.ToImmutableDictionary()
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, expected - .5m, expected + .5m);
        Assert.Equal(p90, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 300).ExpectedAttempts);
        if (activation == 1) Assert.DoesNotContain(result.Steps, row => row.RecipeId == 200);
        else Assert.InRange(result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts, .495m, .505m);
        Assert.Empty(result.Byproducts);
    }

    [Fact]
    public void ZeroChanceCancellationAndLimitsNeverPublishPartialMetrics()
    {
        var recipe = Recipe(100, 1, (2, 1)) with { ComboRecipeId = 101, ComboActivationChance = 0 };
        var calculator = new CraftingOutputTargetCalculator(Catalog([recipe, Recipe(101, 3)]));
        var request = Request() with { BuyPrices = Prices((2, 10)) };
        Assert.Equal("TargetUnreachable", Assert.Single(calculator.Calculate(request).Issues).Code);
        Assert.Throws<OperationCanceledException>(() => calculator.Calculate(request, new CancellationToken(true)));
        var slow = new CraftingOutputTargetCalculator(Catalog([recipe with { ComboActivationChance = .5m }, Recipe(101, 3)]));
        var limit = slow.Calculate(request with { MaximumTotalAttempts = 1 });
        Assert.Equal(CraftingPlanStatus.LimitExceeded, limit.Status);
        Assert.Null(limit.ExpectedCost); Assert.Null(limit.P90Cost); Assert.Null(limit.TargetOutput); Assert.Empty(limit.Shopping);
        Assert.Equal(CraftingPlanStatus.Invalid, slow.Calculate(request with { TargetOutputQuantity = null }).Status);
    }

    private static CraftingCatalog Catalog(IEnumerable<CraftingRecipe> recipes) => new("synthetic",
        Enumerable.Range(1, 8).Select(id => new CraftingItem
        { Id = id, NameEn = "Synthetic " + id, NameDe = "Synthetisch " + id, IsTradable = true, IsEquipment = id != 2 }), recipes);
    private static CraftingRecipe Recipe(int id, int output, params (int Item, int Quantity)[] ingredients) => new()
    {
        Id = id, Profession = "Blacksmithing", Subcategory = "Greatsword", OutputItemId = output, OutputQuantity = 1,
        Ingredients = ingredients.Select(row => new CraftingIngredient { ItemId = row.Item, Quantity = row.Quantity }).ToImmutableArray(),
        SuccessRates = [1m], CraftingFee = 0, MechanicsQualified = true
    };
    private static CraftingPlanRequest Request() => new()
    { RecipeId = 101, TargetItemId = 3, TargetOutputQuantity = 1, SellPrices = Prices((3, 100)) };
    private static ImmutableDictionary<int, CraftingPrice> Prices(params (int Item, decimal Unit)[] rows) =>
        rows.ToImmutableDictionary(row => row.Item, row => new CraftingPrice(row.Item, row.Unit, CraftingPriceSource.Manual));
}
