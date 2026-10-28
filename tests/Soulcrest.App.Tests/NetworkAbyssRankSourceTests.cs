using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Complete synthetic Own and inventory messages; no capture, process or real profile.</summary>
public sealed class NetworkAbyssRankSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticRank");

    [Theory]
    [InlineData(0L)]
    [InlineData(952_540L)]
    [InlineData(long.MaxValue)]
    public void CompleteOwnConfirmsRankWithoutAnInventoryBaselineOrIncomeAuthority(long rankPoints)
    {
        using var f = new Fixture();
        f.Send(OwnPacket(rankPoints));
        var snapshot = f.Network.AbyssSnapshot;
        Assert.Same(f.Network.OwnCharacter, snapshot.Character);
        Assert.Equal(rankPoints, snapshot.RankPoints);
        Assert.Equal(At, snapshot.RankPointsObservedAt);
        Assert.Equal(At, snapshot.At);
        Assert.True(snapshot.Sequence > 0);
        Assert.Null(snapshot.AbyssPointBalance);
        Assert.Null(snapshot.FarmedAbyssPoints);
        Assert.False(snapshot.AbyssPointGainsQualified);
        Assert.False(snapshot.RankPointChangesQualified);
        Assert.False(snapshot.KinaGainsQualified);
        Assert.Empty(f.RankChanges);
    }

    [Fact]
    public void NewOwnTotalReplacesTheBaselineWithoutTurningItsDifferenceIntoIncome()
    {
        using var f = new Fixture();
        f.Send(OwnPacket(900_000));
        var before = f.Network.AbyssSnapshot;
        f.Send(OwnPacket(899_400), At.AddMinutes(1));
        var after = f.Network.AbyssSnapshot;
        Assert.NotSame(before.Character, after.Character);
        Assert.Equal(899_400, after.RankPoints);
        Assert.Equal(At.AddMinutes(1), after.RankPointsObservedAt);
        Assert.True(after.Sequence > before.Sequence);
        Assert.Empty(f.RankChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void MissingOrNegativeRankRetainsOwnIdentityWithoutInventingARank(long? rankPoints)
    {
        using var f = new Fixture();
        f.Send(OwnPacket(900_000));
        f.Send(OwnPacket(rankPoints), At.AddSeconds(1));
        Assert.NotNull(f.Network.OwnCharacter);
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Null(f.Network.AbyssSnapshot.RankPointsObservedAt);
        Assert.Empty(f.RankChanges);
    }

    [Fact]
    public void UnknownOrIncompleteOwnDoesNotAuthorizeAnyRank()
    {
        using var f = new Fixture();
        var message = OwnPacket(900_000);
        foreach (var length in Enumerable.Range(0, message.Length))
        {
            f.Send(message[..length]);
            Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        }
        f.Send([.. message, 0]);
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        f.Send(message);
        Assert.Equal(900_000, f.Network.AbyssSnapshot.RankPoints);
        f.Send(message[..^1], At.AddSeconds(1));
        Assert.Null(f.Network.OwnCharacter);
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Empty(f.RankChanges);
    }

    [Theory]
    [InlineData("stream-gap")]
    [InlineData("world-entry-preparation")]
    [InlineData("capture-stopped")]
    public void OwnBoundariesRevokeRankAuthority(string boundary)
    {
        using var f = new Fixture(); f.Send(OwnPacket(900_000));
        f.Network.ResetProgressContext(boundary, At.AddSeconds(1));
        Assert.Null(f.Network.AbyssSnapshot.Character);
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Null(f.Network.AbyssSnapshot.RankPointsObservedAt);
    }

    [Fact]
    public void ForeignStreamGapAndUnqualifiedRankingMessagesDoNotChangeOwnTotal()
    {
        using var f = new Fixture(); f.Send(OwnPacket(900_000));
        var before = f.Network.AbyssSnapshot;
        f.Network.ProcessProgressGap("synthetic-foreign");
        f.Send([0x57, 0x8D, .. new byte[139]], At.AddSeconds(1));
        Assert.Equal(before, f.Network.AbyssSnapshot);
        Assert.Empty(f.RankChanges);
    }

    [Fact]
    public void StoppedNetworkDoesNotPresentAnOwnRankAsLive()
    {
        using var f = new Fixture(running: false); f.Send(OwnPacket(900_000));
        Assert.Null(f.Network.AbyssSnapshot.Character);
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
    }

    [Fact]
    public void OwnThenTypedTotalsPublishSignedNetChangesAndIndependentAbsoluteFacts()
    {
        using var f = new Fixture(); f.Send(OwnPacket(994_030));
        var own = f.Network.OwnCharacter;
        f.Send(RankPacket(994_042), At.AddSeconds(1));
        var first = f.Network.AbyssSnapshot;
        f.Send(RankPacket(993_457), At.AddSeconds(2));
        f.Send(RankPacket(993_457), At.AddSeconds(3));
        var last = f.Network.AbyssSnapshot;
        Assert.Equal(new long[] { 12, -585 }, f.RankChanges.Select(change => change.Delta));
        Assert.All(f.RankChanges, change => Assert.Same(own, change.Character));
        Assert.True(f.RankChanges[1].Sequence > f.RankChanges[0].Sequence);
        Assert.Equal(994_042, first.RankPoints); Assert.Equal(993_457, last.RankPoints);
        Assert.Equal(At.AddSeconds(3), last.RankPointsObservedAt);
        Assert.True(last.Sequence > f.RankChanges[^1].Sequence);
        Assert.True(last.RankPointChangesQualified);
        Assert.False(last.AbyssPointGainsQualified); Assert.False(last.KinaGainsQualified);
        Assert.Null(last.AbyssPointBalance); Assert.Null(last.FarmedAbyssPoints);
    }

    [Theory]
    [InlineData(0L, long.MaxValue)]
    [InlineData(long.MaxValue, 0L)]
    public void NonnegativeInt64EndpointsHaveAnExactSignedDifference(long baseline, long next)
    {
        using var f = new Fixture(); f.Send(OwnPacket(baseline));
        f.Send(RankPacket(next), At.AddSeconds(1));
        Assert.Equal(next - baseline, Assert.Single(f.RankChanges).Delta);
        Assert.Equal(next, f.Network.AbyssSnapshot.RankPoints);
    }

    [Fact]
    public void EqualTimeMessagesFollowWireOrderAndEqualTotalsDoNotProduceIncome()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        f.Send(RankPacket(101)); f.Send(RankPacket(101)); f.Send(RankPacket(102));
        Assert.Equal(new long[] { 1, 1 }, f.RankChanges.Select(change => change.Delta));
        Assert.True(f.RankChanges[1].Sequence > f.RankChanges[0].Sequence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void ATotalCannotReplaceAMissingOwnRankBaselineOrReplayAPreOwnNotification(long? ownRank)
    {
        using var f = new Fixture();
        f.Send(RankPacket(100), At.AddSeconds(-1));
        f.Send(OwnPacket(ownRank)); f.Send(RankPacket(120), At.AddSeconds(1));
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.False(f.Network.AbyssSnapshot.RankPointChangesQualified);
        Assert.Empty(f.RankChanges);
        f.Send(OwnPacket(500), At.AddSeconds(2));
        Assert.Equal(500, f.Network.AbyssSnapshot.RankPoints);
        Assert.Empty(f.RankChanges);
        f.Send(RankPacket(503), At.AddSeconds(3));
        Assert.Equal(3, Assert.Single(f.RankChanges).Delta);
    }

    [Fact]
    public void ForeignStreamTotalsAndMalformedMessagesCannotMutateTheCurrentOwnRank()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        var before = f.Network.AbyssSnapshot;
        f.Send(RankPacket(200), At.AddSeconds(1), "synthetic-foreign");
        f.Send([0x73, 0xE2], At.AddSeconds(2), "synthetic-foreign");
        Assert.Equal(before, f.Network.AbyssSnapshot); Assert.Empty(f.RankChanges);
        f.Send(RankPacket(101), At.AddSeconds(3));
        Assert.Equal(1, Assert.Single(f.RankChanges).Delta);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("type")]
    [InlineData("cut")]
    [InlineData("tail")]
    public void InvalidOwnRankMessageRevokesOnlyRankAndCannotCatchUpWithoutAFreshOwn(string shape)
    {
        using var f = new Fixture(); f.LoginWithWallet(100);
        var own = f.Network.OwnCharacter; var before = f.Network.AbyssSnapshot;
        var world = f.Network.WorldContext.Snapshot;
        f.Send(RankPacket(101), At.AddSeconds(1));
        byte[] invalid = shape switch
        {
            "negative" => RankPacket(-1), "type" => RankPacket(102, type: 1),
            "cut" => RankPacket(102)[..^1], "tail" => [.. RankPacket(102), (byte)0],
            _ => throw new InvalidOperationException(),
        };
        f.Send(invalid, At.AddSeconds(2));
        var after = f.Network.AbyssSnapshot;
        Assert.Same(own, f.Network.OwnCharacter); Assert.Equal(world, f.Network.WorldContext.Snapshot);
        Assert.Equal(before.AbyssPointBalance, after.AbyssPointBalance);
        Assert.Equal(before.AbyssPointBalanceObservedAt, after.AbyssPointBalanceObservedAt);
        Assert.Null(after.RankPoints); Assert.Null(after.RankPointsObservedAt);
        Assert.False(after.FormatRejected); Assert.False(after.RankPointChangesQualified);
        f.Send(RankPacket(150), At.AddSeconds(3));
        Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Equal(1, Assert.Single(f.RankChanges).Delta);
        f.Send(OwnPacket(500), At.AddSeconds(4)); f.Send(RankPacket(502), At.AddSeconds(5));
        Assert.Equal(new long[] { 1, 2 }, f.RankChanges.Select(change => change.Delta));
    }

    [Theory]
    [InlineData("before-own")]
    [InlineData("backward")]
    [InlineData("default")]
    [InlineData("future")]
    public void InvalidObservationTimeBreaksRankContinuityRatherThanBridgingIt(string timing)
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        f.Send(RankPacket(101), At.AddSeconds(2));
        var invalidAt = timing switch
        {
            "before-own" => At.AddTicks(-1), "backward" => At.AddSeconds(1),
            "default" => default, "future" => DateTimeOffset.MaxValue,
            _ => throw new InvalidOperationException(),
        };
        f.Send(RankPacket(102), invalidAt);
        Assert.NotNull(f.Network.OwnCharacter); Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        f.Send(RankPacket(110), At.AddSeconds(3));
        Assert.Equal(1, Assert.Single(f.RankChanges).Delta);
    }

    [Fact]
    public void ANewOwnOrOwnGapCannotTurnTheInterveningDifferenceIntoSessionIncome()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        var originalOwn = f.Network.OwnCharacter;
        f.Network.ProcessProgressGap("synthetic-rank-own", capturedAtUtc: At.AddSeconds(1));
        f.Send(RankPacket(150), At.AddSeconds(2));
        Assert.Empty(f.RankChanges); Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        f.Send(OwnPacket(300), At.AddSeconds(3));
        Assert.NotSame(originalOwn, f.Network.OwnCharacter);
        Assert.Empty(f.RankChanges);
        f.Send(RankPacket(302), At.AddSeconds(4));
        Assert.Equal(2, Assert.Single(f.RankChanges).Delta);
        f.Send(OwnPacket(900), At.AddSeconds(5)); f.Send(RankPacket(901), At.AddSeconds(6));
        Assert.Equal(new long[] { 2, 1 }, f.RankChanges.Select(change => change.Delta));
    }

    [Fact]
    public void ReentrantTotalUpdatesKeepEverySubscriberAndSnapshotInObservationOrder()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        var calls = new List<string>();
        var firstSnapshots = new List<(DateTimeOffset? At, long Sequence)>();
        var secondSnapshots = new List<(long? Total, DateTimeOffset? At)>();
        f.Network.RankPointsChanged += change =>
        {
            calls.Add("A:" + change.Delta);
            if (change.Delta == 1) f.Send(RankPacket(103), At.AddSeconds(2));
            firstSnapshots.Add((f.Network.AbyssSnapshot.RankPointsObservedAt, f.Network.AbyssSnapshot.Sequence));
        };
        f.Network.RankPointsChanged += change =>
        {
            calls.Add("B:" + change.Delta);
            secondSnapshots.Add((f.Network.AbyssSnapshot.RankPoints, f.Network.AbyssSnapshot.At));
        };
        f.Send(RankPacket(101), At.AddSeconds(1));
        Assert.Equal(new[] { "A:1", "B:1", "A:2", "B:2" }, calls);
        Assert.Equal(new long[] { 1, 2 }, f.RankChanges.Select(change => change.Delta));
        Assert.Equal(f.RankChanges.Select(change => ((DateTimeOffset?)change.At, change.Sequence)), firstSnapshots);
        Assert.Equal(new (long?, DateTimeOffset?)[] { (101, At.AddSeconds(1)), (103, At.AddSeconds(2)) }, secondSnapshots);
    }

    [Fact]
    public void ReentrantChangedCallbackAlsoDefersNewTotalsUntilAllChangeSubscribersFinish()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        var notifications = new List<long?>(); var nested = false; var snapshotsUnchanged = true;
        f.Network.Changed += () =>
        {
            var snapshot = f.Network.AbyssSnapshot;
            notifications.Add(snapshot.RankPoints);
            if (!nested) { nested = true; f.Send(RankPacket(103), At.AddSeconds(2)); }
            snapshotsUnchanged &= snapshot == f.Network.AbyssSnapshot;
        };
        f.Send(RankPacket(101), At.AddSeconds(1));
        Assert.Equal(new long?[] { 101, 103 }, notifications);
        Assert.Equal(new long[] { 1, 2 }, f.RankChanges.Select(change => change.Delta));
        Assert.True(snapshotsUnchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantOwnBoundaryStopsOldSubscribersAndDiscardsQueuedOldObservations(bool newOwn)
    {
        using var f = new Fixture(); f.Send(OwnPacket(100));
        var lastSubscriber = new List<long>(); var reset = false;
        f.Network.RankPointsChanged += _ =>
        {
            if (reset) return;
            reset = true;
            f.Send(RankPacket(103), At.AddSeconds(2));
            if (newOwn) f.Send(OwnPacket(500), At.AddSeconds(3));
            else f.Network.ProcessProgressGap("synthetic-rank-own", capturedAtUtc: At.AddSeconds(3));
        };
        f.Network.RankPointsChanged += change => lastSubscriber.Add(change.Delta);
        f.Send(RankPacket(101), At.AddSeconds(1));
        Assert.Empty(lastSubscriber); Assert.Equal(1, Assert.Single(f.RankChanges).Delta);
        Assert.Equal(newOwn ? (long?)500 : null, f.Network.AbyssSnapshot.RankPoints);
        if (newOwn)
        {
            f.Send(RankPacket(502), At.AddSeconds(4));
            Assert.Equal(new long[] { 2 }, lastSubscriber);
            Assert.Equal(new long[] { 1, 2 }, f.RankChanges.Select(change => change.Delta));
        }
    }

    [Fact]
    public void ReentrantQueueOverflowRevokesRankWithoutAlteringTheOwnBinding()
    {
        using var f = new Fixture(); f.Send(OwnPacket(100)); var own = f.Network.OwnCharacter;
        f.Network.RankPointsChanged += _ =>
        {
            for (var total = 102; total < 180; total++) f.Send(RankPacket(total), At.AddSeconds(2));
        };
        f.Send(RankPacket(101), At.AddSeconds(1));
        Assert.Same(own, f.Network.OwnCharacter); Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Equal(1, Assert.Single(f.RankChanges).Delta);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-rank-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkAbyssRankChange> RankChanges { get; } = [];
        internal Fixture(bool running = true)
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, running);
            Network.RankPointsChanged += RankChanges.Add;
        }
        internal void Send(byte[] message, DateTimeOffset? at = null, string stream = "synthetic-rank-own") =>
            Network.ProcessProgressMessage(stream, message, at ?? At);
        internal void LoginWithWallet(long rank)
        {
            Send(Packet(0x3620, w => { w.Write((ushort)0); V(w, (uint)Own.EntityKey); w.Write(new byte[4]); w.Write(Own.CharacterId); }), At.AddSeconds(-3));
            Send(Packet(0x5611, w =>
            {
                w.Write((ushort)528); V(w, 1);
                w.Write((byte)0); w.Write(91L); w.Write(930100003); w.Write(125_000L); w.Write((byte)1); w.Write(0);
                w.Write((byte)0); V(w, 0); w.Write(0L); // Two false bits share the bool reservoir byte.
                w.Write(new byte[6]);
            }), At.AddSeconds(-2));
            Send(Packet(0x3621, w => w.Write(new byte[46])), At.AddSeconds(-1));
            Send(OwnPacket(rank));
            Assert.Equal(125_000, Network.AbyssSnapshot.AbyssPointBalance);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] RankPacket(long total, byte type = 0) => Packet(0xE273, w => { w.Write(type); w.Write(total); });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }

    // Minimal complete MyCharAppear with only the optional absolute rank field enabled.
    private static byte[] OwnPacket(long? rankPoints)
    {
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        w.Write((ushort)0x3633); V(w, (uint)Own.EntityKey); w.Write(rankPoints.HasValue ? 65536 : 0);
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes(Own.Name); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)Own.ServerId); w.Write(29); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(0L); w.Write(20); w.Write(Own.CharacterId); V(w, 0); w.Write((byte)1);
        if (rankPoints.HasValue) w.Write(rankPoints.Value);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
        return bytes.ToArray();
    }

    private static void V(BinaryWriter writer, uint value)
    {
        do { var next = (byte)(value & 127); value >>= 7; writer.Write(value == 0 ? next : (byte)(next | 128)); } while (value > 0);
    }
}
