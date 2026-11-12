using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Soulcrest.Core.Crafting;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Tracks an explicitly pinned exact item from newly committed own receipts. Inventory baselines,
/// manual quantities, journal recovery and calculator browsing never create goal progress.
/// </summary>
public sealed class CraftingProgressService : IDisposable
{
    public static readonly TimeSpan CompletionDuration = TimeSpan.FromSeconds(60);
    private const int QueueCapacity = 512;
    private readonly object _gate = new(), _ingressGate = new(), _tickGate = new();
    private readonly ICommittedLootFactSource _source;
    private readonly CraftingProgressStore _store;
    private readonly CraftingCatalog _catalog;
    private readonly Func<CraftingStockSnapshot> _stock;
    private readonly Func<string?> _profile;
    private readonly Func<bool> _persistentGoal;
    private readonly Func<int> _displaySeconds;
    private readonly TimeProvider _time;
    private readonly Func<CraftingPlanRequest, CancellationToken, CraftingPlanResult> _calculate;
    private readonly ITimer _timer;
    private readonly Queue<Receipt> _queue = new();
    private readonly Dictionary<int, Recent> _recent = [];
    private readonly Dictionary<string, long> _pinCutoffs = new(StringComparer.Ordinal);
    private Action? _detach;
    private CraftingProgressSnapshot _snapshot = CraftingProgressSnapshot.Empty;
    private CraftingProgressPin? _pin;
    private CraftingCalculatorSnapshot? _planState;
    private CancellationTokenSource? _planCancellation;
    private Task _planning = Task.CompletedTask;
    private long _planRevision, _plannedStockRevision = -1;
    private bool _needsPlan, _planIsFresh, _planBusy;
    private string? _activeProfile, _error, _planError;
    private long _ingressSequence, _revision;
    private long? _completionStamp;
    private DateTimeOffset? _lastReceipt;
    private int _ignored, _overflow, _ticking;
    private bool _historical, _disposed;
    private sealed record Receipt(CommittedLootFact Fact, long Ordinal);
    private sealed record Recent(long Quantity, ImmutableDictionary<CraftingProgressSource, long> Sources,
        DateTimeOffset At, long Stamp);
    public event Action? Changed;
    public CraftingProgressSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public CraftingProgressService(ICommittedLootFactSource source, CraftingStockService stock,
        ExplorationService profiles, CraftingProgressStore store, SettingsService settings, TimeProvider? time = null)
        : this(source, store, CraftingCatalog.Default, () => stock.Snapshot,
            () => stock.Snapshot.ProfileId ?? profiles.ActiveId,
            () => settings.Current.CraftingProgressOverlayPersistentGoal,
            () => settings.Current.CraftingProgressOverlayDisplaySeconds, time)
    {
        profiles.CharacterRemoved += ProfileRemoved;
        _detach = () => profiles.CharacterRemoved -= ProfileRemoved;
    }

    internal CraftingProgressService(ICommittedLootFactSource source, CraftingProgressStore store, CraftingCatalog catalog,
        Func<CraftingStockSnapshot> stock, Func<string?> profile, Func<bool>? persistentGoal = null,
        Func<int>? displaySeconds = null, TimeProvider? time = null,
        Func<CraftingPlanRequest, CancellationToken, CraftingPlanResult>? calculate = null)
    {
        _source = source; _store = store; _catalog = catalog; _stock = stock; _profile = profile;
        _persistentGoal = persistentGoal ?? (() => true); _displaySeconds = displaySeconds ?? (() => 20);
        _time = time ?? TimeProvider.System;
        _calculate = calculate ?? ((request, token) => new CraftingOutputTargetCalculator(_catalog).Calculate(request, token));
        source.LootFactCommitted += ReceiptCommitted;
        // This timer observes existing states only. It starts neither capture nor a price request.
        _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        Tick();
    }

