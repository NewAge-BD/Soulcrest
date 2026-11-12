using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingDependencyMapProjectionTests
{
    [Theory]
    [InlineData(4, 80, false)]
    [InlineData(5, 100, true)]
    public void SharedIngredientUsesWholePlanDemandAndCountsManualStockOnce(long stock, int coverage, bool complete)
    {
        var catalog = Catalog(Recipe(100, 1, Input(2), Input(10, 2)), Recipe(200, 2, Input(10, 3)));
        var state = State(100, 1) with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(2, 200),
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(10, stock).Add(1, 100)
        };
        var map = CraftingDependencyMapProjection.Build(catalog, Calculate(catalog, state));
        var shared = Assert.Single(map.Nodes, node => node.ItemId == 10);
        Assert.True(map.QuantitiesKnown);
        Assert.Equal(5m, shared.RequiredQuantity);
        Assert.Equal((decimal)stock, shared.OwnedQuantity);
        Assert.Equal((decimal)coverage, shared.CoveragePercent);
        Assert.Equal(complete, shared.IsComplete);
        Assert.Equal(new[] { 1, 2 }, shared.ConsumerItemIds);
        Assert.True(shared.IsActive); // Purchases are active work in the selected plan too.
        var target = Assert.Single(map.Nodes, node => node.IsTarget);
        Assert.Equal(0m, target.OwnedQuantity);
        Assert.Null(target.CoveragePercent);
        Assert.False(target.IsComplete);
    }

    [Fact]
    public void ExactComboKeepsItsItemAndRecipeButUsesTheBaseIngredientsOnlyOnce()
    {
        var catalog = Catalog(Recipe(100, 1, Input(10, 3)) with { ComboRecipeId = 101, ComboActivationChance = 1m }, Recipe(101, 3));
        var state = Calculate(catalog, State(101, 3));
        var map = CraftingDependencyMapProjection.Build(catalog, state);
        var target = Assert.Single(map.Nodes, node => node.IsTarget);
        Assert.Equal(3, target.ItemId);
        Assert.Equal(101, target.RecipeId);
        Assert.True(target.IsCombo);
        Assert.Equal(1m, target.RequiredQuantity);
        Assert.Equal(new[] { 10 }, target.DependencyItemIds);
        Assert.DoesNotContain(map.Nodes, node => node.ItemId == 1);
        Assert.Equal(3m, Assert.Single(map.Nodes, node => node.ItemId == 10).RequiredQuantity);
    }

    [Fact]
    public void ExactComboTargetSurvivesACallerHoldingItsBaseSelection()
    {
        var catalog = Catalog(Recipe(100, 1, Input(10)) with { ComboRecipeId = 101, ComboActivationChance = 1m }, Recipe(101, 3));
        var map = CraftingDependencyMapProjection.Build(catalog, State(100, 3));
        var target = Assert.Single(map.Nodes, node => node.IsTarget);
        Assert.Equal(3, target.ItemId);
        Assert.Equal(101, target.RecipeId);
        Assert.True(target.IsCombo);
    }

    [Fact]
    public void NonEquipmentComboTargetUsesExpectedOutputOfFixedBaseAttempts()
    {
        var catalog = Catalog(Recipe(100, 20, Input(10)) with { ComboRecipeId = 101, ComboActivationChance = 1m },
            Recipe(101, 21) with { OutputQuantity = 2 });
        var state = State(101, 21);
        var result = new CraftingCalculator(catalog).Calculate(new()
        {
            RecipeId = 101, TargetItemId = 21, Samples = 1,
            BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(10, new(10, 1m, CraftingPriceSource.Manual)),
            SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(21, new(21, 10m, CraftingPriceSource.Manual))
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.False(CraftingCalculatorPresentation.IsComboEquipmentTarget(catalog, 101));
        var expected = Assert.Single(result.Steps, step => step.RecipeId == 101).ExpectedSuccesses * 2m;
        Assert.Equal(2m, expected);
        var target = Assert.Single(CraftingDependencyMapProjection.Build(catalog, state with { Result = result }).Nodes, node => node.IsTarget);
        Assert.Equal(21, target.ItemId);
        Assert.True(target.IsCombo);
        Assert.Equal(expected, target.RequiredQuantity);
        Assert.Null(target.CoveragePercent);
    }

    [Fact]
    public void AmbiguousCraftRouteRequiresAnExplicitChoiceAndBuyingDoesNotExpandItsSubrecipes()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)), Recipe(200, 2, Input(10)), Recipe(300, 2, Input(11)));
        var state = State(100, 1) with { Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft) };
        var ambiguous = CraftingDependencyMapProjection.Build(catalog, state);
        var ingredient = Assert.Single(ambiguous.Nodes, node => node.ItemId == 2);
        Assert.Null(ingredient.RecipeId);
        Assert.Equal(new[] { 200, 300 }, ingredient.RecipeCandidateIds);
        Assert.Empty(ingredient.DependencyItemIds);
        Assert.Equal(2, ambiguous.Nodes.Length);
        var chosen = CraftingDependencyMapProjection.Build(catalog, state with { RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(2, 300) });
        Assert.Contains(chosen.Nodes, node => node.ItemId == 11);
        Assert.DoesNotContain(chosen.Nodes, node => node.ItemId == 10);
        var bought = CraftingDependencyMapProjection.Build(catalog, state with { Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty });
        Assert.Equal(2, bought.Nodes.Length);
        Assert.All(bought.Nodes, node => Assert.True(node.IsActive));
    }

    [Fact]
    public void MarketFactionFiltersCandidateRecipesAndNeverFallsBackFromAnInvalidExplicitChoice()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)), Recipe(200, 2, Input(10)) with { Faction = "Elyos" },
            Recipe(300, 2, Input(11)) with { Faction = "Asmodians" });
        var state = State(100, 1) with
        {
            Faction = "elyos",
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft)
        };
        var map = CraftingDependencyMapProjection.Build(catalog, state);
        Assert.Equal(200, Assert.Single(map.Nodes, node => node.ItemId == 2).RecipeId);
        Assert.DoesNotContain(map.Nodes, node => node.ItemId == 11);
        map = CraftingDependencyMapProjection.Build(catalog, state with { RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(2, 300) });
        Assert.Null(Assert.Single(map.Nodes, node => node.ItemId == 2).RecipeId);
        Assert.DoesNotContain(map.Nodes, node => node.ItemId is 10 or 11);
        map = CraftingDependencyMapProjection.Build(catalog, state with { Faction = null });
        Assert.Equal(new[] { 200, 300 }, Assert.Single(map.Nodes, node => node.ItemId == 2).RecipeCandidateIds);
    }

    [Fact]
    public void ReplacementChoicesAffectBothExactEdgesAndWholePlanDemand()
    {
        var root = Recipe(100, 1, Input(10, 2) with { ReplacementItemIds = [11] });
        var catalog = Catalog(root, Recipe(200, 11));
        var state = State(100, 1) with { ReplacementChoices = ImmutableDictionary<CraftingIngredientKey, int>.Empty.Add(new(100, 10), 11) };
        var map = CraftingDependencyMapProjection.Build(catalog, Calculate(catalog, state));
        Assert.DoesNotContain(map.Nodes, node => node.ItemId == 10);
        var material = Assert.Single(map.Nodes, node => node.ItemId == 11);
        Assert.Equal(2m, material.RequiredQuantity);
        Assert.Equal(new[] { 1 }, material.ConsumerItemIds);
    }

    [Fact]
    public void StochasticShoppingTailCannotLookCompleteEvenWhenStockExceedsMeanDemand()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)), Recipe(200, 2, Input(10)) with { SuccessRates = [0.5m] });
        var state = State(100, 1) with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft),
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(10, 3)
        };
        state = Calculate(catalog, state);
        var material = Assert.Single(CraftingDependencyMapProjection.Build(catalog, state).Nodes, node => node.ItemId == 10);
        Assert.True(material.RequiredQuantity < material.OwnedQuantity);
        Assert.Contains(state.Result!.Shopping, row => row.ItemId == 10 && row.ExpectedQuantity > 0);
        Assert.False(material.IsComplete);
        Assert.Equal(99.9m, material.CoveragePercent);
        Assert.True(material.RemainingQuantity > 0);
    }

    [Fact]
    public void StochasticProducerTailCannotLookCompleteEvenWhenStockExceedsMeanDemand()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)) with { SuccessRates = [0.5m], ComboRecipeId = 101, ComboActivationChance = 1m },
            Recipe(101, 3), Recipe(200, 2, Input(10)));
        var state = State(101, 3) with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft),
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 3)
        };
        state = Calculate(catalog, state);
        var gear = Assert.Single(CraftingDependencyMapProjection.Build(catalog, state).Nodes, node => node.ItemId == 2);
        Assert.True(gear.RequiredQuantity < gear.OwnedQuantity);
        Assert.Contains(state.Result!.Steps, step => step.RecipeId == 200 && step.ExpectedAttempts > 0);
        Assert.False(gear.IsComplete);
        Assert.Equal(99.9m, gear.CoveragePercent);
        Assert.True(gear.RemainingQuantity > 0);
    }

    [Fact]
    public void MissingResultNeverInventsRequirementsOrCompletedStockCoverage()
    {
        var catalog = Catalog(Recipe(100, 1, Input(10)) with { ComboRecipeId = 101, ComboActivationChance = 1m }, Recipe(101, 3));
        var map = CraftingDependencyMapProjection.Build(catalog, State(101, 3) with { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(10, 100) });
        Assert.False(map.QuantitiesKnown);
        Assert.All(map.Nodes, node =>
        {
            Assert.Null(node.RequiredQuantity);
            Assert.Null(node.RemainingQuantity);
            Assert.Null(node.CoveragePercent);
            Assert.False(node.IsComplete);
        });
    }

    [Fact]
    public void StockCoveredIntermediateDoesNotInventItsUnneededInputQuantities()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)), Recipe(200, 2, Input(10, 3)));
        var state = State(100, 1) with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft),
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 1)
        };
        var map = CraftingDependencyMapProjection.Build(catalog, Calculate(catalog, state));
        Assert.True(Assert.Single(map.Nodes, node => node.ItemId == 2).IsComplete);
        var unneededInput = Assert.Single(map.Nodes, node => node.ItemId == 10);
        Assert.Null(unneededInput.RequiredQuantity);
        Assert.False(unneededInput.IsComplete);
    }

    [Fact]
    public void CyclesAreBoundedWithoutCyclicOrDanglingEdges()
    {
        var catalog = Catalog(Recipe(100, 1, Input(2)), Recipe(200, 2, Input(1)));
        var state = State(100, 1) with { Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(2, CraftingAcquisition.Craft).Add(1, CraftingAcquisition.Craft) };
        var map = CraftingDependencyMapProjection.Build(catalog, state);
        Assert.True(map.Truncated);
        Assert.Equal(2, map.Nodes.Length);
        Assert.Empty(Assert.Single(map.Nodes, node => node.ItemId == 2).DependencyItemIds);
    }

    [Fact]
    public void DepthAndNodeLimitsStopExpansionAndOnlyKeepExistingEdgeEndpoints()
    {
        var deepCatalog = Catalog(Enumerable.Range(1, 20).Select(id => Recipe(id * 100, id, Input(id + 1))).ToArray());
        var state = State(100, 1) with { Acquisition = Enumerable.Range(2, 20).ToImmutableDictionary(id => id, _ => CraftingAcquisition.Craft) };
        var deepMap = CraftingDependencyMapProjection.Build(deepCatalog, state);
        Assert.True(deepMap.Truncated);
        Assert.InRange(deepMap.Nodes.Length, 1, 13);
        var wideCatalog = Catalog(Recipe(100, 1, Enumerable.Range(2, 150).Select(id => Input(id)).ToArray()));
        var wideMap = CraftingDependencyMapProjection.Build(wideCatalog, State(100, 1));
        Assert.True(wideMap.Truncated);
        Assert.Equal(128, wideMap.Nodes.Length);
        Assert.All(wideMap.Nodes.SelectMany(node => node.DependencyItemIds), id => Assert.Contains(wideMap.Nodes, node => node.ItemId == id));
    }

    private static CraftingCalculatorSnapshot State(int recipeId, int itemId) => new() { SelectedRecipeId = recipeId, TargetItemId = itemId };
    private static CraftingIngredient Input(int itemId, int quantity = 1) => new() { ItemId = itemId, Quantity = quantity };
    private static CraftingRecipe Recipe(int recipeId, int itemId, params CraftingIngredient[] ingredients) => new()
    {
        Id = recipeId, Profession = "Blacksmithing", Subcategory = "Mace", OutputItemId = itemId, OutputQuantity = 1,
        Ingredients = ingredients.ToImmutableArray(), SuccessRates = [1m], CraftingFee = 0m, MechanicsQualified = true
    };
    private static CraftingCatalog Catalog(params CraftingRecipe[] recipes) => new("synthetic-dependency-map",
        recipes.SelectMany(recipe => recipe.Ingredients.SelectMany(input => input.ReplacementItemIds.Prepend(input.ItemId)).Prepend(recipe.OutputItemId))
            .Distinct().Select(id => new CraftingItem { Id = id, NameEn = $"Synthetic {id}", NameDe = $"Beispiel {id}", IsEquipment = id < 10, IsTradable = true }), recipes);
    private static CraftingCalculatorSnapshot Calculate(CraftingCatalog catalog, CraftingCalculatorSnapshot state)
    {
        var request = new CraftingPlanRequest
        {
            RecipeId = state.SelectedRecipeId!.Value, TargetItemId = state.TargetItemId,
            TargetOutputQuantity = 1, Samples = 4096, Seed = 20261112, Faction = state.Faction,
            Acquisition = state.Acquisition, RecipeChoices = state.RecipeChoices, OwnedItems = state.OwnedItems,
            ReplacementChoices = state.ReplacementChoices,
            BuyPrices = catalog.Items.ToImmutableDictionary(item => item.Id, item => new CraftingPrice(item.Id, 1m, CraftingPriceSource.Manual)),
            SellPrices = catalog.Items.ToImmutableDictionary(item => item.Id, item => new CraftingPrice(item.Id, 10m, CraftingPriceSource.Manual))
        };
        var result = catalog.IsComboRecipe(request.RecipeId) ? new CraftingOutputTargetCalculator(catalog).Calculate(request) : new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        return state with { Result = result };
    }
}
