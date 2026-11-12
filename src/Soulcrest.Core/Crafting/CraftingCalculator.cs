using System.Collections.Immutable;

namespace Soulcrest.Core.Crafting;

/// <summary>
/// Simulates a fixed number of starting recipe attempts, including shared intermediate stock.
/// Qualified recipes alone define mechanics; price feeds never qualify a recipe.
/// </summary>
public sealed class CraftingCalculator(CraftingCatalog catalog)
{
    public CraftingPlanResult Calculate(CraftingPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.TargetAttempts is <= 0 or > 100_000 || request.Samples is <= 0 or > 100_000
            || request.MaximumAttemptsPerSample <= 0 || request.MaximumTotalAttempts <= 0
            || request.SalesTaxPercent is < 0 or > 100 || request.RegistrationFeePercent is < 0 or > 100
            || request.SalesTaxPercent + request.RegistrationFeePercent > 100
            || request.OwnedItems.Any(row => row.Key <= 0 || row.Value < 0)
            || request.ProficiencyLevels.Any(row => !CraftingCatalog.Professions.Contains(row.Key) || row.Value is < 0 or > 100))
            return Failed(CraftingPlanStatus.Invalid, "InvalidRequest");
        var selected = catalog.FindRecipe(request.RecipeId);
        if (selected is null) return Failed(CraftingPlanStatus.Invalid, "RecipeMissing", recipe: request.RecipeId);
        var targetId = request.TargetItemId ?? selected.OutputItemId;
        if (catalog.FindItem(targetId) is null) return Failed(CraftingPlanStatus.Invalid, "ItemMissing", targetId);
        try
        {
            var run = new Calculation(catalog, request, targetId, cancellationToken);
            return run.Calculate(catalog.FindBaseRecipe(selected.Id)!);
        }
        catch (CalculationFailure error)
        { return Failed(error.Status, error.Code, error.ItemId, error.RecipeId); }
        catch (OverflowException)
        { return Failed(CraftingPlanStatus.LimitExceeded, "NumericLimit"); }

