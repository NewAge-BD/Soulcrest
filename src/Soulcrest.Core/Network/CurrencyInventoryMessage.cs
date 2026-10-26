using System.Text.Json.Serialization;

namespace Soulcrest.Core.Network;

/// <summary>A fully read absolute inventory record. Keys are transient and amounts are never income deltas.</summary>
public sealed record CurrencyInventoryItem([property: JsonIgnore] long InstanceKey, int ItemId, long Count, byte Container)
{
    public override string ToString() => $"CurrencyInventoryItem {{ ItemId = {ItemId}, Count = {Count}, Container = {Container} }}";
}

/// <summary>The known opaque envelope variant; this does not assert inventory completeness or success.</summary>
public sealed record CurrencyInventoryList(ushort Header, IReadOnlyList<CurrencyInventoryItem> Items)
{
    public IReadOnlyList<CurrencyInventoryItem> AllItems { get; init; } = Array.AsReadOnly(Array.Empty<CurrencyInventoryItem>());
}

/// <summary>Raw absolute item change. The reasons are observations, never an income classification.</summary>
public sealed record CurrencyInventoryChange(byte ModificationType, CurrencyInventoryItem Item,
    byte IncreaseReason, byte DecreaseReason, byte UpdateReason);

public sealed record CurrencyInventoryChanges(IReadOnlyList<CurrencyInventoryChange> Items)
{
    public IReadOnlyList<CurrencyInventoryChange> AllChanges { get; init; } = Array.AsReadOnly(Array.Empty<CurrencyInventoryChange>());
}

/// <summary>
/// Strict readers for the observed 0x5611/0x561B envelopes. Every item is consumed, including
/// unrelated equipment. Lists return only explicit bound/unbound Kina wallet records; changes
/// also retain other Kina containers so a matching wallet instance can be invalidated on movement.
/// The additive AllItems/AllChanges collections retain every fully validated record in wire order.
/// Callers must independently establish own identity, source continuity and the
/// entry transaction. An absent record is unknown, never a zero balance or an income event.
/// </summary>
public static class CurrencyInventoryMessage
{
    public const ushort KnownListHeader = 528;
    public const int BoundKinaItemId = 930100001;
    public const int UnboundKinaItemId = 930100031;
    public const byte WalletContainer = 1;
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    internal const int MaximumItems = 10000;
    internal const int MaximumStringBytes = 65536;
    private static readonly int[] EquipmentArrayWidths = [6, 6, 6, 5, 5, 6, 5];

    public static CurrencyInventoryList? TryParseList(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x5611)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var header = reader.U16();
            // User-qualified variant only. No result/status/flag interpretation is established.
            if (header != KnownListHeader) return null;
            var count = reader.Count(35, MaximumItems);
            var wallets = new List<CurrencyInventoryItem>(2);
            var allItems = new List<CurrencyInventoryItem>(count);
            var seen = new WalletUniqueness();
            for (var index = 0; index < count; index++)
            {
                var item = ReadItem(ref reader);
                seen.Observe(item);
                var observation = item.Observation();
                allItems.Add(observation);
                if (item.IsWallet) wallets.Add(observation);
            }
            reader.Skip(6); // Entire observed opaque extension tail, without slot-count semantics.
            reader.Exact();
            return new(header, wallets.AsReadOnly()) { AllItems = allItems.AsReadOnly() };
        }
        catch (InvalidDataException) { return null; }
    }

    public static CurrencyInventoryChanges? TryParseChanges(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x561B)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var count = reader.Count(39, MaximumItems);
            var changes = new List<CurrencyInventoryChange>(2);
            var allChanges = new List<CurrencyInventoryChange>(count);
            var seen = new WalletUniqueness();
            for (var index = 0; index < count; index++)
            {
                var modification = reader.U8();
                if (modification > 2) return null;
                var item = ReadItem(ref reader);
                var increase = reader.U8();
                var decrease = reader.U8();
                var update = reader.U8();
                seen.Observe(item);
                var change = new CurrencyInventoryChange(modification, item.Observation(), increase, decrease, update);
                allChanges.Add(change);
                if (item.IsKina) changes.Add(change);
            }
            reader.Exact();
            return new(changes.AsReadOnly()) { AllChanges = allChanges.AsReadOnly() };
        }
        catch (InvalidDataException) { return null; }
    }

    private static RawItem ReadItem(ref ProgressPacketReader reader)
    {
        var flags = reader.U8();
        if ((flags & ~1) != 0) throw new InvalidDataException("Unknown currency item option flags.");
        var instance = reader.I64();
        var itemId = reader.I32();
        var amount = reader.I64();
        var container = reader.U8();
        reader.Skip(4); // Slot position is not a wallet identity.
        reader.Bool();
        reader.SkipString(MaximumStringBytes); // Maker bytes are never decoded or retained.
        reader.Skip(8);
        reader.Bool();
        if ((flags & 1) != 0) Equipment(ref reader);
        if (itemId <= 0 || amount < 0) throw new InvalidDataException("Invalid currency item amount or definition.");
        var result = new RawItem(instance, itemId, amount, container);
        if (result.IsWallet && (instance <= 0 || flags != 0))
            throw new InvalidDataException("Unsupported wallet record variant.");
        return result;
    }

    private static void Equipment(ref ProgressPacketReader reader)
    {
        reader.Skip(1 + 4 + 4 + 1 + 4 + 4);
        reader.Bool();
        reader.Skip(1);
        reader.Skip(reader.Count(7, MaximumItems) * 7);
        reader.Skip(8 + 1); // Opaque binding identifier is never retained.
        reader.Skip(reader.Count(4, MaximumItems) * 4);
        // Four stat arrays and three skill arrays, in their observed interleaved order.
        foreach (var width in EquipmentArrayWidths)
            reader.Skip(reader.Count(width, MaximumItems) * width);
        reader.Skip(1);
    }

    private readonly record struct RawItem(long Instance, int ItemId, long Count, byte Container)
    {
        public bool IsKina => ItemId is BoundKinaItemId or UnboundKinaItemId;
        public bool IsWallet => Container == WalletContainer && IsKina;
        public CurrencyInventoryItem Observation() => new(Instance, ItemId, Count, Container);
    }

    private sealed class WalletUniqueness
    {
        private readonly Dictionary<long, bool> _instances = [];
        private readonly HashSet<int> _currencies = [];

        public void Observe(RawItem item)
        {
            // A collision with an unrelated item is also ambiguous for this wallet identity.
            if ((_instances.TryGetValue(item.Instance, out var priorWallet) && (priorWallet || item.IsWallet))
                || (item.IsWallet && !_currencies.Add(item.ItemId)))
                throw new InvalidDataException("Ambiguous wallet record.");
            _instances[item.Instance] = item.IsWallet;
        }
    }
}
