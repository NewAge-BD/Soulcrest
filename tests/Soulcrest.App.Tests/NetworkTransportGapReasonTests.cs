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
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Actual synthetic TCP packets through live dispatch; no native device, OS lookup or user capture.</summary>
[Collection("Settings file")]
public sealed class NetworkTransportGapReasonTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Other = World with { LocalPort = 50001 };
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticProfile");
    private const byte Ack = 0x10, Fin = 0x01, Rst = 0x04;

    [Fact]
    public void MissingSegmentOnlyReportsTcpPendingOverflowOnThe257thFutureSegment()
    {
        using var f = new Fixture();
        f.Bind();
        var before = f.Network.ProgressDiagnostics;
        var packets = f.Network.Packets;
        var clock = Frame(Clock());
        var missingByte = f.Next(World);

        // Leave exactly one byte missing. All later segments form valid, contiguous clock frames,
        // so the overflow is a TCP gap rather than a malformed-frame/resynchronisation failure.
        for (var i = 0; i < 256; i++)
            f.SendAt(World, missingByte + 1 + (uint)(i * clock.Length), clock);

        Assert.True(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Equal(before.ContextResets, f.Network.ProgressDiagnostics.ContextResets);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Empty(f.Gaps);

        f.SendAt(World, missingByte + 1 + (uint)(256 * clock.Length), clock);

        AssertReset(f, before, "tcp-pending-overflow");
        Assert.Equal(packets + 257, f.Network.Packets);
        Assert.Equal(0, f.Network.Resyncs);
        Assert.Equal(World.StreamKey, Assert.Single(f.Gaps));
    }

    [Theory]
    [InlineData(Rst, false, "tcp-reset", 0)]
    [InlineData(Rst, true, "tcp-reset", 0)]
    [InlineData(Fin, false, "tcp-finished", 0)]
    [InlineData(Fin, true, "tcp-finished", 2)]
    public void TcpCloseReportsItsReasonAndFinAloneDeliversTheFinalGain(byte control, bool withPayload,
        string reason, int acceptedPoints)
    {
        using var f = new Fixture();
        f.Bind();
        var before = f.Network.ProgressDiagnostics;
        var souls = f.Progress.Souls("fossa");

        f.Send(World, withPayload ? Frame(Gain()) : [], (byte)(Ack | control));

        AssertReset(f, before, reason);
        Assert.Equal(acceptedPoints, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Equal(acceptedPoints == 0 ? 0 : 1, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(souls + acceptedPoints, f.Progress.Souls("fossa"));
        Assert.Equal(acceptedPoints == 0 ? 0 : 1, f.Progress.Recent().Count);
        Assert.False(f.Streams.ContainsKey(World));
        Assert.Equal(World.StreamKey, Assert.Single(f.Gaps));
        if (acceptedPoints > 0)
        {
            var events = f.Network.ProgressDiagnostics.Events;
            Assert.Equal(NetworkProgressEventKind.Gained, events[^2].Kind);
            Assert.Equal(NetworkProgressEventKind.Reset, events[^1].Kind);
        }
    }

    [Fact]
    public void RemovingTheOwnedTupleReportsOwnershipRemovedWithoutReopeningCapture()
    {
        using var f = new Fixture();
        f.Bind();
        var before = f.Network.ProgressDiagnostics;

        // Same adapter and already-covered endpoint: this path never creates a native handle.
        Assert.True(f.Network.TryRetainCapture(Target(Other)));

        AssertReset(f, before, "ownership-removed");
        Assert.False(f.Streams.ContainsKey(World));
        Assert.Equal(World.StreamKey, Assert.Single(f.Gaps));
    }

    [Theory]
    [InlineData(Fin)]
    [InlineData(Rst)]
    public void ClosingAForeignTupleDoesNotChangeOwnIdentityBaselinesOrResetDiagnostics(byte control)
    {
        using var f = new Fixture();
        f.Bind();
        f.Send(Other, Frame(Clock()));
        f.Gaps.Clear();
        var before = f.Network.ProgressDiagnostics;
        var petAt = f.Network.PetBaselineAt;

        f.Send(Other, [], (byte)(Ack | control));

        AssertOwnUnchanged(f, before, petAt);
        Assert.False(f.Streams.ContainsKey(Other));
        Assert.Equal(Other.StreamKey, Assert.Single(f.Gaps));
        f.Send(World, Frame(Gain()));
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
    }

    [Fact]
    public void ActualPendingOverflowOnAForeignTupleDoesNotInvalidateOwnProgress()
    {
        using var f = new Fixture();
        f.Bind();
        f.Send(Other, Frame(Clock()));
        f.Gaps.Clear();
        var before = f.Network.ProgressDiagnostics;
        var petAt = f.Network.PetBaselineAt;
        var clock = Frame(Clock());
        var missingByte = f.Next(Other);

        for (var i = 0; i < 257; i++)
            f.SendAt(Other, missingByte + 1 + (uint)(i * clock.Length), clock);

        AssertOwnUnchanged(f, before, petAt);
        Assert.Equal(Other.StreamKey, Assert.Single(f.Gaps));
        f.Send(World, Frame(Gain()));
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
    }

    [Fact]
    public void RemovingAForeignTupleKeepsOwnSessionAndContinuesCounting()
    {
        using var f = new Fixture();
        f.Bind();
        f.Send(Other, Frame(Clock()));
        f.Gaps.Clear();
        var before = f.Network.ProgressDiagnostics;
        var petAt = f.Network.PetBaselineAt;

        Assert.True(f.Network.TryRetainCapture(Target(World)));

        AssertOwnUnchanged(f, before, petAt);
        Assert.False(f.Streams.ContainsKey(Other));
        Assert.Equal(Other.StreamKey, Assert.Single(f.Gaps));
        f.Send(World, Frame(Gain()));
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
    }

    private static void AssertReset(Fixture f, NetworkProgressDiagnostics before, string reason)
    {
        var after = f.Network.ProgressDiagnostics;
        Assert.False(after.IdentityBound);
        Assert.Equal(before.ContextResets + 1, after.ContextResets);
        Assert.Equal(reason, after.LastResetReason);
        Assert.NotNull(after.LastResetAt);
        Assert.Equal(reason, after.Events[^1].ResetReason);
        Assert.Equal(NetworkProgressEventKind.Reset, after.Events[^1].Kind);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Equal(f.BaselineAt, f.Progress.NetworkBaselineAt); // retained observation, not live authority
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot("fossa").Freshness);
        Assert.False(f.Network.ProgressSources.IdentityIsLive);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.False(f.Network.ProgressIsLive);
    }

    private static void AssertOwnUnchanged(Fixture f, NetworkProgressDiagnostics before, DateTimeOffset? petAt)
    {
        var after = f.Network.ProgressDiagnostics;
        Assert.True(after.IdentityBound);
        Assert.Equal(before.ContextResets, after.ContextResets);
        Assert.Equal(before.LastResetReason, after.LastResetReason);
        Assert.Equal(before.LastResetAt, after.LastResetAt);
        Assert.Equal(before.IdentityBindings, after.IdentityBindings);
        Assert.Equal(before.AcceptedSoulPoints, after.AcceptedSoulPoints);
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(petAt, f.Network.PetBaselineAt);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.True(f.Streams.ContainsKey(World));
    }

    private static CaptureTarget Target(params CaptureConnection[] connections) => CaptureTarget.FromRows(
        connections.Select(c => c.GamePid).ToHashSet(), connections.Select(c => new TcpTable.Row(
            new IPEndPoint(IPAddress.Parse(c.LocalAddress), c.LocalPort), new IPEndPoint(IPAddress.Parse(c.RemoteAddress), c.RemotePort), c.GamePid, true)).ToArray(),
        _ => "adapter", _ => "Synthetic relay")!;
    private static byte[] Clock() => [0, 0x36, .. BitConverter.GetBytes(1791849600000L)];
    private static byte[] Pets() => [0, 0x90, .. BitConverter.GetBytes(0), 1, .. BitConverter.GetBytes(999), .. BitConverter.GetBytes(1004),
        .. BitConverter.GetBytes(1), 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(3), 0, 0, 0, 0];
    private static byte[] Gain() => [0x0D, 0x90, 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(2)];
    private static byte[] OwnMessage()
    {
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        w.Write((ushort)0x3633); V(w, (uint)Own.EntityKey); w.Write(0);
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes(Own.Name); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)Own.ServerId); w.Write(0); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(Own.CharacterId); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
        return bytes.ToArray();
    }
    private static byte[] Frame(byte[] message)
    {
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        V(w, (uint)(message.Length + 4)); w.Write(message); return bytes.ToArray();
    }
    private static void V(BinaryWriter w, uint value)
    {
        while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; }
        w.Write((byte)value);
    }
    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload, byte flags)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14))); bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence); bytes[46] = 0x50; bytes[47] = flags;
        payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-transport-gap-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<CaptureConnection, uint> _sequences = [];
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public List<string> Gaps { get; } = [];
        public DateTimeOffset? BaselineAt { get; private set; }
        public Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;

        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress, new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            var target = Target(World, Other);
            Field("_target").SetValue(Network, target);
            Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.BossStreamGap += Gaps.Add;
        }

        public void Bind()
        {
            var ownMessage = OwnMessage();
            Assert.Equal(Own, OwnIdentityMessage.TryParse(ownMessage));
            Send(World, [.. Frame(Clock()), .. Frame(ownMessage), .. Frame(Pets()),
                .. Frame([0x09, 0x8D, 0]), .. Frame([0x32, 0x92, 0])]);
            Assert.True(Network.ProgressDiagnostics.IdentityBound);
            Assert.True(Network.ProgressSources.InitialProgressConfirmed);
            Assert.Equal(1, Network.ProgressDiagnostics.IdentityBindings);
            BaselineAt = Progress.NetworkBaselineAt;
            Assert.NotNull(BaselineAt);
            Gaps.Clear();
        }

        public uint Next(CaptureConnection connection) => _sequences.GetValueOrDefault(connection, 1000u);
        public void Send(CaptureConnection connection, byte[] payload, byte flags = Ack)
        {
            var sequence = Next(connection);
            _sequences[connection] = sequence + (uint)payload.Length;
            SendAt(connection, sequence, payload, flags);
        }
        public void SendAt(CaptureConnection connection, uint sequence, byte[] payload, byte flags = Ack) =>
            Network.ProcessCapturedPacket(Capture(connection, sequence, payload, flags));
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }
}
