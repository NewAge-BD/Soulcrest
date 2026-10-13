using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic PID-owned connections and packets; no device, socket or private capture is opened.</summary>
[Collection("Settings file")]
public sealed class CaptureTargetRetentionTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Web = new(42, "192.0.2.2", 50001, "198.51.100.1", 443);
    private static readonly CaptureConnection AddedWeb = Web with { LocalPort = 50002 };
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic capture profile");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalFilterIgnoresDuplicateAndReorderedRowsAndAdditionalLocalPorts()
    {
        var original = Target(World, Web);
        var duplicate = Target(Web, World, World, Web);
        var extraPort = Target(AddedWeb, Web, World);

        Assert.Equal(original.Filter, duplicate.Filter);
        Assert.Equal(original.Filter, extraPort.Filter);
        Assert.True(NetworkLootService.SameConnection(original, duplicate));
        Assert.False(NetworkLootService.SameConnection(original, extraPort));
        Assert.True(NetworkLootService.CanRetainCapture(original, extraPort, original.RemoteEndpoints));
        Assert.Equal(2, duplicate.Connections!.Count);
        Assert.Equal(3, extraPort.Connections!.Count);
        Assert.Equal(2, extraPort.RemoteEndpoints.Count);
    }

    [Fact]
    public void TargetOwnershipUsesOnlyEstablishedRowsOfTheRequestedGamePids()
    {
        var rows = new[] { Row(World), Row(Web), Row(AddedWeb with { GamePid = 99 }),
            Row(World with { LocalPort = 50003 }) with { Established = false } };
        var target = CaptureTarget.FromRows(new HashSet<int> { 42 }, rows, _ => "adapter", _ => "Synthetic relay")!;

        Assert.True(target.Connections!.SetEquals(new[] { World, Web }));
        Assert.DoesNotContain(50002, target.GamePorts);
        Assert.DoesNotContain(50003, target.GamePorts);
    }

    [Fact]
    public void RelayFilterCoverageIsCanonicalAndUsesTheExactRemoteAddresses()
    {
        var own = new CaptureConnection(42, "127.0.0.1", 50000, "127.0.0.1", 20000);
        var owner = new TcpTable.Row(new IPEndPoint(IPAddress.Loopback, 20000), new IPEndPoint(IPAddress.Any, 0), 99, false);
        var first = CaptureTarget.FromRows(new HashSet<int> { 42 }, new[] { Row(own), owner }, _ => "unused", _ => "Synthetic relay")!;
        var second = CaptureTarget.FromRows(new HashSet<int> { 42 },
            new[] { Row(own with { LocalPort = 50001 }), owner, Row(own), Row(own) }, _ => "unused", _ => "Synthetic relay")!;

        Assert.Equal(CaptureTarget.LoopbackDevice, first.Device);
        Assert.Equal("tcp and ((host 127.0.0.1 and port 20000))", first.Filter);
        Assert.Equal(first.Filter, second.Filter);
        Assert.True(first.RemoteEndpoints.SetEquals(new[] { new CaptureEndpoint("127.0.0.1", 20000) }));
        Assert.True(NetworkLootService.CanRetainCapture(first, second, first.RemoteEndpoints));
    }

    [Fact]
    public void AdditionalWebPortKeepsTheBoundWorldAndNeverTouchesTheCaptureHandle()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        var before = fixture.Progress.Souls("fossa");
        var generation = fixture.Generation;
        var resets = fixture.SessionResets;
        var context = fixture.Streams[World];

        Assert.True(fixture.Network.TryRetainCapture(Target(World, Web, AddedWeb)));
        fixture.Send(World, Frame(Gain()));

        Assert.Equal(before + 2, fixture.Progress.Souls("fossa"));
        Assert.Equal(Own.Name, fixture.Network.Character);
        Assert.NotNull(fixture.Network.PetBaselineAt);
        Assert.Equal(generation, fixture.Generation);
        Assert.Equal(resets, fixture.SessionResets);
        Assert.Same(context.Tcp, fixture.Streams[World].Tcp);
        Assert.Same(context.Messages, fixture.Streams[World].Messages);
        Assert.Empty(fixture.Gaps);
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public void RemovedWebConnectionInvalidatesOnlyThatStream()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        fixture.Send(Web, Frame(Clock()));
        fixture.Gaps.Clear();
        var baseline = fixture.Network.PetBaselineAt;

        Assert.True(fixture.Network.TryRetainCapture(Target(World)));

        Assert.Equal(new[] { Web.StreamKey }, fixture.Gaps);
        Assert.False(fixture.Streams.ContainsKey(Web));
        Assert.True(fixture.Streams.ContainsKey(World));
        Assert.Equal(Own.Name, fixture.Network.Character);
        Assert.Equal(baseline, fixture.Network.PetBaselineAt);
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public void RemovedWorldConnectionDropsItsIdentityAndBaselineButRetainsSavedSouls()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        var before = fixture.Progress.Souls("fossa");

        Assert.True(fixture.Network.TryRetainCapture(Target(Web)));

        Assert.Equal(new[] { World.StreamKey }, fixture.Gaps);
        Assert.Null(fixture.Network.Character);
        Assert.Null(fixture.Network.PetBaselineAt);
        Assert.False(fixture.Network.ProgressIsLive);
        Assert.False(fixture.Streams.ContainsKey(World));
        Assert.Equal(before, fixture.Progress.Souls("fossa"));
        fixture.Send(World, Frame(Gain()));
        Assert.Equal(before, fixture.Progress.Souls("fossa"));
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public void InstalledCoverageSurvivesShrinkAndCanCoverAReaddedRemoteWithANewLocalPort()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        var coverage = fixture.InstalledCoverage;

        Assert.True(fixture.Network.TryRetainCapture(Target(World)));
        Assert.True(fixture.Network.TryRetainCapture(Target(World, AddedWeb)));

        Assert.Same(coverage, fixture.InstalledCoverage);
        Assert.Equal(2, coverage.Count);
        Assert.Equal(Own.Name, fixture.Network.Character);
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public void NewRemoteEndpointExpandsCoverageButAdapterChangeRequiresAReplacement()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        var expanded = Target(World, Web with { RemoteAddress = "203.0.113.1" });

        Assert.True(fixture.Network.TryRetainCapture(expanded));
        Assert.False(fixture.Network.TryRetainCapture(Target(World, Web) with { Device = "other-adapter" }));
        Assert.Same(expanded, fixture.CurrentTarget);
        Assert.Equal(3, fixture.InstalledCoverage.Count);
        Assert.Equal(Own.Name, fixture.Network.Character);
        Assert.Equal(1, fixture.HandleCalls.Count("set_Filter"));
        foreach (var method in new[] { "Open", "Close", "StartCapture", "StopCapture" })
            Assert.Equal(0, fixture.HandleCalls.Count(method));
    }

    [Theory]
    [InlineData("remote-address")]
    [InlineData("local-address")]
    [InlineData("remote-port")]
    [InlineData("cross-product")]
    public void LiveFeedRejectsAnythingOutsideTheExactCurrentOwnerTuples(string mismatch)
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        var before = fixture.Progress.Souls("fossa");
        var messages = fixture.Network.Messages;
        var foreign = mismatch switch
        {
            "remote-address" => World with { RemoteAddress = "203.0.113.1" },
            "local-address" => World with { LocalAddress = "192.0.2.3" },
            "remote-port" => World with { RemotePort = 443 },
            _ => World with { LocalPort = Web.LocalPort },
        };

        fixture.Send(foreign, [.. Frame(Clock()), .. Frame(Gain())]);

        Assert.Equal(messages, fixture.Network.Messages);
        Assert.Equal(before, fixture.Progress.Souls("fossa"));
        Assert.False(fixture.Streams.ContainsKey(foreign));
        Assert.Empty(fixture.Gaps);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("local-port")]
    public void ReplacedWorldConnectionNeverInheritsTheOldStreamOrIdentity(string replacementKind)
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        var before = fixture.Progress.Souls("fossa");
        var replacement = replacementKind == "pid" ? World with { GamePid = 43 } : World with { LocalPort = 50004 };

        Assert.True(fixture.Network.TryRetainCapture(Target(replacement, Web)));
        Assert.Equal(new[] { World.StreamKey }, fixture.Gaps);
        Assert.Null(fixture.Network.Character);
        Assert.Null(fixture.Network.PetBaselineAt);
        Assert.False(fixture.Streams.ContainsKey(World));
        fixture.Send(replacement, [.. Frame(Clock()), .. Frame(Gain())]);
        Assert.True(fixture.Streams.ContainsKey(replacement));
        Assert.Null(fixture.Network.Character);
        Assert.Equal(before, fixture.Progress.Souls("fossa"));
        Assert.NotEqual(World.StreamKey, replacement.StreamKey);
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public void PartialSnapshotAndRetransmissionSurviveAnOwnershipRefreshExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        var frame = Frame(PetSnapshot());
        fixture.Send(World, frame[..10]);
        var context = fixture.Streams[World];

        Assert.True(fixture.Network.TryRetainCapture(Target(World, Web, AddedWeb)));
        fixture.Send(World, frame[10..]);

        Assert.Same(context.Messages, fixture.Streams[World].Messages);
        Assert.NotNull(fixture.Network.PetBaselineAt);
        Assert.Equal(3, fixture.Progress.AvailableSouls("fossa"));
        var before = fixture.Progress.Souls("fossa");
        var sequence = fixture.NextSequence(World);
        var gain = Frame(Gain());
        fixture.Send(World, gain);
        fixture.Network.ProcessCapturedPacket(Capture(World, sequence, gain));
        Assert.Equal(before + 2, fixture.Progress.Souls("fossa"));
        Assert.Single(fixture.Network.Recent);
        Assert.Empty(fixture.Gaps);
        AssertHandleUntouched(fixture);
    }

    [Fact]
    public async Task TargetRefreshCannotInterleaveBetweenMessagesOfOneCapturedPacket()
    {
        using var fixture = new Fixture();
        fixture.BeginWorld();
        fixture.SeedBaseline();
        var before = fixture.Progress.Souls("fossa");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempting = new ManualResetEventSlim();
        var notices = 0;
        fixture.Network.SoulGained += _ =>
        {
            if (Interlocked.Increment(ref notices) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic dispatch was not released.");
        };
        var dispatch = Task.Run(() => fixture.Send(World, [.. Frame(Gain()), .. Frame(Gain())]));
        Task<bool>? refresh = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            refresh = Task.Run(() => { attempting.Set(); return fixture.Network.TryRetainCapture(Target(Web)); });
            Assert.True(attempting.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(refresh.IsCompleted);
        }
        finally { release.Set(); }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(refresh);
        Assert.True(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(before + 4, fixture.Progress.Souls("fossa"));
        Assert.Null(fixture.Network.Character);
        Assert.Null(fixture.Network.PetBaselineAt);
    }

    private static void AssertHandleUntouched(Fixture fixture)
    {
        Assert.Equal(0, fixture.HandleCalls.Count("Open"));
        Assert.Equal(0, fixture.HandleCalls.Count("Close"));
        Assert.Equal(0, fixture.HandleCalls.Count("StartCapture"));
        Assert.Equal(0, fixture.HandleCalls.Count("StopCapture"));
        Assert.Equal(0, fixture.HandleCalls.Count("set_Filter"));
    }

    private static CaptureTarget Target(params CaptureConnection[] connections) => CaptureTarget.FromRows(
        connections.Select(connection => connection.GamePid).ToHashSet(), connections.Select(Row).ToArray(), _ => "adapter", _ => "Synthetic relay")!;
    private static TcpTable.Row Row(CaptureConnection connection) => new(new IPEndPoint(IPAddress.Parse(connection.LocalAddress), connection.LocalPort),
        new IPEndPoint(IPAddress.Parse(connection.RemoteAddress), connection.RemotePort), connection.GamePid, true);

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] Clock() => Packet(0x3600, writer => writer.Write(1791849600000L));
    private static byte[] PetSnapshot() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
        writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
    });
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });
    private static byte[] Frame(byte[] message) => [checked((byte)(message.Length + 4)), .. message];

    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload)
    {
        var bytes = new byte[14 + 20 + 20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800);
        bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14)));
        bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence);
        bytes[46] = 0x50; bytes[47] = 0x10;
        payload.CopyTo(bytes, 54);
        return new RawCapture(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }

    public class CountingDeviceProxy : DispatchProxy
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public int Count(string method) => _calls.GetValueOrDefault(method);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            _calls[method.Name] = Count(method.Name) + 1;
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-capture-retain-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<CaptureConnection, uint> _sequences = [];
        public NetworkLootService Network { get; }
        public ProgressService Progress { get; }
        public CountingDeviceProxy HandleCalls { get; }
        public List<string> Gaps { get; } = [];
        public int SessionResets { get; private set; }
        public long Generation => (long)Field("_generation").GetValue(Network)!;
        public CaptureTarget CurrentTarget => (CaptureTarget)Field("_target").GetValue(Network)!;
        public IReadOnlySet<CaptureEndpoint> InstalledCoverage => (IReadOnlySet<CaptureEndpoint>)Field("_installedCoverage").GetValue(Network)!;
        public Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress);
            var target = Target(World, Web);
            var handle = DispatchProxy.Create<ILiveDevice, CountingDeviceProxy>();
            HandleCalls = (CountingDeviceProxy)(object)handle;
            Field("_target").SetValue(Network, target);
            Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            Field("_device").SetValue(Network, handle);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.BossStreamGap += Gaps.Add;
            Network.BossSessionReset += () => SessionResets++;
        }
        public void BeginWorld()
        {
            Send(World, Frame(Clock()));
            Network.ObserveIdentity(World.StreamKey, Own, At);
            Gaps.Clear();
        }
        public void SeedBaseline() => Send(World, Frame(PetSnapshot()));
        public uint NextSequence(CaptureConnection connection) => _sequences.GetValueOrDefault(connection, 1000u);
        public void Send(CaptureConnection connection, byte[] payload)
        {
            var sequence = NextSequence(connection);
            Network.ProcessCapturedPacket(Capture(connection, sequence, payload));
            _sequences[connection] = sequence + (uint)payload.Length;
        }
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose()
        {
            Network.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
