using System.Buffers.Binary;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic complete Own and counter messages only; no process, capture or real data.</summary>
public sealed class NetworkAbyssFarmSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Game = "synthetic-abyss-farm", Foreign = "synthetic-foreign";

    [Theory]
    [InlineData(0L)] [InlineData(676_730L)] [InlineData(717_422L)] [InlineData(long.MaxValue)]
    public void FirstFreshOwnFarmFactIsAbsoluteIndependentOfWalletAndRank(long total)
    {
        using var f = new Fixture(); f.Own(rank: null); f.Farm(total, 1);
        var snapshot = f.Network.AbyssSnapshot;
        Assert.Same(f.Network.OwnCharacter, snapshot.Character);
        Assert.Equal(total, snapshot.FarmedAbyssPoints); Assert.Equal(At.AddSeconds(1), snapshot.FarmedAbyssPointsObservedAt);
        Assert.Equal(At.AddSeconds(1), snapshot.At); Assert.True(snapshot.Sequence > 0);
        Assert.Null(snapshot.AbyssPointBalance); Assert.Null(snapshot.RankPoints);
        Assert.False(snapshot.AbyssPointGainsQualified); Assert.False(snapshot.RankPointChangesQualified);
        Assert.Empty(f.ApGains); Assert.Empty(f.RankGains); Assert.Equal(0, f.CombatEvents);
    }

    [Fact]
    public void FarmTotalsAndOpaqueTailChangesNeverGenerateIncome()
    {
        using var f = new Fixture(); f.Own(); f.Farm(717_422, 1);
        var first = f.Network.AbyssSnapshot; f.Farm(717_427, 2, tail: 0xA5); f.Farm(717_427, 3, tail: 0xFF);
        var last = f.Network.AbyssSnapshot;
        Assert.Equal(717_427, last.FarmedAbyssPoints); Assert.Equal(At.AddSeconds(3), last.FarmedAbyssPointsObservedAt);
        Assert.Equal(first.RankPoints, last.RankPoints); Assert.Equal(first.RankPointsObservedAt, last.RankPointsObservedAt);
        Assert.True(last.Sequence > first.Sequence); Assert.Empty(f.ApGains); Assert.Empty(f.RankGains);
    }

    [Fact]
    public void WalletAndFarmFactsForTheSameGainDoNotDoubleCountAp()
    {
        using var f = new Fixture(); f.Own(); f.Farm(1_000, 1); f.Ap(100, 1);
        f.Ap(105, 2); var wallet = f.Network.AbyssSnapshot; f.Farm(1_005, 3);
        Assert.Equal(5, Assert.Single(f.ApGains).Amount);
        var after = f.Network.AbyssSnapshot;
        Assert.Equal(1_005, after.FarmedAbyssPoints); Assert.Equal(105, after.AbyssPointBalance);
        Assert.Equal(wallet.AbyssPointBalanceObservedAt, after.AbyssPointBalanceObservedAt);
        Assert.True(after.AbyssPointGainsQualified); Assert.Empty(f.RankGains);
    }

    [Fact]
    public void PreOwnAndUnqualifiedInitialOpcodeNeverBufferOrConfirmAFarmCounter()
    {
        using var f = new Fixture(); f.Farm(717_422, -1); f.Send(FarmPacket(717_422, opcode: 0xFFA8));
        f.Own(); var before = f.Network.AbyssSnapshot;
        Assert.Null(before.FarmedAbyssPoints);
        f.Send(FarmPacket(717_422, opcode: 0xFFA8), At.AddSeconds(1));
        Assert.Equal(before, f.Network.AbyssSnapshot);
        f.Farm(717_427, 2); Assert.Equal(717_427, f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.Empty(f.ApGains);
    }

    [Fact]
    public void ForeignCounterAndForeignGapCannotChangeOrInvalidateTheOwnFarmFact()
    {
        using var f = new Fixture(); f.Own(); f.Farm(717_422, 1); var before = f.Network.AbyssSnapshot;
        f.Send(FarmPacket(999_999), At.AddSeconds(2), Foreign);
        f.Send([0xA9, 0xFF], At.AddSeconds(3), Foreign); f.Network.ProcessProgressGap(Foreign);
        Assert.Equal(before, f.Network.AbyssSnapshot);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("lower")]
    [InlineData("truncated")]
    [InlineData("tail")]
    [InlineData("before-own")]
    [InlineData("backward")]
    [InlineData("default")]
    [InlineData("future")]
    public void InvalidOwnCounterRevokesOnlyFarmUntilANewFullOwn(string invalid)
    {
        using var f = new Fixture(); f.Own(); f.Ap(100, 1); f.Ap(105, 2); f.Farm(1_000, 3); f.Rank(1_002, 4);
        var before = f.Network.AbyssSnapshot; var own = f.Network.OwnCharacter; var world = f.Network.WorldContext.Snapshot;
        byte[] message = invalid switch
        {
            "negative" => FarmPacket(-1), "lower" => FarmPacket(999), "truncated" => FarmPacket(1_001)[..^1],
            "tail" => [.. FarmPacket(1_001), (byte)0], _ => FarmPacket(1_001),
        };
        var time = invalid switch
        {
            "before-own" => At.AddTicks(-1), "backward" => At.AddSeconds(2), "default" => default,
            "future" => DateTimeOffset.MaxValue, _ => At.AddSeconds(5),
        };
        f.Send(message, time); var after = f.Network.AbyssSnapshot;
        Assert.Same(own, f.Network.OwnCharacter); Assert.Equal(world, f.Network.WorldContext.Snapshot);
        Assert.Null(after.FarmedAbyssPoints); Assert.Null(after.FarmedAbyssPointsObservedAt); Assert.False(after.FormatRejected);
        Assert.Equal(before.AbyssPointBalance, after.AbyssPointBalance);
        Assert.Equal(before.AbyssPointBalanceObservedAt, after.AbyssPointBalanceObservedAt);
        Assert.Equal(before.RankPoints, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.True(after.AbyssPointGainsQualified && after.RankPointChangesQualified);
        f.Farm(1_001, 6); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        f.Ap(106, 7); f.Rank(1_003, 8);
        Assert.Equal(new long[] { 5, 1 }, f.ApGains.Select(gain => gain.Amount));
        Assert.Equal(new long[] { 2, 1 }, f.RankGains.Select(gain => gain.Delta));
        f.Own(seconds: 9); f.Farm(900, 10);
        Assert.Equal(900, f.Network.AbyssSnapshot.FarmedAbyssPoints);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("world-entry-preparation")]
    [InlineData("capture-stopped")]
    public void OwnBoundariesRevokeFarmAndNeverRecoverAuthorityFromAnEarlierFact(string boundary)
    {
        using var f = new Fixture(); f.Own(); f.Farm(1_000, 1);
        if (boundary == "gap") f.Network.ProcessProgressGap(Game, capturedAtUtc: At.AddSeconds(2));
        else f.Network.ResetProgressContext(boundary, At.AddSeconds(2));
        f.Farm(1_005, 3); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        f.Own(seconds: 4); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        f.Farm(1_005, 5); Assert.Equal(1_005, f.Network.AbyssSnapshot.FarmedAbyssPoints); Assert.Empty(f.ApGains);
    }

    [Fact]
    public void InventoryAndRankFailuresDoNotRevokeOrRefreshTheIndependentFarmFact()
    {
        using var f = new Fixture(); f.Own(); f.Ap(100, 1); f.Ap(105, 2); f.Farm(717_422, 3);
        var before = f.Network.AbyssSnapshot;
        f.Send(ApPacket(110, increase: 12), At.AddSeconds(4)); f.Rank(-1, 5);
        var after = f.Network.AbyssSnapshot;
        Assert.Equal(before.FarmedAbyssPoints, after.FarmedAbyssPoints);
        Assert.Equal(before.FarmedAbyssPointsObservedAt, after.FarmedAbyssPointsObservedAt);
        Assert.Null(after.AbyssPointBalance); Assert.Null(after.RankPoints);
        Assert.NotNull(after.Character); Assert.Equal(5, Assert.Single(f.ApGains).Amount);
        f.Farm(717_427, 6); Assert.Equal(717_427, f.Network.AbyssSnapshot.FarmedAbyssPoints); Assert.Single(f.ApGains);
    }

    [Fact]
    public void ReentrantFarmCallbackDefersNewRankAndWalletFactsUntilEverySubscriberFinishes()
    {
        using var f = new Fixture(); f.Own(); var injected = false; var unchanged = true;
        var snapshots = new List<NetworkAbyssSnapshot>();
        f.Network.Changed += () =>
        {
            var before = f.Network.AbyssSnapshot;
            if (injected || before.FarmedAbyssPoints != 1_000) return;
            injected = true; f.Rank(1_002, 2); f.Ap(100, 3); unchanged &= before == f.Network.AbyssSnapshot;
        };
        f.Network.Changed += () => snapshots.Add(f.Network.AbyssSnapshot);
        f.Farm(1_000, 1);
        Assert.True(injected && unchanged); Assert.Equal(3, snapshots.Count);
        Assert.Equal(new DateTimeOffset?[] { At.AddSeconds(1), At.AddSeconds(2), At.AddSeconds(3) }, snapshots.Select(s => s.At));
        Assert.All(snapshots, s => Assert.Equal(At.AddSeconds(1), s.FarmedAbyssPointsObservedAt));
        Assert.Null(snapshots[0].AbyssPointBalance); Assert.Equal(1_000, snapshots[0].RankPoints);
    }

    [Fact]
    public void ApGainCallbackCannotPublishAFutureFarmSnapshotToLaterGainSubscribers()
    {
        using var f = new Fixture(); f.Own(); f.Farm(500, 1); f.Ap(100, 1);
        var observations = new List<(long? Farm, DateTimeOffset? At)>();
        f.Network.AbyssPointsGained += _ => f.Farm(505, 3);
        f.Network.AbyssPointsGained += _ => observations.Add((f.Network.AbyssSnapshot.FarmedAbyssPoints, f.Network.AbyssSnapshot.At));
        f.Ap(105, 2);
        Assert.Equal(new[] { ((long?)500, (DateTimeOffset?)At.AddSeconds(2)) }, observations);
        Assert.Equal(505, f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.Equal(5, Assert.Single(f.ApGains).Amount);
    }

    [Fact]
    public void ReentrantOwnReplacementDiscardsQueuedFarmFactsFromTheOldGeneration()
    {
        using var f = new Fixture(); f.Own(); var replaced = false;
        f.Network.Changed += () =>
        {
            if (replaced || f.Network.AbyssSnapshot.FarmedAbyssPoints != 1_000) return;
            replaced = true; f.Farm(1_005, 2); f.Own(5_000, 3);
        };
        f.Farm(1_000, 1); Assert.True(replaced); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.Equal(5_000, f.Network.AbyssSnapshot.RankPoints);
        f.Farm(1_005, 4); Assert.Equal(1_005, f.Network.AbyssSnapshot.FarmedAbyssPoints); Assert.Empty(f.ApGains);
    }

    [Fact]
    public void FarmQueueOverflowPreservesOwnRankAndInventoryWork()
    {
        using var f = new Fixture(); f.Own(); f.Farm(500, 1); f.Ap(100, 1);
        var own = f.Network.OwnCharacter; var injected = false;
        f.Network.AbyssPointsGained += _ =>
        {
            if (injected) return;
            injected = true; f.Rank(1_002, 3); f.Ap(106, 4);
            for (var i = 0; i < 70; i++) f.Farm(1_000 + i, 5);
        };
        f.Ap(105, 2);
        Assert.Same(own, f.Network.OwnCharacter); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.Equal(1_002, f.Network.AbyssSnapshot.RankPoints); Assert.Equal(106, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(new long[] { 5, 1 }, f.ApGains.Select(gain => gain.Amount));
        Assert.Equal(2, Assert.Single(f.RankGains).Delta);
    }

    [Fact]
    public void RankQueueOverflowPreservesQueuedFarmWork()
    {
        using var f = new Fixture(); f.Own(); var injected = false;
        f.Network.RankPointsChanged += _ =>
        {
            if (injected) return;
            injected = true; f.Farm(1_000, 2);
            for (var i = 0; i < 70; i++) f.Rank(1_003 + i, 3);
        };
        f.Rank(1_002, 1);
        Assert.NotNull(f.Network.OwnCharacter); Assert.Null(f.Network.AbyssSnapshot.RankPoints);
        Assert.Equal(1_000, f.Network.AbyssSnapshot.FarmedAbyssPoints); Assert.Empty(f.ApGains);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-farm-total-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkAbyssPointGain> ApGains { get; } = [];
        internal List<NetworkAbyssRankChange> RankGains { get; } = [];
        internal int CombatEvents { get; private set; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.AbyssPointsGained += ApGains.Add; Network.RankPointsChanged += RankGains.Add; Network.CombatEvent += _ => CombatEvents++;
        }
        internal void Send(byte[] message, DateTimeOffset? at = null, string stream = Game) => Network.ProcessProgressMessage(stream, message, at ?? At);
        internal void Own(long? rank = 1_000, int seconds = 0) => Send(OwnPacket(rank), At.AddSeconds(seconds));
        internal void Farm(long total, int seconds, byte tail = 0) => Send(FarmPacket(total, tail), At.AddSeconds(seconds));
        internal void Ap(long total, int seconds) => Send(ApPacket(total), At.AddSeconds(seconds));
        internal void Rank(long total, int seconds) => Send(Packet(0xE273, w => { w.Write((byte)0); w.Write(total); }), At.AddSeconds(seconds));
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] FarmPacket(long total, byte tail = 0, ushort opcode = 0xFFA9)
    {
        var bytes = new byte[18]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), total); bytes.AsSpan(10).Fill(tail); return bytes;
    }
    private static byte[] OwnPacket(long? rank) => Packet(0x3633, w =>
    {
        V(w, 300); w.Write(rank.HasValue ? 65536 : 0); w.Write((byte)1); var name = Encoding.UTF8.GetBytes("SyntheticFarmCounter");
        V(w, name.Length); w.Write(name); w.Write((ushort)501); w.Write(29); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0); w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1);
        V(w, 0); V(w, 0); w.Write(new byte[52]); V(w, 0); V(w, 0); V(w, 0);
        w.Write(0L); w.Write(20); w.Write(101L); V(w, 0); w.Write((byte)1);
        if (rank.HasValue) w.Write(rank.Value); w.Write(new byte[32]); V(w, 0); V(w, 0);
    });
    private static byte[] ApPacket(long total, byte increase = 11) => Packet(0x561B, w =>
    {
        V(w, 1); w.Write((byte)1); w.Write((byte)0); w.Write(91L); w.Write(930100003); w.Write(total); w.Write((byte)1); w.Write(0);
        w.Write((byte)0); V(w, 0); w.Write(0L); // Two false bools share this reservoir byte.
        w.Write(increase); w.Write((byte)0); w.Write((byte)0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter writer, int value)
    { var rest = unchecked((uint)value); while (rest >= 128) { writer.Write((byte)(rest | 128)); rest >>= 7; } writer.Write((byte)rest); }
}
