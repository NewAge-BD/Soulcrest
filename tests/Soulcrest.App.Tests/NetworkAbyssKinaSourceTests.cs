using System.Reflection;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic own/inventory observations with an isolated public Abyss map fixture.</summary>
public sealed class NetworkAbyssKinaSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string OwnStream = "synthetic-abyss-kina", Foreign = "synthetic-abyss-kina-foreign";
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticAbyssKina");
    private sealed record Change(long Count, long Key = 91, int Item = 930100031, byte Container = 1,
        byte Modification = 1, byte Increase = 11, byte Decrease = 0, byte Update = 0, bool Equipment = false);

    [Fact]
    public void UnknownWalletUsesTheFirstSupportedModificationAsBaselineAndOnlyCountsTheFollowing99()
    {
        using var f = new Fixture(); f.Own(); var before = f.Network.AbyssSnapshot;
        Assert.Equal(WorldContextStatus.Confirmed, f.Network.WorldContext.Snapshot.Status);
        Assert.Equal("Abyss", f.Network.WorldContext.Snapshot.Category);
        f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        Assert.Equal(488_546, f.Network.KinaSnapshot.UnboundBalance);
        Assert.Equal(At.AddSeconds(1), f.Network.KinaSnapshot.BaselineAt);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Empty(f.Gains);
        Assert.Equal(At.AddSeconds(1), f.Network.AbyssSnapshot.KinaGainsObservedAt);
        Assert.True(f.Network.AbyssSnapshot.Sequence > before.Sequence);
        f.Rank(1_002, 2); var rank = f.Network.AbyssSnapshot;
        f.Send(Changes(new Change(488_645)), At.AddSeconds(3));
        var after = f.Network.AbyssSnapshot; var gain = Assert.Single(f.Gains);
        Assert.Equal(99, gain.UnboundKina); Assert.Same(f.Network.OwnCharacter, gain.Character);
        Assert.Equal(At.AddSeconds(3), gain.At); Assert.True(after.KinaGainsQualified);
        Assert.Equal(At.AddSeconds(3), after.KinaGainsObservedAt); Assert.Equal(At.AddSeconds(3), after.At);
        Assert.True(after.Sequence > rank.Sequence);
        Assert.Equal(rank.RankPointsObservedAt, after.RankPointsObservedAt); Assert.Equal(1_002, after.RankPoints);
        f.Send(Changes(new Change(488_645)), At.AddSeconds(4));
        Assert.Single(f.Gains); Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified); AssertNoOtherIncome(f);
    }

    [Fact]
    public void ExistingLoginWalletKeepsNormalFirstGainButAbyssRequiresTwoPostOwnModifications()
    {
        using var f = new Fixture(); f.Login(488_243);
        f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        Assert.Equal(303, Assert.Single(f.Gains).UnboundKina); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Send(Changes(new Change(488_645)), At.AddSeconds(2));
        Assert.Equal(new long[] { 303, 99 }, f.Gains.Select(gain => gain.UnboundKina));
        Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedLoginInventoryCannotQualifyAnUnknownKinaWalletUntilFreshOwn(bool duplicateList)
    {
        using var f = new Fixture();
        f.Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), At.AddSeconds(-4));
        var list = Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(new Change(488_243)); w.Z(6); });
        if (duplicateList)
        {
            f.Send(list, At.AddSeconds(-3)); f.Send(list, At.AddSeconds(-2));
        }
        else f.Send(list[..^1], At.AddSeconds(-2));
        f.Send(Packet(0x3621, w => w.Z(46)), At.AddSeconds(-1)); f.Own();
        Assert.True(f.Network.KinaSnapshot.FormatRejected); Assert.False(f.Network.KinaSnapshot.IsReady);
        f.Pair(); f.Rank(1_002, 3);
        Assert.False(f.Network.KinaSnapshot.IsReady); Assert.True(f.Network.KinaSnapshot.FormatRejected);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt);
        Assert.Empty(f.Gains);
        f.Own(seconds: 4); Assert.False(f.Network.KinaSnapshot.FormatRejected);
        f.Send(Changes(new Change(488_546)), At.AddSeconds(5));
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Empty(f.Gains);
        f.Send(Changes(new Change(488_645)), At.AddSeconds(6));
        Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Equal(99, Assert.Single(f.Gains).UnboundKina); AssertNoOtherIncome(f);
    }

    [Fact]
    public void BoundKinaNeverPrimesOrQualifiesTheUnboundAbyssChannel()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(100, Item: CurrencyInventoryMessage.BoundKinaItemId)), At.AddSeconds(1));
        f.Send(Changes(new Change(199, Item: CurrencyInventoryMessage.BoundKinaItemId)), At.AddSeconds(2));
        Assert.Null(f.Network.KinaSnapshot.UnboundBalance); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt); Assert.Empty(f.Gains); AssertNoOtherIncome(f);
    }

    [Fact]
    public void RevokedAbyssPairDoesNotDisableNormalGenericKinaAccounting()
    {
        using var f = new Fixture(); f.Own(); f.Pair();
        f.Send(Changes(new Change(488_600, Increase: 0, Decrease: 66)), At.AddSeconds(3));
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Equal(488_600, f.Network.KinaSnapshot.UnboundBalance);
        f.Send(Changes(new Change(488_605)), At.AddSeconds(4));
        Assert.Equal(new long[] { 99, 5 }, f.Gains.Select(gain => gain.UnboundKina));
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt);
        AssertNoOtherIncome(f);
    }

    [Fact]
    public void OwnGapAndFreshOwnDiscardThePairWithoutCatchingUp()
    {
        using var f = new Fixture(); f.Own(); f.Pair();
        f.Network.ProcessProgressGap(Foreign); Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(3));
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Null(f.Network.KinaSnapshot.UnboundBalance);
        f.Send(Changes(new Change(999_000)), At.AddSeconds(4)); Assert.Single(f.Gains);
        f.Own(seconds: 5); f.Send(Changes(new Change(600_000)), At.AddSeconds(6));
        Assert.Single(f.Gains); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Send(Changes(new Change(600_001)), At.AddSeconds(7));
        Assert.Equal(new long[] { 99, 1 }, f.Gains.Select(gain => gain.UnboundKina)); AssertNoOtherIncome(f);
    }

    [Fact]
    public void PreOwnAndForeignMessagesCannotPrimeOrInvalidateTheOwnPair()
    {
        using var f = new Fixture(); f.Send(Changes(new Change(488_243)), At.AddSeconds(-1)); f.Own();
        Assert.Null(f.Network.KinaSnapshot.UnboundBalance);
        f.Pair(); var before = f.Network.AbyssSnapshot; var balance = f.Network.KinaSnapshot.UnboundBalance;
        f.Send(Changes(new Change(900_000)), At.AddSeconds(3), Foreign);
        f.Send([0x1B, 0x56], At.AddSeconds(4), Foreign);
        Assert.Equal(before, f.Network.AbyssSnapshot); Assert.Equal(balance, f.Network.KinaSnapshot.UnboundBalance);
        Assert.Equal(99, Assert.Single(f.Gains).UnboundKina);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("reason5")]
    [InlineData("reason12")]
    [InlineData("reason63")]
    [InlineData("spending")]
    [InlineData("update")]
    [InlineData("container")]
    [InlineData("zero-key")]
    [InlineData("changed-key")]
    [InlineData("equipment")]
    [InlineData("lower")]
    [InlineData("negative")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("two-wallets")]
    [InlineData("collision")]
    [InlineData("replaced-id")]
    public void UnsupportedOwnVariantRevokesOnlyAbyssKinaQualificationUntilNewOwn(string variant)
    {
        using var f = new Fixture(); f.Own(); f.Pair();
        f.Send(Changes(new Change(52_576, Key: 92, Item: 930100003)), At.AddSeconds(3));
        f.Farm(717_422, 4); f.Rank(1_002, 5);
        var before = f.Network.AbyssSnapshot; var own = f.Network.OwnCharacter; var world = f.Network.WorldContext.Snapshot;
        byte[] invalid = variant switch
        {
            "add" => Changes(new Change(488_646, Modification: 0)), "remove" => Changes(new Change(0, Modification: 2)),
            "reason5" => Changes(new Change(488_646, Increase: 5)), "reason12" => Changes(new Change(488_646, Increase: 12)),
            "reason63" => Changes(new Change(488_646, Increase: 63)), "spending" => Changes(new Change(488_644, Increase: 0, Decrease: 66)),
            "update" => Changes(new Change(488_646, Update: 1)), "container" => Changes(new Change(488_646, Container: 2)),
            "zero-key" => Changes(new Change(488_646, Key: 0)), "changed-key" => Changes(new Change(488_646, Key: 93)),
            "equipment" => Changes(new Change(488_646, Equipment: true)), "lower" => Changes(new Change(488_644)),
            "negative" => Changes(new Change(-1)), "truncated" => Changes(new Change(488_646))[..^1],
            "duplicate" => Changes(new Change(488_646), new Change(488_646)),
            "two-wallets" => Changes(new Change(488_646), new Change(488_646, Key: 93)),
            "collision" => Changes(new Change(488_646), new Change(1, Item: 610660025)),
            "replaced-id" => Changes(new Change(1, Item: 610660025)), _ => throw new InvalidOperationException(),
        };
        f.Send(invalid, At.AddSeconds(6)); var after = f.Network.AbyssSnapshot;
        Assert.False(after.KinaGainsQualified); Assert.Null(after.KinaGainsObservedAt);
        Assert.Same(own, f.Network.OwnCharacter); Assert.Equal(world, f.Network.WorldContext.Snapshot);
        Assert.Equal(before.RankPoints, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.True(after.RankPointChangesQualified);
        // Parser-rejected inventories also invalidate AP; parseable unsupported Kina reasons
        // must leave its independent fact intact. FFA9 did not pass through this inventory.
        if (CurrencyInventoryMessage.TryParseChanges(invalid) is not null)
            Assert.Equal(before.AbyssPointBalance, after.AbyssPointBalance);
        Assert.Equal(before.FarmedAbyssPoints, after.FarmedAbyssPoints);
        f.Send(Changes(new Change(488_700)), At.AddSeconds(7)); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Own(seconds: 8); f.Send(Changes(new Change(500_000)), At.AddSeconds(9));
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Send(Changes(new Change(500_001)), At.AddSeconds(10)); Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
    }

    [Theory]
    [InlineData("before-own")]
    [InlineData("backward")]
    [InlineData("default")]
    [InlineData("future")]
    public void InvalidFactTimeRevokesOnlyTheAbyssKinaQualification(string timing)
    {
        using var f = new Fixture(); f.Own(); f.Pair(); var own = f.Network.OwnCharacter;
        var invalidAt = timing switch
        {
            "before-own" => At.AddTicks(-1), "backward" => At.AddSeconds(1), "default" => default,
            "future" => DateTimeOffset.MaxValue, _ => throw new InvalidOperationException(),
        };
        f.Send(Changes(new Change(488_646)), invalidAt);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt);
        Assert.Same(own, f.Network.OwnCharacter); Assert.Equal(1_000, f.Network.AbyssSnapshot.RankPoints);
        f.Send(Changes(new Change(488_647)), At.AddSeconds(3)); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
    }

    [Theory]
    [InlineData("open-world")]
    [InlineData("unknown-map")]
    [InlineData("transitioning")]
    [InlineData("other-generation")]
    [InlineData("world-after-record")]
    public void WithoutCurrentConfirmedAbyssTheFirstModificationCannotPrimeAGenericWallet(string context)
    {
        using var f = new Fixture(); f.Own(map: context == "open-world" ? 21 : context == "unknown-map" ? 999 : 20);
        var generation = f.Network.OwnCharacter!.Generation;
        if (context == "transitioning") f.Network.WorldContext.Invalidate(WorldContextStatus.Transitioning, generation, "synthetic-transition", At);
        if (context == "other-generation") f.Network.WorldContext.ObserveOwn(20, generation + 1, At);
        if (context == "world-after-record") f.Network.WorldContext.ObserveOwn(20, generation, At.AddSeconds(5));
        f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        f.Send(Changes(new Change(488_645)), At.AddSeconds(2));
        Assert.Null(f.Network.KinaSnapshot.UnboundBalance); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt); Assert.Empty(f.Gains); AssertNoOtherIncome(f);
    }

    [Fact]
    public void KinaCallbacksSerializeNestedApRankAndKinaBeforePublishingNewerFacts()
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        var calls = new List<string>(); var snapshots = new List<(long Amount, long? Balance, long? Ap, long? Rank, DateTimeOffset? KinaAt)>();
        f.Network.KinaGained += gain =>
        {
            calls.Add("A:" + gain.UnboundKina);
            if (gain.UnboundKina != 99) return;
            f.Send(Changes(new Change(52_576, Key: 92, Item: 930100003)), At.AddSeconds(3));
            f.Rank(1_002, 4); var next = Changes(new Change(488_646)); f.Send(next, At.AddSeconds(5)); Array.Clear(next);
        };
        f.Network.KinaGained += gain =>
        {
            calls.Add("B:" + gain.UnboundKina); var snapshot = f.Network.AbyssSnapshot;
            snapshots.Add((gain.UnboundKina, f.Network.KinaSnapshot.UnboundBalance, snapshot.AbyssPointBalance,
                snapshot.RankPoints, snapshot.KinaGainsObservedAt));
        };
        f.Send(Changes(new Change(488_645)), At.AddSeconds(2));
        Assert.Equal(new[] { "A:99", "B:99", "A:1", "B:1" }, calls);
        Assert.Equal(new[] { (99L, (long?)488_645, (long?)null, (long?)1_000, (DateTimeOffset?)At.AddSeconds(2)),
            (1L, (long?)488_646, (long?)52_576, (long?)1_002, (DateTimeOffset?)At.AddSeconds(5)) }, snapshots);
        Assert.Equal(new long[] { 99, 1 }, f.Gains.Select(gain => gain.UnboundKina)); AssertNoOtherIncome(f);
    }

    [Fact]
    public void ApNotificationOwnBoundaryCannotPrimeKinaFromTheOlderMixedInventory()
    {
        using var f = new Fixture(); f.Own(); var switched = false;
        f.Network.Changed += () =>
        {
            if (switched || f.Network.AbyssSnapshot.AbyssPointBalance != 52_576) return;
            switched = true; f.Own(5_000, 2);
        };
        f.Send(Changes(new Change(52_576, Key: 92, Item: 930100003), new Change(488_546)), At.AddSeconds(1));
        Assert.True(switched); Assert.Null(f.Network.KinaSnapshot.UnboundBalance);
        Assert.Null(f.Network.AbyssSnapshot.KinaGainsObservedAt); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Equal(5_000, f.Network.AbyssSnapshot.RankPoints); Assert.Empty(f.Gains); AssertNoOtherIncome(f);
    }

    [Fact]
    public void RankCallbacksDeferKinaSoAllRankSubscribersSeeTheEarlierKinaFact()
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        var observations = new List<(DateTimeOffset? At, bool Qualified)>();
        f.Network.RankPointsChanged += _ => f.Send(Changes(new Change(488_645)), At.AddSeconds(3));
        f.Network.RankPointsChanged += _ => observations.Add((f.Network.AbyssSnapshot.KinaGainsObservedAt,
            f.Network.AbyssSnapshot.KinaGainsQualified));
        f.Rank(1_002, 2);
        Assert.Equal(new[] { ((DateTimeOffset?)At.AddSeconds(1), false) }, observations);
        Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Equal(At.AddSeconds(3), f.Network.AbyssSnapshot.KinaGainsObservedAt);
        Assert.Equal(99, Assert.Single(f.Gains).UnboundKina); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("own")]
    [InlineData("gap")]
    [InlineData("capture")]
    public void CallbackBoundariesStopRemainingOldKinaSubscribersAndDiscardQueuedInventory(string boundary)
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(488_546)), At.AddSeconds(1));
        var lastSubscriber = new List<long>();
        f.Network.KinaGained += _ =>
        {
            f.Send(Changes(new Change(488_646)), At.AddSeconds(3));
            if (boundary == "own") f.Own(5_000, 4);
            else if (boundary == "gap") f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(4));
            else f.ChangeCaptureGeneration();
        };
        f.Network.KinaGained += gain => lastSubscriber.Add(gain.UnboundKina);
        f.Send(Changes(new Change(488_645)), At.AddSeconds(2));
        Assert.Empty(lastSubscriber); Assert.Equal(99, Assert.Single(f.Gains).UnboundKina);
    }

    private static void AssertNoOtherIncome(Fixture f)
    { Assert.Empty(f.ApGains); Assert.Empty(f.Drops); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-kina-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkKinaGain> Gains { get; } = [];
        internal List<NetworkAbyssPointGain> ApGains { get; } = [];
        internal List<NetworkFarmDrop> Drops { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var world = new WorldContextService(InstanceCatalog.FromJson("""
                {"schemaVersion":1,"maps":[
                  {"id":20,"kind":"instance","category":"Abyss","sourceRefs":["synthetic"]},
                  {"id":21,"kind":"openWorld","category":"OpenWorld","sourceRefs":["synthetic"]}],"dungeons":[]}
                """));
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])), worldContext: world);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.KinaGained += Gains.Add; Network.AbyssPointsGained += ApGains.Add; Network.FarmDropGained += Drops.Add;
        }
        internal void Send(byte[] message, DateTimeOffset? at = null, string stream = OwnStream) => Network.ProcessProgressMessage(stream, message, at ?? At);
        internal void Own(long? rank = 1_000, int seconds = 0, int map = 20) => Send(OwnPacket(rank, map), At.AddSeconds(seconds));
        internal void Pair() { Send(Changes(new Change(488_546)), At.AddSeconds(1)); Send(Changes(new Change(488_645)), At.AddSeconds(2)); }
        internal void Rank(long total, int seconds) => Send(Packet(0xE273, w => { w.U8(0); w.I64(total); }), At.AddSeconds(seconds));
        internal void Farm(long total, int seconds) => Send(Packet(0xFFA9, w => { w.I64(total); w.I64(0); }), At.AddSeconds(seconds));
        internal void Login(long unbound)
        {
            Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), At.AddSeconds(-3));
            Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(new Change(unbound)); w.Z(6); }), At.AddSeconds(-2));
            Send(Packet(0x3621, w => w.Z(46)), At.AddSeconds(-1)); Own();
        }
        internal void ChangeCaptureGeneration()
        {
            var field = typeof(NetworkLootService).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(Network, (long)field.GetValue(Network)! + 1);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] OwnPacket(long? rank, int map) => Packet(0x3633, w =>
    {
        w.V(Identity.EntityKey); w.I32(rank.HasValue ? 65536 : 0); w.U8(1);
        var name = Encoding.UTF8.GetBytes(Identity.Name); w.V(name.Length); w.Bytes(name);
        w.U16((ushort)Identity.ServerId); w.I32(29); w.Z(36); w.V(0); w.V(0); w.Z(16); w.U8(0);
        w.U16(0); w.Z(18); w.U8(1); w.V(0); w.V(0); w.Z(52); w.V(0); w.V(0); w.V(0);
        w.I64(0); w.I32(map); w.I64(Identity.CharacterId); w.V(0); w.U8(1);
        if (rank.HasValue) w.I64(rank.Value); w.Z(32); w.V(0); w.V(0);
    });
    private static byte[] Changes(params Change[] changes) => Packet(0x561B, w =>
    {
        w.V(changes.Length);
        foreach (var change in changes) { w.U8(change.Modification); w.Item(change); w.U8(change.Increase); w.U8(change.Decrease); w.U8(change.Update); }
    });
    private static byte[] Packet(ushort opcode, Action<WireWriter> write)
    { using var writer = new WireWriter(); writer.U16(opcode); write(writer); return writer.ToArray(); }
    private sealed class WireWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private int _boolOffset = -1, _boolBit;
        internal WireWriter() => _writer = new(_stream);
        internal void U8(byte value) => _writer.Write(value);
        internal void U16(ushort value) => _writer.Write(value);
        internal void I32(int value) => _writer.Write(value);
        internal void I64(long value) => _writer.Write(value);
        internal void Bytes(byte[] value) => _writer.Write(value);
        internal void Z(int count) => _writer.Write(new byte[count]);
        internal void V(int value) { var rest = unchecked((uint)value); while (rest >= 128) { U8((byte)(rest | 128)); rest >>= 7; } U8((byte)rest); }
        private void False() { if (_boolOffset < 0) { _boolOffset = (int)_stream.Position; U8(0); _boolBit = 0; } if (++_boolBit == 8) _boolOffset = -1; }
        internal void Item(Change item)
        {
            U8(item.Equipment ? (byte)1 : (byte)0); I64(item.Key); I32(item.Item); I64(item.Count); U8(item.Container); I32(0);
            False(); V(0); I64(0); False();
            if (item.Equipment) { Z(18); False(); Z(1); V(0); Z(9); V(0); for (var i = 0; i < 7; i++) V(0); Z(1); }
        }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
