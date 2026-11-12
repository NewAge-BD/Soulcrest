using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

public enum StockCompleteness { Unknown, Partial, Complete }
public enum StockScope { Bag, CharacterStorage, ServerStorage }
public enum ItemReceiptKind { Unknown, Acquisition, Transfer }

public sealed record StockInventorySnapshot(ImmutableDictionary<int, long> Items, bool Available,
    DateTimeOffset? ObservedAt)
{
    public static StockInventorySnapshot Empty { get; } = new(ImmutableDictionary<int, long>.Empty, false, null);
}

public sealed record StockInventoryDelta(int ItemId, long Quantity, ItemReceiptKind Kind,
    bool CoveredByCommittedLoot = false);

/// <summary>
/// Absolute observed records only. Container 1 is the qualified bag; other container numbers have no
/// invented storage meaning. The observed envelope never establishes absent-item or scope completeness.
/// Instance keys remain inside this RAM reducer and never appear in its public snapshots or receipts.
/// </summary>
public sealed class StockInventoryReducer
{
    private const int MaximumStacks = 10000;
    private const int MaximumObservedItems = 50000;
    private Dictionary<long, CurrencyInventoryItem> _stacks = [];
    private HashSet<int> _observedBagItems = [];
    public StockInventorySnapshot Snapshot { get; private set; } = StockInventorySnapshot.Empty;

    public void Reset()
    {
        _stacks.Clear(); _observedBagItems.Clear(); Snapshot = StockInventorySnapshot.Empty;
    }

    public bool Baseline(CurrencyInventoryList? list, DateTimeOffset at)
    {
        Reset();
        if (list is null || list.AllItems.Count > MaximumStacks) return false;
        var next = new Dictionary<long, CurrencyInventoryItem>();
        foreach (var item in list.AllItems)
            if (item.InstanceKey <= 0 || item.ItemId <= 0 || item.Count < 0 || !next.TryAdd(item.InstanceKey, item)) return false;
        try
        {
            _observedBagItems = next.Values.Where(item => item.Container == 1).Select(item => item.ItemId).ToHashSet();
            Snapshot = new(Aggregate(next, _observedBagItems), true, at);
            _stacks = next;
            return true;
        }
        catch (OverflowException) { Reset(); return false; }
    }

    public ImmutableArray<StockInventoryDelta> Apply(CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (changes is null || changes.AllChanges.Count > MaximumStacks || Snapshot.ObservedAt > at) { Reset(); return []; }
        var next = new Dictionary<long, CurrencyInventoryItem>(_stacks);
        var known = new HashSet<int>(_observedBagItems);
        var seen = new HashSet<long>();
        var acquisitions = new Dictionary<int, long>();
        var moved = new HashSet<int>();
        try
        {
            foreach (var change in changes.AllChanges)
            {
                var item = change.Item;
                if (item.InstanceKey <= 0 || item.ItemId <= 0 || item.Count < 0 || change.ModificationType > 2
                    || !seen.Add(item.InstanceKey)) { Reset(); return []; }
                next.TryGetValue(item.InstanceKey, out var previous);
                if (previous is not null && previous.ItemId != item.ItemId) { Reset(); return []; }
                if (previous?.Container == 1) known.Add(previous.ItemId);
                if (item.Container == 1) known.Add(item.ItemId);
                if (change.ModificationType == 2) { next.Remove(item.InstanceKey); continue; }
                if (change.ModificationType == 0 && previous is not null && previous != item) { Reset(); return []; }
                if (previous is not null && previous.Container != item.Container) moved.Add(item.ItemId);
                next[item.InstanceKey] = item;
                // Existing user-qualified monster acquisition variant only. Other positive absolute
                // changes may be splits, moves, baselines or unknown causes and grant no receipt.
                if (item.Container == 1 && change.IncreaseReason == 5 && change.DecreaseReason == 0 && change.UpdateReason == 0)
                {
                    var amount = change.ModificationType == 0 && previous is null ? item.Count
                        : change.ModificationType == 1 && previous?.Container == 1 ? Math.Max(0, item.Count - previous.Count) : 0;
                    if (amount > 0) acquisitions[item.ItemId] = checked(acquisitions.GetValueOrDefault(item.ItemId) + amount);
                }
            }
            if (next.Count > MaximumStacks || known.Count > MaximumObservedItems) { Reset(); return []; }
            var beforeAll = AggregateAll(_stacks);
            var afterAll = AggregateAll(next);
            var deltas = ImmutableArray.CreateBuilder<StockInventoryDelta>();
            foreach (var pair in acquisitions)
            {
                var net = Math.Max(0, checked(afterAll.GetValueOrDefault(pair.Key) - beforeAll.GetValueOrDefault(pair.Key)));
                if (net > 0) deltas.Add(new(pair.Key, Math.Min(net, pair.Value), ItemReceiptKind.Acquisition, true));
            }
            foreach (var id in moved)
                deltas.Add(new(id, 0, ItemReceiptKind.Transfer));
            Snapshot = new(Aggregate(next, known), true, at);
            _stacks = next; _observedBagItems = known;
            return deltas.ToImmutable();
        }
        catch (OverflowException) { Reset(); return []; }
    }

    private static ImmutableDictionary<int, long> Aggregate(Dictionary<long, CurrencyInventoryItem> stacks, HashSet<int> known)
    {
        var quantities = known.ToDictionary(id => id, _ => 0L);
        foreach (var item in stacks.Values.Where(item => item.Container == 1))
            quantities[item.ItemId] = checked(quantities.GetValueOrDefault(item.ItemId) + item.Count);
        return quantities.ToImmutableDictionary();
    }

    private static Dictionary<int, long> AggregateAll(Dictionary<long, CurrencyInventoryItem> stacks)
    {
        var result = new Dictionary<int, long>();
        foreach (var item in stacks.Values) result[item.ItemId] = checked(result.GetValueOrDefault(item.ItemId) + item.Count);
        return result;
    }
}
