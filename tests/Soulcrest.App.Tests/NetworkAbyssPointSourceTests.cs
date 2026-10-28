using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Fully synthetic own/inventory records, isolated data, no capture or app startup.</summary>
public sealed class NetworkAbyssPointSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string OwnStream = "synthetic-ap-own", Foreign = "synthetic-ap-foreign";
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticAbyssPoints");
    private sealed record Change(long Count, long Key = 91, int Item = 930100003, byte Container = 1,
        byte Modification = 1, byte Increase = 11, byte Decrease = 0, byte Update = 0, bool Equipment = false);

    [Theory]
    [InlineData(null)]
    [InlineData(994_030L)]
    public void FirstOwnAbsoluteModificationConfirmsOnlyTheWalletWithoutALoginList(long? rank)
    {
        using var f = new Fixture(); f.Own(rank);
        var before = f.Network.AbyssSnapshot;
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        var after = f.Network.AbyssSnapshot;
        Assert.Equal(47_702, after.AbyssPointBalance); Assert.Equal(At.AddSeconds(1), after.AbyssPointBalanceObservedAt);
        Assert.Same(f.Network.OwnCharacter, after.Character); Assert.True(after.Sequence > before.Sequence);
        Assert.Equal(rank, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        AssertNoApIncome(f);
    }

    [Fact]
    public void OnlyThePositiveDifferenceBetweenSupportedModificationsProducesIncome()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        f.Send(Changes(new Change(47_702)), At.AddSeconds(2));
        f.Send(Changes(new Change(47_714)), At.AddSeconds(3));
        Assert.Equal(47_714, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(At.AddSeconds(3), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.Equal(12, Assert.Single(f.Gains).Amount);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        AssertNoFarmInference(f);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void AbsoluteInt64ExtremesNeverBecomeAcquisitionQuantities(long total)
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(total)), At.AddSeconds(1));
        Assert.Equal(total, f.Network.AbyssSnapshot.AbyssPointBalance); AssertNoApIncome(f);
    }

    [Fact]
    public void OwnGapRevokesTheWalletAndNeverReplaysEarlierUpdatesIntoANewOwn()
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        f.Network.ProcessProgressGap(Foreign); Assert.Equal(47_702, f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(2));
        f.Send(Changes(new Change(47_714)), At.AddSeconds(3)); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(seconds: 4); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(47_720)), At.AddSeconds(5)); Assert.Equal(47_720, f.Network.AbyssSnapshot.AbyssPointBalance);
        AssertNoApIncome(f);
    }

    [Fact]
    public void KnownLoginWalletAcceptsTheSameAbsoluteVariantWithoutCountingItsDifference()
    {
        using var f = new Fixture();
        f.Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), At.AddSeconds(-3));
        f.Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(new Change(47_690)); w.Z(6); }), At.AddSeconds(-2));
        f.Send(Packet(0x3621, w => w.Z(46)), At.AddSeconds(-1)); f.Own();
        Assert.Equal(47_690, f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        Assert.Equal(47_702, f.Network.AbyssSnapshot.AbyssPointBalance); AssertNoApIncome(f);
        f.Send(Changes(new Change(47_707)), At.AddSeconds(2));
        Assert.Equal(5, Assert.Single(f.Gains).Amount); AssertNoFarmInference(f);
    }

    [Fact]
    public void PreOwnAndForeignRecordsCannotAuthorizeOrInvalidateTheOwnWallet()
    {
        using var f = new Fixture(); f.Send(Changes(new Change(47_690)), At.AddSeconds(-1)); f.Own();
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1)); var before = f.Network.AbyssSnapshot;
        f.Send(Changes(new Change(99_999)), At.AddSeconds(2), Foreign);
        f.Send([0x1B, 0x56], At.AddSeconds(3), Foreign);
        Assert.Equal(before, f.Network.AbyssSnapshot); AssertNoApIncome(f);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("reward12")]
    [InlineData("reward63")]
    [InlineData("itemdrop5")]
    [InlineData("decrease")]
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
    public void UnsupportedOwnVariantRevokesOnlyApUntilANewFullOwn(string variant)
    {
        using var f = new Fixture(); f.Own(); f.Rank(1_002, 1);
        f.Send(Changes(new Change(47_702)), At.AddSeconds(2));
        var before = f.Network.AbyssSnapshot; var own = f.Network.OwnCharacter; var world = f.Network.WorldContext.Snapshot;
        byte[] invalid = variant switch
        {
            "add" => Changes(new Change(47_714, Modification: 0)), "remove" => Changes(new Change(0, Modification: 2)),
            "reward12" => Changes(new Change(47_714, Increase: 12)), "reward63" => Changes(new Change(47_714, Increase: 63)),
            "itemdrop5" => Changes(new Change(47_714, Increase: 5)), "decrease" => Changes(new Change(47_714, Decrease: 1)),
            "update" => Changes(new Change(47_714, Update: 1)), "container" => Changes(new Change(47_714, Container: 2)),
            "zero-key" => Changes(new Change(47_714, Key: 0)), "changed-key" => Changes(new Change(47_714, Key: 92)),
            "equipment" => Changes(new Change(47_714, Equipment: true)), "lower" => Changes(new Change(47_701)),
            "negative" => Changes(new Change(-1)), "truncated" => Changes(new Change(47_714))[..^1],
            "duplicate" => Changes(new Change(47_714), new Change(47_714)), "two-wallets" => Changes(new Change(47_714), new Change(47_714, Key: 92)),
            "collision" => Changes(new Change(47_714), new Change(1, Item: 610660025)), "replaced-id" => Changes(new Change(1, Item: 610660025)),
            _ => throw new InvalidOperationException(),
        };
        f.Send(invalid, At.AddSeconds(3)); var after = f.Network.AbyssSnapshot;
        Assert.Null(after.AbyssPointBalance); Assert.Null(after.AbyssPointBalanceObservedAt);
        Assert.Same(own, f.Network.OwnCharacter); Assert.Equal(world, f.Network.WorldContext.Snapshot);
        Assert.Equal(before.RankPoints, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.True(after.RankPointChangesQualified); Assert.False(after.FormatRejected);
        f.Send(Changes(new Change(47_720)), At.AddSeconds(4)); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(seconds: 5); f.Send(Changes(new Change(47_720)), At.AddSeconds(6));
        Assert.Equal(47_720, f.Network.AbyssSnapshot.AbyssPointBalance); AssertNoApIncome(f);
    }

    [Theory]
    [InlineData("before-own")]
    [InlineData("backward")]
    [InlineData("default")]
    [InlineData("future")]
    public void InvalidApFactTimeRevokesApWithoutTouchingRank(string timing)
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(47_702)), At.AddSeconds(2));
        var invalidAt = timing switch
        {
            "before-own" => At.AddTicks(-1), "backward" => At.AddSeconds(1),
            "default" => default, "future" => DateTimeOffset.MaxValue, _ => throw new InvalidOperationException(),
        };
        f.Send(Changes(new Change(47_714)), invalidAt);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Equal(1_000, f.Network.AbyssSnapshot.RankPoints);
        AssertNoApIncome(f);
    }

    [Fact]
    public void ApChangedCallbacksDeferRankUntilEverySubscriberSeesTheMatchingWalletFact()
    {
        using var f = new Fixture(); f.Own(); var nested = false; var unchanged = true;
        f.Network.Changed += () =>
        {
            var snapshot = f.Network.AbyssSnapshot;
            if (!nested && snapshot.AbyssPointBalance == 47_702)
            {
                nested = true; f.Rank(1_002, 2); unchanged &= snapshot == f.Network.AbyssSnapshot;
            }
        };
        var rankSnapshots = new List<(long Delta, long? Wallet, DateTimeOffset? RankAt, DateTimeOffset? EnvelopeAt)>();
        f.Network.RankPointsChanged += change => rankSnapshots.Add((change.Delta, f.Network.AbyssSnapshot.AbyssPointBalance,
            f.Network.AbyssSnapshot.RankPointsObservedAt, f.Network.AbyssSnapshot.At));
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        Assert.True(nested && unchanged);
        Assert.Equal(new[] { (2L, (long?)47_702, (DateTimeOffset?)At.AddSeconds(2), (DateTimeOffset?)At.AddSeconds(2)) }, rankSnapshots);
        AssertNoApIncome(f);
    }

    [Fact]
    public void RankCallbacksDeferApSoLaterRankSubscribersNeverSeeAFutureSnapshot()
    {
        using var f = new Fixture(); f.Own(); var observations = new List<NetworkAbyssSnapshot>();
        f.Network.RankPointsChanged += _ => { f.Send(Changes(new Change(47_702)), At.AddSeconds(2)); observations.Add(f.Network.AbyssSnapshot); };
        f.Network.RankPointsChanged += _ => observations.Add(f.Network.AbyssSnapshot);
        f.Rank(1_002, 1);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, snapshot => { Assert.Null(snapshot.AbyssPointBalance); Assert.Equal(At.AddSeconds(1), snapshot.At); });
        Assert.Equal(47_702, f.Network.AbyssSnapshot.AbyssPointBalance); AssertNoApIncome(f);
    }

    [Fact]
    public void ReentrantApMessagesAreFrozenAndObservedInOrder()
    {
        using var f = new Fixture(); f.Own(); var nested = false; var calls = new List<string>();
        f.Network.Changed += () =>
        {
            calls.Add("A:" + f.Network.AbyssSnapshot.AbyssPointBalance);
            if (nested) return;
            nested = true; var next = Changes(new Change(47_714)); f.Send(next, At.AddSeconds(2)); Array.Clear(next);
        };
        f.Network.Changed += () => calls.Add("B:" + f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(47_702)), At.AddSeconds(1));
        Assert.Equal(new[] { "A:47702", "B:47702", "A:47714", "B:47714" }, calls);
        Assert.Equal(12, Assert.Single(f.Gains).Amount); AssertNoFarmInference(f);
    }

    [Fact]
    public void ReentrantOwnChangeDuringApNotificationCannotApplyOldFarmRecordsToTheNewOwn()
    {
        using var f = new Fixture(); f.Own(); var switched = false; var drops = new List<NetworkFarmDrop>();
        f.Network.FarmDropGained += drops.Add;
        f.Network.Changed += () =>
        {
            if (switched || f.Network.AbyssSnapshot.AbyssPointBalance != 47_702) return;
            switched = true; f.Own(rank: 5_000, seconds: 2);
        };
        f.Send(Changes(new Change(47_702), new Change(1, Key: 92, Item: 610660025, Modification: 0, Increase: 5)), At.AddSeconds(1));
        Assert.True(switched); Assert.Empty(drops); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(5_000, f.Network.AbyssSnapshot.RankPoints); AssertNoApIncome(f);
    }

    [Fact]
    public void AQueuedApUpdateCannotCrossAReentrantOwnGeneration()
    {
        using var f = new Fixture(); f.Own();
        f.Network.RankPointsChanged += _ => { f.Send(Changes(new Change(47_702)), At.AddSeconds(2)); f.Own(5_000, 3); };
        f.Rank(1_002, 1);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Equal(5_000, f.Network.AbyssSnapshot.RankPoints);
        AssertNoApIncome(f);
    }

    [Fact]
    public void QualifiedPairSharesTheAbsoluteSequenceAndPreservesTheIndependentRankFactTime()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(52_576)), At.AddSeconds(1)); AssertNoApIncome(f);
        var baselineSequence = f.Network.AbyssSnapshot.Sequence;
        f.Rank(1_003, 2); var rank = f.Network.AbyssSnapshot;
        f.Send(Changes(new Change(52_581)), At.AddSeconds(3));
        var gain = Assert.Single(f.Gains); var snapshot = f.Network.AbyssSnapshot;
        Assert.Equal(5, gain.Amount); Assert.Equal(At.AddSeconds(3), gain.At);
        Assert.Same(f.Network.OwnCharacter, gain.Character); Assert.Equal(snapshot.Sequence, gain.Sequence);
        Assert.True(gain.Sequence > rank.Sequence && rank.Sequence > baselineSequence);
        Assert.True(snapshot.AbyssPointGainsQualified); Assert.Equal(52_581, snapshot.AbyssPointBalance);
        Assert.Equal(gain.At, snapshot.AbyssPointBalanceObservedAt);
        Assert.Equal(rank.RankPoints, snapshot.RankPoints); Assert.Equal(rank.RankPointsObservedAt, snapshot.RankPointsObservedAt);
        f.Send(Changes(new Change(52_581)), At.AddSeconds(4));
        Assert.Single(f.Gains); Assert.True(f.Network.AbyssSnapshot.Sequence > gain.Sequence); AssertNoFarmInference(f);
    }

    [Fact]
    public void EqualSupportedPairConfirmsZeroObservedIncomeWithoutAnEvent()
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(100)), At.AddSeconds(1));
        f.Send(Changes(new Change(100)), At.AddSeconds(2));
        Assert.Empty(f.Gains); Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(100, f.Network.AbyssSnapshot.AbyssPointBalance); AssertNoFarmInference(f);
    }

    [Fact]
    public void PositiveInt64DifferenceIsExactWithoutUsingTheTotalAsTheGain()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(long.MaxValue - 5)), At.AddSeconds(1));
        f.Send(Changes(new Change(long.MaxValue)), At.AddSeconds(2));
        Assert.Equal(5, Assert.Single(f.Gains).Amount); AssertNoFarmInference(f);
    }

    [Theory]
    [InlineData("reason12")]
    [InlineData("reason63")]
    [InlineData("spending")]
    [InlineData("lower")]
    [InlineData("malformed")]
    [InlineData("gap")]
    public void RevokedQualifiedPairCannotCatchUpFromAnUnsupportedInterval(string boundary)
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(100)), At.AddSeconds(1)); f.Send(Changes(new Change(105)), At.AddSeconds(2));
        if (boundary == "gap") f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(3));
        else
        {
            byte[] invalid = boundary switch
            {
                "reason12" => Changes(new Change(110, Increase: 12)), "reason63" => Changes(new Change(110, Increase: 63)),
                "spending" => Changes(new Change(99, Increase: 0, Decrease: 15)), "lower" => Changes(new Change(99)),
                "malformed" => [0x1B, 0x56], _ => throw new InvalidOperationException(),
            };
            f.Send(invalid, At.AddSeconds(3));
        }
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        f.Send(Changes(new Change(150)), At.AddSeconds(4)); Assert.Equal(5, Assert.Single(f.Gains).Amount);
        f.Own(seconds: 5); f.Send(Changes(new Change(200)), At.AddSeconds(6));
        Assert.Single(f.Gains); Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        f.Send(Changes(new Change(201)), At.AddSeconds(7));
        Assert.Equal(new long[] { 5, 1 }, f.Gains.Select(gain => gain.Amount)); AssertNoFarmInference(f);
    }

    [Fact]
    public void ApGainCallbacksKeepEverySubscriberAndSnapshotInOrderAcrossNestedApAndRank()
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(100)), At.AddSeconds(1));
        var calls = new List<string>();
        var observations = new List<(long Amount, long? Wallet, long Sequence, DateTimeOffset? At, bool Qualified)>();
        f.Network.AbyssPointsGained += gain =>
        {
            calls.Add("A:" + gain.Amount);
            if (gain.Amount == 5)
            {
                f.Send(Changes(new Change(107)), At.AddSeconds(3));
                f.Rank(1_002, 4);
            }
        };
        f.Network.AbyssPointsGained += gain =>
        {
            calls.Add("B:" + gain.Amount); var snapshot = f.Network.AbyssSnapshot;
            observations.Add((gain.Amount, snapshot.AbyssPointBalance, snapshot.Sequence,
                snapshot.AbyssPointBalanceObservedAt, snapshot.AbyssPointGainsQualified));
        };
        f.Send(Changes(new Change(105)), At.AddSeconds(2));
        Assert.Equal(new[] { "A:5", "B:5", "A:2", "B:2" }, calls);
        Assert.Equal(new long[] { 5, 2 }, f.Gains.Select(gain => gain.Amount));
        Assert.Equal(new[] { (5L, (long?)105, f.Gains[0].Sequence, (DateTimeOffset?)At.AddSeconds(2), true),
            (2L, (long?)107, f.Gains[1].Sequence, (DateTimeOffset?)At.AddSeconds(3), true) }, observations);
        Assert.Equal(1_002, f.Network.AbyssSnapshot.RankPoints); AssertNoFarmInference(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantOwnBoundaryStopsRemainingOldGainSubscribersAndDiscardsQueuedAp(bool freshOwn)
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(new Change(100)), At.AddSeconds(1));
        var reset = false; var lastSubscriber = new List<long>();
        f.Network.AbyssPointsGained += _ =>
        {
            if (reset) return;
            reset = true; f.Send(Changes(new Change(107)), At.AddSeconds(3));
            if (freshOwn) f.Own(5_000, 4);
            else f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(4));
        };
        f.Network.AbyssPointsGained += gain => lastSubscriber.Add(gain.Amount);
        f.Send(Changes(new Change(105)), At.AddSeconds(2));
        Assert.Empty(lastSubscriber); Assert.Equal(5, Assert.Single(f.Gains).Amount);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(5_000, 5); f.Send(Changes(new Change(200)), At.AddSeconds(6));
        Assert.Single(f.Gains); f.Send(Changes(new Change(201)), At.AddSeconds(7));
        Assert.Equal(new long[] { 1 }, lastSubscriber);
        Assert.Equal(new long[] { 5, 1 }, f.Gains.Select(gain => gain.Amount)); AssertNoFarmInference(f);
    }

    private static void AssertNoFarmInference(Fixture f)
    {
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints); Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
    }

    private static void AssertNoApIncome(Fixture f)
    {
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified); Assert.Equal(0, f.ApGains);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-ap-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkAbyssPointGain> Gains { get; } = [];
        internal int ApGains { get; private set; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.AbyssPointsGained += gain => { ApGains++; Gains.Add(gain); };
        }
        internal void Send(byte[] message, DateTimeOffset? at = null, string stream = OwnStream) => Network.ProcessProgressMessage(stream, message, at ?? At);
        internal void Own(long? rank = 1_000, int seconds = 0) => Send(OwnPacket(rank), At.AddSeconds(seconds));
        internal void Rank(long total, int seconds) => Send(Packet(0xE273, w => { w.U8(0); w.I64(total); }), At.AddSeconds(seconds));
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] OwnPacket(long? rank) => Packet(0x3633, w =>
    {
        w.V(Identity.EntityKey); w.I32(rank.HasValue ? 65536 : 0); w.U8(1);
        var name = Encoding.UTF8.GetBytes(Identity.Name); w.V(name.Length); w.Bytes(name);
        w.U16((ushort)Identity.ServerId); w.I32(29); w.Z(36); w.V(0); w.V(0); w.Z(16); w.U8(0);
        w.U16(0); w.Z(18); w.U8(1); w.V(0); w.V(0); w.Z(52); w.V(0); w.V(0); w.V(0);
        w.I64(0); w.I32(20); w.I64(Identity.CharacterId); w.V(0); w.U8(1);
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
