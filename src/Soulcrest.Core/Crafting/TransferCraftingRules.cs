using System.Collections.Immutable;

namespace Soulcrest.Core.Crafting;

/// <summary>Cost-only upgrades. Newly made base or better COMBO products meet the goal, including producer shortcuts.</summary>
public sealed record TransferCraftingPlanRequest
{
    public int RecipeId { get; init; }
    public int? TargetItemId { get; init; }
    /// <summary>Number of newly made products at the chosen base tier or its better COMBO tier.</summary>
    public int TargetQuantity { get; init; } = 1;
    public string? Faction { get; init; }
    public ImmutableDictionary<string, int> ProficiencyLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<int, CraftingAcquisition> Acquisition { get; init; } = ImmutableDictionary<int, CraftingAcquisition>.Empty;
    public ImmutableDictionary<int, int> RecipeChoices { get; init; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableDictionary<CraftingIngredientKey, int> ReplacementChoices { get; init; } = ImmutableDictionary<CraftingIngredientKey, int>.Empty;
    public ImmutableDictionary<int, long> OwnedItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, CraftingPrice> BuyPrices { get; init; } = ImmutableDictionary<int, CraftingPrice>.Empty;
    public int Samples { get; init; } = 100_000;
    public int Seed { get; init; } = 20261110;
    public int MaximumAttemptsPerSample { get; init; } = 100_000;
    public long MaximumTotalAttempts { get; init; } = 50_000_000;
}

/// <summary>Shared transfer routing policy. The guarantee is an explicit user observation, not an inferred missing rate.</summary>
public static class TransferCraftingRules
{
    public const string PolicyGuaranteeSource = "user-confirmed-transfer-crafting-2026-11-10";

    public static bool IsTransfer(CraftingCatalog catalog, CraftingRecipe recipe)
    {
        var root = catalog.FindBaseRecipe(recipe.Id) ?? recipe;
        if (recipe.IsTransferCrafting is { } recipeKind) return recipeKind;
        if (root.IsTransferCrafting is { } rootKind) return rootKind;
        return root.TransferItemIds.Length > 0;
    }

    /// <summary>Explicit native equipment classification, with the exact protected item IDs as fixture compatibility.</summary>
    public static bool IsEquipment(CraftingCatalog catalog, int itemId)
    {
        if (catalog.FindItem(itemId)?.IsEquipment is { } known) return known;
        return catalog.Recipes.Any(recipe => recipe.TransferItemIds.Contains(itemId));
    }

    /// <summary>Exact output IDs and faction only. Multiple routes remain choices; a nearby or similarly named item is never substituted.</summary>
    public static ImmutableArray<CraftingRecipe> ProducerChoices(CraftingCatalog catalog, int itemId, string? faction) =>
        catalog.Recipes.Where(recipe => recipe.OutputItemId == itemId && CraftingCatalog.IsFactionCompatible(recipe.Faction, faction))
            .OrderBy(recipe => recipe.Id).ToImmutableArray();

    public static ImmutableArray<int> TransferGearOptions(CraftingRecipe recipe) => recipe.TransferItemIds.Distinct().ToImmutableArray();

    public static bool IsTransferIngredient(CraftingRecipe recipe, CraftingIngredient ingredient) =>
        recipe.TransferItemIds.Contains(ingredient.ItemId) || ingredient.ReplacementItemIds.Any(recipe.TransferItemIds.Contains);
}
