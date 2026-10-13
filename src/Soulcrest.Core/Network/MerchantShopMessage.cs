namespace Soulcrest.Core.Network;

public sealed record MerchantOffer(int GoodsId, int ItemId, long Quantity, long OriginalPrice, long CurrentPrice);
public sealed record MerchantShopSnapshot(byte GroupKey, IReadOnlyList<MerchantOffer> Offers);

/// <summary>
/// Complete observed merchant-list envelopes. Group keys remain raw; opaque fields and boolean
/// bits have no assigned gameplay meaning. A snapshot is not a purchase or progress event.
/// </summary>
public static class MerchantShopMessage
{
    public static MerchantShopSnapshot? TryParse(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x5691)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var groupKey = r.U8();
            var count = r.Count(34, 10);
            if (groupKey == 0 || count == 0) return null;
            var offers = new List<MerchantOffer>(count);
            var goodsIds = new HashSet<int>();
            var itemIds = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var goodsId = r.I32();
                var itemId = r.I32();
                var quantity = r.I64();
                r.Skip(2); // Opaque field: no currency, price rule or flag semantics established.
                var originalPrice = r.I64();
                var currentPrice = r.I64();
                // Boolean bits carry across intervening scalars and records in this envelope.
                _ = r.Bool();
                _ = r.Bool();
                if (goodsId <= 0 || itemId <= 0 || !goodsIds.Add(goodsId) || !itemIds.Add(itemId)
                    || quantity <= 0 || originalPrice < 0 || currentPrice < 0 || currentPrice > originalPrice)
                    return null;
                offers.Add(new(goodsId, itemId, quantity, originalPrice, currentPrice));
            }
            r.Skip(12); // Entire observed opaque tail; no timing semantics established.
            r.Exact();
            return new(groupKey, offers.ToArray());
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Only the complete observed successful group marker; no list contents or purchase result inferred.</summary>
    public static byte? TryParseStatus(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x5693)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var result = r.U16();
            var groupKey = r.U8();
            r.Exact();
            return result == 0 && groupKey > 0 ? groupKey : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
