using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic incoming TCP controls; no Npcap device, socket, private capture or user files.</summary>
public sealed class NetworkBossConnectionTests
{
    private const ushort ServerPort = 9000;
    private const ushort OtherServerPort = 9001;
    private const ushort GamePort = 50000;
    private const long Now = 1791257500000;
    private const byte Fin = 0x01, Rst = 0x04;
    private static readonly HashSet<int> ServerPorts = [ServerPort, OtherServerPort];
    private static readonly HashSet<int> GamePorts = [GamePort];
    private static readonly MethodInfo FeedMethod = typeof(NetworkLootService)
        .GetMethod("Feed", BindingFlags.Static | BindingFlags.NonPublic)!;
    private sealed record Received(string Stream, byte[] Data, bool Gap);

    private sealed class Fixture
    {
        internal readonly Dictionary<(string, int, int), (TcpReassembler Tcp, GameMessageStream Messages)> Streams = [];
        internal readonly BossRushState Bosses = new();
        internal uint NextSequence = 1000;

        internal void Bind()
        {
            var payload = Join(Frame(Clock()), Frame(BossList(false)));
            Apply(Feed(Streams, Capture(NextSequence, payload)));
            NextSequence += (uint)payload.Length;
            Assert.False(Bosses.Snapshot!.Cached);
        }

        internal void Apply(IEnumerable<Received> batch)
        {
            foreach (var received in batch)
            {
                if (received.Gap) Bosses.Invalidate(received.Stream);
                else Bosses.Accept(received.Stream, received.Data);
            }
        }
    }

    [Theory]
    [InlineData(Fin)]
    [InlineData(Rst)]
    public void EmptyIncomingCloseInvalidatesOnlyItsStreamAndRemovesItsReassembler(byte flags)
    {
        var f = new Fixture();
        f.Bind();
        _ = Feed(f.Streams, Capture(2000, Frame(Clock()), sourcePort: OtherServerPort));
        Assert.Equal(2, f.Streams.Count);

        var closed = Feed(f.Streams, Capture(f.NextSequence, [], flags));
        var gap = Assert.Single(closed);
        Assert.True(gap.Gap);
        Assert.Empty(gap.Data);
        Assert.Equal("192.0.2.1:9000:50000", gap.Stream);
        Assert.Single(f.Streams);
        Assert.Contains(("192.0.2.1", (int)OtherServerPort, (int)GamePort), f.Streams.Keys);
        f.Apply(closed);
        Assert.True(f.Bosses.Snapshot!.Cached);
        Assert.False(f.Bosses.Snapshot.Next!.Spawned);
    }

    [Fact]
    public void ResetDiscardsEvenACompleteBossListInItsPayload()
    {
        var f = new Fixture();
        f.Bind();
        var reset = Feed(f.Streams, Capture(f.NextSequence, Frame(BossList(true)), Rst));
        Assert.True(Assert.Single(reset).Gap);
        Assert.Empty(f.Streams);
        f.Apply(reset);
        Assert.True(f.Bosses.Snapshot!.Cached);
        Assert.False(f.Bosses.Snapshot.Next!.Spawned);
    }

    [Fact]
    public void FinDeliversItsFinalCompleteListBeforeInvalidatingTheResult()
    {
        var f = new Fixture();
        f.Bind();
        var closed = Feed(f.Streams, Capture(f.NextSequence, Frame(BossList(true)), Fin));
        Assert.Equal(2, closed.Count);
        Assert.False(closed[0].Gap);
        Assert.NotNull(BossSpawnMessage.TryParse(closed[0].Data));
        Assert.True(closed[1].Gap);
        Assert.Empty(f.Streams);

        f.Apply(closed.Take(1));
        Assert.True(f.Bosses.Snapshot!.Next!.Spawned);
        Assert.False(f.Bosses.Snapshot.Cached);
        f.Apply(closed.Skip(1));
        Assert.True(f.Bosses.Snapshot.Cached);
    }

    [Theory]
    [InlineData(Fin)]
    [InlineData(Rst)]
    public void UnrelatedStreamClosureDoesNotInvalidateTheConfirmedBossList(byte flags)
    {
        var f = new Fixture();
        f.Bind();
        _ = Feed(f.Streams, Capture(2000, Frame(Clock()), sourcePort: OtherServerPort));
        var closed = Feed(f.Streams, Capture(2011, [], flags, sourcePort: OtherServerPort));
        Assert.True(Assert.Single(closed).Gap);
        f.Apply(closed);
        Assert.False(f.Bosses.Snapshot!.Cached);
        Assert.Single(f.Streams);
        Assert.Contains(("192.0.2.1", (int)ServerPort, (int)GamePort), f.Streams.Keys);
    }

