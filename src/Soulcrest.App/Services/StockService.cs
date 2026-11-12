using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Usable observed minima, separately labelled history, and protected manual corrections.</summary>
public sealed class CraftingStockService : IDisposable
{
    private readonly INetworkStockSource _source;
    private readonly CraftingStockStore _store;
    private readonly CraftingScenarioStore _scenarios;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _resolve;
    private readonly Func<string?> _selected;
    private readonly ExplorationService? _profiles;
    private CraftingStockSnapshot _snapshot = CraftingStockSnapshot.Empty;
    private OwnCharacterContext? _publishedCharacter;
    private string? _savedProfile;
    private long _savedSequence = -1, _savedGeneration = -1, _revision;
    private int _refreshing, _refreshPending;
    private bool _disposed;
    public event Action? Changed;
    public CraftingStockSnapshot Snapshot
    {
        get
        {
            var current = Volatile.Read(ref _snapshot);
            if (_disposed) return CraftingStockSnapshot.Empty;
            if (!current.IsFresh) return current;
            var evidence = _source.StockSnapshot;
            return _source.Running && evidence.IsFresh && ReferenceEquals(evidence.Character, Volatile.Read(ref _publishedCharacter))
                && evidence.Character is { } own && _resolve(own)?.ProfileId == current.ProfileId ? current
                : current with { IsFresh = false, EffectiveItems = current.EffectiveItems.ToImmutableDictionary(pair => pair.Key,
                    pair => pair.Value.Origin == StockOrigin.Automatic ? pair.Value with { Freshness = StockFreshness.Historical } : pair.Value),
                    ObservedItems = current.ObservedItems.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Freshness = StockFreshness.Historical }) };
        }
    }

    public CraftingStockService(INetworkStockSource source, ExplorationService profiles, CraftingStockStore store, CraftingScenarioStore scenarios)
        : this(source, profiles.TryGetBoundCharacter, () => profiles.ActiveId, store, scenarios)
    { _profiles = profiles; profiles.Changed += Refresh; profiles.CharacterRemoved += DeleteProfile; Refresh(); }

    internal CraftingStockService(INetworkStockSource source, Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> resolve,
        Func<string?> selected, CraftingStockStore store, CraftingScenarioStore scenarios)
    {
        _source = source; _resolve = resolve; _selected = selected; _store = store; _scenarios = scenarios;
        source.Changed += Refresh; source.OwnCharacterChanged += Refresh; store.Changed += Refresh;
        scenarios.Changed += Refresh;
        Refresh();
    }

    public CraftingStockSnapshot Read(string? profileId)
    {
        var current = Snapshot;
        return current.ProfileId == profileId ? current : Build(profileId, null);
    }

    public void SetOverride(int itemId, long quantity)
    {
        if (itemId <= 0 || quantity is < 0 or > 1_000_000_000 || Snapshot.ProfileId is not { } profile) return;
        var row = _store.Read(profile) ?? new() { ProfileId = profile };
        _store.Save(row with { Overrides = row.Overrides.SetItem(itemId, quantity) });
        Refresh();
    }

    public void UseAutomatic(int itemId)
    {
        if (itemId <= 0 || Snapshot.ProfileId is not { } profile) return;
        var row = _store.Read(profile) ?? new() { ProfileId = profile };
        _store.Save(row with { Overrides = row.Overrides.Remove(itemId), IgnoredLegacyItems = row.IgnoredLegacyItems.Add(itemId) });
        Refresh();
    }

    private void DeleteProfile(string profileId) { _store.DeleteProfile(profileId); Refresh(); }
    private void Refresh()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            while (!_disposed && Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
                var network = _source.StockSnapshot;
                var bound = network.Character is { } own ? _resolve(own) : null;
                var profile = bound?.ProfileId ?? _selected();
                var currentNetwork = _source.StockSnapshot;
                var live = _source.Running && network.IsFresh && network.Available && bound?.ProfileId == profile
                    && currentNetwork.Sequence == network.Sequence && ReferenceEquals(network.Character, currentNetwork.Character) ? network : null;
                if (live?.Character is { } character && profile is not null
                    && (_savedProfile != profile || _savedSequence != live.Sequence || _savedGeneration != character.Generation))
                {
                    _savedProfile = profile; _savedSequence = live.Sequence; _savedGeneration = character.Generation;
                    var previous = _store.Read(profile) ?? new() { ProfileId = profile };
                    var fingerprint = Fingerprint(character);
                    var ignored = live.IsComplete ? previous.IgnoredLegacyItems.Union(_scenarios.Read(profile)?.OwnedItems.Keys ?? []) : previous.IgnoredLegacyItems;
                    var retainHistory = !live.IsComplete && (previous.OwnerFingerprint is null || previous.OwnerFingerprint == fingerprint);
                    var automatic = retainHistory ? previous.AutomaticItems.SetItems(live.Items) : live.Items;
                    var observedTimes = retainHistory ? previous.ItemObservedAt : ImmutableDictionary<int, DateTimeOffset>.Empty;
                    if (live.ObservedAt is { } observedAt) observedTimes = observedTimes.SetItems(live.Items.Keys.Select(id => new KeyValuePair<int, DateTimeOffset>(id, observedAt)));
                    _store.Save(previous with { OwnerFingerprint = fingerprint, AutomaticItems = automatic, ItemObservedAt = observedTimes,
                        ObservedAt = live.ObservedAt, Complete = live.IsComplete, IgnoredLegacyItems = ignored });
                }
                var next = Build(profile, live);
                var before = Volatile.Read(ref _snapshot);
                if (Equivalent(before, next)) continue;
                Volatile.Write(ref _publishedCharacter, live?.Character);
                Volatile.Write(ref _snapshot, next with { Revision = Interlocked.Increment(ref _revision) });
                SafeEvent.Raise(Changed, "Items in Stock");
            }
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
        if (!_disposed && Volatile.Read(ref _refreshPending) != 0) Refresh();
    }

    private CraftingStockSnapshot Build(string? profileId, NetworkStockSnapshot? live)
    {
        if (!Guid.TryParse(profileId, out var parsed) || parsed == Guid.Empty) return CraftingStockSnapshot.Empty;
        var saved = _store.Read(profileId);
        var historical = saved?.AutomaticItems ?? ImmutableDictionary<int, long>.Empty;
        var automatic = live is null ? historical : live.IsComplete ? live.Items : historical.SetItems(live.Items);
        var fresh = live is not null;
        var complete = live?.IsComplete ?? saved?.Complete ?? false;
        var legacy = _scenarios.Read(profileId)?.OwnedItems ?? ImmutableDictionary<int, long>.Empty;
        var ignored = saved?.IgnoredLegacyItems ?? ImmutableHashSet<int>.Empty;
        var effective = ImmutableDictionary.CreateBuilder<int, CraftingStockItem>();
        var observed = ImmutableDictionary.CreateBuilder<int, CraftingStockItem>();
        var manual = ImmutableDictionary.CreateBuilder<int, long>();
        foreach (var pair in automatic)
        {
            var itemFresh = live?.Items.ContainsKey(pair.Key) == true;
            DateTimeOffset? itemAt = itemFresh ? live?.ObservedAt : saved?.ObservedAt;
            if (!itemFresh && saved is not null && saved.ItemObservedAt.TryGetValue(pair.Key, out var historicalAt)) itemAt = historicalAt;
            var item = new CraftingStockItem(pair.Key, pair.Value, itemFresh ? StockFreshness.Fresh : StockFreshness.Historical,
                complete ? StockCompleteness.Complete : StockCompleteness.Partial, StockOrigin.Automatic)
            { ObservedAt = itemAt };
            effective[pair.Key] = item; observed[pair.Key] = item;
        }
        foreach (var pair in legacy.Where(pair => !ignored.Contains(pair.Key)))
        {
            effective[pair.Key] = new(pair.Key, pair.Value, StockFreshness.Unknown, StockCompleteness.Unknown, StockOrigin.LegacyManual);
            manual[pair.Key] = pair.Value;
        }
        foreach (var pair in saved?.Overrides ?? ImmutableDictionary<int, long>.Empty)
        {
            effective[pair.Key] = new(pair.Key, pair.Value, StockFreshness.Unknown, StockCompleteness.Unknown, StockOrigin.ManualOverride);
            manual[pair.Key] = pair.Value;
        }
        return new()
        {
            ProfileId = profileId, EffectiveItems = effective.ToImmutable(), ObservedMinimumItems = automatic, ObservedItems = observed.ToImmutable(),
            ManualItems = manual.ToImmutable(), IsFresh = fresh, IsComplete = complete,
            ObservedAt = live?.ObservedAt ?? saved?.ObservedAt, StorageError = _store.StorageError,
            Scopes = live?.Scopes ?? [new(StockScope.Bag, complete ? StockCompleteness.Complete : saved?.ObservedAt is null ? StockCompleteness.Unknown : StockCompleteness.Partial),
                new(StockScope.CharacterStorage, complete ? StockCompleteness.Complete : StockCompleteness.Unknown),
                new(StockScope.ServerStorage, complete ? StockCompleteness.Complete : StockCompleteness.Unknown)]
        };
    }

    private static string Fingerprint(OwnCharacterContext own) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        own.Identity.ServerId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + own.Identity.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private static bool Equivalent(CraftingStockSnapshot a, CraftingStockSnapshot b) => a.ProfileId == b.ProfileId
        && a.IsFresh == b.IsFresh && a.IsComplete == b.IsComplete && a.ObservedAt == b.ObservedAt && a.StorageError == b.StorageError
        && a.EffectiveItems.Count == b.EffectiveItems.Count && a.EffectiveItems.All(pair => b.EffectiveItems.GetValueOrDefault(pair.Key) == pair.Value)
        && a.ObservedMinimumItems.Count == b.ObservedMinimumItems.Count && a.ObservedMinimumItems.All(pair => b.ObservedMinimumItems.TryGetValue(pair.Key, out var value) && pair.Value == value)
        && a.ObservedItems.Count == b.ObservedItems.Count && a.ObservedItems.All(pair => b.ObservedItems.GetValueOrDefault(pair.Key) == pair.Value)
        && a.Scopes.SequenceEqual(b.Scopes);

    public void Dispose()
    {
        _disposed = true; _source.Changed -= Refresh; _source.OwnCharacterChanged -= Refresh;
        _store.Changed -= Refresh; _scenarios.Changed -= Refresh;
        if (_profiles is not null) { _profiles.Changed -= Refresh; _profiles.CharacterRemoved -= DeleteProfile; }
    }
}
