using System.Collections.Immutable;

namespace Soulcrest.Core.Crafting;

/// <summary>Seeded whole-chain cost distribution. Transfer gear can be owned or made, never bought or valued for sale.</summary>
public sealed class TransferCraftingCalculator(CraftingCatalog catalog)
{
    public CraftingPlanResult Calculate(TransferCraftingPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.TargetQuantity is <= 0 or > 100_000 || request.Samples is <= 0 or > 100_000
            || request.MaximumAttemptsPerSample <= 0 || request.MaximumTotalAttempts <= 0
            || request.ProficiencyLevels is null || request.Acquisition is null || request.RecipeChoices is null
            || request.ReplacementChoices is null || request.OwnedItems is null || request.BuyPrices is null
            || request.ProficiencyLevels.Any(row => !CraftingCatalog.Professions.Contains(row.Key) || row.Value is < 0 or > 100)
            || request.OwnedItems.Any(row => row.Key <= 0 || catalog.FindItem(row.Key) is null || row.Value < 0
                || TransferCraftingRules.IsEquipment(catalog, row.Key) && row.Value > 1))
            return Failed(CraftingPlanStatus.Invalid, "InvalidRequest");
        var selected = catalog.FindRecipe(request.RecipeId);
        if (selected is null) return Failed(CraftingPlanStatus.Invalid, "RecipeMissing", recipe: request.RecipeId);
        var target = request.TargetItemId ?? selected.OutputItemId;
        if (catalog.FindItem(target) is null) return Failed(CraftingPlanStatus.Invalid, "ItemMissing", target);
        try
        {
            return new Calculation(catalog, request, target, cancellationToken)
                .Calculate(catalog.FindBaseRecipe(selected.Id)!);
        }
        catch (CalculationFailure error) { return Failed(error.Status, error.Code, error.ItemId, error.RecipeId); }
        catch (OverflowException) { return Failed(CraftingPlanStatus.LimitExceeded, "NumericLimit"); }

