using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CraftingCalculatorTests
{
    [Fact]
    public void BundledOwnCatalogLoadsWithoutQualifyingMissingMechanics()
    {
        var catalog = CraftingCatalog.Default;
        Assert.NotEmpty(catalog.Items); Assert.NotEmpty(catalog.Recipes);
        Assert.All(catalog.Recipes, recipe => Assert.NotNull(catalog.FindItem(recipe.OutputItemId)));
        Assert.Contains(catalog.Recipes, recipe => !recipe.MechanicsQualified);
        Assert.All(catalog.Recipes.Where(recipe => recipe.MechanicsQualified), recipe =>
            Assert.NotNull(recipe.SuccessRates));
        Assert.True(CraftingCatalog.IsFactionCompatible("Light", "elyos"));
        Assert.True(CraftingCatalog.IsFactionCompatible("Dark", "asmodians"));
        Assert.False(CraftingCatalog.IsFactionCompatible("Light", "asmodians"));
    }

    [Fact]
    public void SharedIntermediateInventoryIsUsedAcrossTheWholeChain()
    {
        var catalog = Catalog([
            Recipe(1, 1, 1, [(2, 2), (3, 1)]),
            Recipe(2, 2, 3, [(4, 3)]),
            Recipe(3, 3, 1, [(2, 3)])]);
        var result = new CraftingCalculator(catalog).Calculate(Request(1, 1) with
        {
            Samples = 100,
            Acquisition = new Dictionary<int, CraftingAcquisition> { [2] = CraftingAcquisition.Craft, [3] = CraftingAcquisition.Craft }.ToImmutableDictionary(),
            BuyPrices = Prices((4, 10))
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(60m, result.ExpectedCost); Assert.Equal(60m, result.P90Cost);
        Assert.Equal(6m, Assert.Single(result.Shopping).ExpectedQuantity);
        Assert.Equal(2m, result.Steps.Single(row => row.RecipeId == 2).ExpectedAttempts);
        Assert.Equal(1m, Assert.Single(result.Byproducts).ExpectedQuantity);
        Assert.Equal(28m, result.ExpectedProfit); // 100 less 12% fees, less all six raw units.
    }

    [Fact]
    public void SameItemComboYieldCountsEveryStartingAttemptAndValuesAllProducedUnits()
    {
        var basic = Recipe(1, 1, 1, [(2, 1)]) with { ComboRecipeId = 2, ComboActivationChance = .5m };
        var combo = Recipe(2, 1, 2, []);
        var calculator = new CraftingCalculator(Catalog([basic, combo]));
        var request = Request(1, 1) with { TargetAttempts = 3, BuyPrices = Prices((2, 10)) };
        var result = calculator.Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(3m, result.Steps.Single(row => row.RecipeId == 1).ExpectedAttempts);
        Assert.Equal(30m, result.ExpectedCost); Assert.Equal(30m, result.P90Cost);
        Assert.InRange(Assert.Single(result.Outputs).ExpectedQuantity, 4.48m, 4.52m);
        Assert.Empty(result.Byproducts);
        Assert.InRange(result.ExpectedNetRevenue!.Value, 394m, 398m);
        Assert.Equal(result.ExpectedNetRevenue - result.ExpectedCost, result.ExpectedProfit);
        var repeat = calculator.Calculate(request);
        Assert.Equal(result.ExpectedCost, repeat.ExpectedCost); Assert.Equal(result.P90Cost, repeat.P90Cost);
        Assert.Equal(result.Shopping.ToArray(), repeat.Shopping.ToArray());
        Assert.Equal(result.Byproducts.ToArray(), repeat.Byproducts.ToArray());
    }

    [Fact]
    public void SeparateComboFailureCountsAsOneStartingAttemptWithoutFallbackOrRetry()
    {
        var basic = Recipe(1, 1, 1, [(2, 1)]) with { ComboRecipeId = 2, ComboActivationChance = .4m };
        var combo = Recipe(2, 3, 1, []) with { SuccessRates = [.5m] };
        var result = new CraftingCalculator(Catalog([basic, combo])).Calculate(Request(2, 3) with { BuyPrices = Prices((2, 10)), SellPrices = Prices((1, 100), (3, 100)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(10m, result.ExpectedCost); Assert.Equal(10m, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 1).ExpectedAttempts);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 2).ExpectedAttempts, .395m, .405m);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 2).ExpectedFailures, .195m, .205m);
        Assert.InRange(result.Outputs.Single(row => row.ItemId == 1).ExpectedQuantity, .595m, .605m);
        Assert.InRange(result.Outputs.Single(row => row.ItemId == 3).ExpectedQuantity, .195m, .205m);
        Assert.InRange(result.ExpectedNetRevenue!.Value, 69.5m, 71.3m);
        Assert.Equal(result.Outputs.Sum(row => row.NetValue), result.ExpectedNetRevenue);
        Assert.Equal(result.ExpectedNetRevenue - 10m, result.ExpectedProfit);
        Assert.Empty(result.Byproducts);
    }

    [Fact]
    public void QualifiedProtectedTransferInputIsReusedAfterFailedAttempts()
    {
        var recipe = Recipe(1, 1, 1, [(2, 1), (3, 1)]) with { SuccessRates = [.5m], TransferItemIds = [2] };
        var result = new CraftingCalculator(Catalog([recipe])).Calculate(Request(1, 1) with { TargetAttempts = 4, BuyPrices = Prices((2, 100), (3, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.Shopping.Single(row => row.ItemId == 2).ExpectedQuantity, 2.48m, 2.52m);
        Assert.Equal(4m, result.Shopping.Single(row => row.ItemId == 3).ExpectedQuantity);
        Assert.InRange(result.ExpectedCost!.Value, 288m, 292m);
        Assert.Equal(440m, result.P90Cost);
    }

    [Fact]
    public void FailedStartingAttemptsConsumeOrdinaryMaterialsAndFeesWithoutAnOutputGuarantee()
    {
        var recipe = Recipe(1, 1, 2, [(2, 3)]) with { SuccessRates = [.5m], CraftingFee = 7 };
        var result = new CraftingCalculator(Catalog([recipe])).Calculate(Request(1, 1) with
        { TargetAttempts = 4, BuyPrices = Prices((2, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(148m, result.ExpectedCost); Assert.Equal(148m, result.P90Cost);
        Assert.Equal(12m, Assert.Single(result.Shopping).ExpectedQuantity);
        var step = Assert.Single(result.Steps);
        Assert.Equal(4m, step.ExpectedAttempts);
        Assert.InRange(step.ExpectedFailures, 1.98m, 2.02m);
        Assert.InRange(Assert.Single(result.Outputs).ExpectedQuantity, 3.96m, 4.04m);
        Assert.Equal(result.ExpectedNetRevenue - result.ExpectedCost, result.ExpectedProfit);
    }

    [Fact]
    public void ZeroSuccessChanceStillCompletesTheRequestedAttemptsAndConsumesMaterials()
    {
        var recipe = Recipe(1, 1, 1, [(2, 1)]) with { SuccessRates = [0m] };
        var result = new CraftingCalculator(Catalog([recipe])).Calculate(Request(1, 1) with
        { TargetAttempts = 3, Samples = 10, BuyPrices = Prices((2, 10)), SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(30m, result.ExpectedCost); Assert.Equal(30m, result.P90Cost);
        Assert.Equal(3m, Assert.Single(result.Steps).ExpectedFailures);
        Assert.Equal(0m, Assert.Single(result.Outputs).ExpectedQuantity);
        Assert.Equal(0m, result.ExpectedNetRevenue); Assert.Equal(-30m, result.ExpectedProfit);
    }

    [Fact]
    public void IntermediateFailuresAreRetriedWhileStartingAttemptsStayFixed()
    {
        var catalog = Catalog([Recipe(1, 1, 1, [(2, 1)]), Recipe(2, 2, 1, [(3, 1)]) with { SuccessRates = [.5m] }]);
        var result = new CraftingCalculator(catalog).Calculate(Request(1, 1) with
        {
            TargetAttempts = 2, BuyPrices = Prices((3, 10)),
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft)
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(2m, result.Steps.Single(row => row.RecipeId == 1).ExpectedAttempts);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 2).ExpectedAttempts, 3.97m, 4.03m);
        Assert.Equal(2m, Assert.Single(result.Outputs).ExpectedQuantity);
        Assert.InRange(result.ExpectedCost!.Value, 39.7m, 40.3m);
        Assert.Equal(70m, result.P90Cost);
    }

    [Fact]
    public void EveryPossibleStartingOutputNeedsItsOwnQualifiedSalePrice()
    {
        var basic = Recipe(1, 1, 1, [(2, 1)]) with { ComboRecipeId = 2, ComboActivationChance = .5m };
        var result = new CraftingCalculator(Catalog([basic, Recipe(2, 3, 1, [])])).Calculate(Request(1, 1) with
        { Samples = 1, BuyPrices = Prices((2, 10)) });
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status);
        Assert.Equal(new("OutputPriceMissing", 3), Assert.Single(result.Issues));
        Assert.Null(result.ExpectedNetRevenue); Assert.Null(result.ExpectedProfit);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public void FeesAreAveragedAfterRoundingEachSimulatedOutputBatch()
    {
        var recipe = Recipe(1, 1, 1, []) with { SuccessRates = [.5m] };
        var result = new CraftingCalculator(Catalog([recipe])).Calculate(Request(1, 1) with
        { SellPrices = Prices((1, 1m)), SalesTaxPercent = 10, RegistrationFeePercent = 0 });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(0m, result.ExpectedNetRevenue);
        Assert.InRange(result.ExpectedFees!.Value, .49m, .51m);
    }

    [Fact]
    public void IngredientReplacementOwnedStockAndNpcBundlesAreAppliedTogether()
    {
        var recipe = Recipe(1, 1, 1, []) with
        {
            Ingredients = [new() { ItemId = 2, Quantity = 2, ReplacementItemIds = [3] }]
        };
        var catalog = Catalog([recipe], [new() { ItemId = 3, UnitPrice = 4, BundleQuantity = 5, ConditionsQualified = true, Faction = "Light" }]);
        var result = new CraftingCalculator(catalog).Calculate(Request(1, 1) with
        {
            Samples = 20, Faction = "elyos", OwnedItems = new Dictionary<int, long> { [3] = 1 }.ToImmutableDictionary(),
            ReplacementChoices = new Dictionary<CraftingIngredientKey, int> { [new(1, 2)] = 3 }.ToImmutableDictionary()
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(20m, result.ExpectedCost);
        Assert.Equal(5m, Assert.Single(result.Shopping).ExpectedQuantity);
        Assert.Equal(CraftingPriceSource.Npc, Assert.Single(result.Shopping).Source);
    }

    [Theory]
    [InlineData("mechanics")]
    [InlineData("rate")]
    [InlineData("input-price")]
    [InlineData("sell-price")]
    [InlineData("npc-condition")]
    public void UnknownInputsNeverProducePartialOrZeroFinancialMetrics(string missing)
    {
        var recipe = Recipe(1, 1, 1, [(2, 1)]);
        recipe = missing switch { "mechanics" => recipe with { MechanicsQualified = false }, "rate" => recipe with { SuccessRates = null, MechanicsQualified = false }, _ => recipe };
        var catalog = Catalog([recipe], [new() { ItemId = 2, UnitPrice = 10, ConditionsQualified = false }]);
        var request = Request(1, 1) with { Samples = 1, BuyPrices = Prices((2, 10)) };
        if (missing is "input-price" or "npc-condition") request = request with { BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty };
        if (missing == "sell-price") request = request with { SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty };
        var result = new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status);
        Assert.Null(result.ExpectedCost); Assert.Null(result.P90Cost); Assert.Null(result.ExpectedProfit);
        Assert.Empty(result.Shopping); Assert.Empty(result.Steps); Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void SimulationLimitsAndCancellationCannotPublishPartialMetrics()
    {
        var calculator = new CraftingCalculator(Catalog([Recipe(1, 1, 1, [(2, 1)])]));
        var request = Request(1, 1) with { Samples = 10, BuyPrices = Prices((2, 1)), MaximumTotalAttempts = 1 };
        var result = calculator.Calculate(request);
        Assert.Equal(CraftingPlanStatus.LimitExceeded, result.Status); Assert.Null(result.ExpectedCost);
        Assert.Throws<OperationCanceledException>(() => calculator.Calculate(request, new CancellationToken(true)));
    }

    [Fact]
    public void CraftDependencyCycleAndInvalidSubstituteHaveNoMetrics()
    {
        var catalog = Catalog([Recipe(1, 1, 1, [(2, 1)]), Recipe(2, 2, 1, [(1, 1)])]);
        var request = Request(1, 1) with { Samples = 1, Acquisition = new Dictionary<int, CraftingAcquisition> { [2] = CraftingAcquisition.Craft }.ToImmutableDictionary() };
        var result = new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Invalid, result.Status); Assert.Contains(result.Issues, row => row.Code == "RecipeCycle");
        result = new CraftingCalculator(catalog).Calculate(request with
        {
            ReplacementChoices = new Dictionary<CraftingIngredientKey, int> { [new(1, 2)] = 4 }.ToImmutableDictionary()
        });
        Assert.Equal(CraftingPlanStatus.Invalid, result.Status); Assert.Null(result.ExpectedProfit);
    }

    [Fact]
    public void ProficiencyUsesItsExactCurveIndexAndNeverExtrapolates()
    {
        var recipe = Recipe(1, 1, 1, []) with { RequiredProficiency = 20, SuccessRates = [.7m, .8m] };
        var catalog = Catalog([recipe]);
        Assert.True(catalog.TrySuccessChance(recipe, new Dictionary<string, int> { ["Blacksmithing"] = 21 }, out var rate));
        Assert.Equal(.8m, rate);
        Assert.False(catalog.TrySuccessChance(recipe, new Dictionary<string, int> { ["Blacksmithing"] = 19 }, out _));
        Assert.False(catalog.TrySuccessChance(recipe, new Dictionary<string, int> { ["Blacksmithing"] = 22 }, out _));
    }

    [Fact]
    public void PricesFromDifferentMarketsCannotCreateAProfit()
    {
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var request = Request(1, 1) with
        {
            Samples = 1,
            BuyPrices = new Dictionary<int, CraftingPrice> { [2] = new(2, 10, CraftingPriceSource.Exchange, at, 2) }.ToImmutableDictionary(),
            SellPrices = new Dictionary<int, CraftingPrice> { [1] = new(1, 100, CraftingPriceSource.Exchange, at, 1) }.ToImmutableDictionary()
        };
        var result = new CraftingCalculator(Catalog([Recipe(1, 1, 1, [(2, 1)])])).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status);
        Assert.Contains(result.Issues, row => row.Code == "MarketMismatch"); Assert.Null(result.ExpectedProfit);
    }

    [Theory]
    [InlineData(null, "ProficiencyUnknown")]
    [InlineData(19, "ProficiencyTooLow")]
    [InlineData(22, "SuccessRateUnavailable")]
    public void ProficiencyAndCurveGapsHaveTheirOwnExplanation(int? level, string code)
    {
        var recipe = Recipe(1, 1, 1, []) with { RequiredProficiency = 20, SuccessRates = [.7m, .8m] };
        var request = Request(1, 1) with { Samples = 1, ProficiencyLevels = level is { } number
            ? new Dictionary<string, int> { ["Blacksmithing"] = number }.ToImmutableDictionary()
            : ImmutableDictionary<string, int>.Empty };
        var result = new CraftingCalculator(Catalog([recipe])).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status); Assert.Equal(code, Assert.Single(result.Issues).Code);
        Assert.Null(result.ExpectedCost); Assert.Null(result.ExpectedProfit);
    }

    [Fact]
    public void CatalogRejectsAmbiguousInputsMissingQualifiedRatesAndRemoteIcons()
    {
        Assert.Throws<InvalidDataException>(() => Catalog([Recipe(1, 1, 1, [(2, 1), (2, 2)])]));
        Assert.Throws<InvalidDataException>(() => Catalog([Recipe(1, 1, 1, []) with { SuccessRates = null }]));
        Assert.Throws<InvalidDataException>(() => new CraftingCatalog("synthetic",
            [new() { Id = 1, NameEn = "Synthetic", NameDe = "Synthetisch", Icon = "https://invalid.example/icon.png" }], []));
        Assert.Throws<InvalidDataException>(() => CraftingCatalog.FromJson("{\"schema\":2}"));
        Assert.Throws<InvalidDataException>(() => Catalog([Recipe(1, 1, 1, []) with { ComboRecipeId = 2, ComboActivationChance = .5m },
            Recipe(2, 3, 1, []) with { ComboRecipeId = 1, ComboActivationChance = .5m }]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QualifiedComboCannotHideAnAdditionalFeeOrMaterialCost(bool additionalFee)
    {
        var basic = Recipe(1, 1, 1, [(2, 1)]) with { ComboRecipeId = 2, ComboActivationChance = .5m };
        var combo = additionalFee ? Recipe(2, 3, 1, []) with { CraftingFee = 1 }
            : Recipe(2, 3, 1, [(4, 1)]);
        Assert.Throws<InvalidDataException>(() => Catalog([basic, combo]));
    }

    private static CraftingCatalog Catalog(IEnumerable<CraftingRecipe> recipes, IEnumerable<CraftingNpcOffer>? offers = null) =>
        new("synthetic", Enumerable.Range(1, 8).Select(id => new CraftingItem { Id = id, NameEn = "Synthetic " + id, NameDe = "Synthetisch " + id, IsTradable = true }), recipes, offers);
    private static CraftingRecipe Recipe(int id, int item, int quantity, (int Item, int Quantity)[] ingredients) => new()
    {
        Id = id, Profession = "Blacksmithing", Subcategory = "Materials", OutputItemId = item, OutputQuantity = quantity,
        Ingredients = ingredients.Select(row => new CraftingIngredient { ItemId = row.Item, Quantity = row.Quantity }).ToImmutableArray(),
        SuccessRates = [1m], CraftingFee = 0, MechanicsQualified = true
    };
    private static CraftingPlanRequest Request(int recipe, int target) => new()
    {
        RecipeId = recipe, TargetItemId = target, SellPrices = Prices((target, 100)),
        ProficiencyLevels = ImmutableDictionary<string, int>.Empty
    };
    private static ImmutableDictionary<int, CraftingPrice> Prices(params (int Id, decimal UnitPrice)[] rows) =>
        rows.ToImmutableDictionary(row => row.Id, row => new CraftingPrice(row.Id, row.UnitPrice, CraftingPriceSource.Manual));
}
