using System.Text.Json;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Independent synthetic fixtures only; no captured payloads or native player keys.</summary>
public sealed class CurrencyInventoryMessageTests
{
    [Fact]
    public void ListReturnsOnlyExplicitWalletComponentsAndPreservesInt64()
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(6);
        packet.Item(11, 930100031, (1L << 60) + 17);
        packet.Item(12, 930100001, 0);
        packet.Item(13, 930100030, 999); // Combined is not a third component.
        packet.Item(14, 930100003, 999); // AP is outside this source.
        packet.Item(15, 930100031, 888, container: 2); // Storage is not a wallet.
        packet.Item(16, 1234, 1, equipment: true);
        packet.Tail();

        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        Assert.Equal(528, list.Header);
        Assert.Equal([930100031, 930100001], list.Items.Select(item => item.ItemId));
        Assert.Equal((1L << 60) + 17, list.Items[0].Count);
        Assert.Equal(0, list.Items[1].Count);
        Assert.All(list.Items, item => Assert.Equal(1, item.Container));
        Assert.Equal([930100031, 930100001, 930100030, 930100003, 930100031, 1234], list.AllItems.Select(item => item.ItemId));
        Assert.Same(list.Items[0], list.AllItems[0]);
        Assert.Same(list.Items[1], list.AllItems[1]);
        var writable = Assert.IsAssignableFrom<IList<CurrencyInventoryItem>>(list.Items);
        Assert.Throws<NotSupportedException>(() => writable.Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CurrencyInventoryItem>)list.AllItems).Clear());
    }

    [Fact]
    public void EmptyListAndMissingComponentDoNotCreateZeroRecords()
    {
        var empty = List();
        Assert.Empty(Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(empty)).Items);
        var one = List((11, 930100031, 0, 1));
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(one));
        Assert.Single(list.Items);
        Assert.Equal(0, list.Items[0].Count);
        Assert.DoesNotContain(list.Items, item => item.ItemId == 930100001);
    }

    [Theory]
    [InlineData(0, 11, 0, 0)]
    [InlineData(1, 15, 20, 255)]
    [InlineData(2, 0, 38, 0)]
    public void ChangesReturnRawModificationAndAllReasonsWithoutIncomeClassification(byte kind, byte increase, byte decrease, byte update)
    {
        using var packet = new Packet(0x561B);
        packet.V(1); packet.U8(kind); packet.Item(11, 930100001, 7);
        packet.Reasons(increase, decrease, update);
        var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(packet.Bytes));
        var change = Assert.Single(changes.Items);
        Assert.Equal(kind, change.ModificationType);
        Assert.Equal(7, change.Item.Count); // Remove is retained raw; the reducer applies its zero semantics.
        Assert.Equal(increase, change.IncreaseReason);
        Assert.Equal(decrease, change.DecreaseReason);
        Assert.Equal(update, change.UpdateReason);
        Assert.Same(change, Assert.Single(changes.AllChanges));
        Assert.Throws<NotSupportedException>(() => ((IList<CurrencyInventoryChange>)changes.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CurrencyInventoryChange>)changes.AllChanges).Clear());
    }

    [Fact]
    public void ChangeWithLowerOrZeroBalanceIsStructurallyValid()
    {
        foreach (var count in new long[] { 0, 1 })
        {
            var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(Change(count: count)));
            Assert.Equal(count, Assert.Single(changes.Items).Item.Count);
        }
    }

    [Fact]
    public void TransientInstanceKeysAreExcludedFromTextAndJson()
    {
        const long syntheticKey = 8877665544332211;
        var message = List((syntheticKey, 930100031, 17, 1));
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(message));
        Assert.Equal(syntheticKey, list.Items[0].InstanceKey);
        var json = JsonSerializer.Serialize(list);
        Assert.DoesNotContain(syntheticKey.ToString(), json);
        Assert.DoesNotContain(nameof(CurrencyInventoryItem.InstanceKey), json);
        Assert.DoesNotContain(syntheticKey.ToString(), list.Items[0].ToString());
        Assert.DoesNotContain(syntheticKey.ToString(), new CurrencyInventoryChange(1, list.Items[0], 11, 0, 0).ToString());
    }

    [Fact]
    public void BooleanCacheCrossesEquipmentStringsItemsAndNonWalletRecords()
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(13);
        for (var index = 0; index < 11; index++)
            packet.Item(100 + index, 2000 + index, index, equipment: index % 2 == 0,
                maker: [0xff, 0xfe, 0x80], locked: index % 3 == 0, fixedPeriod: index % 4 == 0);
        packet.Item(11, 930100031, 12345, maker: [0xff, 0x80], locked: false);
        packet.Item(12, 930100001, 6789, fixedPeriod: false);
        packet.Tail();
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        Assert.Equal([12345L, 6789L], list.Items.Select(item => item.Count));
    }

    [Fact]
    public void CompleteEquipmentInUnrelatedChangesIsValidatedAndSkipped()
    {
        using var packet = new Packet(0x561B);
        packet.V(2);
        packet.U8(0); packet.Item(77, 1234, 1, equipment: true); packet.Reasons(5, 0, 0);
        packet.U8(1); packet.Item(11, 930100031, 111); packet.Reasons(11, 0, 0);
        var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(packet.Bytes));
        Assert.Equal(111, Assert.Single(changes.Items).Item.Count);
        Assert.Equal(2, changes.AllChanges.Count);
        Assert.Equal(1234, changes.AllChanges[0].Item.ItemId);
        Assert.Equal(5, changes.AllChanges[0].IncreaseReason);
        Assert.Same(changes.Items[0], changes.AllChanges[1]);
    }

    [Fact]
    public void AllChangesKeepUnrelatedAddsUpdatesRemovalsAndRawReasonsInWireOrder()
    {
        using var packet = new Packet(0x561B);
        packet.V(3);
        packet.U8(0); packet.Item(31, 1234, 2, equipment: true); packet.Reasons(5, 0, 0);
        packet.U8(1); packet.Item(32, 2345, 8, container: 4); packet.Reasons(41, 0, 7);
        packet.U8(2); packet.Item(32, 2345, 0, container: 4); packet.Reasons(0, 38, 0);
        var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(packet.Bytes));
        Assert.Empty(changes.Items);
        Assert.Equal([0, 1, 2], changes.AllChanges.Select(item => (int)item.ModificationType));
        Assert.Equal([2L, 8L, 0L], changes.AllChanges.Select(item => item.Item.Count));
        Assert.Equal([5, 41, 0], changes.AllChanges.Select(item => (int)item.IncreaseReason));
        Assert.Equal(38, changes.AllChanges[2].DecreaseReason);
        Assert.Equal(7, changes.AllChanges[1].UpdateReason);
    }

    [Fact]
    public void AllItemRecordsExcludeGenericInstanceKeysAndMakerBytesFromSerialization()
    {
        const long syntheticKey = 8877665544332244;
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(1); packet.Item(syntheticKey, 1234, 7, maker: [0x53, 0x65, 0x63, 0x72, 0x65, 0x74]); packet.Tail();
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        Assert.Empty(list.Items);
        Assert.Equal(syntheticKey, Assert.Single(list.AllItems).InstanceKey);
        var json = JsonSerializer.Serialize(list);
        Assert.DoesNotContain(syntheticKey.ToString(), json);
        Assert.DoesNotContain("InstanceKey", json);
        Assert.DoesNotContain("Secret", json);
    }

    [Fact]
    public void AllItemsCanReachTheSharedBoundWithoutDroppingValidatedRecords()
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(CurrencyInventoryMessage.MaximumItems);
        for (var index = 0; index < CurrencyInventoryMessage.MaximumItems; index++) packet.Item(index + 1, 1234, 1);
        packet.Tail();
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        Assert.Empty(list.Items);
        Assert.Equal(CurrencyInventoryMessage.MaximumItems, list.AllItems.Count);
        Assert.Equal(CurrencyInventoryMessage.MaximumItems, list.AllItems[^1].InstanceKey);
    }

    [Fact]
    public void EveryTruncationAndTrailingBytesAreRejected()
    {
        using var list = new Packet(0x5611);
        list.U16(528); list.V(2); list.Item(77, 1234, 1, equipment: true); list.Item(11, 930100031, 9); list.Tail();
        using var change = new Packet(0x561B);
        change.V(2); change.U8(0); change.Item(77, 1234, 1, equipment: true); change.Reasons(5, 0, 0);
        change.U8(1); change.Item(11, 930100031, 10); change.Reasons(11, 0, 0);
        EveryCutRejected(list.Bytes, true);
        EveryCutRejected(change.Bytes, false);
        Assert.Null(CurrencyInventoryMessage.TryParseList([.. list.Bytes, 0]));
        Assert.Null(CurrencyInventoryMessage.TryParseChanges([.. change.Bytes, 0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(529)]
    [InlineData(65535)]
    public void UnknownOpaqueListHeaderIsRejected(ushort header)
    {
        using var packet = new Packet(0x5611);
        packet.U16(header); packet.V(0); packet.Tail();
        Assert.Null(CurrencyInventoryMessage.TryParseList(packet.Bytes));
    }

    [Fact]
    public void WrongOpcodesCannotBeUsedAsListsOrChanges()
    {
        Assert.Null(CurrencyInventoryMessage.TryParseList(Change()));
        Assert.Null(CurrencyInventoryMessage.TryParseChanges(List()));
        Assert.Null(CurrencyInventoryMessage.TryParseList([]));
        Assert.Null(CurrencyInventoryMessage.TryParseChanges([0x1b]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExplicitWalletRequiresPositiveInstance(long key) =>
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((key, 930100031, 5, 1))));

    [Theory]
    [InlineData(930100031)]
    [InlineData(1234)]
    public void NegativeAmountsAndUnknownOptionFlagsFailEvenForUnrelatedItems(int id)
    {
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, id, -1, 1))));
        foreach (var flags in new byte[] { 2, 3, 255 })
        {
            using var packet = new Packet(0x5611);
            packet.U16(528); packet.V(1); packet.Item(11, id, 1, flags: flags); packet.Tail();
            Assert.Null(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidItemDefinitionIsRejected(int id) =>
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, id, 1, 1))));

    [Theory]
    [InlineData(930100031)]
    [InlineData(930100001)]
    public void EquipmentVariantIsNotQualifiedForWalletRecords(int id)
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(1); packet.Item(11, id, 1, equipment: true); packet.Tail();
        Assert.Null(CurrencyInventoryMessage.TryParseList(packet.Bytes));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public void UnknownModificationTypeIsRejected(byte kind) =>
        Assert.Null(CurrencyInventoryMessage.TryParseChanges(Change(kind: kind)));

    [Fact]
    public void DuplicateWalletInstanceCurrencyAndCrossItemCollisionAreRejected()
    {
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, 930100031, 5, 1), (11, 930100001, 7, 1))));
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, 930100031, 5, 1), (12, 930100031, 7, 1))));
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, 1234, 1, 1), (11, 930100031, 5, 1))));
        Assert.Null(CurrencyInventoryMessage.TryParseList(List((11, 930100031, 5, 1), (11, 1234, 1, 1))));
        using var packet = new Packet(0x561B);
        packet.V(2);
        packet.U8(1); packet.Item(11, 930100031, 5); packet.Reasons(11, 0, 0);
        packet.U8(1); packet.Item(11, 930100031, 6); packet.Reasons(11, 0, 0);
        Assert.Null(CurrencyInventoryMessage.TryParseChanges(packet.Bytes));
    }

    [Fact]
    public void UnobservedContainersAndUnrelatedIdsDoNotCreateWalletRecords()
    {
        foreach (var container in new byte[] { 0, 2, 3, 4, 15, 255 })
            Assert.Empty(Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(List((11, 930100031, 7, container)))).Items);
        Assert.Empty(Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(Change(id: 930100030))).Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(255)]
    public void KinaChangesInOtherContainersRemainAvailableForWalletInvalidation(byte container)
    {
        var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(Change(container: container)));
        var item = Assert.Single(changes.Items).Item;
        Assert.Equal(container, item.Container);
        Assert.Equal(930100031, item.ItemId);
        Assert.Equal(11, item.InstanceKey);
    }

    [Fact]
    public void SeparateStorageInstanceAndWalletChangeCanCoexistWithoutMergingTheirAmounts()
    {
        using var packet = new Packet(0x561B);
        packet.V(2);
        packet.U8(1); packet.Item(11, 930100031, 123); packet.Reasons(11, 0, 0);
        packet.U8(1); packet.Item(12, 930100031, 987, container: 2); packet.Reasons(15, 0, 0);
        var changes = Assert.IsType<CurrencyInventoryChanges>(CurrencyInventoryMessage.TryParseChanges(packet.Bytes));
        Assert.Equal(2, changes.Items.Count);
        Assert.Equal([123L, 987L], changes.Items.Select(item => item.Item.Count));
        Assert.Equal([1, 2], changes.Items.Select(item => (int)item.Item.Container));
        Assert.NotEqual(changes.Items[0].Item.InstanceKey, changes.Items[1].Item.InstanceKey);
    }

    [Fact]
    public void ResourceAndVarintLimitsFailClosed()
    {
        var tooLarge = new byte[CurrencyInventoryMessage.MaximumMessageBytes + 1];
        tooLarge[0] = 0x11; tooLarge[1] = 0x56;
        Assert.Null(CurrencyInventoryMessage.TryParseList(tooLarge));
        tooLarge[0] = 0x1b;
        Assert.Null(CurrencyInventoryMessage.TryParseChanges(tooLarge));
        foreach (var count in new byte[][] { [0x80, 0], [0xff, 0xff, 0xff, 0xff, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80] })
        {
            Assert.Null(CurrencyInventoryMessage.TryParseList([0x11, 0x56, 0x10, 0x02, .. count]));
            Assert.Null(CurrencyInventoryMessage.TryParseChanges([0x1b, 0x56, .. count]));
        }
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(CurrencyInventoryMessage.MaximumItems + 1);
        for (var index = 0; index <= CurrencyInventoryMessage.MaximumItems; index++) packet.Item(index + 1, 1234, 1);
        packet.Tail();
        Assert.Null(CurrencyInventoryMessage.TryParseList(packet.Bytes));
    }

    [Fact]
    public void MakerStringLimitIsEnforcedWithoutDecoding()
    {
        foreach (var length in new[] { CurrencyInventoryMessage.MaximumStringBytes, CurrencyInventoryMessage.MaximumStringBytes + 1 })
        {
            using var packet = new Packet(0x5611);
            packet.U16(528); packet.V(1); packet.Item(11, 930100031, 1, maker: new byte[length]); packet.Tail();
            if (length == CurrencyInventoryMessage.MaximumStringBytes) Assert.NotNull(CurrencyInventoryMessage.TryParseList(packet.Bytes));
            else Assert.Null(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        }
    }

    private static byte[] List(params (long Key, int Id, long Count, byte Container)[] items)
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(items.Length);
        foreach (var item in items) packet.Item(item.Key, item.Id, item.Count, item.Container);
        packet.Tail();
        return packet.Bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllItemsPreservesEquipmentVariantForIndependentCurrencyQualification(bool equipment)
    {
        using var packet = new Packet(0x5611);
        packet.U16(528); packet.V(1); packet.Item(91, 930100003, 10, equipment: equipment); packet.Tail();
        var list = Assert.IsType<CurrencyInventoryList>(CurrencyInventoryMessage.TryParseList(packet.Bytes));
        Assert.Equal(equipment, Assert.Single(list.AllItems).HasEquipmentData);
        Assert.Empty(list.Items);
    }

    private static byte[] Change(int id = 930100031, long count = 100, byte kind = 1, byte container = 1)
    {
        using var packet = new Packet(0x561B);
        packet.V(1); packet.U8(kind); packet.Item(11, id, count, container); packet.Reasons(11, 0, 0);
        return packet.Bytes;
    }

    private static void EveryCutRejected(byte[] bytes, bool list)
    {
        for (var length = 0; length < bytes.Length; length++)
        {
            if (list) Assert.Null(CurrencyInventoryMessage.TryParseList(bytes.AsSpan(0, length)));
            else Assert.Null(CurrencyInventoryMessage.TryParseChanges(bytes.AsSpan(0, length)));
        }
    }

    private sealed class Packet : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private int? _boolIndex;
        private int _boolBit;

        public Packet(ushort opcode)
        {
            _writer = new(_stream);
            _writer.Write(opcode);
        }

        public byte[] Bytes => _stream.ToArray();
        public void U8(byte value) => _writer.Write(value);
        public void U16(ushort value) => _writer.Write(value);
        public void I32(int value) => _writer.Write(value);
        public void I64(long value) => _writer.Write(value);
        public void V(int value)
        {
            var remaining = (uint)value;
            while (remaining >= 128) { U8((byte)(remaining | 128)); remaining >>= 7; }
            U8((byte)remaining);
        }
        private void Bool(bool value)
        {
            if (_boolIndex is null) { _boolIndex = (int)_stream.Position; U8(0); _boolBit = 0; }
            if (value) _stream.GetBuffer()[_boolIndex.Value] |= (byte)(1 << _boolBit);
            if (++_boolBit == 8) _boolIndex = null;
        }
        public void Tail() { foreach (var value in new byte[] { 7, 9, 11, 13, 15, 17 }) U8(value); }
        public void Reasons(byte increase, byte decrease, byte update) { U8(increase); U8(decrease); U8(update); }
        public void Item(long key, int id, long count, byte container = 1, bool equipment = false,
            byte? flags = null, byte[]? maker = null, bool locked = true, bool fixedPeriod = true)
        {
            U8(flags ?? (equipment ? (byte)1 : (byte)0)); I64(key); I32(id); I64(count); U8(container); I32(99);
            Bool(locked); maker ??= [0xff, 0x80]; V(maker.Length); _writer.Write(maker); I64(123456); Bool(fixedPeriod);
            if (equipment) Equipment();
        }
        private void Equipment()
        {
            U8(9); I32(11); I32(12); U8(3); I32(13); I32(100); Bool(true); U8(4);
            V(2); foreach (var value in new[] { 101, 102 }) { I32(value); U16(12); U8(2); }
            I64(99); U8(1); V(2); I32(41); I32(42);
            foreach (var width in new[] { 6, 6, 6, 5, 5, 6, 5 })
            {
                V(2);
                foreach (var value in new[] { 1, 2 })
                {
                    if (width == 6) { U16((ushort)value); I32(value * 111); }
                    else { I32(value * 222); U8((byte)value); }
                }
            }
            U8(1);
        }
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
