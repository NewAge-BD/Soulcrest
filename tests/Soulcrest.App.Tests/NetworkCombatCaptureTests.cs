using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic transport only; no game, native capture device or existing profile.</summary>
[Collection("Settings file")]
public sealed class NetworkCombatCaptureTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1791849600);
    private static byte[] Clock() => [0x00, 0x36, .. BitConverter.GetBytes(1791849600000L)];
    private static byte[] Gain() => [0x0D, 0x90, 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(2)];

    [Fact]
    public void LiveInputPreservesCaptureClockRegressionAndOrdersFinBeforeNewTransport()
    {
        using var fixture = new Fixture();
        long ticks = 100;
        fixture.Network.CombatElapsedTicks = () => ticks;
        using var subscription = fixture.Network.SubscribeCombat();
        Drain(subscription);
        var first = Frame(Clock());
        fixture.Network.ProcessCapturedPacket(Packet(1000, first, At));
        var opening = Drain(subscription);
        var start = Assert.Single(opening, item => item.Kind == CombatCaptureKind.Gap);
        var message = Assert.Single(opening, item => item.Kind == CombatCaptureKind.Message);
        Assert.Equal("tcp-start", start.Reason);
        Assert.Equal(start.Generation + 1, message.Generation);
        Assert.Equal(start.StreamToken, message.StreamToken);
        Assert.Equal(At, message.CapturedAtUtc);
        Assert.Equal(100, message.ReceivedElapsedTicks);
        Assert.DoesNotContain("192.0.2", message.StreamToken!);

        ticks = 200;
        fixture.Network.ProcessCapturedPacket(Packet(1000 + (uint)first.Length, Frame(Clock()), At.AddSeconds(-1), 0x11));
        var finishing = Drain(subscription);
        Assert.Equal([CombatCaptureKind.Message, CombatCaptureKind.Gap], finishing.Select(item => item.Kind));
        Assert.All(finishing, item => Assert.Equal(At.AddSeconds(-1), item.CapturedAtUtc));
        Assert.Equal("tcp-finished", finishing[1].Reason);
        Assert.True(finishing[0].Sequence < finishing[1].Sequence);
        Assert.Equal(200, finishing[0].ReceivedElapsedTicks);

        fixture.Network.ProcessCapturedPacket(Packet(9000, Frame(Clock()), At.AddSeconds(1)));
        var next = Assert.Single(Drain(subscription), item => item.Kind == CombatCaptureKind.Message);
        Assert.NotEqual(message.StreamToken, next.StreamToken);
    }

    [Fact]
    public void OverflowBarrierIsReadableWithoutAnyFurtherInputAndHasUniqueSequence()
    {
        var feed = new NetworkCombatFeed { ElapsedTicks = () => 123 };
        using var subscription = feed.SubscribeCombat(capacity: 2, maxBytes: 4);
        Drain(subscription);
        feed.Message("private-endpoint", new byte[] { 1 }, At);
        feed.Message("private-endpoint", new byte[] { 2 }, At);
        feed.Message("private-endpoint", new byte[] { 3 }, At);

        var queued = Drain(subscription);
        Assert.Equal([CombatCaptureKind.QueueOverflow, CombatCaptureKind.Message], queued.Select(item => item.Kind));
        Assert.Equal(2, queued[0].DroppedRecords);
        Assert.Null(queued[0].StreamToken);
        Assert.True(queued[0].Sequence < queued[1].Sequence);
        Assert.Equal(new byte[] { 3 }, queued[1].Message.ToArray());

        feed.Message("private-endpoint", new byte[5], At);
        var oversized = Assert.Single(Drain(subscription));
        Assert.Equal(CombatCaptureKind.QueueOverflow, oversized.Kind);
        Assert.Equal(1, oversized.DroppedRecords);
    }

    [Fact]
    public void FullQueueStillReportsResetBeforeNextEpochAndPreservesAccumulatedLoss()
    {
        var feed = new NetworkCombatFeed();
        using var subscription = feed.SubscribeCombat(capacity: 2);
        Drain(subscription);
        feed.Message("stream", new byte[] { 1 }, At);
        feed.Message("stream", new byte[] { 2 }, At);
        feed.Reset("capture-closed");
        var reset = Drain(subscription);
        Assert.Equal([CombatCaptureKind.QueueOverflow, CombatCaptureKind.SessionReset], reset.Select(item => item.Kind));
        Assert.Null(reset[1].CapturedAtUtc);
        feed.Message("stream", new byte[] { 3 }, At);
        Assert.True(Assert.Single(Drain(subscription)).Sequence > reset[1].Sequence);
    }

    [Fact]
    public async Task AwaitingConsumerCanCancelAndDisposalWakesItWithoutRunningOnCaptureThread()
    {
        var feed = new NetworkCombatFeed();
        var subscription = feed.SubscribeCombat();
        Drain(subscription);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = subscription.ReadAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
        var finalRead = subscription.ReadAsync().AsTask();
        subscription.Dispose();
        Assert.Null(await finalRead.WaitAsync(TimeSpan.FromSeconds(2)));
        feed.Message("stream", new byte[] { 1 }, At);
        Assert.False(subscription.TryRead(out _));
    }

    [Fact]
    public async Task StopLeavesSubscriptionOpenButSourceDisposalCompletesWaitingConsumer()
    {
        using var fixture = new Fixture();
        using var subscription = fixture.Network.SubscribeCombat();
        Drain(subscription);
        fixture.Network.Stop();
        Assert.Equal("capture-stopped", Assert.Single(Drain(subscription)).Reason);
        var waiting = subscription.ReadAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        fixture.Network.Dispose();
        // Stop's final boundary can wake an already waiting consumer before disposal wins.
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await subscription.ReadAsync());
        Assert.Throws<ObjectDisposedException>(() => fixture.Network.SubscribeCombat());
        // A delayed lifecycle pump or an explicit late Start cannot resurrect this source.
        fixture.Network.Start();
        Assert.False(fixture.Network.Running);
    }

    [Fact]
    public void ProgressPauseLeavesCombatAndBossDispatchRunningAndRequiresFreshProgressAfterResume()
    {
        using var fixture = new Fixture();
        using var subscription = fixture.Network.SubscribeCombat();
        var bossMessages = 0;
        fixture.Network.BossMessageReceived += (_, _) => bossMessages++;
        fixture.Network.SetProgressProcessingEnabled(false);
        Assert.False(fixture.Network.ProgressSources.PetCanCount);
        var initial = Frame(Clock());
        var gain = Frame(Gain());
        fixture.Network.ProcessCapturedPacket(Packet(1000, [.. initial, .. gain], At));
        Assert.Equal(0, fixture.Progress.Souls("fossa"));
        Assert.Equal(1, bossMessages);
        Assert.Equal(2, Drain(subscription).Count(item => item.Kind == CombatCaptureKind.Message));
        Assert.True(fixture.Network.Running);

        fixture.Network.SetProgressProcessingEnabled(true);
        Assert.Null(fixture.Network.PetBaselineAt);
        Assert.Null(fixture.Network.QuestBaselineAt);
        Assert.True(fixture.Network.ProgressSources.PetCanCount);
        fixture.Network.ProcessCapturedPacket(Packet(1000 + (uint)initial.Length + (uint)gain.Length, gain, At.AddSeconds(1)));
        Assert.Equal(2, fixture.Progress.Souls("fossa"));
        Assert.DoesNotContain(Drain(subscription), item => item.Kind == CombatCaptureKind.SessionReset);
    }

    [Fact]
    public void OfflineReplayUsesSameReassemblyDropsRetransmitAndRetainsEqualRealMessages()
    {
        var payload = Frame(Clock());
        var packets = new[]
        {
            Packet(1000, payload, At), Packet(1000, payload, At.AddSeconds(1)),
            Packet(1000 + (uint)payload.Length, payload, At.AddSeconds(-1)),
            Packet(1000 + (uint)payload.Length * 2, [], At.AddSeconds(2), 0x14)
        };
        long elapsed = 0;
        var replay = NetworkLootService.ReadCombatPackets(packets, new HashSet<int> { World.RemotePort }, () => ++elapsed).ToArray();
        var messages = replay.Where(item => item.Kind == CombatCaptureKind.Message).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0].Message.ToArray(), messages[1].Message.ToArray());
        Assert.Equal(At.AddSeconds(-1), messages[1].CapturedAtUtc);
        Assert.True(messages[1].ReceivedElapsedTicks > messages[0].ReceivedElapsedTicks);
        Assert.Contains(replay, item => item.Reason == "tcp-reset");
        Assert.Equal("replay-completed", replay[^1].Reason);
        Assert.Equal(replay.Select(item => item.Sequence).Distinct().Count(), replay.Length);
    }

    [Theory]
    [InlineData(GetPacketStatus.Error)]
    [InlineData(GetPacketStatus.ReadTimeout)]
    public void OfflineReadFailureCannotMasqueradeAsSuccessfulEmptyCapture(GetPacketStatus status)
    {
        Assert.True(NetworkLootService.OfflinePacketAvailable(GetPacketStatus.PacketRead));
        Assert.False(NetworkLootService.OfflinePacketAvailable(GetPacketStatus.NoRemainingPackets));
        Assert.Throws<InvalidDataException>(() => NetworkLootService.OfflinePacketAvailable(status));
    }

    private static List<CombatCaptureEnvelope> Drain(NetworkCombatSubscription subscription)
    {
        var items = new List<CombatCaptureEnvelope>();
        while (subscription.TryRead(out var item)) items.Add(item!);
        return items;
    }

    private static byte[] Frame(byte[] message)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        var value = (uint)message.Length + 4;
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
        writer.Write(message);
        return bytes.ToArray();
    }

    private static RawCapture Packet(uint sequence, byte[] payload, DateTimeOffset at, byte flags = 0x10)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14))); bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(World.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(World.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), (ushort)World.RemotePort);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), (ushort)World.LocalPort);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence); bytes[46] = 0x50; bytes[47] = flags;
        payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval((ulong)at.ToUnixTimeSeconds(), 0UL), bytes, null);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-combat-capture-" + Guid.NewGuid().ToString("N"));
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress);
            var target = new CaptureTarget("synthetic", "", new HashSet<int> { World.RemotePort }, new HashSet<int> { World.LocalPort }, "synthetic")
                { Connections = new HashSet<CaptureConnection> { World } };
            typeof(NetworkLootService).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Network, target);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }
}
