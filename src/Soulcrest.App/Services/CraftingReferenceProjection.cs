using System.Collections.Immutable;
using Soulcrest.Core.Crafting;

namespace Soulcrest.App.Services;

public sealed record CraftingReferenceProjectionResult(
    ImmutableDictionary<string, int> EffectiveLevels,
    ImmutableDictionary<string, int> RequiredLevels,
    ImmutableDictionary<string, int> ReferenceLevels);

/// <summary>Derives temporary minimum levels from the selected route. It never changes observations, manual inputs or recipe mechanics.</summary>
public static class CraftingReferenceProjection
{
    private const int MaximumDepth = 64;

    public static CraftingReferenceProjectionResult Resolve(CraftingCatalog catalog, CraftingCalculatorSnapshot state, int quantity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        var effective = state.ObservedLevels.SetItems(state.SimulatedLevels).ToBuilder();
        var required = ImmutableDictionary.CreateBuilder<string, int>();
        var reference = ImmutableDictionary.CreateBuilder<string, int>();
        var visited = new HashSet<int>();
        var selected = state.SelectedRecipeId is { } selectedId ? catalog.FindRecipe(selectedId) : null;
        var root = selected is null ? null : catalog.FindBaseRecipe(selected.Id);
        var transferMode = root is not null && TransferCraftingRules.IsTransfer(catalog, root);
        // A COMBO target can require repeated base attempts. An owned copy cannot prune its producer requirements.
        var ownedRootCoversOneAttempt = root is not null && selected is not null && transferMode && quantity == 1 && selected.Id == root.Id
            && (state.TargetItemId is null || state.TargetItemId == root.OutputItemId);
        if (root is not null) Visit(root, 0);
        foreach (var (profession, minimum) in required)
        {
            // Unknown or invalid levels stay unknown/invalid for the calculator's own qualification checks.
            if (effective.TryGetValue(profession, out var level) && level is >= 0 and <= 100 && level < minimum)
            {
                effective[profession] = minimum;
                reference[profession] = minimum;
            }
        }
        return new(effective.ToImmutable(), required.ToImmutable(), reference.ToImmutable());

        void Visit(CraftingRecipe recipe, int depth)
        {
            // This is a bounded projection, not a replacement for the calculator's cycle or mechanics diagnostics.
            if (depth >= MaximumDepth || !visited.Add(recipe.Id)) return;
            var transfer = TransferCraftingRules.IsTransfer(catalog, recipe);
            if (recipe.RequiredProficiency is { } minimum && (!transfer || minimum > 0)
                && (!required.TryGetValue(recipe.Profession, out var previous) || minimum > previous))
                required[recipe.Profession] = minimum;
            if (recipe.ComboActivationChance is > 0 && recipe.ComboRecipeId is { } comboId
                && catalog.FindRecipe(comboId) is { } combo) Visit(combo, depth + 1);

            var inputs = new List<(int ItemId, long Quantity)>();
            foreach (var ingredient in recipe.Ingredients)
            {
                var key = new CraftingIngredientKey(recipe.Id, ingredient.ItemId);
                var hasChoice = state.ReplacementChoices.TryGetValue(key, out var itemId);
                if (!hasChoice) itemId = ingredient.ItemId;
                if (itemId != ingredient.ItemId && !ingredient.ReplacementItemIds.Contains(itemId)) continue;
                if (transferMode && TransferCraftingRules.IsTransferIngredient(recipe, ingredient)
                    && (recipe.TransferItemIds.Length > 1 && !hasChoice || !recipe.TransferItemIds.Contains(itemId))) continue;
                inputs.Add((itemId, ingredient.Quantity));
            }
            foreach (var group in inputs.GroupBy(input => input.ItemId))
            {
                var itemId = group.Key;
                var gear = transferMode && TransferCraftingRules.IsEquipment(catalog, itemId);
                if (gear && ownedRootCoversOneAttempt && recipe.Id == root!.Id
                    && state.OwnedItems.GetValueOrDefault(itemId) >= group.Sum(input => input.Quantity)) continue;
                if (!gear && state.Acquisition.GetValueOrDefault(itemId, CraftingAcquisition.Buy) != CraftingAcquisition.Craft) continue;
                if (Producer(itemId, gear) is { } producer) Visit(producer, depth + 1);
            }
        }

        CraftingRecipe? Producer(int itemId, bool gear)
        {
            if (state.RecipeChoices.TryGetValue(itemId, out var choiceId))
            {
                var choice = catalog.FindRecipe(choiceId);
                // Keep a broken exact choice broken; never hide it with another producer or its unrelated base output.
                if (choice is null || choice.OutputItemId != itemId || !CraftingCatalog.IsFactionCompatible(choice.Faction, state.Faction)) return null;
                var basis = catalog.FindBaseRecipe(choice.Id);
                return basis is not null && CraftingCatalog.IsFactionCompatible(basis.Faction, state.Faction) ? basis : null;
            }
            if (gear)
            {
                var choices = TransferCraftingRules.ProducerChoices(catalog, itemId, state.Faction);
                return choices.Length == 1 ? catalog.FindBaseRecipe(choices[0].Id) : null;
            }
            // Mirror normal crafting's existing no-choice route ordering, including exact COMBO outputs.
            return catalog.Recipes.Where(recipe => recipe.OutputItemId == itemId)
                .Select(recipe => catalog.FindBaseRecipe(recipe.Id)!)
                .Where(recipe => CraftingCatalog.IsFactionCompatible(recipe.Faction, state.Faction))
                .OrderBy(recipe => recipe.RequiredProficiency ?? 0).ThenBy(recipe => recipe.Id).FirstOrDefault();
        }
    }
}
