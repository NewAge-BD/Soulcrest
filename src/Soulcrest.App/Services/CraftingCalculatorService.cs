using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Crafting;

namespace Soulcrest.App.Services;

/// <summary>Owns a reviewable crafting scenario. Shares market transport/cache, never starts game tracking.</summary>
public sealed class CraftingCalculatorService : IDisposable
{
    private readonly object _gate = new();
    private readonly SettingsService _settings;
    private readonly IExchangeMarketClient _client;
    private readonly ExchangeMarketCache _cache;
    private readonly Func<CraftingProficiencySnapshot> _proficiency;
    private readonly Func<(string? ProfileId, int? OwnServer)> _context;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly CraftingScenarioStore? _scenarioStore;
    private readonly CraftingScenarioDraft _defaultDraft;
    private Action? _detach;
    private CancellationTokenSource? _pending;
    private CraftingCalculatorSnapshot _snapshot = new();
    private SourceKey? _sources;
    private int _consumers, _disposed;
    private Task _work = Task.CompletedTask;
    private sealed record SourceKey(string? Profile, int? OwnServer, ExchangeMarketProfileSettings Market,
        bool Online, bool Fresh, bool Saved, DateTimeOffset? At, long Generation, long Sequence, string Levels);

    public CraftingCatalog Catalog { get; }
    public CraftingCalculatorSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool Active { get { lock (_gate) return _disposed == 0 && _consumers > 0; } }
    public event Action? Changed;

    public CraftingCalculatorService(SettingsService settings, IExchangeMarketClient client, ExchangeMarketCache cache,
        CraftingProficiencyStateService proficiency, INetworkFarmSource source, ExplorationService profiles, CraftingScenarioStore scenarios)
        : this(settings, client, cache, CraftingCatalog.Default, () => proficiency.Snapshot, () =>
        {
            var own = source.Running ? source is NetworkLootService native ? native.OwnCharacterSnapshot : source.OwnCharacter : null;
            var bound = own is null ? null : profiles.TryGetBoundCharacter(own);
            var selected = bound?.ProfileId ?? proficiency.Snapshot.ProfileId ?? profiles.ActiveId;
            return (selected, bound?.ProfileId == selected ? own?.Identity.ServerId : null);
        }, TimeProvider.System, scenarios)
    {
        proficiency.Changed += SourcesChanged; profiles.Changed += SourcesChanged; profiles.CharacterRemoved += ProfileRemoved;
        _detach = () => { proficiency.Changed -= SourcesChanged; profiles.Changed -= SourcesChanged; profiles.CharacterRemoved -= ProfileRemoved; };
    }

    internal CraftingCalculatorService(SettingsService settings, IExchangeMarketClient client, ExchangeMarketCache cache,
        CraftingCatalog catalog, Func<CraftingProficiencySnapshot> proficiency,
        Func<(string? ProfileId, int? OwnServer)> context, TimeProvider? time = null, CraftingScenarioStore? store = null)
    {
        _settings = settings; _client = client; _cache = cache; Catalog = catalog;
        _proficiency = proficiency; _context = context; _time = time ?? TimeProvider.System; _scenarioStore = store;
        if (_scenarioStore is not null) _scenarioStore.Changed += ScenarioStorageChanged;
        _timer = _time.CreateTimer(_ => RefreshTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _settings.Changed += SourcesChanged;
        var first = catalog.Recipes.Where(row => !catalog.IsComboRecipe(row.Id) && row.Profession == "Blacksmithing"
            && row.Subcategory == "Greatsword" && row.MechanicsQualified)
            .OrderBy(row => row.RequiredProficiency).ThenBy(row => row.Id).FirstOrDefault();
        if (first is not null) _snapshot = _snapshot with { SelectedRecipeId = first.Id, TargetItemId = first.OutputItemId };
        _defaultDraft = CraftingScenarioDraft.Capture(_snapshot, _snapshot.TargetQuantity);
        UpdateSources();
    }

    public IDisposable Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            _consumers++;
            if (_consumers == 1) _timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        Queue();
        return new Lease(this);
    }
    private sealed class Lease(CraftingCalculatorService owner) : IDisposable
    {
        private CraftingCalculatorService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
    private void Release()
    {
        CancellationTokenSource? pending = null;
        var flush = false;
        lock (_gate)
        {
            if (_consumers > 0 && --_consumers == 0)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                pending = _pending;
                flush = true;
                PublishLocked(_snapshot with { Busy = false, Revision = _snapshot.Revision + 1 });
            }
        }
        Cancel(pending);
        if (flush) _scenarioStore?.Flush();
    }

