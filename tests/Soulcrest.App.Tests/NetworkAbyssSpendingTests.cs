using System.Reflection;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic own wallets and transports only; no game, device, app or real-profile access.</summary>
public sealed class NetworkAbyssSpendingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly CaptureConnection Connection = new(123, "192.0.2.10", 50001, "203.0.113.10", 443);
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticSpending");
    private sealed record Change(long Count, long Key = 91, int Item = 930100003, byte Container = 1,
        byte Modification = 1, byte Increase = 11, byte Decrease = 0, byte Update = 0, bool Equipment = false);
    private static Change Spending(long count) => new(count, Increase: 0, Decrease: 15);
    private static Change ObservedReward(long count) => new(count, Increase: 2);

    [Fact]
    public void ObservedSpendingAdvancesTheWalletWithoutIncomeAndCountsTheFirstFollowingGainExactlyOnce()
    {
        using var f = new Fixture(); f.Baseline(25_471);
        var rank = f.Network.AbyssSnapshot;
        f.Send(Changes(Spending(471)), 2);
        var spent = f.Network.AbyssSnapshot;
        Assert.Equal(471, spent.AbyssPointBalance); Assert.Equal(At.AddSeconds(2), spent.AbyssPointBalanceObservedAt);
        Assert.False(spent.AbyssPointGainsQualified); Assert.Empty(f.Gains);
        Assert.Equal(rank.RankPoints, spent.RankPoints); Assert.Equal(rank.RankPointsObservedAt, spent.RankPointsObservedAt);
        Assert.Null(spent.FarmedAbyssPoints); Assert.True(spent.Sequence > rank.Sequence);
        f.Send(Changes(new Change(485)), 3); f.Send(Changes(new Change(485)), 4);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(14, gain.Amount); Assert.Equal(At.AddSeconds(3), gain.At);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(485, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public void RepeatedSpendingAndLaterLegitimateSpendingNeverDoubleBookOrSubtractIncome()
    {
        using var f = new Fixture(); f.Baseline(100);
        f.Send(Changes(new Change(105)), 2);
        f.Send(Changes(Spending(90)), 3); f.Send(Changes(Spending(90)), 3); f.Send(Changes(Spending(90)), 4);
        Assert.Equal(5, Assert.Single(f.Gains).Amount); Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        f.Send(Changes(new Change(92)), 5); f.Send(Changes(new Change(92)), 6);
        f.Send(Changes(Spending(0)), 7); f.Send(Changes(new Change(3)), 8);
        Assert.Equal(new long[] { 5, 2, 3 }, f.Gains.Select(gain => gain.Amount));
        Assert.Equal(3, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public void DifferentMessagesAtTheSameCaptureTimeRetainTheirObservedOrder()
    {
        using var f = new Fixture(); f.Baseline(100);
        f.Send(Changes(new Change(105)), 2); f.Send(Changes(Spending(90)), 2);
        f.Send(Changes(Spending(90)), 2); f.Send(Changes(new Change(92)), 2);
        Assert.Equal(new long[] { 5, 2 }, f.Gains.Select(gain => gain.Amount));
        Assert.All(f.Gains, gain => Assert.Equal(At.AddSeconds(2), gain.At));
        Assert.Equal(92, f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Theory]
    [InlineData("unknown-decrease")]
    [InlineData("unknown-increase")]
    [InlineData("update")]
    [InlineData("rising")]
    [InlineData("different-wallet")]
    [InlineData("moved-container")]
    [InlineData("equipment")]
    [InlineData("duplicate-wallet")]
    [InlineData("colliding-item")]
    [InlineData("truncated")]
    [InlineData("trailing-bytes")]
    [InlineData("negative")]
    public void OnlyTheExactValidatedNonIncreasingWalletVariantCanPreserveTheBase(string variant)
    {
        using var f = new Fixture(); f.Baseline(100);
        var update = variant switch
        {
            "unknown-decrease" => Changes(Spending(90) with { Decrease = 16 }),
            "unknown-increase" => Changes(Spending(90) with { Increase = 1 }),
            "update" => Changes(Spending(90) with { Update = 1 }),
            "rising" => Changes(Spending(101)),
            "different-wallet" => Changes(Spending(90) with { Key = 92 }),
            "moved-container" => Changes(Spending(90) with { Container = 2 }),
            "equipment" => Changes(Spending(90) with { Equipment = true }),
            "duplicate-wallet" => Changes(Spending(90), Spending(90)),
            "colliding-item" => Changes(Spending(90), new Change(1, Item: 610660025)),
            "truncated" => Changes(Spending(90))[..^1],
            "trailing-bytes" => Changes(Spending(90)).Concat(new byte[] { 0 }).ToArray(),
            "negative" => Changes(Spending(-1)),
            _ => throw new InvalidOperationException(),
        };
        f.Send(update, 2); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(110)), 3); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("no-transport")]
    [InlineData("removed-transport")]
    [InlineData("replaced-transport")]
    [InlineData("capture-generation")]
    public void AnOldWalletCannotAuthorizeSpendingWithoutItsOriginalCurrentCaptureAndTransport(string boundary)
    {
        using var f = new Fixture(seedTransport: boundary != "no-transport"); f.Baseline(100);
        if (boundary == "removed-transport") f.RemoveTransport();
        if (boundary == "replaced-transport") f.ReplaceTransport();
        if (boundary == "capture-generation") f.AdvanceCaptureGeneration();
        f.Send(Changes(Spending(90)), 2);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(110)), 3); Assert.Empty(f.Gains);
    }

    [Fact]
    public void ASoftOwnCannotPromoteAnOldWalletIntoANewerCaptureGeneration()
    {
        using var f = new Fixture(); f.Baseline(100); f.AdvanceCaptureGeneration();
        f.Own(2); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(Spending(90)), 3); f.Send(Changes(new Change(110)), 4);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("no-modification")]
    [InlineData("login-list-only")]
    [InlineData("character")]
    [InlineData("server")]
    [InlineData("gap")]
    public void SpendingCannotEstablishANewOwnBaselineOrBridgeAnUnqualifiedInterval(string boundary)
    {
        using var f = new Fixture();
        if (boundary == "login-list-only")
        {
            f.Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), -3);
            f.Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(new Change(100)); w.Z(6); }), -2);
            f.Send(Packet(0x3621, w => w.Z(46)), -1); f.Own(0);
            Assert.Equal(100, f.Network.AbyssSnapshot.AbyssPointBalance);
        }
        else if (boundary == "no-modification") f.Own(0);
        else
        {
            f.Baseline(100);
            if (boundary == "character") f.Own(2, Identity with { CharacterId = 102 });
            if (boundary == "server") f.Own(2, Identity with { ServerId = 502 });
            if (boundary == "gap") f.Network.ProcessProgressGap(Connection.StreamKey, capturedAtUtc: At.AddSeconds(2));
        }
        f.Send(Changes(Spending(90)), 3); f.Send(Changes(new Change(110)), 4);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("backward")]
    [InlineData("before-own")]
    [InlineData("default")]
    [InlineData("future")]
    public void InvalidExpenseFactTimesCannotMoveTheWalletBackOrReopenTheSource(string timing)
    {
        using var f = new Fixture(); f.Baseline(100);
        f.Send(Changes(Spending(90)), 2); f.Send(Changes(new Change(92)), 3);
        var at = timing switch
        {
            "backward" => At.AddSeconds(2), "before-own" => At.AddTicks(-1),
            "default" => default, "future" => DateTimeOffset.MaxValue, _ => throw new InvalidOperationException(),
        };
        f.SendAt(Changes(Spending(90)), at);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(100)), 4); Assert.Equal(2, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void ForeignSpendingCannotChangeTheCurrentWallet()
    {
        using var f = new Fixture(); f.Baseline(100); var before = f.Network.AbyssSnapshot;
        f.Network.ProcessProgressMessage("synthetic-foreign-spending", Changes(Spending(0)), At.AddSeconds(2));
        Assert.Equal(before, f.Network.AbyssSnapshot);
        f.Send(Changes(new Change(105)), 3); Assert.Equal(5, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void AValidatedSpendingFactCanCarryAcrossTheExistingSoftOwnTransition()
    {
        using var f = new Fixture(); f.Baseline(100); f.Send(Changes(Spending(90)), 2);
        f.Send(Packet(0x3621, w => w.Z(46)), 3); f.Own(4);
        Assert.Equal(90, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(At.AddSeconds(2), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        f.Send(Changes(new Change(92)), 5); Assert.Equal(2, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void ExpenseCallbacksSeeTheSpentWalletBeforeAQueuedFollowingGain()
    {
        using var f = new Fixture(); f.Baseline(100); var nested = false; var seen = new List<long?>();
        f.Network.Changed += () =>
        {
            if (nested || f.Network.AbyssSnapshot.AbyssPointBalance != 90) return;
            nested = true; var packet = Changes(new Change(92)); f.Send(packet, 3); Array.Clear(packet);
            seen.Add(f.Network.AbyssSnapshot.AbyssPointBalance);
        };
        f.Network.Changed += () => seen.Add(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(Spending(90)), 2);
        Assert.True(nested); Assert.Equal(new long?[] { 90, 90, 92 }, seen);
        Assert.Equal(2, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void AQueuedExpenseDoesNotChangeTheSnapshotObservedByEarlierGainSubscribers()
    {
        using var f = new Fixture(); f.Baseline(100); var seen = new List<(long Amount, long? Balance)>();
        f.Network.AbyssPointsGained += gain =>
        {
            if (gain.Amount != 5) return;
            f.Send(Changes(Spending(90)), 3); f.Send(Changes(new Change(92)), 4);
        };
        f.Network.AbyssPointsGained += gain => seen.Add((gain.Amount, f.Network.AbyssSnapshot.AbyssPointBalance));
        f.Send(Changes(new Change(105)), 2);
        Assert.Equal(new[] { (5L, (long?)105), (2L, (long?)92) }, seen);
        Assert.Equal(new long[] { 5, 2 }, f.Gains.Select(gain => gain.Amount));
    }

    [Fact]
    public void AGapInsideTheExpenseCallbackDiscardsAQueuedGainInsteadOfLendingTheSpentBase()
    {
        using var f = new Fixture(); f.Baseline(100); var invalidated = false;
        f.Network.Changed += () =>
        {
            if (invalidated || f.Network.AbyssSnapshot.AbyssPointBalance != 90) return;
            invalidated = true; f.Send(Changes(new Change(92)), 3);
            f.Network.ProcessProgressGap(Connection.StreamKey, capturedAtUtc: At.AddSeconds(4));
        };
        f.Send(Changes(Spending(90)), 2);
        Assert.True(invalidated); Assert.Empty(f.Gains); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(5); f.Send(Changes(new Change(100)), 6); f.Send(Changes(new Change(101)), 7);
        Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void QualifiedObservedRewardCountsOnceAfterOwnHandoffAndRemainsIndependentFromCombatAndRank()
    {
        using var f = new Fixture(); f.QualifiedBaseline(100);
        var rank = f.Network.AbyssSnapshot; var combat = new List<NetworkAbyssCombatEvent>();
        var rankChanges = new List<NetworkAbyssRankChange>();
        f.Network.CombatEvent += combat.Add; f.Network.RankPointsChanged += rankChanges.Add;
        f.Send(Packet(0x3621, w => w.Z(46)), 3); f.Own(4);
        Assert.Equal(At.AddSeconds(2), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified); Assert.Empty(f.Gains);
        f.Send(Changes(ObservedReward(1_320)), 5); f.Send(Changes(ObservedReward(1_320)), 6);
        f.Send(Changes(new Change(1_327)), 7);
        f.Send(Changes(Spending(1_000)), 8); f.Send(Changes(ObservedReward(1_005)), 9);
        Assert.Equal(new long[] { 1_220, 7, 5 }, f.Gains.Select(gain => gain.Amount));
        Assert.Equal(new[] { At.AddSeconds(5), At.AddSeconds(7), At.AddSeconds(9) }, f.Gains.Select(gain => gain.At));
        Assert.Equal(1_005, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(rank.RankPoints, f.Network.AbyssSnapshot.RankPoints);
        Assert.Empty(combat); Assert.Empty(rankChanges); Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public void ARepeatedZeroObservedRewardDoesNotCreateIncomeOrDisableTheQualifiedWallet()
    {
        using var f = new Fixture(); f.QualifiedBaseline(0);
        f.Send(Changes(ObservedReward(0)), 3); f.Send(Changes(ObservedReward(0)), 4);
        Assert.Empty(f.Gains); Assert.Equal(0, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        f.Send(Changes(ObservedReward(1)), 5); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData("no-modification")]
    [InlineData("login-list-only")]
    [InlineData("single-modification")]
    [InlineData("character")]
    [InlineData("server")]
    [InlineData("gap")]
    public void ObservedRewardCannotCreateOrPromoteAnUnqualifiedOwnModificationBase(string boundary)
    {
        using var f = new Fixture();
        if (boundary == "login-list-only")
        {
            f.Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), -3);
            f.Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(new Change(100)); w.Z(6); }), -2);
            f.Send(Packet(0x3621, w => w.Z(46)), -1); f.Own(0);
            Assert.Equal(100, f.Network.AbyssSnapshot.AbyssPointBalance);
        }
        else if (boundary == "no-modification") f.Own(0);
        else if (boundary == "single-modification") f.Baseline(100);
        else
        {
            f.QualifiedBaseline(100);
            if (boundary == "character") f.Own(3, Identity with { CharacterId = 102 });
            if (boundary == "server") f.Own(3, Identity with { ServerId = 502 });
            if (boundary == "gap")
            {
                f.Network.ProcessProgressGap(Connection.StreamKey, capturedAtUtc: At.AddSeconds(3)); f.Own(4);
            }
        }
        f.Send(Changes(ObservedReward(1_320)), 5); f.Send(Changes(new Change(1_325)), 6);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("no-transport")]
    [InlineData("removed-transport")]
    [InlineData("replaced-transport")]
    [InlineData("capture-generation")]
    public void ObservedRewardRequiresTheOriginalNonNullTransportAndCaptureGeneration(string boundary)
    {
        using var f = new Fixture(seedTransport: boundary != "no-transport"); f.QualifiedBaseline(100);
        if (boundary == "removed-transport") f.RemoveTransport();
        if (boundary == "replaced-transport") f.ReplaceTransport();
        if (boundary == "capture-generation") f.AdvanceCaptureGeneration();
        f.Send(Changes(ObservedReward(1_320)), 3);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("reason24")]
    [InlineData("reason31")]
    [InlineData("decrease")]
    [InlineData("update")]
    [InlineData("lower")]
    [InlineData("negative")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("changed-wallet")]
    [InlineData("zero-key")]
    [InlineData("container")]
    [InlineData("equipment")]
    [InlineData("duplicate")]
    [InlineData("collision")]
    [InlineData("truncated")]
    [InlineData("trailing-bytes")]
    public void ObservedRewardDoesNotQualifyOtherReasonsOrConflictingWalletRecords(string variant)
    {
        using var f = new Fixture(); f.QualifiedBaseline(100);
        var reward = ObservedReward(1_320);
        var message = variant switch
        {
            "reason24" => Changes(reward with { Increase = 24 }), "reason31" => Changes(reward with { Increase = 31 }),
            "decrease" => Changes(reward with { Decrease = 15 }), "update" => Changes(reward with { Update = 1 }),
            "lower" => Changes(reward with { Count = 99 }), "negative" => Changes(reward with { Count = -1 }),
            "add" => Changes(reward with { Modification = 0 }), "remove" => Changes(reward with { Modification = 2 }),
            "changed-wallet" => Changes(reward with { Key = 92 }), "zero-key" => Changes(reward with { Key = 0 }),
            "container" => Changes(reward with { Container = 2 }),
            "equipment" => Changes(reward with { Equipment = true }), "duplicate" => Changes(reward, reward),
            "collision" => Changes(reward, new Change(1, Item: 610660025)),
            "truncated" => Changes(reward)[..^1], "trailing-bytes" => Changes(reward).Concat(new byte[] { 0 }).ToArray(),
            _ => throw new InvalidOperationException(),
        };
        f.Send(message, 3); f.Send(Changes(new Change(1_325)), 4);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("backward")]
    [InlineData("before-own")]
    [InlineData("default")]
    [InlineData("future")]
    public void ObservedRewardCannotUseAnInvalidOrRegressingFactTime(string timing)
    {
        using var f = new Fixture(); f.QualifiedBaseline(100);
        var at = timing switch
        {
            "backward" => At.AddSeconds(1), "before-own" => At.AddTicks(-1),
            "default" => default, "future" => DateTimeOffset.MaxValue, _ => throw new InvalidOperationException(),
        };
        f.SendAt(Changes(ObservedReward(1_320)), at);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
    }

    [Fact]
    public void ForeignObservedRewardCannotChangeTheOwnWalletOrCreateIncome()
    {
        using var f = new Fixture(); f.QualifiedBaseline(100); var before = f.Network.AbyssSnapshot;
        f.Network.ProcessProgressMessage("synthetic-foreign-reward", Changes(ObservedReward(1_320)), At.AddSeconds(3));
        Assert.Equal(before, f.Network.AbyssSnapshot); Assert.Empty(f.Gains);
        f.Send(Changes(ObservedReward(1_320)), 4); Assert.Equal(1_220, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void ObservedRewardCommitsTheWalletBeforeReentrantMessagesAndNeverRepeatsItsGain()
    {
        using var f = new Fixture(); f.QualifiedBaseline(100);
        var seen = new List<(long Amount, long? Balance)>();
        f.Network.AbyssPointsGained += gain =>
        {
            if (gain.Amount != 1_220) return;
            f.Send(Changes(ObservedReward(1_320)), 4); f.Send(Changes(new Change(1_325)), 5);
        };
        f.Network.AbyssPointsGained += gain => seen.Add((gain.Amount, f.Network.AbyssSnapshot.AbyssPointBalance));
        f.Send(Changes(ObservedReward(1_320)), 3);
        Assert.Equal(new[] { (1_220L, (long?)1_320), (5L, (long?)1_325) }, seen);
        Assert.Equal(new long[] { 1_220, 5 }, f.Gains.Select(gain => gain.Amount));
    }

    [Fact]
    public void AReentrantGapDuringObservedRewardDoesNotLendItsBaseToAQueuedFollowingGain()
    {
        using var f = new Fixture(); f.QualifiedBaseline(100); var invalidated = false;
        f.Network.AbyssPointsGained += gain =>
        {
            if (invalidated || gain.Amount != 1_220) return;
            invalidated = true; f.Send(Changes(new Change(1_325)), 4);
            f.Network.ProcessProgressGap(Connection.StreamKey, capturedAtUtc: At.AddSeconds(5));
        };
        f.Send(Changes(ObservedReward(1_320)), 3);
        Assert.Equal(1_220, Assert.Single(f.Gains).Amount); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(6); f.Send(Changes(new Change(1_330)), 7); f.Send(Changes(new Change(1_331)), 8);
        Assert.Equal(new long[] { 1_220, 1 }, f.Gains.Select(gain => gain.Amount));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-ap-spending-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkAbyssPointGain> Gains { get; } = [];
        private Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        internal Fixture(bool seedTransport = true)
        {
            Directory.CreateDirectory(_root); Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Field("_generation").SetValue(Network, 7L);
            if (seedTransport) ReplaceTransport();
            Network.AbyssPointsGained += Gains.Add;
        }
        internal void Send(byte[] message, int seconds) => SendAt(message, At.AddSeconds(seconds));
        internal void SendAt(byte[] message, DateTimeOffset at) => Network.ProcessProgressMessage(Connection.StreamKey, message, at);
        internal void Own(int seconds, OwnCharacterIdentity? identity = null) => Send(OwnPacket(identity ?? Identity), seconds);
        internal void Baseline(long amount) { Own(0); Send(Changes(new Change(amount)), 1); }
        internal void QualifiedBaseline(long amount) { Baseline(amount); Send(Changes(new Change(amount)), 2); }
        internal void RemoveTransport() => Streams.Remove(Connection);
        internal void ReplaceTransport() => Streams[Connection] = (new TcpReassembler(), new GameMessageStream(requireVerifiedSync: true));
        internal void AdvanceCaptureGeneration() => Field("_generation").SetValue(Network, (long)Field("_generation").GetValue(Network)! + 1);
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] OwnPacket(OwnCharacterIdentity identity) => Packet(0x3633, w =>
    {
        w.V(identity.EntityKey); w.I32(65536); w.U8(1);
        var name = Encoding.UTF8.GetBytes(identity.Name); w.V(name.Length); w.Bytes(name);
        w.U16((ushort)identity.ServerId); w.I32(29); w.Z(36); w.V(0); w.V(0); w.Z(16); w.U8(0);
        w.U16(0); w.Z(18); w.U8(1); w.V(0); w.V(0); w.Z(52); w.V(0); w.V(0); w.V(0);
        w.I64(0); w.I32(20); w.I64(identity.CharacterId); w.V(0); w.U8(1); w.I64(1_000); w.Z(32); w.V(0); w.V(0);
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
