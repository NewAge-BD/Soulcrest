using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class MerchantShopMessageTests
{
    [Theory]
    [InlineData((byte)201)]
    [InlineData((byte)203)]
    [InlineData((byte)204)]
    public void TenRowsConsumeTheWholeObservedEnvelopeAndKeepGroupKeysRaw(byte group)
    {
        var expected = Offers(10);
        var message = Snapshot(expected, group);
        Assert.Equal(359, message.Length); // 357-byte body: 2 + 10*34 + 3 packed bytes + 12 tail.
        var actual = Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(message));
        Assert.Equal(group, actual.GroupKey);
        Assert.Equal(expected, actual.Offers);
        Assert.Null(MerchantShopMessage.TryParseStatus(message));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void BooleanPackingCarriesAcrossScalarsAndAcrossFourRowBoundaries(int count)
    {
        var expected = Offers(count);
        var message = Snapshot(expected);
        Assert.Equal(2 + 2 + count * 34 + (count * 2 + 7) / 8 + 12, message.Length);
        Assert.Equal(expected, Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(message)).Offers);
        if (count > 1) Assert.Null(MerchantShopMessage.TryParse(Snapshot(expected, rowLocalBooleans: true)));
    }

    [Fact]
    public void OpaqueFieldsTailAndUnusedBooleanBitsAreNotInterpreted()
    {
        var expected = Offers(10);
        var first = Snapshot(expected, opaqueField: 0, opaqueTail: 0, booleanPattern: 0);
        var second = Snapshot(expected, opaqueField: ushort.MaxValue, opaqueTail: 255, booleanPattern: 255);
        Assert.Equal(expected, Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(first)).Offers);
        Assert.Equal(expected, Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(second)).Offers);
    }

    [Fact]
    public void EveryCutTrailingBytesAndOtherOpcodesAreRejected()
    {
        var message = Snapshot(Offers(10));
        for (var length = 0; length < message.Length; length++)
            Assert.Null(MerchantShopMessage.TryParse(message.AsSpan(0, length)));
        Assert.Null(MerchantShopMessage.TryParse([.. message, 0]));
        foreach (var opcode in new ushort[] { 0x5690, 0x5693, 0x9000, 0xFFFF })
            Assert.Null(MerchantShopMessage.TryParse(Snapshot(Offers(10), opcode: opcode)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void CountMustBeBetweenOneAndTen(int count) =>
        Assert.Null(MerchantShopMessage.TryParse(Snapshot(Offers(count))));

    [Fact]
    public void NoncanonicalOverflowingAndUnterminatedCountsAreRejectedWithoutThrowing()
    {
        foreach (var count in new byte[][] { [0x80, 0], [0xFF, 0xFF, 0xFF, 0xFF, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80], [0xFF, 0xFF, 0xFF, 0xFF, 0x0F] })
            Assert.Null(MerchantShopMessage.TryParse([0x91, 0x56, 201, .. count, .. new byte[400]]));
        Assert.Null(MerchantShopMessage.TryParse([0x91, 0x56, 201, 10]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GoodsAndItemIdsMustBeUniqueWithinTheSnapshot(bool duplicateGoods)
    {
        var offers = Offers(10);
        offers[7] = duplicateGoods ? offers[7] with { GoodsId = offers[0].GoodsId }
            : offers[7] with { ItemId = offers[0].ItemId };
        Assert.Null(MerchantShopMessage.TryParse(Snapshot(offers)));
    }

    public static IEnumerable<object[]> InvalidScalars()
    {
        var offer = Offers(10)[6];
        foreach (var id in new[] { 0, -1, int.MinValue })
        {
            yield return [offer with { GoodsId = id }];
            yield return [offer with { ItemId = id }];
        }
        foreach (var quantity in new[] { 0L, -1L, long.MinValue }) yield return [offer with { Quantity = quantity }];
        yield return [offer with { OriginalPrice = -1 }];
        yield return [offer with { OriginalPrice = long.MinValue }];
        yield return [offer with { CurrentPrice = -1 }];
        yield return [offer with { CurrentPrice = long.MinValue }];
        yield return [offer with { CurrentPrice = offer.OriginalPrice + 1 }];
    }

    [Theory]
    [MemberData(nameof(InvalidScalars))]
    public void InvalidSignedScalarsCannotProduceAPartialSnapshot(MerchantOffer invalid)
    {
        var offers = Offers(10);
        offers[6] = invalid;
        Assert.Null(MerchantShopMessage.TryParse(Snapshot(offers)));
    }

    [Fact]
    public void PositiveInt64QuantityAndOrderedPricesNeedNoInventedGameplayLimit()
    {
        MerchantOffer[] offers = [new(int.MaxValue, int.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue)];
        Assert.Equal(offers, Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(Snapshot(offers, group: 255))).Offers);
        offers[0] = offers[0] with { OriginalPrice = 0, CurrentPrice = 0 };
        Assert.Equal(offers, Assert.IsType<MerchantShopSnapshot>(MerchantShopMessage.TryParse(Snapshot(offers, group: 1))).Offers);
        Assert.Null(MerchantShopMessage.TryParse(Snapshot(offers, group: 0)));
    }

    [Theory]
    [InlineData((byte)201)]
    [InlineData((byte)203)]
    [InlineData((byte)204)]
    [InlineData((byte)255)]
    public void StatusOnlyRecognizesTheCompleteSuccessfulRawGroupMarker(byte group)
    {
        var message = Status(group);
        Assert.Equal(group, MerchantShopMessage.TryParseStatus(message));
        Assert.Equal(5, message.Length);
        Assert.Null(MerchantShopMessage.TryParse(message));
        for (var length = 0; length < message.Length; length++)
            Assert.Null(MerchantShopMessage.TryParseStatus(message.AsSpan(0, length)));
        Assert.Null(MerchantShopMessage.TryParseStatus([.. message, 0]));
    }

    [Fact]
    public void StatusDoesNotAcceptFailedResultsZeroGroupOrAnotherOpcode()
    {
        Assert.Null(MerchantShopMessage.TryParseStatus(Status(0)));
        Assert.Null(MerchantShopMessage.TryParseStatus(Status(204, result: 1)));
        Assert.Null(MerchantShopMessage.TryParseStatus(Status(204, result: ushort.MaxValue)));
        Assert.Null(MerchantShopMessage.TryParseStatus(Status(204, opcode: 0x5691)));
    }

    private static MerchantOffer[] Offers(int count) => Enumerable.Range(0, count).Select(i =>
        new MerchantOffer(10_000 + i * 257, 20_000 + i * 129, 3 + i * 2, 1000 + i * 17, 1000 + i * 13)).ToArray();

    private static byte[] Snapshot(IReadOnlyList<MerchantOffer> offers, byte group = 201, ushort opcode = 0x5691,
        ushort opaqueField = 0xA17F, byte opaqueTail = 0xD3, byte booleanPattern = 0xB6, bool rowLocalBooleans = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(opcode); writer.Write(group); writer.Write(checked((byte)offers.Count));
        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            writer.Write(offer.GoodsId); writer.Write(offer.ItemId); writer.Write(offer.Quantity);
            writer.Write(opaqueField); writer.Write(offer.OriginalPrice); writer.Write(offer.CurrentPrice);
            // Independent fixed placement: one byte after rows 0, 4 and 8, carried over all
            // intervening scalar bytes. A row-local writer intentionally produces a wrong layout.
            if (rowLocalBooleans || i % 4 == 0) writer.Write(booleanPattern);
        }
        writer.Write(Enumerable.Repeat(opaqueTail, 12).ToArray());
        return stream.ToArray();
    }

    private static byte[] Status(byte group, ushort result = 0, ushort opcode = 0x5693) =>
        [.. BitConverter.GetBytes(opcode), .. BitConverter.GetBytes(result), group];
}