    public void SetProfession(string profession)
    {
        var definition = CraftingCalculatorPresentation.BrowserCategories.FirstOrDefault(row => row.Id == profession);
        if (definition is null) return;
        Edit(state => state with { Profession = profession, Subcategory = definition.Subcategories[0], Query = "" }, false);
        var selectedTransfer = Snapshot.SelectedRecipeId is { } id && Catalog.FindRecipe(id) is { } selected
            && TransferCraftingRules.IsTransfer(Catalog, selected);
        if (profession == CraftingCalculatorPresentation.TransferCategory || selectedTransfer) SelectFirstBrowserRecipe();
    }
    public void SetSubcategory(string value)
    {
        if (!CraftingCalculatorPresentation.BrowserCategories.First(row => row.Id == Snapshot.Profession).Subcategories.Contains(value)) return;
        Edit(state => state with { Subcategory = value, Query = "" }, false);
        if (Snapshot.Profession == CraftingCalculatorPresentation.TransferCategory) SelectFirstBrowserRecipe();
    }
    private void SelectFirstBrowserRecipe()
    {
        var state = Snapshot;
        var first = Catalog.Recipes.Where(row => !Catalog.IsComboRecipe(row.Id)
            && CraftingCalculatorPresentation.MatchesBrowser(Catalog, row, state.Profession, state.Subcategory)
            && (state.Faction is null || CraftingCatalog.IsFactionCompatible(row.Faction, state.Faction)))
            .OrderBy(row => row.RequiredProficiency ?? 0).ThenBy(row => row.Id).FirstOrDefault();
        if (first is not null) SelectRecipe(first.Id);
        else Edit(value => value with { SelectedRecipeId = null, TargetItemId = null });
    }
    public void SetQuery(string? value) => Edit(state => state with { Query = (value ?? "")[..Math.Min(value?.Length ?? 0, 160)] }, false);
    public void SelectRecipe(int id)
    {
        if (Catalog.FindRecipe(id) is not { } recipe || !CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, Snapshot.Faction)) return;
        Edit(state => state with { SelectedRecipeId = id, TargetItemId = recipe.OutputItemId, TargetQuantity = 1,
            Profession = TransferCraftingRules.IsTransfer(Catalog, recipe) ? CraftingCalculatorPresentation.TransferCategory : recipe.Profession,
            Subcategory = recipe.Subcategory,
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty,
            RecipeChoices = ImmutableDictionary<int, int>.Empty,
            ReplacementChoices = ImmutableDictionary<CraftingIngredientKey, int>.Empty });
    }
    public void SetTarget(int recipeId, int itemId)
    {
        if (Catalog.FindRecipe(recipeId) is not { } recipe || recipe.OutputItemId != itemId
            || !CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, Snapshot.Faction)) return;
        Edit(state => state with { SelectedRecipeId = recipeId, TargetItemId = itemId });
    }
    public void SetQuantity(int value)
    {
        if (value < 1 || value > 100_000) return;
        Edit(state => state with { TargetQuantity = value });
    }
    public void SetSimulationSamples(int value)
    {
        if (value is < 1 or > 100_000) return;
        Edit(state => state with { SimulationSamples = value });
    }
    public void SetProficiency(string profession, int? level)
    {
        if (!CraftingCatalog.Professions.Contains(profession) || level is < 0 or > 100) return;
        Edit(state => state with { SimulatedLevels = level is { } number
            ? state.SimulatedLevels.SetItem(profession, number) : state.SimulatedLevels.Remove(profession) });
    }
    public void ResetProficiencies() => Edit(state => state with { SimulatedLevels = ImmutableDictionary<string, int>.Empty });
    public void SetOwnership(int itemId, bool owned)
    {
        if (!TransferCraftingRules.IsEquipment(Catalog, itemId)) return;
        Edit(state => state with { OwnedItems = owned ? state.OwnedItems.SetItem(itemId, 1) : state.OwnedItems.Remove(itemId) });
    }
    public void SetOwnedQuantity(int itemId, long quantity)
    {
        if (Catalog.FindItem(itemId) is null || quantity is < 0 or > 1_000_000_000) return;
        Edit(state => state with { OwnedItems = quantity > 0 ? state.OwnedItems.SetItem(itemId, quantity) : state.OwnedItems.Remove(itemId) });
    }
    public void SetAcquisition(int itemId, CraftingAcquisition value, int? recipeId = null)
    {
        if (Catalog.FindItem(itemId) is null || !Enum.IsDefined(value)) return;
        if (value == CraftingAcquisition.Buy && Snapshot.SelectedRecipeId is { } rootId
            && Catalog.FindRecipe(rootId) is { } root && TransferCraftingRules.IsTransfer(Catalog, root)
            && TransferCraftingRules.IsEquipment(Catalog, itemId)) return;
        if (value == CraftingAcquisition.Craft)
        {
            // No silent choice among recipes. The UI supplies the exact recipe selected by the user.
            if (recipeId is not { } id || Catalog.FindRecipe(id) is not { } recipe || recipe.OutputItemId != itemId
                || !CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, Snapshot.Faction)) return;
        }
        Edit(state => state with { Acquisition = state.Acquisition.SetItem(itemId, value),
            RecipeChoices = recipeId is { } selected ? state.RecipeChoices.SetItem(itemId, selected) : state.RecipeChoices.Remove(itemId) });
    }
    // User request 2026-11-12: apply a map selection as one scenario edit and one recalculation.
    public void SetAcquisitions(ImmutableArray<CraftingSourceSelection> selections)
    {
        if (selections.IsDefaultOrEmpty || selections.Length > 128) return;
        var state = Snapshot;
        foreach (var selection in selections)
        {
            if (Catalog.FindItem(selection.ItemId) is null) return;
            if (selection.RecipeId is { } recipeId)
            {
                if (Catalog.FindRecipe(recipeId) is not { } recipe || recipe.OutputItemId != selection.ItemId
                    || !CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, state.Faction)) return;
            }
            else if (state.SelectedRecipeId is { } rootId && Catalog.FindRecipe(rootId) is { } root
                && TransferCraftingRules.IsTransfer(Catalog, root) && TransferCraftingRules.IsEquipment(Catalog, selection.ItemId)) return;
        }
        Edit(current =>
        {
            var acquisition = current.Acquisition;
            var recipes = current.RecipeChoices;
            foreach (var selection in selections)
            {
                acquisition = acquisition.SetItem(selection.ItemId, selection.RecipeId is null ? CraftingAcquisition.Buy : CraftingAcquisition.Craft);
                recipes = selection.RecipeId is { } recipeId ? recipes.SetItem(selection.ItemId, recipeId) : recipes.Remove(selection.ItemId);
            }
            return current with { Acquisition = acquisition, RecipeChoices = recipes };
        });
    }

    public void SetReplacement(int recipeId, int ingredientId, int selectedId)
    {
        var row = Catalog.FindRecipe(recipeId)?.Ingredients.FirstOrDefault(row => row.ItemId == ingredientId);
        if (row is null || selectedId != ingredientId && !row.ReplacementItemIds.Contains(selectedId)) return;
        Edit(state => state with { ReplacementChoices = state.ReplacementChoices.SetItem(new(recipeId, ingredientId), selectedId) });
    }

    private void Edit(Func<CraftingCalculatorSnapshot, CraftingCalculatorSnapshot> change, bool calculate = true)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            var next = change(_snapshot);
            PublishLocked(calculate ? next with { Revision = _snapshot.Revision + 1, Result = null,
                Busy = _consumers > 0 && next.SelectedRecipeId is not null } : next);
            // Stage only immutable RAM preferences here; the store's writer performs IO separately.
            Remember(_snapshot);
        }
        if (calculate) Queue(); else Raise();
    }

    private void Remember(CraftingCalculatorSnapshot state) => _scenarioStore?.Stage(state.ProfileId,
        CraftingScenarioDraft.Capture(state, state.TargetQuantity));
    private void ScenarioStorageChanged()
    {
        if (Volatile.Read(ref _disposed) != 0 || _scenarioStore is null) return;
        lock (_gate) PublishLocked(_snapshot with { ScenarioStorage = StorageState(_snapshot.ProfileId) });
        Raise();
    }
    private void ProfileRemoved(string profileId) => _scenarioStore?.DeleteProfile(profileId);
    private CraftingScenarioStorageState StorageState(string? profileId) => _scenarioStore is not null
        && Guid.TryParse(profileId, out var profile) && profile != Guid.Empty ? _scenarioStore.State : CraftingScenarioStorageState.Unavailable;

    private bool UpdateSources()
    {
        var context = _context(); var proficiency = _proficiency();
        var proficiencyMatches = proficiency.ProfileId == context.ProfileId && proficiency.IsKnown;
        var model = context.OwnServer is > 0 ? _settings.GetExchangeMarketProfileForOwnServer(context.ProfileId, context.OwnServer)
            : _settings.GetExchangeMarketProfile(context.ProfileId);
        var levels = proficiencyMatches
            ? CraftingCalculatorPresentation.Professions.Where(row => proficiency.Level(row.Category) is not null)
                .ToImmutableDictionary(row => row.Id, row => proficiency.Level(row.Category)!.Value)
            : ImmutableDictionary<string, int>.Empty;
        var key = new SourceKey(context.ProfileId, context.OwnServer, model, _settings.Current.OnlineMarketPricesEnabled,
            proficiencyMatches && proficiency.IsFresh, proficiencyMatches && proficiency.UsingSaved,
            proficiencyMatches ? proficiency.ObservedAt : null, proficiencyMatches ? proficiency.Generation : 0, proficiencyMatches ? proficiency.Sequence : 0,
            string.Join(",", levels.OrderBy(row => row.Key).Select(row => row.Key + ":" + row.Value)));
        lock (_gate)
        {
            if (_sources == key) return false;
            var changedProfile = _sources?.Profile != key.Profile;
            if (changedProfile && _scenarioStore is not null)
            {
                Remember(_snapshot);
                var draft = (_scenarioStore.Read(key.Profile) ?? _defaultDraft).Sanitize(Catalog, model.Faction);
                _snapshot = draft.RestoreInto(_snapshot) with { TargetQuantity = draft.Quantity };
            }
            _sources = key;
            var server = FindSelectedServer(key);
            var selectedRecipe = _snapshot.SelectedRecipeId is { } selected ? Catalog.FindRecipe(selected) : null;
            var incompatible = selectedRecipe is not null && !CraftingCalculatorPresentation.MatchesFaction(selectedRecipe.Faction, model.Faction);
            var candidates = incompatible ? Catalog.Recipes.Where(row =>
                CraftingCalculatorPresentation.MatchesBrowser(Catalog, row, _snapshot.Profession, _snapshot.Subcategory)
                && CraftingCalculatorPresentation.MatchesFaction(row.Faction, model.Faction)).ToArray() : [];
            var defaultRecipe = candidates.Where(row => row.OutputItemId == selectedRecipe?.OutputItemId)
                .OrderBy(row => Catalog.IsComboRecipe(row.Id) == Catalog.IsComboRecipe(selectedRecipe!.Id) ? 0 : 1)
                .ThenBy(row => row.RequiredProficiency).ThenBy(row => row.Id).FirstOrDefault()
                ?? candidates.Where(row => !Catalog.IsComboRecipe(row.Id))
                    .OrderBy(row => row.RequiredProficiency).ThenBy(row => row.Id).FirstOrDefault();
            var invalidRoutes = _snapshot.RecipeChoices.Where(row => Catalog.FindRecipe(row.Value) is not { } producer
                || !CraftingCalculatorPresentation.MatchesFaction(producer.Faction, model.Faction)).Select(row => row.Key).ToArray();
            PublishLocked(_snapshot with
            {
                ProfileId = key.Profile, ObservedLevels = levels, ProficiencyFresh = key.Fresh,
                UsingSavedProficiency = key.Saved, ProficiencyObservedAt = key.At,
                ReferenceLevels = ImmutableDictionary<string, int>.Empty,
                RequiredProficiencyLevels = ImmutableDictionary<string, int>.Empty,
                SimulatedLevels = changedProfile && _scenarioStore is null ? ImmutableDictionary<string, int>.Empty : _snapshot.SimulatedLevels,
                OwnedItems = changedProfile && _scenarioStore is null ? ImmutableDictionary<int, long>.Empty : _snapshot.OwnedItems,
                ScenarioStorage = StorageState(key.Profile),
                ServerName = server?.Name, Faction = model.Faction, Servers = _cache.GetServers(),
                SalesTaxPercent = model.SalesTaxPercent, RegistrationFeePercent = model.RegistrationFeePercent,
                BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty,
                SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty, Result = null,
                SelectedRecipeId = incompatible ? defaultRecipe?.Id : _snapshot.SelectedRecipeId,
                TargetItemId = incompatible ? defaultRecipe?.OutputItemId : _snapshot.TargetItemId,
                Acquisition = incompatible ? ImmutableDictionary<int, CraftingAcquisition>.Empty : _snapshot.Acquisition.RemoveRange(invalidRoutes),
                RecipeChoices = incompatible ? ImmutableDictionary<int, int>.Empty : _snapshot.RecipeChoices.RemoveRange(invalidRoutes),
                ReplacementChoices = incompatible ? ImmutableDictionary<CraftingIngredientKey, int>.Empty : _snapshot.ReplacementChoices
            });
            if (changedProfile || incompatible || invalidRoutes.Length > 0) Remember(_snapshot);
        }
        return true;
    }
    private void SourcesChanged() { if (Volatile.Read(ref _disposed) == 0 && UpdateSources()) Queue(); }
    private void RefreshTick() { if (Volatile.Read(ref _disposed) == 0 && !Snapshot.Busy) Queue(); }

    public Task RefreshAsync()
    {
        Queue(loadServers: true);
        lock (_gate) return _work;
    }
    internal Task Pending { get { lock (_gate) return _work; } }

    private void Queue(bool loadServers = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        UpdateSources();
        CancellationTokenSource? old;
        lock (_gate)
        {
            old = _pending;
            var reference = CraftingReferenceProjection.Resolve(Catalog, _snapshot, _snapshot.TargetQuantity);
            _snapshot = _snapshot with { ReferenceLevels = reference.ReferenceLevels, RequiredProficiencyLevels = reference.RequiredLevels };
            var revision = _snapshot.Revision + 1;
            var active = _consumers > 0;
            PublishLocked(_snapshot with { Revision = revision, Busy = active && _snapshot.SelectedRecipeId is not null,
                IsError = false, PriceRefreshFailure = _snapshot.Result is not null ? _snapshot.PriceRefreshFailure : null,
                CalculationFailure = null, Status = active && _snapshot.SelectedRecipeId is not null
                    ? _snapshot.Result is not null ? "Preise werden aktualisiert; die bisherige Kalkulation bleibt sichtbar …"
                        : "Preise und Herstellung werden berechnet …" : "Wähle ein Rezept für die Kalkulation." });
            _pending = null;
            if (active && _snapshot.SelectedRecipeId is not null)
            {
                var pending = new CancellationTokenSource(); _pending = pending;
                var state = _snapshot; var sources = _sources!;
                _work = Task.Run(async () =>
                {
                    try { await CalculateAsync(state, sources, pending.Token, loadServers).ConfigureAwait(false); }
                    finally { pending.Dispose(); }
                });
            }
        }
        Cancel(old); Raise();
    }

    private async Task CalculateAsync(CraftingCalculatorSnapshot state, SourceKey sources, CancellationToken token, bool loadServers)
    {
        var stage = CraftingFailureStage.MarketServers;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), _time, token).ConfigureAwait(false);
            var needed = NeededItems(state);
            var transfer = Catalog.FindRecipe(state.SelectedRecipeId!.Value) is { } selectedRecipe
                && TransferCraftingRules.IsTransfer(Catalog, selectedRecipe);
            // User request 2026-11-10: bound and unqualified items have no usable market
            // price. Keep them in the plan/NPC lookup, but never send their IDs online.
            var marketItems = needed.Where(id => Catalog.FindItem(id)?.IsTradable == true).Order().ToArray();
            var selectedServer = FindSelectedServer(sources);
            CraftingCalculationFailure? priceFailure = null;
            try
            {
                if (sources.Online && marketItems.Length > 0
                    && (loadServers || sources.Market.HasServerSelection && selectedServer is null))
                {
                    await _cache.GetServersAsync(_client, token).ConfigureAwait(false);
                    selectedServer = FindSelectedServer(sources);
                }
                stage = CraftingFailureStage.MarketQuotes;
                if (sources.Online && marketItems.Length > 0 && selectedServer is not null)
                    await _cache.GetQuotesAsync(_client, selectedServer.ServerId, marketItems, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is ExchangeMarketApiException or HttpRequestException or IOException
                or TimeoutException or OperationCanceledException)
            {
                // A refresh outage does not invalidate a still-usable quote. GetQuote enforces the shared
                // maximum age and exact market identity; absent/expired prices remain unknown.
                priceFailure = CraftingCalculationFailure.From(error, stage, _time.GetUtcNow());
                selectedServer = FindSelectedServer(sources);
            }
            token.ThrowIfCancellationRequested();
            if (sources.Online && selectedServer is not null && _client is IExchangeMarketAverageClient averages)
            {
                var missing = marketItems.Where(id => _cache.GetQuote(selectedServer.ServerId, id) is null).ToArray();
                if (missing.Length > 0)
                {
                    stage = CraftingFailureStage.MarketAverages;
                    try { await _cache.GetAveragePricesAsync(averages, selectedServer.ServerId, missing, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is ExchangeMarketApiException or HttpRequestException or IOException
                        or TimeoutException or OperationCanceledException)
                    { priceFailure ??= CraftingCalculationFailure.From(error, stage, _time.GetUtcNow()); }
                }
            }
            token.ThrowIfCancellationRequested();
            stage = CraftingFailureStage.PriceProjection;
            var buy = ImmutableDictionary.CreateBuilder<int, CraftingPrice>();
            var sell = ImmutableDictionary.CreateBuilder<int, CraftingPrice>();
            var faction = state.Faction ?? Catalog.FindRecipe(state.SelectedRecipeId!.Value)?.Faction;
            foreach (var id in needed)
            {
                var npc = Catalog.NpcOffers.Where(offer => offer.ItemId == id && offer.ConditionsQualified
                    && CraftingCalculatorPresentation.SameFaction(offer.Faction, faction))
                    .OrderBy(offer => offer.UnitPrice).FirstOrDefault();
                if (npc is not null) buy[id] = new(id, npc.UnitPrice, CraftingPriceSource.Npc, BundleQuantity: npc.BundleQuantity);
                if (Catalog.FindItem(id)?.IsTradable != true) continue;
                var quote = selectedServer is not null ? _cache.GetQuote(selectedServer.ServerId, id) : null;
                var average = quote is null && selectedServer is not null ? _cache.GetAveragePrice(selectedServer.ServerId, id) : null;
                var price = quote is not null
                    ? new CraftingPrice(id, quote.UnitPrice, CraftingPriceSource.Exchange, quote.CapturedAt, quote.ServerId)
                    : average is not null ? new CraftingPrice(id, average.UnitPrice, CraftingPriceSource.ExchangeAverage, average.RangeEnd, average.ServerId)
                        { AverageRangeStart = average.RangeStart, AverageSamples = average.Samples } : null;
                if (price is null) continue;
                if (!buy.TryGetValue(id, out var existing) || price.UnitPrice < existing.UnitPrice) buy[id] = price;
                if (!transfer) sell[id] = price;
            }
            var request = new CraftingPlanRequest
            {
                RecipeId = state.SelectedRecipeId!.Value, TargetItemId = state.TargetItemId, TargetQuantity = state.TargetQuantity,
                Faction = faction, ProficiencyLevels = state.ObservedLevels.SetItems(state.SimulatedLevels).SetItems(state.ReferenceLevels),
                TargetOutputQuantity = CraftingCalculatorPresentation.IsComboEquipmentTarget(Catalog, state.SelectedRecipeId)
                    ? state.TargetQuantity : null,
                // A native three-precursor COMBO chain can exceed 100 million rolls across 100k complete runs.
                MaximumTotalAttempts = CraftingCalculatorPresentation.IsComboEquipmentTarget(Catalog, state.SelectedRecipeId)
                    ? 200_000_000 : 50_000_000,
                Acquisition = state.Acquisition, RecipeChoices = state.RecipeChoices, ReplacementChoices = state.ReplacementChoices,
                OwnedItems = state.OwnedItems,
                BuyPrices = buy.ToImmutable(), SellPrices = sell.ToImmutable(),
                SalesTaxPercent = state.SalesTaxPercent, RegistrationFeePercent = state.RegistrationFeePercent,
                Samples = state.SimulationSamples
            };
            stage = CraftingFailureStage.Calculation;
            // Edits and source changes already invalidate Result. An identical scenario with identical
            // qualified prices can reuse its deterministic result while the shared cache is refreshed.
            if (request.TargetOutputQuantity is not null && !(state.Result is not null
                && SamePrices(state.BuyPrices, request.BuyPrices) && SamePrices(state.SellPrices, request.SellPrices)))
                Complete(state.Revision, sources, current => current with
                { Status = "Herstellungsketten werden simuliert. Tiefe COMBO-Ketten können einige Minuten benötigen …" });
            var result = state.Result is { } previous && SamePrices(state.BuyPrices, request.BuyPrices)
                && SamePrices(state.SellPrices, request.SellPrices)
                    ? previous : transfer
                        ? new TransferCraftingCalculator(Catalog).Calculate(new TransferCraftingPlanRequest
                        {
                            RecipeId = request.RecipeId, TargetItemId = request.TargetItemId, TargetQuantity = state.TargetQuantity, Faction = faction,
                            ProficiencyLevels = request.ProficiencyLevels, Acquisition = request.Acquisition,
                            RecipeChoices = request.RecipeChoices, ReplacementChoices = request.ReplacementChoices,
                            OwnedItems = state.OwnedItems.ToImmutableDictionary(row => row.Key,
                                row => TransferCraftingRules.IsEquipment(Catalog, row.Key) ? Math.Min(1, row.Value) : row.Value),
                            BuyPrices = request.BuyPrices, Samples = request.Samples,
                            MaximumTotalAttempts = request.MaximumTotalAttempts,
                            MaximumAttemptsPerSample = 1_000_000
                        }, token)
                        : request.TargetOutputQuantity is not null
                            ? new CraftingOutputTargetCalculator(Catalog).Calculate(request, token)
                            : new CraftingCalculator(Catalog).Calculate(request, token);
            token.ThrowIfCancellationRequested();
            if (FindSelectedServer(sources) != selectedServer) { Queue(); return; }
            Complete(state.Revision, sources, current => current with
            {
                Busy = false, BuyPrices = request.BuyPrices, SellPrices = request.SellPrices, Result = result,
                PriceRefreshFailure = priceFailure, CalculationFailure = null, IsError = false,
                CalculatedAt = _time.GetUtcNow(), RequiredPriceCount = needed.Count,
                Servers = _cache.GetServers(), ServerName = selectedServer?.Name,
                Status = result.Status == CraftingPlanStatus.Complete ? "Kalkulation aktualisiert."
                    : result.Samples > 0 && result.ExpectedCost is not null && result.TargetOutput is not null
                        ? "Herstellungskosten berechnet; Erlös und Profit bleiben ohne belegten Zielverkauf unbekannt."
                        : "Die Kalkulation ist noch nicht vollständig."
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is ExchangeMarketApiException or HttpRequestException or IOException or InvalidDataException
            or ArgumentException or OverflowException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            Complete(state.Revision, sources, current => current with { Busy = false, IsError = true, Result = null,
                CalculationFailure = CraftingCalculationFailure.From(error, stage, _time.GetUtcNow()),
                Status = "Die Kalkulation konnte nicht aktualisiert werden. Bitte erneut versuchen." });
        }
    }

    private static bool SamePrices(ImmutableDictionary<int, CraftingPrice> left, ImmutableDictionary<int, CraftingPrice> right) =>
        left.Count == right.Count && left.All(row => right.TryGetValue(row.Key, out var price) && row.Value == price);

    private ExchangeMarketServer? FindSelectedServer(SourceKey sources)
    {
        var model = sources.Market;
        if (!model.HasServerSelection) return null;
        var servers = _cache.GetServers();
        if (model.AutomaticServerSelection && (sources.OwnServer ?? model.GameServerKey) is { } ownKey
            && servers.Count(row => row.GameServerKey == ownKey) != 1) return null;
        var matches = servers.Where(row => row.ServerId == model.ServerId && row.Region == model.Region && row.Faction == model.Faction
            && (!model.AutomaticServerSelection || (sources.OwnServer ?? model.GameServerKey) is not { } key || row.GameServerKey == key)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal ImmutableHashSet<int> NeededItems(CraftingCalculatorSnapshot state)
    {
        var ids = ImmutableHashSet.CreateBuilder<int>(); var visited = new HashSet<int>();
        var selectedRoot = state.SelectedRecipeId is { } rootId ? Catalog.FindRecipe(rootId) : null;
        var transferRoot = selectedRoot is not null ? Catalog.FindBaseRecipe(selectedRoot.Id) ?? selectedRoot : null;
        var transfer = selectedRoot is not null && TransferCraftingRules.IsTransfer(Catalog, selectedRoot);
        void Visit(CraftingRecipe? candidate)
        {
            if (candidate is null) return;
            var recipe = Catalog.FindBaseRecipe(candidate.Id) ?? candidate;
            if (!visited.Add(recipe.Id)) return;
            var current = recipe; var combos = new HashSet<int>();
            while (combos.Add(current.Id))
            {
                if (!transfer) ids.Add(current.OutputItemId);
                if (current.ComboRecipeId is not { } combo || Catalog.FindRecipe(combo) is not { } next) break;
                current = next;
            }
            foreach (var row in recipe.Ingredients)
            {
                var key = new CraftingIngredientKey(recipe.Id, row.ItemId);
                if (transfer && TransferCraftingRules.IsTransferIngredient(recipe, row)
                    && row.ReplacementItemIds.Length > 0 && !state.ReplacementChoices.ContainsKey(key)) continue;
                var id = state.ReplacementChoices.GetValueOrDefault(new(recipe.Id, row.ItemId), row.ItemId);
                if (transfer && TransferCraftingRules.IsEquipment(Catalog, id))
                {
                    // Only prune the guaranteed root transfer's selected protected copy. Deeper routes
                    // can need more stock after normal failures or because sibling branches share it.
                    if (selectedRoot?.Id == transferRoot?.Id && recipe.Id == transferRoot?.Id && TransferCraftingRules.IsTransferIngredient(recipe, row)
                        && state.TargetQuantity == 1 && state.OwnedItems.GetValueOrDefault(id) >= transferRoot.Ingredients
                            .Where(input => state.ReplacementChoices.GetValueOrDefault(new(recipe.Id, input.ItemId), input.ItemId) == id)
                            .Sum(input => (long)input.Quantity)) continue;
                    var candidates = TransferCraftingRules.ProducerChoices(Catalog, id, state.Faction ?? recipe.Faction);
                    var choice = state.RecipeChoices.TryGetValue(id, out var selectedGear)
                        ? candidates.FirstOrDefault(value => value.Id == selectedGear)
                        : candidates.Length == 1 ? candidates[0] : null;
                    Visit(choice);
                    continue;
                }
                ids.Add(id);
                if (state.Acquisition.GetValueOrDefault(id) == CraftingAcquisition.Craft
                    && state.RecipeChoices.TryGetValue(id, out var selected)) Visit(Catalog.FindRecipe(selected));
            }
        }
        Visit(state.SelectedRecipeId is { } id ? Catalog.FindRecipe(id) : null);
        return ids.ToImmutable();
    }

    private void Complete(long revision, SourceKey sources, Func<CraftingCalculatorSnapshot, CraftingCalculatorSnapshot> apply)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (UpdateSources()) { Queue(); return; }
        lock (_gate)
        {
            if (_disposed != 0 || _consumers == 0 || _snapshot.Revision != revision || _sources != sources) return;
            PublishLocked(apply(_snapshot));
        }
        Raise();
    }
    private void PublishLocked(CraftingCalculatorSnapshot state) => Volatile.Write(ref _snapshot, state);
    private void Raise() => SafeEvent.Raise(Changed, "Crafting-Rechner");
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= SourcesChanged; _detach?.Invoke(); _timer.Dispose();
        lock (_gate) { _consumers = 0; Cancel(_pending); }
        if (_scenarioStore is not null) { _scenarioStore.Changed -= ScenarioStorageChanged; _scenarioStore.Flush(); }
    }
}
