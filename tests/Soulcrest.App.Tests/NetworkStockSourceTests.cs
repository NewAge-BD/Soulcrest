using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Actual shared entry/inventory path with synthetic wire messages; no capture or real profile.</summary>
public sealed class NetworkStockSourceTests
{
    private const string Game = "synthetic-stock-stream";
    private const int Material = 610660025;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Identity = new(7, 9, 300, "SyntheticStock");

    [Fact]
    public void StockSnapshotsAndReceiptsNeverSerializeOrFormatNativeOwnership()
    {
        var own = new OwnCharacterContext(Identity, 1) { ObservedAt = At };
        var snapshot = NetworkStockSnapshot.Empty with { Character = own, ObservedAt = At };
        var receipt = new ItemReceipt("synthetic-receipt", 1, own, Material, 2, ItemReceiptKind.Unknown, At);
        foreach (var text in new[] { System.Text.Json.JsonSerializer.Serialize(snapshot), System.Text.Json.JsonSerializer.Serialize(receipt), snapshot.ToString(), receipt.ToString() })
        {
            Assert.DoesNotContain("SyntheticStock", text);
            Assert.DoesNotContain("CharacterId", text);
            Assert.DoesNotContain("EntityKey", text);
            Assert.DoesNotContain("ServerId", text);
        }
    }

    [Fact]
    public void OwnEntryPublishesExplicitBagAggregatesWithoutBaselineReceiptsOrStorageInference()
    {
        using var f = new Fixture();
        f.Login(new Item(11, Material, 3), new Item(12, Material, 4), new Item(13, Material, 100, 2));
        var stock = f.Network.StockSnapshot;
        Assert.True(stock.IsFresh); Assert.Equal(7, stock.Items[Material]);
        Assert.Same(f.Network.OwnCharacterSnapshot, stock.Character);
        Assert.Equal(At.AddMilliseconds(20), stock.ObservedAt);
        Assert.False(stock.IsComplete); Assert.Empty(f.Receipts);
        Assert.Equal(StockCompleteness.Unknown, stock.Scopes.Single(scope => scope.Scope == StockScope.CharacterStorage).Completeness);
    }

    [Fact]
    public void AbsoluteUnknownReasonUpdatesStockButDoesNotInventCraftOrMarketReceipt()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 3));
        f.Send(Changes(new Change(new Item(11, Material, 9), Increase: 41)), 200);
        Assert.Equal(9, f.Network.StockSnapshot.Items[Material]); Assert.Empty(f.Receipts);
        f.Send(Changes(new Change(new Item(11, Material, 90), Increase: 5)), 300, "synthetic-foreign");
        Assert.Equal(9, f.Network.StockSnapshot.Items[Material]); Assert.Empty(f.Receipts);
    }

    [Fact]
    public void QualifiedReceiptIsOwnBoundAndMarkedAsAlreadyCoveredByCommittedLoot()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 3));
        f.Send(Changes(new Change(new Item(11, Material, 5), Increase: 5)), 200);
        var receipt = Assert.Single(f.Receipts);
        Assert.Equal(ItemReceiptKind.Acquisition, receipt.Kind); Assert.Equal(2, receipt.Quantity);
        Assert.Equal(At.AddMilliseconds(200), receipt.At); Assert.True(receipt.CoveredByCommittedLoot);
        Assert.Same(f.Network.OwnCharacterSnapshot, receipt.Character);
        f.Send(Changes(new Change(new Item(11, Material, 5), Increase: 5)), 250);
        Assert.Single(f.Receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedUpdatesAndLateListsRevokeOnlyCurrentStockAuthority(bool lateList)
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 3));
        f.Send(lateList ? Snapshot(new Item(11, Material, 99)) : [0x1B, 0x56], 200);
        Assert.False(f.Network.StockSnapshot.Available); Assert.Empty(f.Receipts);
    }

    [Fact]
    public void OwnChangeClearsOldMinimaAndReentrantQueuedReceipts()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 3));
        var old = f.Network.OwnCharacterSnapshot;
        f.Network.FarmDropGained += _ => f.Network.ObserveIdentity(Game, new(8, 9, 301, "SyntheticOther"), At.AddMilliseconds(250));
        f.Send(Changes(new Change(new Item(11, Material, 5), Increase: 5)), 200);
        Assert.NotSame(old, f.Network.OwnCharacterSnapshot);
        Assert.False(f.Network.StockSnapshot.Available); Assert.Empty(f.Receipts);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-stock-network-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<ItemReceipt> Receipts { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Network = new(new ProgressService(Path.Combine(_directory, "progress.json"), new PetCatalog([])), worldContext: new(WorldContextTestCatalog.Create()));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.ItemReceived += Receipts.Add;
        }
        internal void Login(params Item[] items)
        {
            Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Bytes(new byte[4]); w.I64(Identity.CharacterId); }));
            Send(Snapshot(items), 20); Send(Packet(0x3621, w => w.Bytes(new byte[46])), 40);
            Network.ObserveIdentity(Game, Identity, At.AddMilliseconds(80), 100);
        }
        internal void Send(byte[] message, long milliseconds = 0, string stream = Game) => Network.ProcessProgressMessage(stream, message, At.AddMilliseconds(milliseconds));
        public void Dispose() { Network.Dispose(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed record Item(long Key, int Id, long Count, byte Container = 1);
    private sealed record Change(Item Item, byte Modification = 1, byte Increase = 0, byte Decrease = 0, byte Update = 0);
    private static byte[] Snapshot(params Item[] items) => Packet(0x5611, w => { w.U16(528); w.V(items.Length); foreach (var item in items) w.Item(item); w.Bytes(new byte[6]); });
    private static byte[] Changes(params Change[] changes) => Packet(0x561B, w =>
    {
        w.V(changes.Length);
        foreach (var change in changes) { w.U8(change.Modification); w.Item(change.Item); w.U8(change.Increase); w.U8(change.Decrease); w.U8(change.Update); }
    });
    private static byte[] Packet(ushort opcode, Action<Writer> write) { using var w = new Writer(); w.U16(opcode); write(w); return w.ToArray(); }
    private sealed class Writer : IDisposable
    {
        private readonly MemoryStream _stream = new(); private readonly BinaryWriter _writer;
        private int _boolOffset = -1, _boolBit;
        internal Writer() => _writer = new(_stream);
        internal void U8(byte value) => _writer.Write(value); internal void U16(ushort value) => _writer.Write(value);
        internal void I32(int value) => _writer.Write(value); internal void I64(long value) => _writer.Write(value);
        internal void Bytes(byte[] bytes) => _writer.Write(bytes);
        internal void V(int value) { var rest = (uint)value; while (rest >= 128) { U8((byte)(rest | 128)); rest >>= 7; } U8((byte)rest); }
        private void Bool() { if (_boolOffset < 0) { _boolOffset = (int)_stream.Position; U8(0); _boolBit = 0; } if (++_boolBit == 8) _boolOffset = -1; }
        internal void Item(Item item) { U8(0); I64(item.Key); I32(item.Id); I64(item.Count); U8(item.Container); I32(0); Bool(); V(0); I64(0); Bool(); }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