    public bool Pin(CraftingCalculatorSnapshot state, int targetQuantity = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        var profile = CraftingProgressStore.NormalizeProfile(state.ProfileId);
        var current = CraftingProgressStore.NormalizeProfile(_profile());
        var target = state.TargetItemId ?? (state.SelectedRecipeId is { } id ? _catalog.FindRecipe(id)?.OutputItemId : null);
        if (profile is null || profile != current || targetQuantity is < 1 or > 100000
            || target is not { } itemId || _catalog.FindItem(itemId) is null || state.SelectedRecipeId is not { } selected
            || _catalog.FindRecipe(selected) is not { } recipe
            || !_catalog.RecipesForItem(itemId).Any(row => _catalog.FindBaseRecipe(row.Id)?.Id == _catalog.FindBaseRecipe(recipe.Id)?.Id)) return false;
        var map = CraftingDependencyMapProjection.Build(_catalog, state);
        if (map.Nodes.IsDefaultOrEmpty || map.Truncated) return false;
        var pin = new CraftingProgressPin
        {
            ProfileId = profile, PinId = Guid.NewGuid().ToString("N"), TargetItemId = itemId,
            TargetQuantity = targetQuantity, PinnedAt = _time.GetUtcNow(),
            Scenario = CaptureDraft(state with { TargetItemId = itemId }),
            // Until the exact-quantity worker completes, demand is explicitly unknown. Never reuse
            // the calculator's fixed-attempt result for the independent pinned piece goal.
            PlanItems = UnknownDemand(PlanItems(map))
        };
        lock (_gate)
        {
            if (_disposed || !_store.Stage(pin)) return false;
            _activeProfile = profile; _pin = pin; _historical = false;
            _planState = state with { Result = null }; _needsPlan = true; _planIsFresh = false;
            _pinCutoffs[profile] = Interlocked.Read(ref _ingressSequence);
            ClearTransient(); _error = null; _planError = null;
        }
        Tick();
        return true;
    }

