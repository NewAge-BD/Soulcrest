using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class TransferCraftingCalculatorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitTransferGuaranteeDoesNotRewriteNativeRatesAndNeverCreatesASaleValuation()
    {
        var root = Transfer(100, 1, [2], (2, 1), (3, 1)) with { ComboRecipeId = 101, ComboActivationChance = .25m };
        var combo = Transfer(101, 5, []) with { IsTransferCrafting = null };
        var catalog = Catalog([root, combo], [1, 2, 5]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        { OwnedItems = Owned((2, 1)), BuyPrices = Prices((3, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(10m, result.ExpectedCost); Assert.Equal(10m, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity, .745m, .755m);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 5).ExpectedQuantity, .245m, .255m);
        Assert.Equal(1m, result.Byproducts.Sum(row => row.ExpectedQuantity));
        Assert.All(result.Steps, row => Assert.Equal(1m, row.SuccessChance));
        Assert.Null(root.SuccessRates); Assert.Null(combo.SuccessRates);
        Assert.Equal("user-confirmed-transfer-crafting-2026-11-10", TransferCraftingRules.PolicyGuaranteeSource);
        NoSaleFields(result);
        Assert.Null(typeof(TransferCraftingPlanRequest).GetProperty("SellPrices"));
    }

    [Fact]
    public void OwnedGearIsOneFiniteCopyAndFurtherTransfersCraftOnlyTheDeficit()
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 1)), Normal(200, 2, 1, (4, 2))], [1, 2]);
        var request = Request() with
        {
            TargetQuantity = 3, Samples = 100,
            OwnedItems = Owned((2, 1)), BuyPrices = Prices((2, 999999), (3, 10), (4, 20)),
            Acquisition = new Dictionary<int, CraftingAcquisition> { [2] = CraftingAcquisition.Buy }.ToImmutableDictionary()
        };
        var result = new TransferCraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(110m, result.ExpectedCost); Assert.Equal(110m, result.P90Cost);
        Assert.Equal(3m, result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity);
        Assert.Equal(2m, result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts);
        Assert.Equal(4m, result.Shopping.Single(row => row.ItemId == 4).ExpectedQuantity);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId == 2);
        NoSaleFields(result);
    }

    [Theory]
    [InlineData(3, 5)]
    [InlineData(7, 1)]
    [InlineData(20, 0)]
    public void OwnedRawMaterialsAreFiniteAcrossNestedCraftsAndOnlyTheDeficitCostsCash(long owned, long purchased)
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 3)), Normal(200, 2, 1, (3, 2))], [1, 2]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        { TargetQuantity = 2, Samples = 100, OwnedItems = Owned((2, 1), (3, owned)), BuyPrices = Prices((3, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(purchased * 10m, result.ExpectedCost); Assert.Equal(purchased * 10m, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts);
        if (purchased == 0) Assert.Empty(result.Shopping);
        else Assert.Equal((decimal)purchased, Assert.Single(result.Shopping).ExpectedQuantity);
        Assert.Equal(2m, Assert.Single(result.Byproducts).ExpectedQuantity);
    }

    [Fact]
    public void IntermediateGearAndItsSharedBatchRemainderAreUsedAcrossTheWholePlan()
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 1)), Transfer(200, 2, [5], (5, 1), (4, 1)),
            Normal(300, 5, 3, (6, 3))], [1, 2, 5]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        { TargetQuantity = 2, Samples = 100, BuyPrices = Prices((3, 2), (4, 5), (6, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(44m, result.ExpectedCost); Assert.Equal(44m, result.P90Cost);
        Assert.Equal(1m, result.Steps.Single(row => row.RecipeId == 300).ExpectedAttempts);
        Assert.Equal(1m, result.Byproducts.Single(row => row.ItemId == 5).ExpectedQuantity);
        Assert.Equal(2m, result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId is 1 or 2 or 5);
    }

    [Fact]
    public void NormalProducerFailureAndComboReplacementRemainStochasticAndConsumeEveryAttempt()
    {
        var basic = Normal(200, 2, 1, (4, 1)) with { SuccessRates = [.5m], ComboRecipeId = 201, ComboActivationChance = .5m };
        var combo = Normal(201, 5, 1) with { SuccessRates = [.5m] };
        var catalog = Catalog([Transfer(100, 1, [5], (5, 1), (3, 1)), basic, combo], [1, 2, 5]);
        var request = Request() with { BuyPrices = Prices((3, 10), (4, 10)) };
        var calculator = new TransferCraftingCalculator(catalog);
        var result = calculator.Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, 89.1m, 90.9m); Assert.Equal(190m, result.P90Cost);
        Assert.InRange(result.Shopping.Single(row => row.ItemId == 4).ExpectedQuantity, 7.91m, 8.09m);
        Assert.InRange(result.Steps.Single(row => row.RecipeId == 201).ExpectedAttempts, 1.97m, 2.03m);
        Assert.InRange(result.Byproducts.Single(row => row.ItemId == 2).ExpectedQuantity, 1.97m, 2.03m);
        var repeat = calculator.Calculate(request);
        Assert.Equal(result.ExpectedCost, repeat.ExpectedCost); Assert.Equal(result.P90Cost, repeat.P90Cost);
        Assert.Equal(result.Shopping.ToArray(), repeat.Shopping.ToArray()); Assert.Equal(result.Byproducts.ToArray(), repeat.Byproducts.ToArray());
        NoSaleFields(result);
    }

    [Fact]
    public void ExplicitlyNormalFailurePreservesOnlyProtectedGearAndConsumesOrdinaryMaterials()
    {
        var root = Normal(100, 1, 1, (2, 1), (3, 1)) with { TransferItemIds = [2], SuccessRates = [.5m] };
        var result = new TransferCraftingCalculator(Catalog([root], [1, 2])).Calculate(Request() with
        { OwnedItems = Owned((2, 1)), BuyPrices = Prices((3, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, 19.8m, 20.2m); Assert.Equal(40m, result.P90Cost);
        Assert.InRange(Assert.Single(result.Shopping).ExpectedQuantity, 1.98m, 2.02m);
        Assert.Equal(.5m, Assert.Single(result.Steps).SuccessChance);
        Assert.Equal(1m, Assert.Single(result.Byproducts).ExpectedQuantity);
        NoSaleFields(result);
    }

    [Fact]
    public void P90UsesTheJointCostDistributionRatherThanAddingLocalP90Budgets()
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (5, 1), (3, 1)),
            Normal(200, 2, 1, (4, 1)) with { SuccessRates = [.5m] },
            Normal(300, 5, 1, (6, 1)) with { SuccessRates = [.5m] }], [1, 2, 5]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with { BuyPrices = Prices((3, 1), (4, 10), (6, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.InRange(result.ExpectedCost!.Value, 40.6m, 41.4m);
        Assert.Equal(71m, result.P90Cost); // Sum of two local geometric P90 values would be 81.
    }

    [Fact]
    public void NewlyMadeAlternativeFromACommonComboSkipsTheUnnecessaryIntermediateTransfer()
    {
        var root = Transfer(100, 1, [2, 3], (2, 1), (4, 1)) with
        { Ingredients = [new() { ItemId = 2, Quantity = 1, ReplacementItemIds = [3] }, new() { ItemId = 4, Quantity = 1 }] };
        var intermediate = Transfer(200, 2, [5], (5, 1), (6, 1));
        var common = Normal(300, 5, 1, (7, 1)) with { ComboRecipeId = 301, ComboActivationChance = 1m };
        var catalog = Catalog([root, intermediate, common, Normal(301, 3, 1)], [1, 2, 3, 5]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        {
            TargetQuantity = 2, Samples = 100, OwnedItems = Owned((3, 1)),
            ReplacementChoices = Replacements((100, 2, 2)), BuyPrices = Prices((4, 100), (6, 1000), (7, 10))
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(220m, result.ExpectedCost);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId == 6);
        Assert.DoesNotContain(result.Steps, row => row.RecipeId == 200);
        Assert.Equal(2m, result.Steps.Single(row => row.RecipeId == 300).ExpectedAttempts);
        Assert.DoesNotContain(result.Byproducts, row => row.ItemId == 3); // Existing unselected gear was not consumed instead of new gear.
        Assert.Equal(2m, Assert.Single(result.Byproducts).ExpectedQuantity);
    }

    [Fact]
    public void ProducerTransferComboThatAlreadyMakesTheGoalSkipsAllRemainingOuterCosts()
    {
        var root = Transfer(100, 1, [2], (2, 1), (4, 1));
        var intermediate = Transfer(200, 2, [5], (5, 1), (6, 1)) with { ComboRecipeId = 201, ComboActivationChance = 1m };
        var catalog = Catalog([root, intermediate, Transfer(201, 1, []), Normal(300, 5, 1, (7, 1))], [1, 2, 5]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        { TargetQuantity = 2, Samples = 100, OwnedItems = Owned((1, 1)), BuyPrices = Prices((4, 1000), (6, 20), (7, 10)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(60m, result.ExpectedCost); Assert.Equal(60m, result.P90Cost);
        Assert.DoesNotContain(result.Shopping, row => row.ItemId == 4);
        Assert.DoesNotContain(result.Steps, row => row.RecipeId == 100);
        Assert.Equal(2m, result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity); // Owned target is not counted.
        Assert.Equal(2m, result.Steps.Single(row => row.RecipeId == 200).ExpectedAttempts);
    }

    [Fact]
    public void ProtectedTransferGearPrecedesLowerIdOrdinaryEquipmentWhenItsComboAlreadyMakesTheGoal()
    {
        var root = Transfer(100, 1, [5], (2, 1), (5, 1), (4, 1));
        var intermediate = Transfer(200, 5, [6], (6, 1), (7, 1)) with { ComboRecipeId = 201, ComboActivationChance = 1m };
        var expensiveOrdinaryGear = Normal(300, 2, 1, (8, 1));
        var catalog = Catalog([root, intermediate, Transfer(201, 1, []), expensiveOrdinaryGear], [1, 2, 5, 6]);
        var result = new TransferCraftingCalculator(catalog).Calculate(Request() with
        { Samples = 100, OwnedItems = Owned((6, 1)), BuyPrices = Prices((4, 1000), (7, 10), (8, 1000000)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(10m, result.ExpectedCost); Assert.Equal(10m, result.P90Cost);
        Assert.DoesNotContain(result.Steps, row => row.RecipeId is 100 or 300);
        Assert.Equal(7, Assert.Single(result.Shopping).ItemId);
        Assert.Equal(1m, Assert.Single(result.Byproducts).ExpectedQuantity);
    }

    [Fact]
    public void ExistingUnselectedAlternativeIsNotAutomaticallyUsedAndExplicitChoiceConsumesExactlyOneGear()
    {
        var root = Transfer(100, 1, [2, 5], (2, 1), (3, 1)) with
        { Ingredients = [new() { ItemId = 2, Quantity = 1, ReplacementItemIds = [5] }, new() { ItemId = 3, Quantity = 1 }] };
        var calculator = new TransferCraftingCalculator(Catalog([root], [1, 2, 5]));
        var request = Request() with { Samples = 100, OwnedItems = Owned((5, 1)), BuyPrices = Prices((2, 1), (3, 10)), ReplacementChoices = Replacements((100, 2, 2)) };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "CraftRouteMissing");
        var result = calculator.Calculate(request with { ReplacementChoices = Replacements((100, 2, 5)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(10m, result.ExpectedCost);
        Assert.Single(result.Shopping); Assert.Equal(3, result.Shopping[0].ItemId);
    }

    [Fact]
    public void MultipleTransferGearCandidatesRequireAnExplicitValidSelection()
    {
        var root = Transfer(100, 1, [2, 5], (2, 1)) with
        { Ingredients = [new() { ItemId = 2, Quantity = 1, ReplacementItemIds = [5] }] };
        var calculator = new TransferCraftingCalculator(Catalog([root], [1, 2, 5]));
        var request = Request() with { OwnedItems = Owned((2, 1)) };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "TransferGearSelectionMissing");
        AssertIssue(calculator.Calculate(request with { ReplacementChoices = Replacements((100, 2, 7)) }), CraftingPlanStatus.Invalid, "ReplacementInvalid");
    }

    [Fact]
    public void GearWithMultipleExactProducersNeedsAnExplicitRecipeWhileOneProducerIsAutomatic()
    {
        var root = Transfer(100, 1, [2], (2, 1), (3, 1));
        var first = Normal(200, 2, 1, (4, 1)); var second = Normal(201, 2, 1, (4, 2));
        var opposite = Normal(202, 2, 1, (4, 3)) with { Faction = "Dark" };
        var catalog = Catalog([root, opposite, second, first], [1, 2]);
        Assert.Equal(new[] { 200, 201 }, TransferCraftingRules.ProducerChoices(catalog, 2, "Elyos").Select(row => row.Id));
        var request = Request() with { Samples = 100, BuyPrices = Prices((3, 10), (4, 20)) };
        var calculator = new TransferCraftingCalculator(catalog);
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "CraftRouteAmbiguous");
        var result = calculator.Calculate(request with { RecipeChoices = new Dictionary<int, int> { [2] = 201 }.ToImmutableDictionary() });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(50m, result.ExpectedCost);
        AssertIssue(calculator.Calculate(request with { RecipeChoices = new Dictionary<int, int> { [2] = 100 }.ToImmutableDictionary() }),
            CraftingPlanStatus.Invalid, "CraftRouteInvalid");
    }

    [Fact]
    public void APriceForGearCannotFillAMissingProducerAndOwnedTargetCannotBecomeAGratisGoal()
    {
        var calculator = new TransferCraftingCalculator(Catalog([Transfer(100, 1, [2], (2, 1), (3, 1))], [1, 2]));
        var request = Request() with { OwnedItems = Owned((1, 1)), BuyPrices = Prices((2, 1), (3, 10)) };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "CraftRouteMissing");
        AssertIssue(calculator.Calculate(request with { OwnedItems = Owned((2, 2)) }), CraftingPlanStatus.Invalid, "InvalidRequest");
        var result = calculator.Calculate(request with { OwnedItems = Owned((1, 1), (2, 1)), Samples = 100 });
        Assert.Equal(10m, result.ExpectedCost); Assert.Equal(1m, result.Byproducts.Single(row => row.ItemId == 1).ExpectedQuantity);
    }

    [Fact]
    public void UnqualifiedNormalPrecursorDoesNotInheritTheTransferGuaranteeButOwnedGearCanBypassItsAcquisition()
    {
        var unknown = Normal(200, 2, 1, (4, 1)) with { MechanicsQualified = false, SuccessRates = null };
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 1)), unknown], [1, 2]);
        var calculator = new TransferCraftingCalculator(catalog); var request = Request() with { BuyPrices = Prices((3, 10), (4, 20)) };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "MechanicsUnqualified");
        var result = calculator.Calculate(request with { OwnedItems = Owned((2, 1)), Samples = 100 });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(10m, result.ExpectedCost);
    }

    [Fact]
    public void ZeroChancePrecursorAndRealDependencyCyclesTerminateWithoutFinancialMetrics()
    {
        var root = Transfer(100, 1, [2], (2, 1));
        var zero = Normal(200, 2, 1, (4, 1)) with { SuccessRates = [0m] };
        AssertIssue(new TransferCraftingCalculator(Catalog([root, zero], [1, 2])).Calculate(Request() with { BuyPrices = Prices((4, 1)) }),
            CraftingPlanStatus.Unknown, "TargetUnreachable");
        var cycle = Normal(200, 2, 1, (1, 1));
        AssertIssue(new TransferCraftingCalculator(Catalog([root, cycle], [1, 2])).Calculate(Request()), CraftingPlanStatus.Invalid, "RecipeCycle");
    }

    [Fact]
    public void QualifiedNpcBundlesReplacementsAndOwnedMaterialsShareOneInventory()
    {
        var root = Transfer(100, 1, [2], (2, 1), (3, 2)) with
        { Ingredients = [new() { ItemId = 2, Quantity = 1 }, new() { ItemId = 3, Quantity = 2, ReplacementItemIds = [4] }] };
        CraftingNpcOffer[] offers = [new() { ItemId = 4, UnitPrice = 4, BundleQuantity = 5, ConditionsQualified = true, Faction = "Light" },
            new() { ItemId = 4, UnitPrice = .1m, ConditionsQualified = false }, new() { ItemId = 4, UnitPrice = 1, ConditionsQualified = true, Faction = "Dark" }];
        var result = new TransferCraftingCalculator(Catalog([root], [1, 2], offers)).Calculate(Request() with
        { Samples = 100, OwnedItems = Owned((2, 1), (4, 1)), ReplacementChoices = Replacements((100, 3, 4)) });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status); Assert.Equal(20m, result.ExpectedCost);
        Assert.Equal(5m, Assert.Single(result.Shopping).ExpectedQuantity); Assert.Equal(CraftingPriceSource.Npc, result.Shopping[0].Source);
        Assert.Equal(4m, result.Byproducts.Single(row => row.ItemId == 4).ExpectedQuantity);
    }

    [Theory]
    [InlineData("mixed-market", "MarketMismatch")]
    [InlineData("average-window", "PriceInvalid")]
    [InlineData("average-samples", "PriceInvalid")]
    [InlineData("npc-condition", "NpcConditionsUnqualified")]
    public void InvalidOrIncompatiblePriceEvidenceNeverProducesPartialCosts(string variant, string code)
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 1), (4, 1))], [1, 2]);
        var average = new CraftingPrice(3, 10, CraftingPriceSource.ExchangeAverage, At, 1)
        { AverageRangeStart = At.AddHours(-24), AverageSamples = 10 };
        var prices = Prices((3, 10), (4, 10));
        prices = variant switch
        {
            "mixed-market" => prices.SetItem(3, new(3, 10, CraftingPriceSource.Exchange, At, 1)).SetItem(4, new(4, 10, CraftingPriceSource.Exchange, At, 2)),
            "average-window" => prices.SetItem(3, average with { AverageRangeStart = At.AddHours(-23) }),
            "average-samples" => prices.SetItem(3, average with { AverageSamples = 0 }),
            _ => prices.SetItem(3, new(3, 10, CraftingPriceSource.Npc))
        };
        AssertIssue(new TransferCraftingCalculator(catalog).Calculate(Request() with { OwnedItems = Owned((2, 1)), BuyPrices = prices }),
            CraftingPlanStatus.Unknown, code);
    }

    [Fact]
    public void ValidRollingAverageCanPriceMaterialsButNeverEquipment()
    {
        var average = new CraftingPrice(3, 10, CraftingPriceSource.ExchangeAverage, At, 1)
        { AverageRangeStart = At.AddHours(-24), AverageSamples = 10 };
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 1))], [1, 2]);
        var request = Request() with { Samples = 100, OwnedItems = Owned((2, 1)), BuyPrices = Prices().SetItem(3, average) };
        var calculator = new TransferCraftingCalculator(catalog);
        Assert.Equal(10m, calculator.Calculate(request).ExpectedCost);
        AssertIssue(calculator.Calculate(request with { OwnedItems = Owned(), BuyPrices = request.BuyPrices.SetItem(2, average with { ItemId = 2 }) }),
            CraftingPlanStatus.Unknown, "CraftRouteMissing");
    }

    [Fact]
    public void MissingInputBudgetLimitsCancellationAndOverflowCannotExposePartialMetrics()
    {
        var catalog = Catalog([Transfer(100, 1, [2], (2, 1), (3, 2))], [1, 2]);
        var calculator = new TransferCraftingCalculator(catalog); var request = Request() with { OwnedItems = Owned((2, 1)) };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.Unknown, "InputPriceMissing");
        request = request with { Samples = 2, BuyPrices = Prices((3, 10)), MaximumTotalAttempts = 1 };
        AssertIssue(calculator.Calculate(request), CraftingPlanStatus.LimitExceeded, "SimulationLimit");
        Assert.Throws<OperationCanceledException>(() => calculator.Calculate(request, new CancellationToken(true)));
        AssertIssue(calculator.Calculate(request with { BuyPrices = Prices((3, decimal.MaxValue)), MaximumTotalAttempts = 100 }),
            CraftingPlanStatus.LimitExceeded, "NumericLimit");
    }

    [Fact]
    public void ExplicitNativeClassificationWinsAndLegacyProtectedIdsRemainSupported()
    {
        var root = Transfer(100, 1, [2], (2, 1));
        var catalog = Catalog([root], [1, 2]);
        Assert.True(TransferCraftingRules.IsEquipment(catalog, 2)); Assert.False(TransferCraftingRules.IsEquipment(catalog, 3));
        var legacyItems = catalog.Items.Select(item => item.Id == 2 ? item with { IsEquipment = null } : item);
        var legacy = new CraftingCatalog("synthetic", legacyItems, [root with { IsTransferCrafting = null }]);
        Assert.True(TransferCraftingRules.IsTransfer(legacy, legacy.FindRecipe(100)!));
        Assert.True(TransferCraftingRules.IsEquipment(legacy, 2));
        var explicitNormal = new CraftingCatalog("synthetic", catalog.Items, [root with { IsTransferCrafting = false }]);
        Assert.False(TransferCraftingRules.IsTransfer(explicitNormal, explicitNormal.FindRecipe(100)!));
        AssertIssue(new TransferCraftingCalculator(explicitNormal).Calculate(Request() with { OwnedItems = Owned((2, 1)) }),
            CraftingPlanStatus.Unknown, "MechanicsUnqualified");
    }

    private static TransferCraftingPlanRequest Request() => new()
    { RecipeId = 100, Faction = "Elyos", ProficiencyLevels = new Dictionary<string, int> { ["Blacksmithing"] = 100 }.ToImmutableDictionary() };
    private static CraftingRecipe Normal(int id, int output, int quantity, params (int Item, int Quantity)[] inputs) => new()
    {
        Id = id, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Light", OutputItemId = output,
        OutputQuantity = quantity, CraftingFee = 0, SuccessRates = [1m], MechanicsQualified = true, IsTransferCrafting = false,
        Ingredients = inputs.Select(row => new CraftingIngredient { ItemId = row.Item, Quantity = row.Quantity }).ToImmutableArray()
    };
    private static CraftingRecipe Transfer(int id, int output, int[] protectedIds, params (int Item, int Quantity)[] inputs) =>
        Normal(id, output, 1, inputs) with { IsTransferCrafting = true, RequiredProficiency = 0,
            MechanicsQualified = false, SuccessRates = null, TransferItemIds = protectedIds.ToImmutableArray() };
    private static CraftingCatalog Catalog(IEnumerable<CraftingRecipe> recipes, int[] gear, IEnumerable<CraftingNpcOffer>? offers = null) =>
        new("synthetic", Enumerable.Range(1, 30).Select(id => new CraftingItem { Id = id, NameEn = "Synthetic item " + id,
            NameDe = "Synthetisches Item " + id, IsEquipment = gear.Contains(id), IsTradable = !gear.Contains(id) }), recipes, offers);
    private static ImmutableDictionary<int, long> Owned(params (int Item, long Quantity)[] rows) => rows.ToImmutableDictionary(row => row.Item, row => row.Quantity);
    private static ImmutableDictionary<int, CraftingPrice> Prices(params (int Item, decimal Unit)[] rows) =>
        rows.ToImmutableDictionary(row => row.Item, row => new CraftingPrice(row.Item, row.Unit, CraftingPriceSource.Manual));
    private static ImmutableDictionary<CraftingIngredientKey, int> Replacements(params (int Recipe, int Ingredient, int Selected)[] rows) =>
        rows.ToImmutableDictionary(row => new CraftingIngredientKey(row.Recipe, row.Ingredient), row => row.Selected);
    private static void NoSaleFields(CraftingPlanResult result)
    {
        foreach (var property in typeof(CraftingPlanResult).GetProperties().Where(property =>
            property.Name.Contains("Revenue", StringComparison.Ordinal) || property.Name.Contains("Profit", StringComparison.Ordinal)
            || property.Name.Contains("Fees", StringComparison.Ordinal))) Assert.Null(property.GetValue(result));
        Assert.All(result.Byproducts, row => Assert.Null(row.NetValue));
    }
    private static void AssertIssue(CraftingPlanResult result, CraftingPlanStatus status, string code)
    {
        Assert.Equal(status, result.Status); Assert.Contains(result.Issues, issue => issue.Code == code);
        Assert.Null(result.ExpectedCost); Assert.Null(result.P90Cost); Assert.Empty(result.Shopping); Assert.Empty(result.Steps);
        Assert.Empty(result.Byproducts); NoSaleFields(result);
    }
}