    [Theory]
    [InlineData(Fin, (ushort)9002, GamePort)]
    [InlineData(Rst, (ushort)9002, GamePort)]
    [InlineData(Fin, ServerPort, (ushort)50001)]
    [InlineData(Rst, ServerPort, (ushort)50001)]
    public void CloseOutsideTheExistingPortGuardsIsIgnored(byte flags, ushort sourcePort, ushort destinationPort)
    {
        var f = new Fixture();
        f.Bind();
        var closed = Feed(f.Streams, Capture(f.NextSequence, [], flags, sourcePort, destinationPort));
        Assert.Empty(closed);
        Assert.Single(f.Streams);
        Assert.False(f.Bosses.Snapshot!.Cached);
    }

    [Theory]
    [InlineData(Fin)]
    [InlineData(Rst)]
    public void CloseWithoutAnExistingReassemblerStillReportsTheStreamGap(byte flags)
    {
        var f = new Fixture();
        var closed = Feed(f.Streams, Capture(1000, [], flags));
        Assert.True(Assert.Single(closed).Gap);
        Assert.Empty(f.Streams);
    }

    [Fact]
    public void ReusedTupleStartsWithFreshFramingAndRequiresANewClockAndList()
    {
        var f = new Fixture();
        f.Bind();
        f.Apply(Feed(f.Streams, Capture(f.NextSequence, [], Fin)));
        Assert.True(f.Bosses.Snapshot!.Cached);

        // A new sequence number can be lower than the closed connection's sequence number.
        var clock = Frame(Clock());
        f.Apply(Feed(f.Streams, Capture(10, clock)));
        Assert.True(f.Bosses.Snapshot.Cached); // a clock cannot confirm the old retained list
        f.Apply(Feed(f.Streams, Capture(10 + (uint)clock.Length, Frame(BossList(true)))));
        Assert.True(f.Bosses.Snapshot.Next!.Spawned);
        Assert.False(f.Bosses.Snapshot.Cached);
    }

    private static List<Received> Feed(Dictionary<(string, int, int), (TcpReassembler Tcp, GameMessageStream Messages)> streams, RawCapture raw)
    {
        var batch = Assert.IsAssignableFrom<IEnumerable>(FeedMethod.Invoke(null, [streams, raw, ServerPorts, GamePorts]));
        return batch.Cast<object>().Select(item =>
        {
            var type = item.GetType();
            return new Received((string)type.GetProperty("Stream")!.GetValue(item)!,
                (byte[])type.GetProperty("Data")!.GetValue(item)!, (bool)type.GetProperty("Gap")!.GetValue(item)!);
        }).ToList();
    }

    private static byte[] Clock() => [0, 0x36, .. BitConverter.GetBytes(Now)];
    private static byte[] BossList(bool spawned) =>
        [1, 0x91, 0, 0, .. BitConverter.GetBytes(1110), 1, (byte)(spawned ? 1 : 0), 0x99, 0xe3, 0x06,
            .. (spawned ? new byte[12] : []), (byte)(spawned ? 1 : 0), .. BitConverter.GetBytes(Now + 60000), 0, 0, 0];
    private static byte[] Frame(byte[] message) => [checked((byte)(message.Length + 4)), .. message];
    private static byte[] Join(byte[] first, byte[] second) => [.. first, .. second];

    private static RawCapture Capture(uint sequence, byte[] payload, byte flags = 0, ushort sourcePort = ServerPort, ushort destinationPort = GamePort)
    {
        // Ethernet + IPv4 + TCP, documentation-only addresses (RFC 5737). No checksum validation is requested.
        var bytes = new byte[14 + 20 + 20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800);
        bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14)));
        bytes[22] = 64;
        bytes[23] = 6;
        new byte[] { 192, 0, 2, 1 }.CopyTo(bytes, 26);
        new byte[] { 192, 0, 2, 2 }.CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence);
        bytes[46] = 0x50;
        bytes[47] = (byte)(0x10 | flags);
        payload.CopyTo(bytes, 54);
        return new RawCapture(LinkLayers.Ethernet, new PosixTimeval(1791257500UL, 0UL), bytes, null);
    }
}