        CraftingPlanResult Failed(CraftingPlanStatus status, string code, int? item = null, int? recipe = null) =>
            new() { Status = status, Issues = [new(code, item, recipe)], Seed = request.Seed };
    }

    private static bool ValidPrice(CraftingPrice price, int item, bool sell = false) =>
        price.ItemId == item && price.UnitPrice >= 0 && (!price.IsMarket || price.UnitPrice > 0)
        && (!price.IsMarket || price.ServerId is > 0 && price.ObservedAt is not null)
        && (price.Source != CraftingPriceSource.ExchangeAverage || price.AverageSamples is > 0
            && price.AverageRangeStart is { } start && price.ObservedAt - start == TimeSpan.FromHours(24))
        && Enum.IsDefined(price.Source) && price.BundleQuantity > 0 && (!sell || price.Source != CraftingPriceSource.Npc);

    private sealed class CalculationFailure(CraftingPlanStatus status, string code, int? item = null, int? recipe = null) : Exception(code)
    {
        public CraftingPlanStatus Status { get; } = status;
        public string Code { get; } = code;
        public int? ItemId { get; } = item;
        public int? RecipeId { get; } = recipe;
    }

    private sealed class Calculation(CraftingCatalog catalog, CraftingPlanRequest request, int targetId, CancellationToken token)
    {
        private readonly Dictionary<int, Route> _routes = [];
        private readonly Dictionary<int, Mechanics> _mechanics = [];
        private readonly Dictionary<int, Counter> _steps = [];
        private readonly Dictionary<int, long> _shopping = [];
        private readonly Dictionary<int, decimal> _shoppingCosts = [];
        private readonly Dictionary<int, CraftingPrice> _usedPrices = [];
        private readonly Dictionary<int, long> _byproducts = [];
        private readonly Dictionary<int, decimal> _byproductValues = [];
        private readonly Dictionary<int, long> _outputs = [];
        private readonly Dictionary<int, decimal> _outputValues = [];
        private readonly Dictionary<int, CraftingPrice> _outputPrices = [];
        private readonly Dictionary<int, long> _sampleOutputs = [];
        private readonly Dictionary<int, Stock> _inventory = [];
        private readonly HashSet<int> _path = [];
        private readonly List<decimal> _costs = new(request.Samples);
        private readonly RandomSource _random = new(request.Seed);
        private decimal _sampleCost;
        private long _totalAttempts;
        private int _sampleAttempts;
        private int? _marketServer;

        private sealed record Route(CraftingRecipe? Recipe, CraftingPrice? Price);
        private sealed record Mechanics(CraftingRecipe Recipe, decimal Success, decimal Combo, Mechanics? Next);
        private sealed class Counter { public long Attempts; public long Successes; public long Failures; }
        private readonly record struct Stock(long Owned, long Made, long Bought)
        {
            public long Total => checked(Owned + Made + Bought);
            public static Stock operator +(Stock left, Stock right) =>
                new(checked(left.Owned + right.Owned), checked(left.Made + right.Made), checked(left.Bought + right.Bought));
        }

        public CraftingPlanResult Calculate(CraftingRecipe root)
        {
            var mechanics = GetMechanics(root, []);
            if (!catalog.Recipes.Any(row => row.OutputItemId == targetId && catalog.FindBaseRecipe(row.Id)?.Id == root.Id))
                throw new CalculationFailure(CraftingPlanStatus.Invalid, "TargetUnreachable", targetId, root.Id);
            QualifyOutputPrices(mechanics, 1m);
            _routes[targetId] = new(root, null);
            decimal totalCost = 0, totalRevenue = 0, totalFees = 0;
            for (var sample = 0; sample < request.Samples; sample++)
            {
                token.ThrowIfCancellationRequested();
                _inventory.Clear(); _path.Clear(); _sampleOutputs.Clear(); _sampleAttempts = 0; _sampleCost = 0;
                foreach (var (item, quantity) in request.OwnedItems)
                    if (quantity > 0) _inventory[item] = new(quantity, 0, 0);
                _path.Add(targetId);
                // User request 2026-11-10: each starting attempt counts, regardless of its final outcome.
                // Automatic COMBO rolls and retries for intermediate materials do not increase this target.
                for (var attempt = 0; attempt < request.TargetAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (CraftOnce(root, mechanics) is { } output)
                        _sampleOutputs[output.OutputItemId] = checked(_sampleOutputs.GetValueOrDefault(output.OutputItemId) + output.OutputQuantity);
                }
                foreach (var (id, quantity) in _sampleOutputs)
                {
                    var price = _outputPrices[id];
                    var outputRevenue = Net(price.UnitPrice, quantity);
                    _outputs[id] = checked(_outputs[id] + quantity);
                    _outputValues[id] = checked(_outputValues.GetValueOrDefault(id) + outputRevenue);
                    totalRevenue = checked(totalRevenue + outputRevenue);
                    totalFees = checked(totalFees + decimal.Floor(price.UnitPrice * quantity) - outputRevenue);
                }
                _costs.Add(_sampleCost); totalCost = checked(totalCost + _sampleCost);
                foreach (var (id, stock) in _inventory)
                {
                    if (stock.Made <= 0) continue;
                    _byproducts[id] = checked(_byproducts.GetValueOrDefault(id) + stock.Made);
                    if (catalog.FindItem(id)?.IsTradable == true && request.SellPrices.TryGetValue(id, out var price)
                        && ValidPrice(price, id, sell: true)
                        && (!price.IsMarket || _marketServer is null || price.ServerId == _marketServer))
                        _byproductValues[id] = checked(_byproductValues.GetValueOrDefault(id) + Net(price.UnitPrice, stock.Made));
                }
            }
            _costs.Sort();
            var samples = (decimal)request.Samples;
            var expected = totalCost / samples;
            var revenue = totalRevenue / samples;
            return new()
            {
                Status = CraftingPlanStatus.Complete, Samples = request.Samples, Seed = request.Seed,
                ExpectedCost = expected, P90Cost = _costs[(int)Math.Ceiling(request.Samples * .9) - 1],
                ExpectedNetRevenue = revenue, ExpectedProfit = revenue - expected, ExpectedFees = totalFees / samples,
                Shopping = _shopping.OrderBy(row => row.Key).Select(row =>
                    new CraftingShoppingRow(row.Key, row.Value / samples, _usedPrices[row.Key].UnitPrice,
                        _shoppingCosts[row.Key] / samples, _usedPrices[row.Key].Source)).ToImmutableArray(),
                Steps = _steps.OrderBy(row => row.Key).Select(row =>
                {
                    var rule = _mechanics[row.Key]; var count = row.Value;
                    return new CraftingStepResult(row.Key, count.Attempts / samples, count.Successes / samples,
                        count.Failures / samples, rule.Success, rule.Next is null ? null : rule.Combo);
                }).ToImmutableArray(),
                Outputs = _outputs.OrderBy(row => row.Key).Select(row => new CraftingOutputResult(row.Key,
                    row.Value / samples, _outputValues.GetValueOrDefault(row.Key) / samples)).ToImmutableArray(),
                Byproducts = _byproducts.OrderBy(row => row.Key).Select(row => new CraftingOutputResult(row.Key,
                    row.Value / samples, _byproductValues.TryGetValue(row.Key, out var value) ? value / samples : null)).ToImmutableArray()
            };
        }

        private decimal Net(decimal unit, long quantity) => decimal.Floor(checked(unit * quantity)
            * (1m - request.SalesTaxPercent / 100m - request.RegistrationFeePercent / 100m));

        private void QualifyOutputPrices(Mechanics rule, decimal reachChance)
        {
            var id = rule.Recipe.OutputItemId;
            _outputs.TryAdd(id, 0);
            var chance = reachChance * rule.Success;
            var finalChance = chance * (rule.Next is null ? 1m : 1m - rule.Combo);
            if (finalChance > 0)
            {
                var item = catalog.FindItem(id)!;
                if (item.IsTradable != true) throw new CalculationFailure(CraftingPlanStatus.Unknown,
                    item.IsTradable == false ? "TargetNotTradable" : "TargetTradeabilityUnknown", id);
                if (!request.SellPrices.TryGetValue(id, out var price) || !ValidPrice(price, id, sell: true))
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "OutputPriceMissing", id);
                if (price.IsMarket)
                {
                    if (_marketServer is { } market && price.ServerId != market)
                        throw new CalculationFailure(CraftingPlanStatus.Unknown, "MarketMismatch", id);
                    _marketServer = price.ServerId;
                }
                _outputPrices[id] = price;
            }
            if (rule.Next is not null) QualifyOutputPrices(rule.Next, chance * rule.Combo);
        }

        private Mechanics GetMechanics(CraftingRecipe recipe, HashSet<int> visited)
        {
            if (_mechanics.TryGetValue(recipe.Id, out var cached)) return cached;
            if (!visited.Add(recipe.Id)) throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", recipe: recipe.Id);
            if (!recipe.MechanicsQualified || recipe.CraftingFee is null)
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "MechanicsUnqualified", recipe: recipe.Id);
            if (catalog.IsComboRecipe(recipe.Id) && (recipe.Ingredients.Length != 0 || recipe.CraftingFee != 0))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "MechanicsUnqualified", recipe: recipe.Id);
            if (recipe.RequiredProficiency is { } required)
            {
                if (!request.ProficiencyLevels.TryGetValue(recipe.Profession, out var level))
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "ProficiencyUnknown", recipe: recipe.Id);
                if (level < required)
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "ProficiencyTooLow", recipe: recipe.Id);
            }
            if (!catalog.TrySuccessChance(recipe, request.ProficiencyLevels, out var success))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "SuccessRateUnavailable", recipe: recipe.Id);
            if (!CraftingCatalog.IsFactionCompatible(recipe.Faction, request.Faction))
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "FactionUnavailable", recipe: recipe.Id);
            var activation = 0m; Mechanics? next = null;
            if (recipe.ComboRecipeId is { } comboId)
            {
                if (recipe.ComboActivationChance is not { } chance)
                    throw new CalculationFailure(CraftingPlanStatus.Unknown, "ComboChanceMissing", recipe: recipe.Id);
                activation = chance;
                if (chance > 0) next = GetMechanics(catalog.FindRecipe(comboId)!, visited);
            }
            visited.Remove(recipe.Id);
            var result = new Mechanics(recipe, success, activation, next);
            _mechanics[recipe.Id] = result; return result;
        }

        private static decimal TargetProbability(Mechanics rule, int target) => rule.Success *
            ((rule.Recipe.OutputItemId == target ? 1m - rule.Combo : 0m)
                + (rule.Next is null ? 0m : rule.Combo * TargetProbability(rule.Next, target)));

        private Route ResolveRoute(int itemId)
        {
            if (_routes.TryGetValue(itemId, out var cached)) return cached;
            var acquisition = request.Acquisition.GetValueOrDefault(itemId, CraftingAcquisition.Buy);
            if (!Enum.IsDefined(acquisition)) throw new CalculationFailure(CraftingPlanStatus.Invalid, "AcquisitionInvalid", itemId);
            if (acquisition == CraftingAcquisition.Buy)
            {
                CraftingPrice? price = null;
                if (request.BuyPrices.TryGetValue(itemId, out var supplied))
                {
                    if (!ValidPrice(supplied, itemId)) throw new CalculationFailure(CraftingPlanStatus.Unknown, "PriceInvalid", itemId);
                    if (supplied.IsMarket && catalog.FindItem(itemId)?.IsTradable != true)
                        throw new CalculationFailure(CraftingPlanStatus.Unknown, "TradeabilityUnqualified", itemId);
                    if (supplied.IsMarket)
                    {
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
            if (request.RecipeChoices.TryGetValue(itemId, out var chosen)) recipe = catalog.FindBaseRecipe(chosen);
            else recipe = catalog.Recipes.Where(row => row.OutputItemId == itemId).Select(row => catalog.FindBaseRecipe(row.Id)!)
                .Where(row => CraftingCatalog.IsFactionCompatible(row.Faction, request.Faction))
                .OrderBy(row => row.RequiredProficiency ?? 0).ThenBy(row => row.Id).FirstOrDefault();
            if (recipe is null) throw new CalculationFailure(CraftingPlanStatus.Unknown, "CraftRouteMissing", itemId);
            if (TargetProbability(GetMechanics(recipe, []), itemId) <= 0)
                throw new CalculationFailure(CraftingPlanStatus.Unknown, "TargetUnreachable", itemId, recipe.Id);
            return _routes[itemId] = new(recipe, null);
        }

        private IEnumerable<CraftingNpcOffer> Offers(int itemId) => catalog.NpcOffers.Where(offer =>
            offer.ItemId == itemId && offer.ConditionsQualified && CraftingCatalog.IsFactionCompatible(offer.Faction, request.Faction));

        private long Available(int itemId) => _inventory.GetValueOrDefault(itemId).Total;

        private void Ensure(int itemId, long quantity)
        {
            if (Available(itemId) >= quantity) return;
            if (_path.Count >= 64 || !_path.Add(itemId))
                throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", itemId);
            try
            {
                var route = ResolveRoute(itemId);
                if (route.Recipe is not { } recipe)
                {
                    if (route.Price is not { } price) throw new CalculationFailure(CraftingPlanStatus.Unknown, "InputPriceMissing", itemId);
                    var missing = quantity - Available(itemId);
                    var bought = checked(((missing + price.BundleQuantity - 1) / price.BundleQuantity) * price.BundleQuantity);
                    var cost = checked(bought * price.UnitPrice);
                    _inventory[itemId] = _inventory.GetValueOrDefault(itemId) + new Stock(0, 0, bought);
                    _shopping[itemId] = checked(_shopping.GetValueOrDefault(itemId) + bought);
                    _shoppingCosts[itemId] = checked(_shoppingCosts.GetValueOrDefault(itemId) + cost);
                    _usedPrices[itemId] = price; _sampleCost = checked(_sampleCost + cost); return;
                }
                var rules = GetMechanics(recipe, []);
                while (Available(itemId) < quantity)
                {
                    if (CraftOnce(recipe, rules) is { } result)
                        _inventory[result.OutputItemId] = _inventory.GetValueOrDefault(result.OutputItemId) + new Stock(0, result.OutputQuantity, 0);
                }
            }
            finally { _path.Remove(itemId); }
        }

        private CraftingRecipe? CraftOnce(CraftingRecipe recipe, Mechanics rules)
        {
            token.ThrowIfCancellationRequested();
            var inputs = new Dictionary<int, (long Quantity, long Protected)>();
            foreach (var ingredient in recipe.Ingredients)
            {
                var selected = request.ReplacementChoices.GetValueOrDefault(new(recipe.Id, ingredient.ItemId), ingredient.ItemId);
                if (selected != ingredient.ItemId && !ingredient.ReplacementItemIds.Contains(selected))
                    throw new CalculationFailure(CraftingPlanStatus.Invalid, "ReplacementInvalid", selected, recipe.Id);
                var row = inputs.GetValueOrDefault(selected);
                var retained = recipe.TransferItemIds.Contains(selected) ? ingredient.Quantity : 0;
                inputs[selected] = (checked(row.Quantity + ingredient.Quantity), checked(row.Protected + retained));
            }
            // Acquire all requirements before consuming any of them, so shared stock cannot be counted twice.
            var passes = 0;
            while (inputs.Any(row => Available(row.Key) < row.Value.Quantity))
            {
                if (++passes > 64) throw new CalculationFailure(CraftingPlanStatus.Invalid, "RecipeCycle", recipe.OutputItemId, recipe.Id);
                foreach (var (id, input) in inputs) Ensure(id, input.Quantity);
            }
            var protectedStock = new Dictionary<int, Stock>();
            foreach (var (id, input) in inputs)
            {
                if (input.Protected > 0) protectedStock[id] = Take(id, input.Protected);
                Take(id, input.Quantity - input.Protected);
            }
            _sampleCost = checked(_sampleCost + recipe.CraftingFee!.Value);
            var result = Outcome(rules);
            if (result is null)
                foreach (var (id, stock) in protectedStock) _inventory[id] = _inventory.GetValueOrDefault(id) + stock;
            return result;
        }

        private CraftingRecipe? Outcome(Mechanics rule)
        {
            if (++_sampleAttempts > request.MaximumAttemptsPerSample || ++_totalAttempts > request.MaximumTotalAttempts)
                throw new CalculationFailure(CraftingPlanStatus.LimitExceeded, "SimulationLimit", recipe: rule.Recipe.Id);
            var counter = _steps.GetValueOrDefault(rule.Recipe.Id);
            if (counter is null) _steps[rule.Recipe.Id] = counter = new();
            counter.Attempts++;
            if (!_random.Roll(rule.Success)) { counter.Failures++; return null; }
            counter.Successes++;
            if (rule.Next is not null && _random.Roll(rule.Combo)) return Outcome(rule.Next);
            return rule.Recipe;
        }

        private Stock Take(int itemId, long quantity)
        {
            if (quantity == 0) return default;
            var stock = _inventory.GetValueOrDefault(itemId);
            if (stock.Total < quantity) throw new InvalidOperationException("Insufficient simulated stock.");
            var owned = Math.Min(stock.Owned, quantity); quantity -= owned;
            var made = Math.Min(stock.Made, quantity); quantity -= made;
            var bought = quantity;
            _inventory[itemId] = new(stock.Owned - owned, stock.Made - made, stock.Bought - bought);
            return new(owned, made, bought);
        }
    }

    /// <summary>Version-independent deterministic SplitMix64 stream; seed is part of the result.</summary>
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