        CraftingPlanResult Failed(CraftingPlanStatus status, string code, int? item = null, int? recipe = null) =>
            new() { Status = status, Issues = [new(code, item, recipe)], Seed = request.Seed };
    }

    private static bool ValidPrice(CraftingPrice price, int item) => price.ItemId == item && price.UnitPrice >= 0
        && (!price.IsMarket || price.UnitPrice > 0 && price.ServerId is > 0 && price.ObservedAt is not null)
        && (price.Source != CraftingPriceSource.ExchangeAverage || price.AverageSamples is > 0
            && price.AverageRangeStart is { } start && price.ObservedAt - start == TimeSpan.FromHours(24))
        && Enum.IsDefined(price.Source) && price.BundleQuantity > 0;

    private sealed class CalculationFailure(CraftingPlanStatus status, string code, int? item = null, int? recipe = null) : Exception(code)
    {
        public CraftingPlanStatus Status { get; } = status;
        public string Code { get; } = code;
        public int? ItemId { get; } = item;
        public int? RecipeId { get; } = recipe;
    }

    private sealed class Calculation(CraftingCatalog catalog, TransferCraftingPlanRequest request, int targetId, CancellationToken token)
    {
        private const int MaximumDepth = 64;
        private readonly Dictionary<int, Route> _routes = [];
        private readonly Dictionary<int, Mechanics> _mechanics = [];
        private readonly Dictionary<int, Counter> _steps = [];
        private readonly Dictionary<int, long> _shopping = [];
        private readonly Dictionary<int, decimal> _shoppingCosts = [];
        private readonly Dictionary<int, CraftingPrice> _usedPrices = [];
        private readonly Dictionary<int, long> _remainders = [];
        private readonly Dictionary<int, Stock> _inventory = [];
        private readonly HashSet<int> _path = [];
        private readonly HashSet<int> _goalItems = [];
        private readonly List<Requirement> _requirements = [];
        private readonly List<decimal> _costs = new(request.Samples);
        private readonly RandomSource _random = new(request.Seed);
        private readonly Dictionary<int, bool> _equipment = [];
        private decimal _sampleCost;
        private long _totalAttempts;
        private int _sampleAttempts;
        private int? _marketServer;

        private sealed record Route(CraftingRecipe? Recipe, CraftingPrice? Price);
        private sealed record Mechanics(CraftingRecipe Recipe, decimal Success, decimal Combo, Mechanics? Next,
            ImmutableArray<InputPlan> Inputs);
        private sealed record InputPlan(int ItemId, long Quantity, long Protected, ImmutableArray<int> Alternatives);
        private sealed record Requirement(int ItemId, long Quantity, ImmutableArray<int> Alternatives, bool MadeOnly = false);
        private readonly record struct Selection(int ItemId, bool MadeOnly);
        private readonly record struct Consumption(int ItemId, long Quantity, long Protected, bool MadeOnly);
        private sealed class Counter { public long Attempts; public long Successes; public long Failures; }
        private readonly record struct Stock(long Owned, long Made, long Bought)
        {
            public long Total => checked(Owned + Made + Bought);
            public static Stock operator +(Stock left, Stock right) => new(checked(left.Owned + right.Owned),
                checked(left.Made + right.Made), checked(left.Bought + right.Bought));
        }

        public CraftingPlanResult Calculate(CraftingRecipe root)
        {
            var rules = GetMechanics(root, []);
            var goalRecipe = root; var goalRecipes = new HashSet<int>();
            while (goalRecipes.Add(goalRecipe.Id))
            {
                if (goalRecipes.Count > MaximumDepth)
                    throw new CalculationFailure(CraftingPlanStatus.LimitExceeded, "RecipeDepthLimit", recipe: root.Id);
                // An explicitly selected COMBO is an exact target. A base transfer retains its
                // established "base or better" semantics for the guaranteed upgrade.
                if (targetId == root.OutputItemId || goalRecipe.OutputItemId == targetId)
                    _goalItems.Add(goalRecipe.OutputItemId);
                if (goalRecipe.ComboRecipeId is not { } combo) break;
                goalRecipe = catalog.FindRecipe(combo)!;
            }
            if (!_goalItems.Contains(targetId) || !(_goalItems.Any(id => TargetProbability(rules, id) > 0)))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "TargetUnreachable", targetId, root.Id);
            _routes[targetId] = new(root, null);
            decimal totalCost = 0;
            for (var sample = 0; sample < request.Samples; sample++)
            {
                token.ThrowIfCancellationRequested();
                _inventory.Clear(); _path.Clear(); _requirements.Clear(); _sampleAttempts = 0; _sampleCost = 0;
                foreach (var (item, quantity) in request.OwnedItems)
                    if (quantity > 0) _inventory[item] = new(quantity, 0, 0);
                _path.Add(targetId);
                while (!GoalSatisfied) CraftOnce(root, rules);
                _costs.Add(_sampleCost); totalCost = checked(totalCost + _sampleCost);
                foreach (var (id, stock) in _inventory)
                {
                    var remainder = checked(stock.Made + stock.Bought);
                    if (remainder > 0) _remainders[id] = checked(_remainders.GetValueOrDefault(id) + remainder);
                }
            }
            token.ThrowIfCancellationRequested();
            _costs.Sort();
            var samples = (decimal)request.Samples;
            return new()
            {
                Status = CraftingPlanStatus.Complete, Samples = request.Samples, Seed = request.Seed,
                ExpectedCost = totalCost / samples, P90Cost = _costs[(int)Math.Ceiling(request.Samples * .9) - 1],
                Shopping = _shopping.OrderBy(row => row.Key).Select(row => new CraftingShoppingRow(row.Key,
                    row.Value / samples, _usedPrices[row.Key].UnitPrice, _shoppingCosts[row.Key] / samples,
                    _usedPrices[row.Key].Source)).ToImmutableArray(),
                Steps = _steps.OrderBy(row => row.Key).Select(row =>
                {
                    var rule = _mechanics[row.Key]; var count = row.Value;
                    return new CraftingStepResult(row.Key, count.Attempts / samples, count.Successes / samples,
                        count.Failures / samples, rule.Success, rule.Next is null ? null : rule.Combo);
                }).ToImmutableArray(),
                Byproducts = _remainders.OrderBy(row => row.Key).Select(row =>
                    new CraftingOutputResult(row.Key, row.Value / samples, null)).ToImmutableArray()
            };
        }

        private bool IsEquipment(int id)
        {
            if (!_equipment.TryGetValue(id, out var value)) _equipment[id] = value = TransferCraftingRules.IsEquipment(catalog, id);
            return value;
        }

        private Mechanics GetMechanics(CraftingRecipe recipe, HashSet<int> visited)
        {
            token.ThrowIfCancellationRequested();
            if (_mechanics.TryGetValue(recipe.Id, out var cached)) return cached;
            if (!visited.Add(recipe.Id)) throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", recipe: recipe.Id);
            if (visited.Count > MaximumDepth) throw new CalculationFailure(CraftingPlanStatus.LimitExceeded, "RecipeDepthLimit", recipe: recipe.Id);
            var transfer = TransferCraftingRules.IsTransfer(catalog, recipe);
            if (recipe.CraftingFee is null || !transfer && !recipe.MechanicsQualified
                || catalog.IsComboRecipe(recipe.Id) && (recipe.Ingredients.Length != 0 || recipe.CraftingFee != 0))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "MechanicsUnqualified", recipe: recipe.Id);
            if (recipe.RequiredProficiency is { } required && (!transfer || required > 0))
            {
                if (!request.ProficiencyLevels.TryGetValue(recipe.Profession, out var level))
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "ProficiencyUnknown", recipe: recipe.Id);
                if (level < required) throw new CalculationFailure(CraftingPlanStatus.Unknown, "ProficiencyTooLow", recipe: recipe.Id);
            }
            var success = 1m;
            if (!transfer && !catalog.TrySuccessChance(recipe, request.ProficiencyLevels, out success))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "SuccessRateUnavailable", recipe: recipe.Id);
            if (!CraftingCatalog.IsFactionCompatible(recipe.Faction, request.Faction))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "FactionUnavailable", recipe: recipe.Id);
            var activation = 0m; Mechanics? next = null;
            if (recipe.ComboRecipeId is { } combo)
            {
                if (recipe.ComboActivationChance is not { } chance)
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "ComboChanceMissing", recipe: recipe.Id);
                activation = chance;
                if (chance > 0) next = GetMechanics(catalog.FindRecipe(combo)!, visited);
            }
            visited.Remove(recipe.Id);
            var result = new Mechanics(recipe, success, activation, next, ResolveInputs(recipe));
            _mechanics[recipe.Id] = result; return result;
        }

        private ImmutableArray<InputPlan> ResolveInputs(CraftingRecipe recipe)
        {
            var transferIds = TransferCraftingRules.TransferGearOptions(recipe);
            var transferRows = recipe.Ingredients.Where(row => TransferCraftingRules.IsTransferIngredient(recipe, row)).ToArray();
            if (transferIds.Length > 0 && (transferRows.Length != 1 || transferRows[0].Quantity != 1))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "MechanicsUnqualified", recipe: recipe.Id);
            var ordinary = new Dictionary<int, long>(); var inputs = new List<InputPlan>();
            foreach (var ingredient in recipe.Ingredients)
            {
                var hasChoice = request.ReplacementChoices.TryGetValue(new(recipe.Id, ingredient.ItemId), out var selected);
                if (!hasChoice) selected = ingredient.ItemId;
                var transferIngredient = TransferCraftingRules.IsTransferIngredient(recipe, ingredient);
                if (transferIngredient && transferIds.Length > 1 && !hasChoice)
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "TransferGearSelectionMissing", ingredient.ItemId, recipe.Id);
                if (selected != ingredient.ItemId && !ingredient.ReplacementItemIds.Contains(selected))
                    throw new CalculationFailure(CraftingPlanStatus.Invalid, "ReplacementInvalid", selected, recipe.Id);
                if (transferIngredient && !transferIds.Contains(selected))
                    throw new CalculationFailure(CraftingPlanStatus.Invalid, "TransferGearSelectionInvalid", selected, recipe.Id);
                if (transferIngredient)
                    inputs.Add(new(selected, ingredient.Quantity, ingredient.Quantity, transferIds.Where(id => id != selected).ToImmutableArray()));
                else ordinary[selected] = checked(ordinary.GetValueOrDefault(selected) + ingredient.Quantity);
            }
            inputs.AddRange(ordinary.Select(row => new InputPlan(row.Key, row.Value, 0, [])));
            return inputs.OrderBy(row => row.Protected > 0 ? 0 : IsEquipment(row.ItemId) ? 1 : 2)
                .ThenBy(row => row.ItemId).ToImmutableArray();
        }

        private static decimal TargetProbability(Mechanics rule, int target) => rule.Success *
            ((rule.Recipe.OutputItemId == target ? 1m - rule.Combo : 0m)
                + (rule.Next is null ? 0m : rule.Combo * TargetProbability(rule.Next, target)));

        private Route ResolveRoute(int itemId)
        {
            if (_routes.TryGetValue(itemId, out var cached)) return cached;
            var acquisition = request.Acquisition.GetValueOrDefault(itemId, CraftingAcquisition.Buy);
            if (!Enum.IsDefined(acquisition)) throw new CalculationFailure(CraftingPlanStatus.Invalid, "AcquisitionInvalid", itemId);
            // The user's transfer policy forbids buying any equipment, even if a cache or caller supplies its price.
            if (!IsEquipment(itemId) && acquisition == CraftingAcquisition.Buy)
            {
                CraftingPrice? price = null;
                if (request.BuyPrices.TryGetValue(itemId, out var supplied))
                {
                    if (!ValidPrice(supplied, itemId)) throw new CalculationFailure(CraftingPlanStatus.Unknown, "PriceInvalid", itemId);
                    if (supplied.IsMarket)
                    {
                        if (catalog.FindItem(itemId)?.IsTradable != true)
                            throw new CalculationFailure(CraftingPlanStatus.Unknown, "TradeabilityUnqualified", itemId);
                        if (_marketServer is { } market && supplied.ServerId != market)
                            throw new CalculationFailure(CraftingPlanStatus.Unknown, "MarketMismatch", itemId);
                        _marketServer = supplied.ServerId;
                    }
                    if (supplied.Source == CraftingPriceSource.Npc && !Offers(itemId).Any(offer =>
                        offer.UnitPrice == supplied.UnitPrice && offer.BundleQuantity == supplied.BundleQuantity))
                        throw new CalculationFailure(CraftingPlanStatus.Unknown, "NpcConditionsUnqualified", itemId);
                    price = supplied;
                }
                else if (Offers(itemId).OrderBy(offer => offer.UnitPrice).ThenBy(offer => offer.BundleQuantity).FirstOrDefault() is { } npc)
                    price = new(itemId, npc.UnitPrice, CraftingPriceSource.Npc, BundleQuantity: npc.BundleQuantity);
                return _routes[itemId] = new(null, price);
            }
            CraftingRecipe? recipe;
            if (request.RecipeChoices.TryGetValue(itemId, out var selected))
            {
                var choice = catalog.FindRecipe(selected);
                if (choice is null || choice.OutputItemId != itemId || !CraftingCatalog.IsFactionCompatible(choice.Faction, request.Faction))
                    throw new CalculationFailure(CraftingPlanStatus.Invalid, "CraftRouteInvalid", itemId, selected);
                recipe = catalog.FindBaseRecipe(choice.Id);
            }
            else
            {
                var choices = TransferCraftingRules.ProducerChoices(catalog, itemId, request.Faction);
                if (choices.Length == 0) throw new CalculationFailure(CraftingPlanStatus.Unknown, "CraftRouteMissing", itemId);
                if (choices.Length > 1) throw new CalculationFailure(CraftingPlanStatus.Unknown, "CraftRouteAmbiguous", itemId);
                recipe = catalog.FindBaseRecipe(choices[0].Id);
            }
            if (recipe is null) throw new CalculationFailure(CraftingPlanStatus.Unknown, "CraftRouteMissing", itemId);
            return _routes[itemId] = new(recipe, null);
        }

        private IEnumerable<CraftingNpcOffer> Offers(int itemId) => catalog.NpcOffers.Where(offer =>
            offer.ItemId == itemId && offer.ConditionsQualified && CraftingCatalog.IsFactionCompatible(offer.Faction, request.Faction));

        private bool GoalSatisfied => _goalItems.Sum(id => _inventory.GetValueOrDefault(id).Made) >= request.TargetQuantity;
        // Newly made goal products are reserved. Existing owned copies never satisfy the goal.
        private long Available(int itemId, bool madeOnly = false)
        {
            var stock = _inventory.GetValueOrDefault(itemId);
            var availableMade = _goalItems.Contains(itemId) ? 0 : stock.Made;
            return madeOnly ? availableMade : checked(stock.Owned + availableMade + stock.Bought);
        }
        private bool TrySelect(Requirement requirement, out Selection selected)
        {
            if (Available(requirement.ItemId, requirement.MadeOnly) >= requirement.Quantity)
            { selected = new(requirement.ItemId, requirement.MadeOnly); return true; }
            foreach (var alternative in requirement.Alternatives)
                if (Available(alternative, madeOnly: true) >= requirement.Quantity)
                { selected = new(alternative, true); return true; }
            selected = default; return false;
        }
        private bool AbortRequested => GoalSatisfied || _requirements.Any(requirement => TrySelect(requirement, out _));

        private void Ensure(Requirement requirement)
        {
            token.ThrowIfCancellationRequested();
            if (TrySelect(requirement, out _) || AbortRequested) return;
            var itemId = requirement.ItemId;
            if (_path.Count >= MaximumDepth) throw new CalculationFailure(CraftingPlanStatus.LimitExceeded, "RecipeDepthLimit", itemId);
            if (!_path.Add(itemId)) throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", itemId);
            _requirements.Add(requirement);
            try
            {
                var route = ResolveRoute(itemId);
                if (route.Recipe is not { } recipe)
                {
                    if (route.Price is not { } price) throw new CalculationFailure(CraftingPlanStatus.Unknown, "InputPriceMissing", itemId);
                    if (requirement.MadeOnly) throw new CalculationFailure(CraftingPlanStatus.Unknown, "CraftRouteMissing", itemId);
                    if (AbortRequested) return;
                    var missing = requirement.Quantity - Available(itemId);
                    var bought = checked(((missing + price.BundleQuantity - 1) / price.BundleQuantity) * price.BundleQuantity);
                    var cost = checked(bought * price.UnitPrice);
                    _inventory[itemId] = _inventory.GetValueOrDefault(itemId) + new Stock(0, 0, bought);
                    _shopping[itemId] = checked(_shopping.GetValueOrDefault(itemId) + bought);
                    _shoppingCosts[itemId] = checked(_shoppingCosts.GetValueOrDefault(itemId) + cost);
                    _usedPrices[itemId] = price; _sampleCost = checked(_sampleCost + cost); return;
                }
                var rules = GetMechanics(recipe, []);
                if (!_goalItems.Any(id => TargetProbability(rules, id) > 0)
                    && !_requirements.Any(pending => TargetProbability(rules, pending.ItemId) > 0
                        || pending.Alternatives.Any(id => TargetProbability(rules, id) > 0)))
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "TargetUnreachable", itemId, recipe.Id);
                while (!TrySelect(requirement, out _))
                {
                    if (AbortRequested) return;
                    CraftOnce(recipe, rules);
                }
            }
            finally { _requirements.RemoveAt(_requirements.Count - 1); _path.Remove(itemId); }
        }

        private void CraftOnce(CraftingRecipe recipe, Mechanics rules)
        {
            token.ThrowIfCancellationRequested();
            Consumption[]? inputs = null;
            for (var pass = 0; pass < MaximumDepth; pass++)
            {
                if (AbortRequested) return;
                // Gear first: its producer may already make the goal or another accepted transfer input.
                foreach (var input in rules.Inputs)
                {
                    Ensure(new(input.ItemId, input.Quantity, input.Alternatives));
                    if (AbortRequested) return;
                }
                var uses = new List<Consumption>();
                foreach (var input in rules.Inputs)
                {
                    if (!TrySelect(new(input.ItemId, input.Quantity, input.Alternatives), out var selected)) break;
                    uses.Add(new(selected.ItemId, input.Quantity, input.Protected, selected.MadeOnly));
                }
                if (uses.Count != rules.Inputs.Length) continue;
                var grouped = uses.GroupBy(row => (row.ItemId, row.MadeOnly)).Select(group => new Consumption(
                    group.Key.ItemId, group.Sum(row => row.Quantity), group.Sum(row => row.Protected), group.Key.MadeOnly)).ToArray();
                var ready = true;
                foreach (var group in grouped.GroupBy(row => row.ItemId))
                {
                    var made = group.Where(row => row.MadeOnly).Sum(row => row.Quantity);
                    var total = group.Sum(row => row.Quantity);
                    if (Available(group.Key, madeOnly: true) < made)
                    { ready = false; Ensure(new(group.Key, made, [], true)); }
                    if (AbortRequested) return;
                    if (Available(group.Key) < total)
                    { ready = false; Ensure(new(group.Key, total, [])); }
                    if (AbortRequested) return;
                }
                if (ready) { inputs = grouped; break; }
            }
            if (inputs is null) throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", recipe.OutputItemId, recipe.Id);
            if (AbortRequested) return;
            var protectedStock = new Dictionary<int, Stock>();
            // Make-only alternatives must never consume an existing, unselected owned copy of that ID.
            foreach (var input in inputs.OrderByDescending(row => row.MadeOnly))
            {
                if (input.Protected > 0) protectedStock[input.ItemId] = protectedStock.GetValueOrDefault(input.ItemId)
                    + Take(input.ItemId, input.Protected, input.MadeOnly);
                Take(input.ItemId, input.Quantity - input.Protected, input.MadeOnly);
            }
            _sampleCost = checked(_sampleCost + recipe.CraftingFee!.Value);
            var result = Outcome(rules);
            if (result is null)
                foreach (var (id, stock) in protectedStock) _inventory[id] = _inventory.GetValueOrDefault(id) + stock;
            else _inventory[result.OutputItemId] = _inventory.GetValueOrDefault(result.OutputItemId) + new Stock(0, result.OutputQuantity, 0);
        }

        private CraftingRecipe? Outcome(Mechanics rule)
        {
            token.ThrowIfCancellationRequested();
            if (++_sampleAttempts > request.MaximumAttemptsPerSample || ++_totalAttempts > request.MaximumTotalAttempts)
                throw new CalculationFailure(CraftingPlanStatus.LimitExceeded, "SimulationLimit", recipe: rule.Recipe.Id);
            var counter = _steps.GetValueOrDefault(rule.Recipe.Id);
            if (counter is null) _steps[rule.Recipe.Id] = counter = new();
            counter.Attempts++;
            if (!_random.Roll(rule.Success)) { counter.Failures++; return null; }
            counter.Successes++;
            return rule.Next is not null && _random.Roll(rule.Combo) ? Outcome(rule.Next) : rule.Recipe;
        }

        private Stock Take(int itemId, long quantity, bool madeOnly = false)
        {
            if (quantity == 0) return default;
            var stock = _inventory.GetValueOrDefault(itemId);
            if (Available(itemId, madeOnly) < quantity) throw new CalculationFailure(CraftingPlanStatus.Invalid, "InventoryConflict", itemId);
            var owned = madeOnly ? 0 : Math.Min(stock.Owned, quantity); quantity -= owned;
            var made = _goalItems.Contains(itemId) ? 0 : Math.Min(stock.Made, quantity); quantity -= made;
            var bought = quantity;
            _inventory[itemId] = new(stock.Owned - owned, stock.Made - made, stock.Bought - bought);
            return new(owned, made, bought);
        }
    }

    private sealed class RandomSource(int seed)
    {
        private ulong _state = unchecked((ulong)(long)seed);
        public bool Roll(decimal chance)
        {
            if (chance == 0) return false;
            if (chance == 1) return true;
            _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
            var value = _state;
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            value ^= value >> 31;
            return (value >> 11) * (1.0 / 9007199254740992.0) < (double)chance;
        }
    }
}
