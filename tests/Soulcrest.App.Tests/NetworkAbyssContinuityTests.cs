using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic transports and records only; no device, network, app or user-data access.</summary>
public sealed class NetworkAbyssContinuityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticContinuity");
    private static readonly CaptureConnection Main = new(123, "192.0.2.10", 50001, "203.0.113.10", 443);
    private static readonly CaptureConnection Foreign = Main with { LocalPort = 50002, RemoteAddress = "203.0.113.11" };
    private sealed record Change(long Count, long Key = 91, int Item = 930100003, byte Container = 1,
        byte Modification = 1, byte Increase = 11, byte Decrease = 0, byte Update = 0);

    [Theory]
    [InlineData("direct")]
    [InlineData("prepare")]
    [InlineData("candidate")]
    public void SameCharacterAndTransportCarriesOnlyTheLastRealFactAcrossACompleteOwn(string path)
    {
        using var f = new Fixture(); f.Baseline(); var before = f.Network.AbyssSnapshot;
        var newIdentity = Identity with { EntityKey = 301, Name = "SyntheticRenamed", ClassId = "synthetic-class" };
        f.Transition(path, newIdentity);
        var rebound = f.Network.AbyssSnapshot;
        Assert.Equal(100, rebound.AbyssPointBalance); Assert.Equal(At.AddSeconds(1), rebound.AbyssPointBalanceObservedAt);
        Assert.Same(f.Network.OwnCharacter, rebound.Character); Assert.NotSame(before.Character, rebound.Character);
        Assert.Equal("Gladiator", rebound.Character!.Identity.ClassId);
        Assert.Equal(At.AddSeconds(5), rebound.RankPointsObservedAt); Assert.False(rebound.AbyssPointGainsQualified);
        Assert.Empty(f.Gains); Assert.True(rebound.Sequence > before.Sequence);
        f.Send(Changes(new Change(112)), 6); f.Send(Changes(new Change(112)), 7);
        var gain = Assert.Single(f.Gains); Assert.Equal(12, gain.Amount); Assert.Equal(At.AddSeconds(6), gain.At);
        Assert.Same(rebound.Character, gain.Character); Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public void AQualifiedZeroIncomePairStaysQualifiedButRebindingItNeverEmitsIncome()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Changes(new Change(100)), 2);
        f.Transition("prepare");
        Assert.Empty(f.Gains); Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(At.AddSeconds(2), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        f.Send(Changes(new Change(105)), 6); Assert.Equal(5, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void TheFullyFramedLiveDispatchUsesTheSameTransportForTheHandoff()
    {
        using var f = new Fixture();
        f.Live(OwnPacket(Identity)); f.Live(Changes(new Change(100)));
        var factAt = f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt;
        f.Live(Prepare()); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Live(OwnPacket(Identity with { EntityKey = 301 }));
        Assert.Equal(factAt, f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt); Assert.Empty(f.Gains);
        f.Live(Changes(new Change(114))); Assert.Equal(14, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewSynWithoutPayloadRevokesTheReusedTupleAndAnySuspendedHandoff(bool suspended)
    {
        using var f = new Fixture(); f.Baseline();
        if (suspended) f.Send(Prepare(), 3);
        var transport = f.Transport;
        f.Syn(9000);
        Assert.NotSame(transport, f.Transport); Assert.Null(f.Network.OwnCharacter);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(5); f.Send(Changes(new Change(112)), 6);
        Assert.Empty(f.Gains); Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
    }

    [Fact]
    public void RepeatingTheInitialSynBeforePayloadPreservesThePendingTransport()
    {
        using var f = new Fixture(seedTransport: false); f.Syn(9000);
        var transport = f.Transport; f.Syn(9000);
        Assert.Same(transport, f.Transport);
        f.Live(OwnPacket(Identity)); f.Live(Changes(new Change(100)));
        f.Live(Changes(new Change(112))); Assert.Equal(12, Assert.Single(f.Gains).Amount);
        f.Syn(12000); Assert.NotSame(transport, f.Transport); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Fact]
    public void TheSameInitialSynSequenceAfterPayloadInvalidatesTheEstablishedTransport()
    {
        using var f = new Fixture(seedTransport: false); f.Syn(9000);
        f.Live(OwnPacket(Identity)); f.Live(Changes(new Change(100)));
        var transport = f.Transport;
        f.Syn(9000);
        Assert.NotSame(transport, f.Transport); Assert.Null(f.Network.OwnCharacter);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Live(OwnPacket(Identity)); f.Live(Changes(new Change(112))); Assert.Empty(f.Gains);
        f.Live(Changes(new Change(113))); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("server")]
    [InlineData("stream")]
    [InlineData("transport")]
    [InlineData("no-transport")]
    public void MatchingTextOrEntityIsInsufficientWithoutStableCharacterServerAndTransport(string boundary)
    {
        using var f = new Fixture(seedTransport: boundary != "no-transport"); f.Baseline();
        var identity = boundary switch
        {
            "character" => Identity with { CharacterId = 102 }, "server" => Identity with { ServerId = 502 }, _ => Identity,
        };
        if (boundary == "transport") f.ReplaceTransport();
        var stream = boundary == "stream" ? Foreign.StreamKey : Main.StreamKey;
        f.Own(5, identity, stream); f.Send(Changes(new Change(112)), 6, stream);
        Assert.Equal(112, f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
    }

    [Fact]
    public void ALoginOnlyBalanceStillDoesNotAuthorizeTheFirstModificationAfterAnotherOwn()
    {
        using var f = new Fixture();
        f.Send(Candidate(Identity), -3); f.Send(List(new Change(100)), -2); f.Send(Prepare(), -1); f.Own(0);
        Assert.Equal(100, f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Own(5); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(112)), 6); Assert.Empty(f.Gains);
        f.Send(Changes(new Change(113)), 7); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData("own-gap")]
    [InlineData("wildcard-gap")]
    [InlineData("manual")]
    [InlineData("stopped")]
    [InlineData("capture-generation")]
    [InlineData("removed-transport")]
    public void HardBoundariesDuringPreparationWithoutAnOwnOrCandidateNeverCatchUp(string boundary)
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3);
        Assert.Null(f.Network.OwnCharacter);
        switch (boundary)
        {
            case "own-gap": f.Network.ProcessProgressGap(Main.StreamKey); break;
            case "wildcard-gap": f.Network.ProcessProgressGap("*"); break;
            case "manual": f.Network.ResetProgressContext(); break;
            case "stopped": f.Network.Stop(); f.SetRunning(); break;
            case "capture-generation": f.AdvanceCaptureGeneration(); break;
            case "removed-transport": f.RemoveTransport(); break;
        }
        f.Own(5); f.Send(Changes(new Change(112)), 6); Assert.Empty(f.Gains);
        Assert.Equal(112, f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(113)), 7); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("ap")]
    [InlineData("spend")]
    [InlineData("key-collision")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    public void AnInventoryReceiptWhileSuspendedRevokesTheTicketBeforeItCanBeRebound(string variant)
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3);
        var update = variant switch
        {
            "list" => List(new Change(100)), "ap" => Changes(new Change(105)),
            "spend" => Changes(new Change(90, Increase: 0, Decrease: 15)),
            "key-collision" => Changes(new Change(1, Item: 610660025)), "malformed" => new byte[] { 0x1B, 0x56 },
            "duplicate" => Changes(new Change(105), new Change(105)), _ => throw new InvalidOperationException(),
        };
        f.Send(update, 4); f.Own(5); f.Send(Changes(new Change(112)), 6);
        Assert.Empty(f.Gains); Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        f.Send(Changes(new Change(113)), 7); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void ForeignTrafficAndCompleteUnrelatedItemsPreserveTheSuspendedBase()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3);
        f.Network.ProcessProgressGap(Foreign.StreamKey);
        f.Send([0x1B, 0x56], 4, Foreign.StreamKey); f.Send(Candidate(Identity), 4, Foreign.StreamKey);
        f.Send(Prepare(), 4, Foreign.StreamKey);
        f.Send(Changes(new Change(1, Key: 92, Item: 610660025)), 4);
        f.Own(5); f.Send(Changes(new Change(112)), 6); Assert.Equal(12, Assert.Single(f.Gains).Amount);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("backward-own")]
    [InlineData("future-own")]
    [InlineData("default-own")]
    [InlineData("backward-prepare")]
    [InlineData("repeated-prepare")]
    [InlineData("candidate-no-prepare")]
    [InlineData("candidate-mismatch")]
    [InlineData("candidate-repeated")]
    [InlineData("unknown-candidate")]
    [InlineData("unknown-own")]
    public void InvalidTransitionsDestroyComparisonAuthority(string boundary)
    {
        using var f = new Fixture(); f.Baseline();
        var ownAt = At.AddSeconds(5); var identity = Identity;
        switch (boundary)
        {
            case "expired": f.Send(Prepare(), 3); ownAt = At.AddSeconds(124); break;
            case "backward-own": f.Send(Prepare(), 3); ownAt = At.AddSeconds(2); break;
            case "future-own": f.Send(Prepare(), 3); ownAt = DateTimeOffset.MaxValue; break;
            case "default-own": f.Send(Prepare(), 3); ownAt = default; break;
            case "backward-prepare": f.Send(Prepare(), 0); break;
            case "repeated-prepare": f.Send(Prepare(), 3); f.Send(Prepare(), 4); break;
            case "candidate-no-prepare": f.Send(Candidate(Identity), 3); break;
            case "candidate-mismatch": f.Send(Candidate(Identity with { EntityKey = 301 }), 3); f.Send(Prepare(), 4); break;
            case "candidate-repeated": f.Send(Candidate(Identity), 2); f.Send(Candidate(Identity), 3); f.Send(Prepare(), 4); break;
            case "unknown-candidate": f.Send([0x20, 0x36], 3); break;
            case "unknown-own": f.Send(Prepare(), 3); f.Send([0x33, 0x36], 4); break;
        }
        f.Network.ProcessProgressMessage(Main.StreamKey, OwnPacket(identity), ownAt);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance); Assert.Empty(f.Gains);
        // Restore a valid full Own when the intentionally invalid timestamp cannot qualify AP.
        f.Own(130); f.Send(Changes(new Change(200)), 131); Assert.Empty(f.Gains);
    }

    [Fact]
    public void TheTwoMinuteBoundaryIsInclusiveAndDoesNotRefreshTheOldFactTime()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3); f.Own(123);
        Assert.Equal(At.AddSeconds(1), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        f.Send(Changes(new Change(112)), 124); Assert.Equal(12, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void SeveralCompleteOwnsWithoutANewApFactRetainTheOriginalComparisonBase()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3); f.Own(5);
        f.Own(50); f.Own(123);
        Assert.Equal(100, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(At.AddSeconds(1), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.Empty(f.Gains); f.Send(Changes(new Change(112)), 124);
        Assert.Equal(12, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void RepeatedOwnsCannotExtendTheOriginalHandoffDeadline()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3); f.Own(5); f.Own(100); f.Own(124);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(112)), 125); Assert.Empty(f.Gains);
        f.Send(Changes(new Change(113)), 126); Assert.Equal(1, Assert.Single(f.Gains).Amount);
    }

    [Fact]
    public void AFreshInventoryFactEndsThePreviousTransitionDeadline()
    {
        using var f = new Fixture(); f.Baseline(); f.Transition("prepare");
        f.Send(Changes(new Change(101)), 6); f.Own(500);
        Assert.Equal(At.AddSeconds(6), f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        f.Send(Changes(new Change(103)), 501); Assert.Equal(new long[] { 1, 2 }, f.Gains.Select(gain => gain.Amount));
    }

    [Fact]
    public void APreviouslyRejectedWalletCannotBeCarriedByAValidSoftTransition()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Changes(new Change(99, Increase: 0, Decrease: 15)), 2);
        f.Transition("prepare"); f.Send(Changes(new Change(112)), 6);
        Assert.Empty(f.Gains); Assert.Equal(112, f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("stop")]
    [InlineData("nested-own")]
    [InlineData("inventory")]
    public void ReentrantInvalidationDuringResetCannotResurrectTheTicket(string boundary)
    {
        using var f = new Fixture(); f.Baseline(); var entered = false;
        f.Network.WorldContext.Changed += () =>
        {
            if (entered || f.Network.WorldContext.Snapshot.Status != WorldContextStatus.Transitioning) return;
            entered = true;
            switch (boundary)
            {
                case "gap": f.Network.ProcessProgressGap(Main.StreamKey); break;
                case "stop": f.Network.Stop(); break;
                case "nested-own": f.Own(4); break;
                case "inventory": f.Send(Changes(new Change(105)), 4); break;
            }
        };
        f.Transition("prepare"); f.SetRunning(); f.Own(8); f.Send(Changes(new Change(112)), 9);
        Assert.True(entered); Assert.Empty(f.Gains);
    }

    [Fact]
    public void AQueuedInventoryReceiptDuringSuspensionCannotBeBypassedByAReentrantOwn()
    {
        using var f = new Fixture(); f.Baseline(); f.Send(Prepare(), 3);
        // Hold the FIFO at the callback boundary while receiving the AP record. Its receipt
        // guard must run now, even though the subsequent Own reset discards its queued apply.
        f.DeferObservations(true);
        try
        {
            f.Send(Changes(new Change(105)), 4); f.Own(5);
        }
        finally { f.DeferObservations(false); }
        f.Send(Changes(new Change(112)), 6);
        Assert.Empty(f.Gains); Assert.Equal(112, f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Fact]
    public void AnOwnInsideAnApSubscriberStopsTheOldSubscribersWithoutCreatingAHandoff()
    {
        using var f = new Fixture(); f.Baseline(); var late = new List<long>();
        f.Network.AbyssPointsGained += _ => f.Own(4);
        f.Network.AbyssPointsGained += gain => late.Add(gain.Amount);
        f.Send(Changes(new Change(105)), 2);
        Assert.Equal(5, Assert.Single(f.Gains).Amount); Assert.Empty(late);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(new Change(112)), 6); Assert.Single(f.Gains);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-ap-continuity-" + Guid.NewGuid().ToString("N"));
        private uint _tcpSequence = 1000;
        internal NetworkLootService Network { get; }
        internal List<NetworkAbyssPointGain> Gains { get; } = [];
        private Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        internal GameMessageStream Transport => Streams[Main].Messages;
        internal Fixture(bool seedTransport = true)
        {
            Directory.CreateDirectory(_root); Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            SetRunning();
            Field("_target").SetValue(Network, new CaptureTarget("synthetic", "", new HashSet<int> { 443 },
                new HashSet<int> { 50001, 50002 }, "Synthetic transport", new HashSet<CaptureConnection> { Main, Foreign }));
            if (seedTransport) Live([0, 0x36, .. BitConverter.GetBytes(1791849600000L)]);
            Network.AbyssPointsGained += Gains.Add;
        }
        internal void SetRunning() => typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        internal void Send(byte[] message, int seconds, string? stream = null) => Network.ProcessProgressMessage(stream ?? Main.StreamKey, message, At.AddSeconds(seconds));
        internal void Own(int seconds, OwnCharacterIdentity? identity = null, string? stream = null) => Send(OwnPacket(identity ?? Identity), seconds, stream);
        internal void Baseline() { Own(0); Send(Changes(new Change(100)), 1); }
        internal void Transition(string path, OwnCharacterIdentity? identity = null)
        {
            identity ??= Identity;
            if (path == "candidate") Send(Candidate(identity), 2);
            if (path != "direct") { Send(Prepare(), 3); Assert.Null(Network.AbyssSnapshot.AbyssPointBalance); }
            Own(5, identity);
        }
        internal void ReplaceTransport() => Streams[Main] = (new TcpReassembler(), new GameMessageStream(requireVerifiedSync: true));
        internal void RemoveTransport() => Streams.Remove(Main);
        internal void AdvanceCaptureGeneration() => Field("_generation").SetValue(Network, (long)Field("_generation").GetValue(Network)! + 1);
        internal void DeferObservations(bool value) => Field("_dispatchingAbyssObservations").SetValue(Network, value);
        internal void Live(byte[] message)
        {
            var payload = Frame(message); var sequence = _tcpSequence; _tcpSequence += (uint)payload.Length;
            Network.ProcessCapturedPacket(Capture(sequence, payload));
        }
        internal void Syn(uint sequence, bool advanceSequence = true)
        {
            Network.ProcessCapturedPacket(Capture(sequence, [], flags: 0x12));
            if (advanceSequence) _tcpSequence = sequence + 1;
        }
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] Prepare() => Packet(0x3621, w => w.Z(46));
    private static byte[] Candidate(OwnCharacterIdentity identity) => Packet(0x3620, w => { w.U16(0); w.V(identity.EntityKey); w.Z(4); w.I64(identity.CharacterId); });
    private static byte[] List(params Change[] items) => Packet(0x5611, w => { w.U16(528); w.V(items.Length); foreach (var item in items) w.Item(item); w.Z(6); });
    private static byte[] OwnPacket(OwnCharacterIdentity identity) => Packet(0x3633, w =>
    {
        w.V(identity.EntityKey); w.I32(65536); w.U8(1);
        var name = Encoding.UTF8.GetBytes(identity.Name); w.V(name.Length); w.Bytes(name);
        w.U16((ushort)identity.ServerId); w.I32(identity.ClassId is null ? 29 : 5); w.Z(36); w.V(0); w.V(0); w.Z(16); w.U8(0);
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
    private static byte[] Frame(byte[] message)
    { using var writer = new WireWriter(); writer.V(message.Length + 4); writer.Bytes(message); return writer.ToArray(); }
    private static RawCapture Capture(uint sequence, byte[] payload, byte flags = 0x10)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14))); bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(Main.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26); IPAddress.Parse(Main.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), (ushort)Main.RemotePort); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), (ushort)Main.LocalPort);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence); bytes[46] = 0x50; bytes[47] = flags; payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }
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
            U8(0); I64(item.Key); I32(item.Item); I64(item.Count); U8(item.Container); I32(0); False(); V(0); I64(0); False();
        }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
