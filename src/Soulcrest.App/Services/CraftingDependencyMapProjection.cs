using System.Collections.Immutable;
using Soulcrest.Core.Crafting;

namespace Soulcrest.App.Services;

public sealed record CraftingDependencyNode
{
    public int ItemId { get; init; }
    public int? RecipeId { get; init; }
    public bool IsTarget { get; init; }
    public bool IsEquipment { get; init; }
    public bool IsCombo { get; init; }
    public bool IsActive { get; init; }
    public decimal? RequiredQuantity { get; init; }
    public decimal OwnedQuantity { get; init; }
    public decimal? RemainingQuantity { get; init; }
    public decimal? CoveragePercent { get; init; }
    public bool IsComplete { get; init; }
    public ImmutableArray<int> ConsumerItemIds { get; init; } = [];
    public ImmutableArray<int> DependencyItemIds { get; init; } = [];
    public ImmutableArray<int> RecipeCandidateIds { get; init; } = [];
}

public sealed record CraftingDependencyMap
{
    public ImmutableArray<CraftingDependencyNode> Nodes { get; init; } = [];
    public bool QuantitiesKnown { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Projects the selected scenario without calculating prices, changing choices, or observing inventory.
/// Owned quantities are manual scenario inputs; coverage is not a live crafting progress signal.
/// </summary>
public static class CraftingDependencyMapProjection
{
    private const int DepthLimit = 12;
    private const int NodeLimit = 128;

    private sealed class Node(int itemId, CraftingRecipe? recipe, ImmutableArray<int> candidates)
    {
        public int ItemId { get; } = itemId;
        public CraftingRecipe? Recipe { get; } = recipe;
        public ImmutableArray<int> Candidates { get; } = candidates;
        public HashSet<int> Consumers { get; } = [];
        public HashSet<int> Dependencies { get; } = [];
    }

    public static CraftingDependencyMap Build(CraftingCatalog catalog, CraftingCalculatorSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(state);
        var selected = state.SelectedRecipeId is { } selectedId ? catalog.FindRecipe(selectedId) : null;
        var targetId = state.TargetItemId ?? selected?.OutputItemId;
        if (targetId is not { } target || catalog.FindItem(target) is null) return new();

        bool Compatible(CraftingRecipe recipe) => CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, state.Faction)
            && catalog.FindBaseRecipe(recipe.Id) is { } basis
            && CraftingCalculatorPresentation.MatchesFaction(basis.Faction, state.Faction);
        ImmutableArray<CraftingRecipe> Candidates(int itemId) => catalog.RecipesForItem(itemId)
            .Where(Compatible).OrderBy(recipe => recipe.RequiredProficiency ?? 0).ThenBy(recipe => recipe.Id).ToImmutableArray();

        var rootCandidates = Candidates(target);
        CraftingRecipe? rootRecipe = selected is not null && selected.OutputItemId == target && Compatible(selected) ? selected : null;
        if (rootRecipe is null && selected is not null && Compatible(selected))
        {
            // Preserve the exact requested COMBO item even if a caller retained its base recipe selection.
            var family = rootCandidates.Where(recipe => catalog.FindBaseRecipe(recipe.Id)?.Id == catalog.FindBaseRecipe(selected.Id)?.Id).ToArray();
            if (family.Length == 1) rootRecipe = family[0];
        }
        var nodes = new Dictionary<int, Node>();
        var root = new Node(target, rootRecipe, rootCandidates.Select(recipe => recipe.Id).ToImmutableArray());
        nodes.Add(target, root);
        var pending = new Queue<(Node Node, int Depth)>();
        pending.Enqueue((root, 0));
        var truncated = false;

        bool Reaches(int start, int sought)
        {
            var visited = new HashSet<int>();
            var remaining = new Stack<int>();
            remaining.Push(start);
            while (remaining.TryPop(out var current))
            {
                if (current == sought) return true;
                if (!visited.Add(current) || !nodes.TryGetValue(current, out var node)) continue;
                foreach (var dependency in node.Dependencies) remaining.Push(dependency);
            }
            return false;
        }

        while (pending.TryDequeue(out var entry))
        {
            if (entry.Node.Recipe is not { } producer || catalog.FindBaseRecipe(producer.Id) is not { } basis) continue;
            if (entry.Depth >= DepthLimit)
            {
                truncated |= basis.Ingredients.Length > 0;
                continue;
            }
            foreach (var ingredient in basis.Ingredients)
            {
                var itemId = IngredientItemId(state, basis, ingredient);
                if (itemId is null || catalog.FindItem(itemId.Value) is null) continue;
                if (Reaches(itemId.Value, entry.Node.ItemId))
                {
                    truncated = true;
                    continue;
                }
                if (!nodes.TryGetValue(itemId.Value, out var dependency))
                {
                    if (nodes.Count >= NodeLimit)
                    {
                        truncated = true;
                        continue;
                    }
                    var candidates = Candidates(itemId.Value);
                    CraftingRecipe? recipe = null;
                    if (state.Acquisition.GetValueOrDefault(itemId.Value) == CraftingAcquisition.Craft)
                    {
                        if (state.RecipeChoices.TryGetValue(itemId.Value, out var choice))
                            recipe = candidates.FirstOrDefault(candidate => candidate.Id == choice);
                        else if (candidates.Length == 1) recipe = candidates[0];
                    }
                    dependency = new(itemId.Value, recipe, candidates.Select(candidate => candidate.Id).ToImmutableArray());
                    nodes.Add(itemId.Value, dependency);
                    pending.Enqueue((dependency, entry.Depth + 1));
                }
                entry.Node.Dependencies.Add(itemId.Value);
                dependency.Consumers.Add(entry.Node.ItemId);
            }
        }

        var required = new Dictionary<int, decimal>();
        var shopping = new Dictionary<int, decimal>();
        var steps = new Dictionary<int, CraftingStepResult>();
        var result = state.Result;
        var quantitiesKnown = result is not null && (!result.Steps.IsDefaultOrEmpty || result.TargetOutput is not null);
        if (quantitiesKnown && result is not null)
        {
            try
            {
                foreach (var step in result.Steps)
                {
                    if (step.ExpectedAttempts < 0 || step.ExpectedSuccesses < 0 || catalog.FindRecipe(step.RecipeId) is not { } recipe)
                    { quantitiesKnown = false; break; }
                    steps[recipe.Id] = step;
                    // COMBO rolls have no ingredients; counting the base ingredients again would double the demand.
                    foreach (var ingredient in recipe.Ingredients)
                    {
                        if (IngredientItemId(state, recipe, ingredient) is not { } itemId)
                        { quantitiesKnown = false; break; }
                        required[itemId] = checked(required.GetValueOrDefault(itemId) + step.ExpectedAttempts * ingredient.Quantity);
                    }
                    if (!quantitiesKnown) break;
                }
                foreach (var row in result.Shopping)
                {
                    if (row.ExpectedQuantity < 0) { quantitiesKnown = false; break; }
                    shopping[row.ItemId] = checked(shopping.GetValueOrDefault(row.ItemId) + row.ExpectedQuantity);
                }
            }
            catch (OverflowException) { quantitiesKnown = false; }
        }

        decimal? TargetQuantity()
        {
            if (!quantitiesKnown || result is null) return null;
            if (result.TargetOutput is { } exactTarget && exactTarget.ItemId == target) return exactTarget.ExpectedQuantity;
            if (rootRecipe is null || !steps.TryGetValue(rootRecipe.Id, out var exactStep)) return null;
            var subsequentAttempts = rootRecipe.ComboRecipeId is { } next && steps.TryGetValue(next, out var nextStep) ? nextStep.ExpectedAttempts : 0m;
            return Math.Max(0m, exactStep.ExpectedSuccesses - subsequentAttempts) * rootRecipe.OutputQuantity;
        }

        return new()
        {
            QuantitiesKnown = quantitiesKnown,
            Truncated = truncated,
            Nodes = nodes.Values.Select(node =>
            {
                var isTarget = node.ItemId == target;
                var need = isTarget ? TargetQuantity() : quantitiesKnown && required.TryGetValue(node.ItemId, out var quantity) ? quantity : (decimal?)null;
                var owned = isTarget ? 0m : Math.Max(0m, state.OwnedItems.GetValueOrDefault(node.ItemId));
                var toBuy = quantitiesKnown ? shopping.GetValueOrDefault(node.ItemId) : 0m;
                var producerPending = quantitiesKnown && node.Recipe is { } selectedProducer
                    && catalog.FindBaseRecipe(selectedProducer.Id) is { } baseProducer
                    && steps.TryGetValue(baseProducer.Id, out var baseStep) && baseStep.ExpectedAttempts > 0;
                var produced = 0m;
                if (producerPending && node.Recipe is { } exactProducer && steps.TryGetValue(exactProducer.Id, out var producerStep))
                {
                    var subsequentAttempts = exactProducer.ComboRecipeId is { } next && steps.TryGetValue(next, out var nextStep) ? nextStep.ExpectedAttempts : 0m;
                    produced = Math.Max(0m, producerStep.ExpectedSuccesses - subsequentAttempts) * exactProducer.OutputQuantity;
                }
                decimal? remaining = need is { } demand ? Math.Max(Math.Max(0m, demand - owned), Math.Max(toBuy, produced)) : null;
                var complete = !isTarget && need > 0 && owned >= need && toBuy == 0 && !producerPending;
                decimal? percent = !isTarget && need is > 0 ? owned >= need.Value ? 100m : owned / need.Value * 100m : null;
                if (percent == 100m && !complete) percent = 99.9m;
                if (!isTarget && producerPending && remaining == 0m) remaining = null;
                return new CraftingDependencyNode
                {
                    ItemId = node.ItemId, RecipeId = node.Recipe?.Id, IsTarget = isTarget,
                    IsEquipment = catalog.FindItem(node.ItemId)?.IsEquipment == true,
                    IsCombo = node.Recipe is { } active ? catalog.IsComboRecipe(active.Id) : node.Candidates.Any(catalog.IsComboRecipe),
                    IsActive = true, RequiredQuantity = need, OwnedQuantity = owned, RemainingQuantity = remaining,
                    CoveragePercent = percent, IsComplete = complete,
                    ConsumerItemIds = node.Consumers.Order().ToImmutableArray(),
                    DependencyItemIds = node.Dependencies.Order().ToImmutableArray(), RecipeCandidateIds = node.Candidates
                };
            }).ToImmutableArray()
        };
    }

    private static int? IngredientItemId(CraftingCalculatorSnapshot state, CraftingRecipe recipe, CraftingIngredient ingredient)
    {
        var selected = state.ReplacementChoices.GetValueOrDefault(new(recipe.Id, ingredient.ItemId), ingredient.ItemId);
        return selected == ingredient.ItemId || ingredient.ReplacementItemIds.Contains(selected) ? selected : null;
    }
}
