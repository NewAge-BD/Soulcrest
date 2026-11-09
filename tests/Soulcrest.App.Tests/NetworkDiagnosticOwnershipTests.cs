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

/// <summary>Recorder at the real ownership gate with fake native devices; no capture device is opened.</summary>
[Collection("Settings file")]
public sealed class NetworkDiagnosticOwnershipTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Web = new(42, "192.0.2.2", 50001, "198.51.100.1", 443);
    private static readonly CaptureConnection NewWorld = World with { LocalPort = 50002 };

    [Fact]
    public async Task BidirectionalCaptureDoesNotChangeDecoderOrSoulCountingAndKeepsRetransmissions()
    {
        await using var f = new Fixture();
        Assert.True(f.Recorder.Start());
        var incoming = Capture(World, LoginGain());
        var outgoing = Capture(World, [8, 9], outgoing: true);
        f.Network.ProcessDevicePacket(f.Device, incoming);
        f.Network.ProcessDevicePacket(f.Device, outgoing);
        f.Network.ProcessDevicePacket(f.Device, incoming);
        await f.Recorder.StopAsync();
        Assert.Equal(3, f.Network.Packets);
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Single(f.Network.Recent);
        var packets = await Frames(f.Recorder);
        Assert.Equal(3, packets.Count);
        Assert.Equal(incoming.Data, packets[0]); Assert.Equal(outgoing.Data, packets[1]); Assert.Equal(incoming.Data, packets[2]);
        Assert.Equal(0, f.Calls.Count("Open")); Assert.Equal(0, f.Calls.Count("set_Filter"));
        Assert.Equal(0, f.Calls.Count("StartCapture"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignTupleOnSameEndpointOrRelayPortNeverEntersCapture(bool relay)
    {
        await using var f = new Fixture(relay: relay);
        Assert.True(f.Recorder.Start());
        var current = relay ? World with { LocalAddress = "127.0.0.1", RemoteAddress = "127.0.0.1" } : World;
        var foreign = current with { LocalPort = 50999 };
        f.Network.IncomingOwnershipReader = () => Target(current, Web);
        f.Network.ProcessDevicePacket(f.Device, Capture(foreign, LoginGain()));
        f.Network.ProcessDevicePacket(f.Device, Capture(foreign, [7], outgoing: true));
        await f.WaitProbe();
        await f.Recorder.StopAsync();
        Assert.Empty(await Frames(f.Recorder));
        Assert.Equal(0, f.Network.Messages);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.True(f.Recorder.Snapshot.DroppedPackets > 0);
    }

    [Fact]
    public async Task FirstPacketIsRecordedOnlyAfterFreshNativeOwnershipProof()
    {
        await using var f = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic proof timeout");
            return Target(World, Web, NewWorld);
        };
        Assert.True(f.Recorder.Start());
        var original = Capture(NewWorld, LoginGain());
        original.PacketLength += 10;
        f.Network.ProcessDevicePacket(f.Device, original);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, f.Recorder.Snapshot.Packets);
            Assert.Equal(0, f.Network.Messages);
        }
        finally { release.Set(); }
        await f.WaitProbe();
        await f.Recorder.StopAsync();
        Assert.Single(await Frames(f.Recorder));
        var file = Assert.Single(await NetworkDiagnosticRecorderTests.Package(f.Recorder)).Value;
        Assert.Equal((uint)original.PacketLength, Assert.Single(NetworkDiagnosticRecorderTests.Read(file).Frames).OriginalLength);
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Equal(1, f.Network.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedProofCannotMoveAnEarlierPacketIntoANewDiagnosticSession(bool beforeFirstStart)
    {
        await using var f = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Target(World, Web, NewWorld);
        };
        if (!beforeFirstStart) Assert.True(f.Recorder.Start());
        f.Network.ProcessDevicePacket(f.Device, Capture(NewWorld, LoginGain()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (!beforeFirstStart) await f.Recorder.StopAsync();
            Assert.True(f.Recorder.Start());
        }
        finally { release.Set(); }
        await f.WaitProbe();
        await f.Recorder.StopAsync();
        Assert.Empty(await Frames(f.Recorder));
        Assert.Equal(2, f.Progress.Souls("fossa"));
    }

    [Fact]
    public async Task RemovedTupleIsRejectedEvenWhileItsKernelCoverageRemainsInstalled()
    {
        await using var f = new Fixture();
        Assert.True(f.Recorder.Start());
        f.Network.UpdateCaptureTarget(Target(Web));
        f.Network.IncomingOwnershipReader = () => Target(Web);
        f.Network.ProcessDevicePacket(f.Device, Capture(World, LoginGain()));
        f.Network.ProcessDevicePacket(f.Device, Capture(World, [7], outgoing: true));
        await f.WaitProbe();
        await f.Recorder.StopAsync();
        Assert.Empty(await Frames(f.Recorder));
        Assert.Equal(0, f.Progress.Souls("fossa"));
    }

    [Fact]
    public async Task SourceStopFinishesRecordingAndIgnoresLateNativeProof()
    {
        await using var f = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return Target(NewWorld); };
        Assert.True(f.Recorder.Start());
        f.Network.ProcessDevicePacket(f.Device, Capture(NewWorld, LoginGain()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            f.Network.Stop();
        }
        finally { release.Set(); }
        await f.WaitProbe();
        await f.Recorder.StopAsync();
        Assert.Equal("network-stopped", f.Recorder.Snapshot.StopReason);
        Assert.Empty(await Frames(f.Recorder));
    }

    [Fact]
    public async Task WriterFailureLeavesProductiveTrackingActive()
    {
        await using var f = new Fixture(createFile: _ => throw new IOException("Synthetic write failure"));
        Assert.True(f.Recorder.Start());
        f.Network.ProcessDevicePacket(f.Device, Capture(World, LoginGain()));
        await NetworkDiagnosticRecorderTests.WaitUntil(() => f.Recorder.Snapshot.State == NetworkDiagnosticRecorderState.Failed);
        Assert.True(f.Network.Running);
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.Equal(1, f.Network.Packets);
    }

    [Fact]
    public async Task PublicRecorderCannotStartAnInactiveNetworkSource()
    {
        using var network = new NetworkLootService(new ProgressService());
        Assert.False(network.DiagnosticRecorder.Start());
        Assert.False(network.Running);
        Assert.Equal(NetworkDiagnosticRecorderState.Unavailable, network.DiagnosticRecorder.Snapshot.State);
        await network.DiagnosticRecorder.StopAsync();
    }

    private static async Task<List<byte[]>> Frames(NetworkDiagnosticRecorder recorder) =>
        (await NetworkDiagnosticRecorderTests.Package(recorder)).Values
            .SelectMany(data => NetworkDiagnosticRecorderTests.Read(data).Frames).Select(frame => frame.Data).ToList();

    private static CaptureTarget Target(params CaptureConnection[] connections) => new("adapter", "synthetic-filter",
        connections.Select(connection => connection.RemotePort).ToHashSet(), connections.Select(connection => connection.LocalPort).ToHashSet(),
        "Synthetic source", connections.ToHashSet());

    private static byte[] LoginGain()
    {
        var clock = new byte[10]; BinaryPrimitives.WriteUInt16LittleEndian(clock, 0x3600);
        BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(2), 1791849600000L);
        var gain = new byte[11]; BinaryPrimitives.WriteUInt16LittleEndian(gain, 0x900D);
        gain[2] = 1; BinaryPrimitives.WriteInt32LittleEndian(gain.AsSpan(3), 1004); BinaryPrimitives.WriteInt32LittleEndian(gain.AsSpan(7), 2);
        return [14, .. clock, 15, .. gain];
    }

    private static RawCapture Capture(CaptureConnection connection, byte[] payload, bool outgoing = false)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14)));
        bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(outgoing ? connection.LocalAddress : connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(outgoing ? connection.RemoteAddress : connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)(outgoing ? connection.LocalPort : connection.RemotePort)));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)(outgoing ? connection.RemotePort : connection.LocalPort)));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), 1000);
        bytes[46] = 0x50; bytes[47] = 0x10; payload.CopyTo(bytes, 54);
        return NetworkDiagnosticRecorderTests.Raw(bytes);
    }

    public class DeviceProxy : DispatchProxy
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public int Count(string method) { lock (_calls) return _calls.GetValueOrDefault(method); }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            lock (_calls) _calls[method.Name] = Count(method.Name) + 1;
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-diagnostic-owner-" + Guid.NewGuid().ToString("N"));
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public NetworkDiagnosticRecorder Recorder { get; }
        public ILiveDevice Device { get; }
        public DeviceProxy Calls { get; }
        public Fixture(bool relay = false, Func<string, Stream>? createFile = null)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress);
            var target = Target(relay ? World with { LocalAddress = "127.0.0.1", RemoteAddress = "127.0.0.1" } : World, Web);
            Device = DispatchProxy.Create<ILiveDevice, DeviceProxy>(); Calls = (DeviceProxy)(object)Device;
            Field("_target").SetValue(Network, target); Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            Field("_device").SetValue(Network, Device);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Recorder = new(begin => Network.Running && begin(), Path.Combine(_folder, "captures"), createFile: createFile);
            Field("_diagnosticRecorder").SetValue(Network, Recorder);
            Network.IncomingOwnershipReader = () => target;
            Network.IncomingOwnershipUtcNow = () => _now;
            Network.IncomingOwnershipWait = delay => { _now += delay; return Task.CompletedTask; };
        }
        public Task WaitProbe() => Network.IncomingOwnershipTask.WaitAsync(TimeSpan.FromSeconds(5));
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        public async ValueTask DisposeAsync()
        {
            Network.Dispose();
            await Recorder.StopAsync();
            await Network.DeviceLifecycleTask;
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
    }
}