    /// <summary>Updates the same explicitly pinned target. Another browsed target cannot replace the pin.</summary>
    public bool UpdatePinnedPlan(CraftingCalculatorSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var map = CraftingDependencyMapProjection.Build(_catalog, state);
        lock (_gate)
        {
            if (_disposed || _pin is not { } pin || CraftingProgressStore.NormalizeProfile(state.ProfileId) != pin.ProfileId
                || state.TargetItemId != pin.TargetItemId || state.SelectedRecipeId is null || map.Truncated || map.Nodes.IsDefaultOrEmpty) return false;
            var draft = CaptureDraft(state);
            _planState = state with { Result = null }; _needsPlan = true;
            if (!pin.Scenario.SameAs(draft))
            {
                var plan = UnknownDemand(PlanItems(map));
                var next = pin with { Scenario = draft, PlanItems = plan };
                if (!_store.Stage(next)) return false;
                _pin = next; _planIsFresh = false;
                foreach (var id in _recent.Keys.Where(id => !plan.Any(row => row.ItemId == id)).ToArray()) _recent.Remove(id);
            }
        }
        Tick();
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed || _activeProfile is not { } profile) return;
            _store.Clear(profile); _pin = null; ClearTransient(); CancelPlan();
        }
        Tick();
    }
    private static ImmutableArray<CraftingProgressPlanItem> PlanItems(CraftingDependencyMap map) => map.Nodes
        .Where(row => row.IsActive || row.IsTarget).Select(row => new CraftingProgressPlanItem(row.ItemId, row.RequiredQuantity, row.IsCombo)
            { RemainingQuantity = row.RemainingQuantity, IsCovered = row.IsComplete })
        .DistinctBy(row => row.ItemId).ToImmutableArray();
    private CraftingScenarioDraft CaptureDraft(CraftingCalculatorSnapshot state)
    {
        var stock = _stock();
        // The pin persists an explicit route, not a second automatic inventory cache. Restored
        // stock must retain its own source/freshness in CraftingStockService.
        var manual = CraftingProgressStore.NormalizeProfile(stock.ProfileId) == CraftingProgressStore.NormalizeProfile(state.ProfileId)
            ? stock.ManualItems : state.OwnedItems;
        return CraftingScenarioDraft.Capture(state with { OwnedItems = manual }, state.TargetAttempts);
    }
    private static ImmutableArray<CraftingProgressPlanItem> UnknownDemand(ImmutableArray<CraftingProgressPlanItem> items) =>
        items.Select(row => row with { RequiredQuantity = null, RemainingQuantity = null, IsCovered = false }).ToImmutableArray();
    private void ReceiptCommitted(CommittedLootFact fact)
    {
        var ordinal = Interlocked.Increment(ref _ingressSequence);
        lock (_ingressGate)
        {
            if (_disposed) return;
            if (_queue.Count >= QueueCapacity) { Interlocked.Exchange(ref _overflow, 1); return; }
            _queue.Enqueue(new(fact, ordinal));
        }
    }
    public void Tick()
    {
        lock (_tickGate)
        {
            if (_ticking != 0) return;
            _ticking = 1;
            try { TickCore(); }
            finally { _ticking = 0; }
        }
    }
    private void TickCore(bool raiseEvents = true)
    {
        Receipt[] receipts;
        lock (_ingressGate) { if (_disposed) return; receipts = _queue.ToArray(); _queue.Clear(); }
        var stock = _stock();
        var profile = CraftingProgressStore.NormalizeProfile(_profile());
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (profile != _activeProfile)
            {
                _activeProfile = profile; _pin = _store.Read(profile); _historical = _pin is not null;
                if (_pin is { } restored) _pin = restored with { PlanItems = UnknownDemand(restored.PlanItems) };
                ClearTransient(); CancelPlan(); _planState = null; _error = null; _planError = null;
            }
            if (Interlocked.Exchange(ref _overflow, 0) != 0)
                _error = "Die Crafting-Erkennung hat eine Ereignislücke. Der bestätigte Stand bleibt erhalten.";
            foreach (var receipt in receipts) Apply(receipt);
            if (_planState is not null && stock.Revision != _plannedStockRevision) _needsPlan = true;
            var now = _time.GetTimestamp();
            var duration = TimeSpan.FromSeconds(Math.Clamp(_displaySeconds(), 5, 300));
            foreach (var id in _recent.Where(row => _time.GetElapsedTime(row.Value.Stamp, now) >= duration).Select(row => row.Key).ToArray()) _recent.Remove(id);
            if (_completionStamp is { } completion && _time.GetElapsedTime(completion, now) >= CompletionDuration) _completionStamp = null;
            var pin = _pin;
            var target = pin is null ? null : _catalog.FindItem(pin.TargetItemId);
            var next = new CraftingProgressSnapshot
            {
                ProfileId = profile, Pinned = pin, TargetNameDe = target?.NameDe ?? "", TargetNameEn = target?.NameEn ?? "",
                TargetIcon = target?.Icon, TargetIsCombo = pin?.PlanItems.FirstOrDefault(row => row.ItemId == pin.TargetItemId)?.IsCombo == true,
                IsFresh = stock.IsFresh && CraftingProgressStore.NormalizeProfile(stock.ProfileId) == profile,
                IsHistorical = _historical, PersistentGoal = _persistentGoal(), IsCompletionVisible = _completionStamp is not null,
                PlanIsFresh = _planIsFresh, PlanBusy = _planBusy,
                CompletionAgeSeconds = _completionStamp is { } stamp ? _time.GetElapsedTime(stamp, now).TotalSeconds : 0,
                Rows = _recent.OrderByDescending(row => row.Value.Stamp).ThenBy(row => row.Key).Take(3).Select(row => Row(row.Key, row.Value, stock, now)).ToImmutableArray(),
                Error = _error ?? _planError ?? (pin?.ReceiptCapacityReached == true ? "Das Crafting-Ziel hat die Beleggrenze erreicht. Neue Belege werden nicht mehr gezählt." : _store.Error),
                LastReceiptAt = _lastReceipt, IgnoredReceipts = _ignored
            };
            if (!Same(_snapshot, next)) { _snapshot = next with { Revision = ++_revision }; changed = true; }
        }
        SchedulePlan(stock);
        if (changed && raiseEvents) SafeEvent.Raise(Changed, "Crafting-Fortschritt");
    }
    private void Apply(Receipt receipt)
    {
        var fact = receipt.Fact;
        var profile = CraftingProgressStore.NormalizeProfile(fact.ProfileId);
        // A committed fact already owns an exact, frozen profile binding. A durable write can
        // finish after a character switch: retain that profile's counter without showing its
        // event on the newly active character or replaying it when the old profile returns.
        var pin = profile == _activeProfile ? _pin : _store.Read(profile);
        var active = profile == _activeProfile;
        if (pin is null || profile != pin.ProfileId
            || fact.Fact.At <= pin.PinnedAt || fact.Fact.At > _time.GetUtcNow().AddSeconds(5)
            || receipt.Ordinal <= _pinCutoffs.GetValueOrDefault(pin.ProfileId)
            || fact.Fact.Kind is not (LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess)
            || fact.Fact.Loot.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(fact.Fact.Id) || fact.Fact.Id.Length > 128
            || string.IsNullOrWhiteSpace(fact.CanonicalSessionId) || fact.CanonicalSessionId.Length > 128)
        { _ignored++; return; }
        var eligible = fact.Fact.Loot.Where(row => row.Kind == LootTrackerLootKind.Item && row.Quantity > 0
                && ExchangeLootValuation.TryItemId(row.Id, out var id) && pin.PlanItems.Any(plan => plan.ItemId == id))
            .GroupBy(row => int.Parse(row.Id.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (eligible.Length == 0) return;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fact.CanonicalSessionId + "/" + fact.Fact.Id)));
        var next = pin;
        var updates = new List<(int ItemId, long Quantity)>();
        try
        {
            foreach (var group in eligible)
            {
                var key = hash + ":" + group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (next.SeenReceipts.Contains(key, StringComparer.Ordinal)) continue;
                if (next.SeenReceipts.Length >= CraftingProgressStore.MaximumReceipts)
                { next = next with { ReceiptCapacityReached = true }; break; }
                var quantity = group.Aggregate(0L, (sum, row) => checked(sum + row.Quantity));
                next = next with { SeenReceipts = next.SeenReceipts.Add(key),
                    ConfirmedQuantity = group.Key == pin.TargetItemId ? checked(next.ConfirmedQuantity + quantity) : next.ConfirmedQuantity };
                updates.Add((group.Key, quantity));
            }
            if (next == pin) return;
            if (!_store.Stage(next)) { _error = _store.Error ?? CraftingProgressStore.SaveError; return; }
            if (!active) return;
            _pin = next;
            if (next.ConfirmedQuantity != pin.ConfirmedQuantity)
            {
                // Invalidate before releasing _gate. An older demand worker must not publish
                // its pre-receipt remainder between this counter update and SchedulePlan.
                CancelPlan(); _needsPlan = true;
            }
            var source = fact.Fact.Kind == LootTrackerTimelineEventKind.HarvestSuccess ? CraftingProgressSource.Gathering : CraftingProgressSource.Other;
            var stamp = _time.GetTimestamp();
            foreach (var (id, quantity) in updates)
            {
                _recent.TryGetValue(id, out var old);
                _recent[id] = new(checked((old?.Quantity ?? 0) + quantity),
                    (old?.Sources ?? ImmutableDictionary<CraftingProgressSource, long>.Empty).SetItem(source,
                        checked((old?.Sources.GetValueOrDefault(source) ?? 0) + quantity)), fact.Fact.At, stamp);
            }
            if (pin.ConfirmedQuantity < pin.TargetQuantity && next.ConfirmedQuantity >= next.TargetQuantity) _completionStamp = stamp;
            if (updates.Count > 0) { _lastReceipt = fact.Fact.At; _historical = false; }
        }
        catch (OverflowException) { _error = "Die Crafting-Menge überschreitet den unterstützten Zahlenbereich."; }
    }
    private CraftingProgressRow Row(int id, Recent recent, CraftingStockSnapshot stock, long now)
    {
        var item = _catalog.FindItem(id);
        var plan = _pin?.PlanItems.FirstOrDefault(row => row.ItemId == id);
        var ownStock = CraftingProgressStore.NormalizeProfile(stock.ProfileId) == _activeProfile;
        var known = ownStock ? stock.EffectiveItems.GetValueOrDefault(id) : null;
        var isTarget = _pin is { } pin && id == pin.TargetItemId;
        return new()
        {
            ItemId = id, NameDe = item?.NameDe ?? "", NameEn = item?.NameEn ?? "", Icon = item?.Icon, IsCombo = plan?.IsCombo == true,
            Quantity = recent.Quantity, SourceQuantities = recent.Sources, LastReceivedAt = recent.At,
            RequiredQuantity = id == _pin?.TargetItemId ? _pin.TargetQuantity : plan?.RequiredQuantity,
            KnownQuantity = isTarget ? _pin!.ConfirmedQuantity : known?.Quantity,
            InventoryFresh = isTarget || known?.Freshness == StockFreshness.Fresh,
            InventoryComplete = isTarget || known?.Completeness == StockCompleteness.Complete,
            IsManualStock = !isTarget && known is not null && known.Origin != StockOrigin.Automatic,
            IsTarget = isTarget, DemandIsFresh = isTarget || _planIsFresh,
            HasOpenDemand = isTarget ? _pin!.ConfirmedQuantity < _pin.TargetQuantity
                : plan is null || !plan.IsCovered || plan.RemainingQuantity is null or > 0,
            EventAgeSeconds = _time.GetElapsedTime(recent.Stamp, now).TotalSeconds
        };
    }
    private static bool Same(CraftingProgressSnapshot left, CraftingProgressSnapshot right) =>
        left.ProfileId == right.ProfileId && left.Pinned == right.Pinned && left.IsFresh == right.IsFresh
        && left.PersistentGoal == right.PersistentGoal && left.IsHistorical == right.IsHistorical
        && left.PlanBusy == right.PlanBusy && left.PlanIsFresh == right.PlanIsFresh
        && left.IsCompletionVisible == right.IsCompletionVisible && left.CompletionAgeSeconds == right.CompletionAgeSeconds
        && left.Error == right.Error && left.IgnoredReceipts == right.IgnoredReceipts && left.Rows.SequenceEqual(right.Rows);
    private void ClearTransient() { _recent.Clear(); _completionStamp = null; _lastReceipt = null; }
    private void ProfileRemoved(string profileId)
    {
        lock (_gate)
        {
            _store.DeleteProfile(profileId);
            if (CraftingProgressStore.NormalizeProfile(profileId) is { } removed) _pinCutoffs.Remove(removed);
            if (CraftingProgressStore.NormalizeProfile(profileId) == _activeProfile) { _pin = null; ClearTransient(); CancelPlan(); }
        }
        Tick();
    }
    public void Dispose()
    {
        lock (_ingressGate) { if (_disposed) return; _disposed = true; _queue.Clear(); }
        _timer.Dispose(); _source.LootFactCommitted -= ReceiptCommitted; _detach?.Invoke();
        lock (_gate) CancelPlan();
    }
    /// <summary>Drain the committed producer first, then checkpoint every receipt already delivered to this service.</summary>
    public async Task FlushAsync()
    {
        // A running timer tick may already have taken receipts out of _queue. The same monitor
        // waits for that tick's Stage calls, then drains anything delivered in the meantime.
        // Bypass Tick's same-thread reentrancy return and avoid UI callbacks during checkpointing.
        lock (_tickGate) TickCore(raiseEvents: false);
        await _store.FlushAsync().ConfigureAwait(false);
    }
    public async Task WaitForPlanAsync()
    {
        Task planning;
        lock (_gate) planning = _planning;
        await planning.ConfigureAwait(false);
        Tick();
    }

    private void CancelPlan()
    {
        _planRevision++; _needsPlan = false; _planBusy = false; _planIsFresh = false; _plannedStockRevision = -1;
        _planError = null;
        CancelCurrentCalculation();
    }
    private void SchedulePlan(CraftingStockSnapshot stock)
    {
        lock (_gate)
        {
            if (_disposed || !_needsPlan || _pin is not { } pin || _planState is not { } state) return;
            _needsPlan = false; _planBusy = true; _planIsFresh = false; _plannedStockRevision = stock.Revision;
            CancelCurrentCalculation();
            var cancellation = new CancellationTokenSource(); _planCancellation = cancellation;
            var token = cancellation.Token;
            var revision = ++_planRevision;
            var remaining = (int)Math.Max(0, pin.TargetQuantity - Math.Min(pin.ConfirmedQuantity, pin.TargetQuantity));
            var owned = CraftingProgressStore.NormalizeProfile(stock.ProfileId) == pin.ProfileId
                ? stock.EffectiveItems.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Quantity) : state.OwnedItems;
            // Restore only the frozen explicit route; prices and observed levels stay in RAM and
            // are never loaded from a persisted scenario or replaced by a different browser target.
            var frozen = pin.Scenario.RestoreInto(state) with { OwnedItems = owned, Result = null };
            _planning = Task.Run(() =>
            {
                try
                {
                    var result = remaining == 0 ? new CraftingPlanResult { TargetOutput = new(pin.TargetItemId, 0, null) }
                        : _calculate(new()
                        {
                            RecipeId = frozen.SelectedRecipeId!.Value, TargetItemId = pin.TargetItemId,
                            TargetOutputQuantity = remaining, TargetAttempts = 1,
                            Faction = frozen.Faction, ProficiencyLevels = frozen.ObservedLevels.SetItems(frozen.SimulatedLevels).SetItems(frozen.ReferenceLevels),
                            Acquisition = frozen.Acquisition, RecipeChoices = frozen.RecipeChoices, ReplacementChoices = frozen.ReplacementChoices,
                            OwnedItems = frozen.OwnedItems, BuyPrices = frozen.BuyPrices, SellPrices = frozen.SellPrices,
                            SalesTaxPercent = frozen.SalesTaxPercent, RegistrationFeePercent = frozen.RegistrationFeePercent,
                            Samples = frozen.SimulationSamples, MaximumAttemptsPerSample = 1_000_000, MaximumTotalAttempts = 200_000_000
                        }, token);
                    token.ThrowIfCancellationRequested();
                    var projection = CraftingDependencyMapProjection.Build(_catalog, frozen with { Result = result });
                    var items = PlanItems(projection);
                    if (remaining == 0) items = items.Select(row => row with { RequiredQuantity = 0 }).ToImmutableArray();
                    lock (_gate)
                    {
                        if (_disposed || revision != _planRevision || _pin is not { } current || current.PinId != pin.PinId) return;
                        var next = current with { PlanItems = items };
                        if (_store.Stage(next)) _pin = next;
                        _planBusy = false; _planError = null;
                        _planIsFresh = projection.QuantitiesKnown && !projection.Truncated && stock.IsFresh
                            && frozen.PriceRefreshFailure is null && frozen.CalculationFailure is null;
                    }
                    Tick();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
                {
                    lock (_gate)
                    {
                        if (_disposed || revision != _planRevision || _pin is not { } current || current.PinId != pin.PinId) return;
                        _planBusy = false; _planIsFresh = false;
                        _pin = current with { PlanItems = UnknownDemand(current.PlanItems) };
                        _planError = "Der Bedarf des verfolgten Crafting-Ziels konnte nicht berechnet werden.";
                    }
                    Tick();
                }
                finally { cancellation.Dispose(); }
            });
        }
    }
    private void CancelCurrentCalculation()
    {
        try { _planCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _planCancellation = null;
    }
}
