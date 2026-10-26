using System.Reflection;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Shared dispatcher with synthetic inventory/Own packets; no capture, process or user profile.</summary>
public sealed class NetworkFarmDropSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Identity = new(7, 9, 300, "SyntheticFarm");
    private const string Game = "synthetic-farm-own", Foreign = "synthetic-farm-foreign";
    private const int Helm = 210350009, Material = 610660025;

    [Fact]
    public void RepeatedCreationCannotBecomeANewDropAfterTheComparisonBaseWasRejected()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 200);
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 210);
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 220);
        f.Send(Changes(new Change(new(11, Helm, 2))), 230);
        Assert.Equal(1, Assert.Single(f.Gains).Quantity);
        f.Send(Changes(new Change(new(11, Helm, 0), 2)), 240);
        f.Send(Changes(new Change(new(11, Helm, 2), 0)), 250);
        Assert.Equal(new long[] { 1, 2 }, f.Gains.Select(gain => gain.Quantity));
    }

    [Fact]
    public void AddCountsFirstDropWithoutAnyWalletOrInventoryBaseline()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, Helm, 3), 0)), 200);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(Helm, gain.DataId); Assert.Equal(3, gain.Quantity);
        Assert.Equal(NetworkFarmDropKind.Item, gain.Kind);
        Assert.Equal(At.AddMilliseconds(200), gain.At);
        Assert.Same(f.Source.OwnCharacter, gain.Character);
        Assert.False(gain.InInstance); Assert.True(gain.Sequence > 0);
    }

    [Fact]
    public void QualifiedLoginSeedsAllItemsAndCountsOnlyTheirSubsequentDifference()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 11))), 200);
        Assert.Equal(3, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void UnknownModificationEstablishesComparisonBaseWithoutInventedGain()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, Material, 8))), 100);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 11))), 200);
        Assert.Equal(3, Assert.Single(f.Gains).Quantity);
    }

    [Theory]
    [InlineData(41, 0, 0)]
    [InlineData(5, 1, 0)]
    [InlineData(5, 0, 1)]
    public void NonQualifiedReasonsAdvanceTheComparisonBaseOnly(byte increase, byte decrease, byte update)
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        f.Send(Changes(new Change(new(11, Material, 12), Increase: increase, Decrease: decrease, Update: update)), 200);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 14))), 300);
        Assert.Equal(2, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void DecreaseRemovalAndDuplicateAbsoluteValuesAreNotIncome()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        f.Send(Changes(new Change(new(11, Material, 7))), 100);
        f.Send(Changes(new Change(new(11, Material, 7))), 200);
        f.Send(Changes(new Change(new(11, Material, 0), 2, 0, 1)), 300);
        f.Send(Changes(new Change(new(11, Material, 12))), 400);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 14))), 500);
        Assert.Equal(2, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void ItemOrContainerChangeCannotCompareUnrelatedStackCounts()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        f.Send(Changes(new Change(new(11, Helm, 9))), 100);
        f.Send(Changes(new Change(new(11, Helm, 10, 2))), 200);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Helm, 11, 2))), 300);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Helm, 12))), 400);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Helm, 13))), 500);
        Assert.Equal(1, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void StorageCreationIsNotFarmIncomeAndDuplicateCreationRevokesItsComparisonBase()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        f.Send(Changes(new Change(new(12, Helm, 1, 2), 0)), 100);
        f.Send(Changes(new Change(new(11, Material, 3), 0)), 200);
        f.Send(Changes(new Change(new(11, Material, 9))), 300);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 10))), 400);
        Assert.Equal(1, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void CurrencyAndAutomaticallyConsumedSoulItemAreExcludedButCanonicalCrystalIsCounted()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, 930100031, 20), 0), new(new(12, 930100001, 20), 0),
            new(new(13, 534660212, 1, 4), 0)), 100);
        Assert.Empty(f.Gains);
        f.Send(Crystals(2, (2, 1)), 200);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(NetworkFarmDropKind.SoulCrystal, gain.Kind);
        Assert.Equal(2, gain.DataId); Assert.Equal(1, gain.Quantity);
    }

    [Fact]
    public void PreparedUniversalCrystalInventoryVariantCountsOneCurrencyWithoutInventingFiveCrystalCategories()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, NetworkFarmDrop.UniversalSoulCrystalItemId, 1000), 0),
            new(new(12, 531900009, 1000), 0), new(new(13, 930100003, 1000), 0)), 100);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(NetworkFarmDropKind.UniversalSoulCrystal, gain.Kind);
        Assert.Equal(930500001, gain.DataId); Assert.Equal(1000, gain.Quantity);
        Assert.Same(f.Source.OwnCharacter, gain.Character);
        // Native 0x9011 type 6 has not been qualified by this preparation.
        f.Send(Crystals(2, (6, 1000)), 200);
        Assert.Single(f.Gains);
        f.Send(Crystals(2, (2, 1)), 300);
        Assert.Equal(NetworkFarmDropKind.SoulCrystal, f.Gains[1].Kind);
        Assert.Equal(2, f.Gains[1].DataId);
        Assert.Equal(1, f.Gains[1].Quantity);
    }

    [Fact]
    public void UniversalCrystalLoginSnapshotIsNotIncomeAndKnownStackUsesOnlyItsPositiveDifference()
    {
        using var f = new Fixture(); f.Login(new Item(11, 930500001, 5000));
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, 930500001, 6000))), 100);
        f.Send(Changes(new Change(new(11, 930500001, 6000))), 200); // Absolute echo.
        Assert.Equal(1000, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void UnknownUniversalCrystalStackCannotTreatAnAbsoluteBalanceAsIncome()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, 930500001, 5000))), 100);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, 930500001, 6000))), 200);
        Assert.Equal(1000, Assert.Single(f.Gains).Quantity);
    }

    [Theory]
    [InlineData(1, 31, 0, 0)]
    [InlineData(1, 35, 0, 0)]
    [InlineData(1, 56, 0, 0)]
    [InlineData(1, 63, 0, 0)]
    [InlineData(1, 81, 0, 0)]
    [InlineData(1, 5, 1, 0)]
    [InlineData(1, 5, 0, 1)]
    [InlineData(2, 5, 0, 0)]
    [InlineData(4, 5, 0, 0)]
    public void UniversalCrystalRequiresTheSameQualifiedPersonalInventoryReasons(byte container, byte increase, byte decrease, byte update)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, 930500001, 1000, container), 0, increase, decrease, update)), 100);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void UniversalCrystalSpendingAndAutoConsumedItemEchoNeverBecomeAdditionalIncome()
    {
        using var f = new Fixture(); f.Login(new Item(11, 930500001, 5000));
        f.Send(Changes(new Change(new(11, 930500001, 4000), Increase: 0, Decrease: 1)), 100);
        f.Send(Changes(new Change(new(12, 531900009, 1000), 0),
            new(new(12, 531900009, 0), 2, 0, 1)), 200);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, 930500001, 5000))), 300);
        f.Send(Changes(new Change(new(11, 930500001, 5000))), 400);
        Assert.Equal(1000, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void RepeatedUniversalCreationRevokesTheBaseUntilAnExplicitModificationRestoresItWithoutIncome()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, 930500001, 1000), 0)), 100);
        f.Send(Changes(new Change(new(11, 930500001, 1000), 0)), 200);
        f.Send(Changes(new Change(new(11, 930500001, 2000), 0)), 300);
        f.Send(Changes(new Change(new(11, 930500001, 3000))), 400);
        Assert.Equal(1000, Assert.Single(f.Gains).Quantity);
        f.Send(Changes(new Change(new(11, 930500001, 4000))), 500);
        Assert.Equal(new long[] { 1000, 1000 }, f.Gains.Select(gain => gain.Quantity));
    }

    [Fact]
    public void UniversalCrystalGapRejectsTheOldAbsoluteComparisonBase()
    {
        using var f = new Fixture(); f.Login(new Item(11, 930500001, 5000));
        f.Network.ResetProgressContext("synthetic-gap", At.AddMilliseconds(100));
        f.Bind();
        f.Send(Changes(new Change(new(11, 930500001, 6000))), 200);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, 930500001, 7000))), 300);
        Assert.Equal(1000, Assert.Single(f.Gains).Quantity);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("foreign")]
    [InlineData("stopped")]
    public void NonCurrentSourcesCannotProduceDrops(string source)
    {
        using var f = new Fixture();
        if (source != "unbound") f.Bind();
        if (source == "stopped") f.SetRunning(false);
        var stream = source == "foreign" ? Foreign : Game;
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 100, stream);
        f.Send(Crystals(2, (2, 1)), 200, stream);
        f.Send(Changes(new Change(new(12, 930500001, 1000), 0)), 300, stream);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void MissingPreparationCannotSeedStackBalances()
    {
        using var f = new Fixture();
        f.Send(Entry()); f.Send(Snapshot(new Item(11, Material, 8)), 20); f.Bind();
        f.Send(Changes(new Change(new(11, Material, 11))), 100);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 12))), 200);
        Assert.Equal(1, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void AmbiguousSnapshotUidRejectsTheWholeGenericBaseline()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8), new(11, Helm, 1), new(12, Material, 4));
        f.Send(Changes(new Change(new(12, Material, 5))), 100);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(12, Material, 7))), 200);
        Assert.Equal(2, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void PreOwnInventoryModificationRejectsStaleGenericBaseline()
    {
        using var f = new Fixture();
        f.Send(Entry()); f.Send(Snapshot(new Item(11, Material, 8)), 20);
        f.Send(Changes(new Change(new(11, Material, 9))), 30);
        f.Send(Preparation(), 40); f.Bind();
        f.Send(Changes(new Change(new(11, Material, 11))), 200);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void LateInventoryListAndMalformedChangesRevokeTheComparisonBase()
    {
        using var f = new Fixture(); f.Login(new Item(11, Material, 8));
        f.Send(Snapshot(new Item(11, Material, 100)), 100);
        f.Send(Changes(new Change(new(11, Material, 11))), 200);
        Assert.Empty(f.Gains);
        f.Send([0x1b, 0x56, 1], 300);
        f.Send(Changes(new Change(new(11, Material, 14))), 400);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 15))), 500);
        Assert.Equal(1, Assert.Single(f.Gains).Quantity);
    }

    [Fact]
    public void ResetAndOwnChangeDoNotLeakOldStackCountsOrResetEventSequence()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 100);
        f.Network.ResetProgressContext("synthetic-reset", At.AddMilliseconds(200));
        f.Bind(new(8, 9, 301, "SyntheticOther"));
        f.Send(Changes(new Change(new(11, Material, 5))), 300);
        Assert.Single(f.Gains);
        f.Send(Changes(new Change(new(11, Material, 6))), 400);
        Assert.Equal(2, f.Gains.Count);
        Assert.True(f.Gains[1].Sequence > f.Gains[0].Sequence);
        Assert.NotSame(f.Gains[0].Character, f.Gains[1].Character);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(3)]
    public void UnqualifiedCrystalReasonsDoNotCount(byte reason)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Crystals(reason, (2, 1)), 100);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void NativeCrystalCategoriesRemainDistinctAndPetHookUsesPetNumber()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Crystals(2, (1, 2), (2, 3), (3, 4), (4, 5), (5, 6)), 100);
        f.Pet(1004, 2, 200);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 1004 }, f.Gains.Select(g => g.DataId));
        Assert.Equal(new long[] { 2, 3, 4, 5, 6, 2 }, f.Gains.Select(g => g.Quantity));
        Assert.All(f.Gains.Take(5), gain => Assert.Equal(NetworkFarmDropKind.SoulCrystal, gain.Kind));
        Assert.Equal(NetworkFarmDropKind.PetSoul, f.Gains[5].Kind);
    }

    [Fact]
    public void InstanceClassificationIsFrozenBeforeSubscriberCanChangeWorldContext()
    {
        using var f = new Fixture(); f.Bind(map: 200);
        var generation = f.Source.OwnCharacter!.Generation;
        f.Network.FarmDropGained += _ => f.Network.WorldContext.ObserveOwn(100, generation, At.AddMilliseconds(300));
        f.Send(Changes(new Change(new(11, Helm, 1), 0), new(new(12, Material, 1), 0)), 200);
        Assert.Equal(2, f.Gains.Count); Assert.All(f.Gains, gain => Assert.True(gain.InInstance));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FutureOrForeignGenerationWorldCannotLabelAnOldDropAsInInstance(bool future)
    {
        using var f = new Fixture(); f.Bind();
        var generation = f.Source.OwnCharacter!.Generation;
        f.Network.WorldContext.ObserveOwn(200, generation + (future ? 0 : 1), At.AddMilliseconds(future ? 300 : 0));
        f.Send(Changes(new Change(new(11, Helm, 1), 0)), 200);
        Assert.False(Assert.Single(f.Gains).InInstance);
    }

    [Fact]
    public void ReentrantPublicationKeepsAllSubscribersInFifoOrder()
    {
        using var f = new Fixture(); f.Bind(); var calls = new List<string>();
        f.Network.FarmDropGained += gain =>
        {
            calls.Add("A:" + gain.DataId);
            if (gain.DataId == Helm) f.Send(Crystals(2, (2, 1)), 250);
        };
        f.Network.FarmDropGained += gain => calls.Add("B:" + gain.DataId);
        f.Send(Changes(new Change(new(11, Helm, 1), 0), new(new(12, Material, 2), 0)), 200);
        Assert.Equal(new[] { "A:" + Helm, "B:" + Helm, "A:" + Material, "B:" + Material, "A:2", "B:2" }, calls);
        Assert.Equal(3, f.Gains.Count);
        Assert.True(f.Gains[0].Sequence < f.Gains[1].Sequence && f.Gains[1].Sequence < f.Gains[2].Sequence);
    }

    [Fact]
    public void ReentrantOwnChangeDiscardsQueuedOldDropsWithoutBlockingNewOwnDrops()
    {
        using var f = new Fixture(); f.Bind(); var changed = false;
        f.Network.FarmDropGained += _ =>
        {
            if (changed) return; changed = true;
            f.Bind(new(8, 9, 301, "SyntheticOther"));
            f.Send(Crystals(2, (2, 1)), 300);
        };
        f.Send(Changes(new Change(new(11, Helm, 1), 0), new(new(12, Material, 2), 0)), 200);
        Assert.Equal(2, f.Gains.Count);
        Assert.Equal(NetworkFarmDropKind.Item, f.Gains[0].Kind);
        Assert.Equal(NetworkFarmDropKind.SoulCrystal, f.Gains[1].Kind);
        Assert.NotSame(f.Gains[0].Character, f.Gains[1].Character);
    }

    [Fact]
    public void ThrowingSubscriberDoesNotLoseAnotherSubscribersDrop()
    {
        using var f = new Fixture(); f.Bind(); var received = new List<int>();
        f.Network.FarmDropGained += _ => throw new InvalidOperationException("Synthetic farm error");
        f.Network.FarmDropGained += gain => received.Add(gain.DataId);
        f.Send(Crystals(2, (2, 1)), 100);
        Assert.Equal(new[] { 2 }, received);
    }

    [Fact]
    public void StackCapacityOverflowDiscardsTheEntireIncomingBatchAndPriorComparisonBase()
    {
        using var f = new Fixture(); f.Bind();
        var items = Enumerable.Range(1, 10000).Select(key => new CurrencyInventoryItem(key, Material, 5, 1)).ToArray();
        f.Invoke("BindFarmDropBaseline", new CurrencyInventoryList(528, []) { AllItems = Array.AsReadOnly(items) });
        f.Send(Changes(new Change(new(1, Material, 6)), new(new(10001, Helm, 1), 0)), 200);
        Assert.Empty(f.Gains);
        f.Send(Changes(new Change(new(1, Material, 7))), 300);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void SameOwnReentrantInventoryCannotRollBackWalletComparisonBase()
    {
        using var f = new Fixture(); f.Login(new Item(11, 930100031, 100)); var nested = false;
        var kina = new List<long>(); f.Network.KinaGained += gain => kina.Add(gain.UnboundKina);
        f.Network.FarmDropGained += _ =>
        {
            if (nested) return; nested = true;
            f.Send(Changes(new Change(new(11, 930100031, 120), Increase: 11)), 250);
        };
        f.Send(Changes(new Change(new(12, Helm, 1), 0), new(new(11, 930100031, 110), Increase: 11)), 200);
        Assert.Equal(120, ((INetworkKinaSource)f.Network).KinaSnapshot.UnboundBalance);
        f.Send(Changes(new Change(new(11, 930100031, 125), Increase: 11)), 300);
        Assert.Equal(5, kina[^1]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-farm-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal INetworkFarmSource Source => Network;
        internal List<NetworkFarmDrop> Gains { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            var progress = new ProgressService(Path.Combine(_directory, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "synthetic-pet", En = "Synthetic Pet", Genus = "Fera", NumericId = "1004" }]));
            Network = new(progress, worldContext: new(WorldContextTestCatalog.Create()));
            SetRunning(true); Network.FarmDropGained += Gains.Add;
        }
        internal void SetRunning(bool value) => typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, value);
        internal void Bind(OwnCharacterIdentity? identity = null, int? map = 100) => Network.ObserveIdentity(Game, identity ?? Identity, At.AddMilliseconds(80), map);
        internal void Send(byte[] message, long milliseconds = 0, string stream = Game) => Network.ProcessProgressMessage(stream, message, At.AddMilliseconds(milliseconds));
        internal void Login(params Item[] items) { Send(Entry()); Send(Snapshot(items), 20); Send(Preparation(), 40); Bind(); }
        internal void Pet(int number, int quantity, long milliseconds) => Invoke("ProcessFarmPetSoulGain", Game, number, quantity, At.AddMilliseconds(milliseconds));
        internal void Invoke(string method, params object[] args) => typeof(NetworkLootService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Network, args);
        public void Dispose() { Network.Dispose(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed record Item(long Key, int Id, long Count, byte Container = 1);
    private sealed record Change(Item Item, byte Modification = 1, byte Increase = 5, byte Decrease = 0, byte Update = 0);
    private static byte[] Entry() => Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Bytes(new byte[4]); w.I64(Identity.CharacterId); });
    private static byte[] Preparation() => Packet(0x3621, w => w.Bytes(new byte[46]));
    private static byte[] Snapshot(params Item[] items) => Packet(0x5611, w => { w.U16(528); w.V(items.Length); foreach (var item in items) w.Item(item); w.Bytes(new byte[6]); });
    private static byte[] Changes(params Change[] changes) => Packet(0x561B, w =>
    {
        w.V(changes.Length);
        foreach (var change in changes) { w.U8(change.Modification); w.Item(change.Item); w.U8(change.Increase); w.U8(change.Decrease); w.U8(change.Update); }
    });
    private static byte[] Crystals(byte reason, params (byte Type, int Amount)[] gains) => Packet(0x9011, w =>
    {
        w.V(gains.Length); foreach (var gain in gains) { w.U8(gain.Type); w.I32(gain.Amount); } w.U8(reason);
    });
    private static byte[] Packet(ushort opcode, Action<WireWriter> write) { using var w = new WireWriter(); w.U16(opcode); write(w); return w.ToArray(); }
    private sealed class WireWriter : IDisposable
    {
        private readonly MemoryStream _stream = new(); private readonly BinaryWriter _writer;
        private int _boolOffset = -1, _boolBit;
        internal WireWriter() => _writer = new(_stream);
        internal void U8(byte value) => _writer.Write(value); internal void U16(ushort value) => _writer.Write(value);
        internal void I32(int value) => _writer.Write(value); internal void I64(long value) => _writer.Write(value);
        internal void Bytes(byte[] value) => _writer.Write(value);
        internal void V(int value) { var rest = (uint)value; while (rest >= 128) { U8((byte)(rest | 128)); rest >>= 7; } U8((byte)rest); }
        private void Bool()
        {
            if (_boolOffset < 0) { _boolOffset = (int)_stream.Position; U8(0); _boolBit = 0; }
            if (++_boolBit == 8) _boolOffset = -1;
        }
        internal void Item(Item item) { U8(0); I64(item.Key); I32(item.Id); I64(item.Count); U8(item.Container); I32(0); Bool(); V(0); I64(0); Bool(); }
        internal byte[] ToArray() => _stream.ToArray(); public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
