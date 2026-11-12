using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingReferenceProjectionTests
{
    [Fact]
    public void BaseAndPositiveComboStepsUseTheMaximumRequiredLevelOfTheirProfession()
    {
        var root = Normal(100, 1, "Cooking", 1) with { ComboRecipeId = 101, ComboActivationChance = .3m };
        var combo = Normal(101, 2, "Cooking", 5) with { ComboRecipeId = 102, ComboActivationChance = .25m };
        var catalog = Catalog([root, combo, Normal(102, 3, "Cooking", 3)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 80));
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.RequiredLevels, ("Cooking", 5));
        EqualLevels(result.EffectiveLevels, ("Cooking", 5), ("Alchemy", 80));
        EqualLevels(result.ReferenceLevels, ("Cooking", 5));
        Assert.Equal(1, state.ObservedLevels["Cooking"]);
    }

    [Fact]
    public void NestedChosenCraftSourcesIncludeTheirCombosButDoNotVisitBoughtSubrecipes()
    {
        var root = Normal(100, 1, "Cooking", 10, 3);
        var source = Normal(200, 3, "Alchemy", 20, 4) with { ComboRecipeId = 201, ComboActivationChance = .25m };
        var catalog = Catalog([root, source, Normal(201, 5, "Alchemy", 30), Normal(300, 4, "Handicrafting", 40, 6),
            Normal(400, 6, "Armorsmithing", 90)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 2), ("Handicrafting", 3), ("Armorsmithing", 4)) with
        { Acquisition = Craft(3, 4), RecipeChoices = Choices((3, 200), (4, 300), (6, 400)) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.RequiredLevels, ("Cooking", 10), ("Alchemy", 30), ("Handicrafting", 40));
        EqualLevels(result.ReferenceLevels, ("Cooking", 10), ("Alchemy", 30), ("Handicrafting", 40));
        Assert.Equal(4, result.EffectiveLevels["Armorsmithing"]);
    }

    [Fact]
    public void ZeroActivationComboAndAnUnselectedCraftSourceDoNotRaiseReferenceLevels()
    {
        var root = Normal(100, 1, "Cooking", 1, 3) with { ComboRecipeId = 101, ComboActivationChance = 0 };
        var catalog = Catalog([root, Normal(101, 2, "Alchemy", 90), Normal(200, 3, "Armorsmithing", 80)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 1), ("Armorsmithing", 1)) with
        { RecipeChoices = Choices((3, 200)) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.RequiredLevels, ("Cooking", 1));
        Assert.Empty(result.ReferenceLevels);
    }

    [Fact]
    public void ManualLevelsOverrideObservedLevelsAndAlreadyQualifiedJobsStayUnchanged()
    {
        var catalog = Catalog([Normal(100, 1, "Cooking", 60)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 27)) with
        { SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Cooking", 100) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.EffectiveLevels, ("Cooking", 100), ("Alchemy", 27));
        EqualLevels(result.RequiredLevels, ("Cooking", 60));
        Assert.Empty(result.ReferenceLevels);
    }

    [Fact]
    public void UnknownProficiencyIsNotInventedEvenWhenTheRecipeRequirementIsKnown()
    {
        var catalog = Catalog([Normal(100, 1, "Cooking", 5)]);
        var result = CraftingReferenceProjection.Resolve(catalog, State(100, ("Alchemy", 100)), 1);
        EqualLevels(result.RequiredLevels, ("Cooking", 5));
        EqualLevels(result.EffectiveLevels, ("Alchemy", 100));
        Assert.Empty(result.ReferenceLevels);
        Assert.False(catalog.TrySuccessChance(catalog.FindRecipe(100)!, result.EffectiveLevels, out _));
    }

    [Fact]
    public void MissingNativeCurvesRemainMissingAfterTheReferenceLevelIsRaised()
    {
        var recipe = Normal(100, 1, "Cooking", 55) with { SuccessRates = null, MechanicsQualified = false };
        var catalog = Catalog([recipe]);
        var result = CraftingReferenceProjection.Resolve(catalog, State(100, ("Cooking", 1)), 1);
        EqualLevels(result.ReferenceLevels, ("Cooking", 55));
        Assert.Null(recipe.SuccessRates);
        Assert.False(recipe.MechanicsQualified);
        Assert.False(catalog.TrySuccessChance(recipe, result.EffectiveLevels, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidKnownLevelsAreNotRepairedIntoAnApparentlyValidRequest(int level)
    {
        var result = CraftingReferenceProjection.Resolve(Catalog([Normal(100, 1, "Cooking", 60)]), State(100, ("Cooking", level)), 1);
        Assert.Equal(level, result.EffectiveLevels["Cooking"]);
        Assert.Empty(result.ReferenceLevels);
    }

    [Fact]
    public void ReferenceLevelsAreDerivedAgainWithoutBecomingManualOrObservedState()
    {
        var catalog = Catalog([Normal(100, 1, "Cooking", 60)]);
        var state = State(100, ("Cooking", 1)) with
        { SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Cooking", 2) };
        var observed = state.ObservedLevels;
        var manual = state.SimulatedLevels;
        var first = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(first.ReferenceLevels, ("Cooking", 60));
        Assert.Same(observed, state.ObservedLevels);
        Assert.Same(manual, state.SimulatedLevels);
        Assert.Equal(2, state.Level("Cooking"));
        var qualified = CraftingReferenceProjection.Resolve(catalog,
            state with { SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Cooking", 100) }, 1);
        Assert.Empty(qualified.ReferenceLevels);
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).ReferenceLevels, ("Cooking", 60));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999)]
    public void MissingSelectionLeavesTheFullObservedAndManualLevelUnionAlone(int? selected)
    {
        var state = State(selected, ("Cooking", 1)) with
        { SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Alchemy", 100) };
        var result = CraftingReferenceProjection.Resolve(Catalog([Normal(100, 1, "Cooking", 60)]), state, 1);
        EqualLevels(result.EffectiveLevels, ("Cooking", 1), ("Alchemy", 100));
        Assert.Empty(result.RequiredLevels);
        Assert.Empty(result.ReferenceLevels);
    }

    [Fact]
    public void NormalNoChoiceFallbackUsesTheSameBaseOrderingIncludingExactComboOutputs()
    {
        var source = Normal(400, 7, "Alchemy", 7) with { ComboRecipeId = 401, ComboActivationChance = .25m };
        var catalog = Catalog([Normal(100, 1, "Cooking", 1, 3), Normal(200, 3, "Alchemy", 30),
            Normal(300, 3, "Alchemy", 10), source, Normal(401, 3, "Alchemy", 50),
            Normal(500, 3, "Alchemy", 2) with { Faction = "Elyos" }]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 1)) with { Acquisition = Craft(3) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.RequiredLevels, ("Cooking", 1), ("Alchemy", 50));
        EqualLevels(result.ReferenceLevels, ("Alchemy", 50));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(999)]
    public void BrokenExplicitNormalRouteNeverFallsBackToAnotherProducerOrAnUnrelatedBaseOutput(int choice)
    {
        var catalog = Catalog([Normal(100, 1, "Cooking", 1, 3), Normal(200, 4, "Alchemy", 50), Normal(300, 3, "Alchemy", 10)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 1)) with
        { Acquisition = Craft(3), RecipeChoices = Choices((3, choice)) };
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).RequiredLevels, ("Cooking", 1));
    }

    [Fact]
    public void AForeignExplicitProducerDoesNotSelectAnotherFactionRouteAsAReference()
    {
        var catalog = Catalog([Normal(100, 1, "Cooking", 1, 3), Normal(200, 3, "Alchemy", 90) with { Faction = "Elyos" },
            Normal(300, 3, "Alchemy", 10)]);
        var state = State(100, ("Cooking", 1), ("Alchemy", 1)) with
        { Acquisition = Craft(3), RecipeChoices = Choices((3, 200)) };
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).RequiredLevels, ("Cooking", 1));
    }

    [Fact]
    public void OneGuaranteedBaseTransferPrunesFullyOwnedProtectedAndOrdinaryGearWithoutInventingAJobLevel()
    {
        var catalog = Catalog([Transfer(100, 1, [3], (3, 1), (4, 1)), Normal(200, 3, "Alchemy", 60),
            Normal(300, 4, "Handicrafting", 80)], 4);
        var state = State(100) with { OwnedItems = Owned((3, 1), (4, 1)) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        Assert.Empty(result.RequiredLevels);
        Assert.Empty(result.ReferenceLevels);
        Assert.Empty(result.EffectiveLevels);
    }

    [Theory]
    [InlineData(101, 1, null)]
    [InlineData(100, 2, null)]
    [InlineData(100, 1, 2)]
    public void ComboGoalsAndMultipleProductsCannotPruneAProducerWithOneOwnedRootCopy(int selected, int quantity, int? target)
    {
        var root = Transfer(100, 1, [3], (3, 1)) with { ComboRecipeId = 101, ComboActivationChance = .25m };
        var catalog = Catalog([root, Transfer(101, 2, []), Normal(200, 3, "Alchemy", 60)]);
        var state = State(selected, ("Alchemy", 1)) with { TargetItemId = target, OwnedItems = Owned((3, 1)) };
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, quantity).ReferenceLevels, ("Alchemy", 60));
    }

    [Fact]
    public void SharedSelectedGearQuantitiesAreAggregatedBeforeAnyOwnedRootPruning()
    {
        var root = Transfer(100, 1, [5], (5, 1), (3, 1), (4, 1)) with
        {
            Ingredients = [new() { ItemId = 5, Quantity = 1 }, new() { ItemId = 3, Quantity = 1 },
                new() { ItemId = 4, Quantity = 1, ReplacementItemIds = [3] }]
        };
        var catalog = Catalog([root, Normal(200, 3, "Alchemy", 60)], 3, 4);
        var state = State(100, ("Alchemy", 1)) with
        {
            OwnedItems = Owned((5, 1), (3, 1)),
            ReplacementChoices = ImmutableDictionary<CraftingIngredientKey, int>.Empty.Add(new(100, 4), 3)
        };
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).ReferenceLevels, ("Alchemy", 60));
    }

    [Fact]
    public void OwnedGearDeeperInTheChainCannotHideProducerRequirementsForPossibleRetries()
    {
        var catalog = Catalog([Transfer(100, 1, [3], (3, 1)), Transfer(200, 3, [4], (4, 1)), Normal(300, 4, "Alchemy", 60)]);
        var state = State(100, ("Alchemy", 1)) with { OwnedItems = Owned((4, 1)) };
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).ReferenceLevels, ("Alchemy", 60));
    }

    [Fact]
    public void TransferGearAlwaysUsesItsCraftRouteEvenWhenTheAcquisitionMapSaysBuy()
    {
        var catalog = Catalog([Transfer(100, 1, [3], (3, 1)), Normal(200, 3, "Alchemy", 60)]);
        var state = State(100, ("Alchemy", 1));
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state, 1).ReferenceLevels, ("Alchemy", 60));
    }

    [Theory]
    [InlineData(201, true)]
    [InlineData(200, false)]
    public void ExactComboProducerIdsAreAcceptedButTheirDifferentBaseOutputIsNotAnEquivalentChoice(int choice, bool valid)
    {
        var source = Normal(200, 4, "Alchemy", 20) with { ComboRecipeId = 201, ComboActivationChance = .25m };
        var catalog = Catalog([Transfer(100, 1, [3], (3, 1)), source, Normal(201, 3, "Alchemy", 30)]);
        var state = State(100, ("Alchemy", 1)) with { RecipeChoices = Choices((3, choice)) };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        if (valid) EqualLevels(result.ReferenceLevels, ("Alchemy", 30));
        else Assert.Empty(result.RequiredLevels);
    }

    [Fact]
    public void AmbiguousTransferSourcesStayUnselectedUntilAnExactProducerIsChosen()
    {
        var catalog = Catalog([Transfer(100, 1, [3], (3, 1)), Normal(200, 3, "Alchemy", 20), Normal(300, 3, "Alchemy", 80)]);
        var state = State(100, ("Alchemy", 1));
        Assert.Empty(CraftingReferenceProjection.Resolve(catalog, state, 1).RequiredLevels);
        EqualLevels(CraftingReferenceProjection.Resolve(catalog,
            state with { RecipeChoices = Choices((3, 300)) }, 1).ReferenceLevels, ("Alchemy", 80));
    }

    [Fact]
    public void TransferAlternativesRequireAnExactSelectionAndNeverVisitTheUnselectedProducer()
    {
        var root = Transfer(100, 1, [3, 5], (3, 1)) with
        { Ingredients = [new() { ItemId = 3, Quantity = 1, ReplacementItemIds = [5] }] };
        var catalog = Catalog([root, Normal(200, 3, "Alchemy", 10), Normal(300, 5, "Alchemy", 40)]);
        var state = State(100, ("Alchemy", 1));
        var unknown = CraftingReferenceProjection.Resolve(catalog, state, 1);
        Assert.Empty(unknown.RequiredLevels);
        var failed = new TransferCraftingCalculator(catalog).Calculate(new()
        { RecipeId = 100, Faction = "Asmodians", ProficiencyLevels = unknown.EffectiveLevels, Samples = 1 });
        Assert.Contains(failed.Issues, issue => issue.Code == "TransferGearSelectionMissing");
        EqualLevels(CraftingReferenceProjection.Resolve(catalog, state with
        { ReplacementChoices = ImmutableDictionary<CraftingIngredientKey, int>.Empty.Add(new(100, 3), 5) }, 1).ReferenceLevels,
            ("Alchemy", 40));
    }

    [Fact]
    public void ProjectionTerminatesCyclesWithoutRemovingTheCalculatorsOwnCycleFailure()
    {
        var catalog = Catalog([Normal(100, 1, "Alchemy", 10, 3), Normal(200, 3, "Alchemy", 20, 1)]);
        var state = State(100, ("Alchemy", 1)) with { Acquisition = Craft(1, 3), RecipeChoices = Choices((1, 100), (3, 200)) };
        var projected = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(projected.ReferenceLevels, ("Alchemy", 20));
        var prices = catalog.Items.ToImmutableDictionary(item => item.Id, item => new CraftingPrice(item.Id, 1, CraftingPriceSource.Manual));
        var result = new CraftingCalculator(catalog).Calculate(new()
        {
            RecipeId = 100, Faction = "Asmodians", ProficiencyLevels = projected.EffectiveLevels,
            Acquisition = state.Acquisition, RecipeChoices = state.RecipeChoices, BuyPrices = prices, SellPrices = prices, Samples = 1
        });
        Assert.Contains(result.Issues, issue => issue.Code == "RecipeCycle");
    }

    [Fact]
    public void NativeEquipmentPredecessorIdsAreTheExactComboOutputsRatherThanTheirDifferentBaseItems()
    {
        var catalog = CraftingCatalog.Default;
        foreach (var (consumer, item, combo, basis, baseItem) in new[]
        {
            (221043001, 110430014, 241044001, 221044001, 110440015),
            (221044001, 110440014, 241045001, 221045001, 110450017),
            (221045001, 110450016, 241046001, 221046001, 110460024)
        })
        {
            Assert.Contains(catalog.FindRecipe(consumer)!.Ingredients, ingredient => ingredient.ItemId == item);
            Assert.True(catalog.IsComboRecipe(combo));
            Assert.Equal(item, catalog.FindRecipe(combo)!.OutputItemId);
            Assert.Equal(basis, catalog.FindBaseRecipe(combo)!.Id);
            Assert.Equal(baseItem, catalog.FindRecipe(basis)!.OutputItemId);
            Assert.NotEqual(item, baseItem);
        }
        var state = State(221043012, ("Handicrafting", 1)) with
        {
            RecipeChoices = Choices((110430100, 221043011), (110430019, 221043001),
                (110430014, 241044001), (110440014, 241045001), (110450016, 241046001)),
            ReplacementChoices = ImmutableDictionary<CraftingIngredientKey, int>.Empty
                .Add(new(221043012, 110430021), 110430100).Add(new(221043011, 110430019), 110430019)
        };
        var result = CraftingReferenceProjection.Resolve(catalog, state, 1);
        EqualLevels(result.RequiredLevels, ("Handicrafting", 55));
        EqualLevels(result.ReferenceLevels, ("Handicrafting", 55));
    }

    private static void EqualLevels(ImmutableDictionary<string, int> actual, params (string Job, int Level)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        foreach (var (job, level) in expected) Assert.Equal(level, actual[job]);
    }
    private static CraftingCalculatorSnapshot State(int? selected, params (string Job, int Level)[] levels) => new()
    { SelectedRecipeId = selected, Faction = "Asmodians", ObservedLevels = levels.ToImmutableDictionary(row => row.Job, row => row.Level) };
    private static ImmutableDictionary<int, CraftingAcquisition> Craft(params int[] ids) => ids.ToImmutableDictionary(id => id, _ => CraftingAcquisition.Craft);
    private static ImmutableDictionary<int, int> Choices(params (int Item, int Recipe)[] choices) => choices.ToImmutableDictionary(row => row.Item, row => row.Recipe);
    private static ImmutableDictionary<int, long> Owned(params (int Item, long Quantity)[] owned) => owned.ToImmutableDictionary(row => row.Item, row => row.Quantity);
    private static CraftingRecipe Normal(int id, int output, string profession, int required, params int[] inputs) => new()
    {
        Id = id, OutputItemId = output, OutputQuantity = 1, Profession = profession, Subcategory = "Materials", Faction = "Asmodians",
        RequiredProficiency = required, SuccessRates = Enumerable.Repeat(1m, 101 - required).ToImmutableArray(),
        MechanicsQualified = true, IsTransferCrafting = false, CraftingFee = 0,
        Ingredients = inputs.Select(item => new CraftingIngredient { ItemId = item, Quantity = 1 }).ToImmutableArray()
    };
    private static CraftingRecipe Transfer(int id, int output, int[] protectedIds, params (int Item, int Quantity)[] inputs) =>
        Normal(id, output, "Blacksmithing", 0) with
        {
            SuccessRates = null, MechanicsQualified = false, IsTransferCrafting = true, TransferItemIds = protectedIds.ToImmutableArray(),
            Ingredients = inputs.Select(row => new CraftingIngredient { ItemId = row.Item, Quantity = row.Quantity }).ToImmutableArray()
        };
    private static CraftingCatalog Catalog(CraftingRecipe[] recipes, params int[] additionalGear)
    {
        var gear = additionalGear.Concat(recipes.SelectMany(recipe => recipe.TransferItemIds))
            .Concat(recipes.Where(recipe => recipe.IsTransferCrafting == true).Select(recipe => recipe.OutputItemId)).ToHashSet();
        return new("synthetic-reference", Enumerable.Range(1, 100).Select(id => new CraftingItem
        { Id = id, NameEn = "Synthetic item " + id, NameDe = "Synthetisches Item " + id, IsEquipment = gear.Contains(id), IsTradable = true }), recipes);
    }
}
