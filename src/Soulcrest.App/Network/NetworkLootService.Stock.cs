using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService : INetworkStockSource
{
    private readonly StockInventoryReducer _stockInventory = new();
    private readonly Guid _stockSourceId = Guid.NewGuid();
    private readonly Queue<(long Revision, ItemReceipt Receipt)> _stockReceipts = [];
    private OwnCharacterContext? _stockCharacter;
    private long _stockSequence, _stockRevision;
    private bool _dispatchingStock;
    private event Action? StockChanged;
    public event Action<ItemReceipt>? ItemReceived;
    // Stock observes ordinary lifecycle/status changes, but its own updates must not duplicate
    // the existing AP/wallet Changed contract or alter its reentrant publication order.
    event Action? INetworkStockSource.Changed
    {
        add { Changed += value; StockChanged += value; }
        remove { Changed -= value; StockChanged -= value; }
    }

    public NetworkStockSnapshot StockSnapshot
    {
        get
        {
            lock (_gate)
            {
                var value = _stockInventory.Snapshot;
                if (!Running || _stockCharacter is null || !ReferenceEquals(_stockCharacter, _ownCharacter) || !value.Available)
                    return NetworkStockSnapshot.Empty with { Sequence = _stockSequence };
                return new(_stockCharacter, value.Items, true, value.ObservedAt, _stockSequence)
                {
                    Scopes = [new(StockScope.Bag, StockCompleteness.Partial), new(StockScope.CharacterStorage, StockCompleteness.Unknown),
                        new(StockScope.ServerStorage, StockCompleteness.Unknown)]
                };
            }
        }
    }

    private void BindStockBaseline(CurrencyInventoryList? list, DateTimeOffset? at)
    {
        ResetStockContext();
        if (_ownCharacter is null || list is null || at is null) return;
        _stockCharacter = _ownCharacter;
        _stockInventory.Baseline(list, at.Value);
        _stockSequence++;
    }

    private void ProcessStockInventoryChanges(string stream, CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (!Running || _ownCharacter is not { } own || _identityStream != stream) return;
        if (!ReferenceEquals(_stockCharacter, own)) { ResetStockContext(); _stockCharacter = own; }
        var deltas = _stockInventory.Apply(changes, at);
        _stockSequence++;
        if (!_stockInventory.Snapshot.Available) { _stockReceipts.Clear(); return; }
        foreach (var delta in deltas)
        {
            if (_stockReceipts.Count >= 10000) { ResetStockContext(); return; }
            var sequence = ++_stockSequence;
            _stockReceipts.Enqueue((_stockRevision, new($"stock:{_stockSourceId:N}:{sequence}", sequence, own,
                delta.ItemId, delta.Quantity, delta.Kind, at, delta.CoveredByCommittedLoot)));
        }
    }

    private void DispatchStockReceipts()
    {
        if (_dispatchingStock) return;
        _dispatchingStock = true;
        try
        {
            while (_stockReceipts.TryDequeue(out var queued))
                foreach (var subscriber in ItemReceived?.GetInvocationList().Cast<Action<ItemReceipt>>() ?? [])
                {
                    if (!Running || queued.Revision != _stockRevision || !ReferenceEquals(queued.Receipt.Character, _ownCharacter)) break;
                    try { subscriber(queued.Receipt); }
                    catch (Exception error) { LogFile.Error("Items-in-Stock-Ereignis", error); }
                }
        }
        finally { _dispatchingStock = false; }
    }

    private void ResetStockContext()
    {
        _stockRevision++; _stockSequence++; _stockInventory.Reset(); _stockCharacter = null; _stockReceipts.Clear();
    }
}
