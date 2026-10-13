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

/// <summary>Only synthetic native snapshots, capture handles and RAM packets. No OS/Pcap lookup.</summary>
[Collection("Settings file")]
public sealed class NetworkIncomingOwnershipTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Web = new(42, "192.0.2.2", 50001, "198.51.100.1", 443);
    private static readonly CaptureConnection NewWorld = World with { LocalPort = 50002 };
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticAlpha");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactFreshNativeProofPreservesFirstLoginAndCountsEachPacketOnce()
    {
        using var f = new Fixture();
        var nativeOutsideLocks = false;
        f.Network.IncomingOwnershipReader = () =>
        {
            nativeOutsideLocks = !Monitor.IsEntered(f.Gate) && !Monitor.IsEntered(f.ConnectionGate);
            return Target(World, Web, NewWorld);
        };
        f.Send(NewWorld, Login(gain: true));
        await f.Wait();

        Assert.True(nativeOutsideLocks);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(5, f.Progress.AvailableSouls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.Equal(1, f.Network.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
        f.AssertUntouched();
    }

    [Fact]
    public async Task FragmentedFirstCandidateSurvivesOrderedQuarantineAndRetransmission()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); };
        var payload = Login(gain: true);
        var split = Frame(Clock()).Length + 20;
        f.Send(NewWorld, payload[..split]);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var tailSequence = f.Next(NewWorld);
        f.Send(NewWorld, payload[split..]);
        Assert.Null(f.Network.Character);
        Assert.Equal(2, f.Network.IncomingOwnershipQueue.Packets);
        release.Set();
        await f.Wait();
        f.Network.ProcessDevicePacket(f.Device, Capture(NewWorld, tailSequence, payload[split..]));
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(5, f.Progress.AvailableSouls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.Equal(3, f.Network.Packets);
        f.AssertUntouched();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("error")]
    [InlineData("unexpected-error")]
    [InlineData("other-device")]
    [InlineData("wrong-local")]
    [InlineData("wrong-pid")]
    public async Task UnprovedLookupCannotDecodeNewFlowOrRevokeExistingAuthority(string kind)
    {
        using var f = new Fixture();
        f.BeginWorld();
        var baseline = f.Network.PetBaselineAt;
        var before = f.Progress.Souls("fossa");
        var original = f.CurrentTarget;
        f.Network.IncomingOwnershipReader = () => kind switch
        {
            "null" => null,
            "error" => throw new InvalidOperationException("Synthetic native lookup failure"),
            "unexpected-error" => throw new IOException("Synthetic unexpected lookup failure"),
            "other-device" => Target(NewWorld) with { Device = "other-adapter" },
            "wrong-local" => Target(NewWorld with { LocalAddress = "192.0.2.3" }),
            // FromRows never accepts a foreign PID row as a game-owned connection.
            "wrong-pid" => CaptureTarget.FromRows(new HashSet<int> { 42 }, [Row(World), Row(NewWorld with { GamePid = 99 })], _ => "adapter", _ => "Synthetic"),
            _ => Target(World, Web)
        };
        f.Send(NewWorld, Login(gain: true));
        await f.Wait();
        Assert.Same(original, f.CurrentTarget);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(baseline, f.Network.PetBaselineAt);
        Assert.Equal(before, f.Progress.Souls("fossa"));
        Assert.False(f.Streams.ContainsKey(NewWorld));
        Assert.InRange(f.Network.IncomingOwnershipQueue.Probes, 1L, 3L);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        f.AssertUntouched();
    }

    [Fact]
    public async Task ProvenSnapshotInvalidatesRemovedScopeBeforeDispatchAndRetainsSurvivorBuffer()
    {
        using var f = new Fixture();
        f.BeginWorld();
        f.SendSynthetic(Web, [.. Frame(Clock()), .. Frame(PetSnapshot())[..5]]);
        var survivor = f.Streams[Web];
        var generation = f.Generation;
        f.Network.IncomingOwnershipReader = () => Target(Web, NewWorld);
        f.Send(NewWorld, [.. Frame(Clock()), .. Frame(Gain())]);
        await f.Wait();
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Contains(World.StreamKey, f.Gaps);
        Assert.False(f.Streams.ContainsKey(World));
        Assert.Same(survivor.Messages, f.Streams[Web].Messages);
        Assert.Same(survivor.Tcp, f.Streams[Web].Tcp);
        Assert.Equal(generation, f.Generation);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Network.Recent);
        f.AssertUntouched();
    }

    [Fact]
    public async Task PeriodicRefreshCannotBypassPendingFirstPacketBarrier()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); };
        var login = Login(gain: true);
        f.Send(NewWorld, login[..20]);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        f.Network.UpdateCaptureTarget(Target(World, Web, NewWorld));
        f.Send(NewWorld, login[20..]);
        Assert.Equal(2, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Null(f.Network.Character);
        release.Set();
        await f.Wait();
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(5, f.Progress.AvailableSouls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.Equal(2, f.Network.Packets);
        f.AssertUntouched();
    }

    [Fact]
    public async Task OlderPeriodicReadCannotRemoveTheConnectionProvenByNewerProbe()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.PeriodicOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web); };
        var periodic = Task.Run(f.Network.CheckConnectionCore);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld);
        f.Send(NewWorld, Login());
        await f.Wait();
        var accepted = f.CurrentTarget;
        release.Set();
        await periodic.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(accepted, f.CurrentTarget);
        Assert.NotNull(f.Network.PetBaselineAt);
        f.AssertUntouched();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerPeriodicObservationSupersedesProbeEvenWhenItIsASameTargetNoOp(bool newTuple)
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        f.Network.IncomingOwnershipReader = () =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); }
            return newTuple ? Target(World, Web, NewWorld) : Target(World, Web);
        };
        f.Send(NewWorld, Login());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var outside = false;
        f.Network.PeriodicOwnershipReader = () =>
        {
            outside = !Monitor.IsEntered(f.Gate) && !Monitor.IsEntered(f.ConnectionGate);
            return newTuple ? Target(World, Web, NewWorld) : Target(World, Web);
        };
        f.Network.CheckConnectionCore();
        release.Set();
        await f.Wait();
        Assert.True(outside);
        if (newTuple) Assert.NotNull(f.Network.PetBaselineAt);
        else
        {
            Assert.Null(f.Network.Character);
            Assert.False(f.CurrentTarget!.Connections!.Contains(NewWorld));
            Assert.False(f.Streams.ContainsKey(NewWorld));
        }
        f.AssertUntouched();
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("reset")]
    [InlineData("expire")]
    [InlineData("backwards")]
    public async Task DelayedNativeResultCannotReviveDiscardedOrOldEpochPackets(string kind)
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); };
        f.Send(NewWorld, Login());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        if (kind == "stop") f.Network.Stop();
        else if (kind == "reset") f.Network.ResetProgressContext();
        else f.Advance(kind == "expire" ? TimeSpan.FromMilliseconds(251) : TimeSpan.FromMilliseconds(-1));
        release.Set();
        await f.Wait();
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.Streams.ContainsKey(NewWorld));
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Bytes);
        if (kind != "stop") f.AssertUntouched();
    }

    [Fact]
    public async Task CooldownRetainsFirstPacketAndClockRollbackCannotCreateAnUnboundedDelay()
    {
        using var f = new Fixture();
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld);
        f.Send(NewWorld, Frame(Clock()));
        await f.Wait();
        var second = NewWorld with { LocalPort = 50003 };
        var waiting = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Network.IncomingOwnershipWait = delay => { waiting.TrySetResult(delay); return release.Task; };
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld, second);
        f.Send(second, Login());
        var delay = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Null(f.Network.Character);
        f.Advance(delay);
        release.SetResult();
        await f.Wait();
        Assert.NotNull(f.Network.PetBaselineAt);
        // The previous UTC observation is now in the future. This new packet gets a bounded fresh probe.
        f.Advance(TimeSpan.FromHours(-1));
        var third = NewWorld with { LocalPort = 50004 };
        f.Network.IncomingOwnershipWait = delay => { Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromMilliseconds(100)); f.Advance(delay); return Task.CompletedTask; };
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld, second, third);
        f.Send(third, Frame(Clock()));
        await f.Wait();
        Assert.True(f.Streams.ContainsKey(third));
        f.AssertUntouched();
    }

    [Theory]
    [InlineData("flows")]
    [InlineData("packets")]
    [InlineData("bytes")]
    public async Task FloodIsBoundedWhileNativeLookupIsBlockedAndCannotDecodeBeforeProof(string kind)
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); };
        f.Send(NewWorld, Login());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var total = kind == "bytes" ? 20 : 160;
        for (var index = 0; index < total; index++)
        {
            var connection = kind == "flows" ? NewWorld with { LocalPort = 51000 + index } : NewWorld;
            f.Send(connection, kind == "bytes" ? new byte[60000] : [0]);
            var queue = f.Network.IncomingOwnershipQueue;
            Assert.InRange(queue.Flows, 0, 16);
            Assert.InRange(queue.Packets, 0, 128);
            Assert.InRange(queue.Bytes, 0, 512 * 1024);
        }
        Assert.Null(f.Network.Character);
        Assert.Equal(0, f.Network.Messages);
        Assert.Equal(1, f.Network.IncomingOwnershipQueue.Probes);
        Assert.True(f.Network.IncomingOwnershipQueue.DroppedPackets > 0);
        f.Advance(TimeSpan.FromMilliseconds(251));
        release.Set();
        await f.Wait();
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Null(f.Network.Character);
        f.AssertUntouched();
    }

    [Fact]
    public async Task ReentrantPacketDuringDrainRemainsBehindTheFirstFragment()
    {
        using var f = new Fixture();
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld);
        var payload = Login(gain: true);
        var split = Frame(Clock()).Length + 20;
        var once = false;
        f.Network.BossMessageReceived += (_, _) =>
        {
            if (once) return;
            once = true;
            f.Send(NewWorld, payload[split..]);
        };
        f.Send(NewWorld, payload[..split]);
        await f.Wait();
        Assert.True(once);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(5, f.Progress.AvailableSouls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.Equal(2, f.Network.Packets);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("remove-target")]
    public async Task ReentrantLifecycleInvalidatesImmediatelyAndClosesOutsideBothDispatchLocks(string kind)
    {
        using var f = new Fixture();
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld);
        f.Network.OwnCharacterEntered += _ =>
        {
            if (kind == "stop") f.Network.Stop();
            else f.Network.UpdateCaptureTarget(null);
        };
        f.Send(NewWorld, Login(gain: true));
        await f.Wait();
        await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Empty(f.Network.Recent);
        Assert.False(f.Calls.StopHeldGate);
        Assert.Equal(1, f.Calls.Count("StopCapture"));
        Assert.Equal(1, f.Calls.Count("Close"));
        Assert.Equal(0, f.Calls.Count("Open"));
    }

    [Fact]
    public async Task ReentrantOverflowAbortsRemainingMessagesFromRemovedTransport()
    {
        using var f = new Fixture();
        f.Network.IncomingOwnershipReader = () => Target(World, Web, NewWorld);
        var overflowed = false;
        f.Network.OwnCharacterEntered += _ =>
        {
            if (overflowed) return;
            overflowed = true;
            for (var index = 0; index < 129; index++) f.Send(NewWorld, [0]);
        };
        f.Send(NewWorld, Login(gain: true));
        await f.Wait();
        Assert.True(overflowed);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Empty(f.Network.Recent);
        Assert.Equal(0, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
    }

    [Fact]
    public async Task ThrowingSubscriberInvalidatesOwnedQueuedTailRatherThanKeepingFreshAuthority()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Network.IncomingOwnershipReader = () => { entered.Set(); release.Wait(); return Target(World, Web, NewWorld); };
        f.Network.SoulGained += _ => throw new InvalidOperationException("Synthetic subscriber failure");
        f.Send(NewWorld, Login(gain: true));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        f.Send(NewWorld, Frame(Gain()));
        release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Wait());
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Equal(1, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
    }

    [Fact]
    public void KnownFlowNeverPerformsEarlyNativeLookupAndFinalFinPayloadIsCountedBeforeGap()
    {
        using var f = new Fixture();
        f.BeginWorld();
        f.Network.IncomingOwnershipReader = () => throw new InvalidOperationException("Unexpected lookup");
        f.Send(World, Frame(Gain()), flags: 0x11);
        Assert.Single(f.Network.Recent);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Contains(World.StreamKey, f.Gaps);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Probes);
    }

    [Fact]
    public async Task StopFromCloseNotificationCannotReopenAnAdapterReplacement()
    {
        using var f = new Fixture();
        f.BeginWorld();
        var stopped = false;
        f.Network.BossSessionReset += () =>
        {
            if (stopped) return;
            stopped = true;
            f.Network.Stop();
        };
        f.Network.UpdateCaptureTarget(Target(World, Web) with { Device = "other-adapter" });
        await f.Network.DeviceLifecycleTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped);
        Assert.False(f.Network.Running);
        Assert.Null(f.CurrentTarget);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Calls.Count("Open"));
        Assert.Equal(0, f.Calls.Count("StartCapture"));
        Assert.Equal(1, f.Calls.Count("StopCapture"));
        Assert.Equal(1, f.Calls.Count("Close"));
        Assert.False(f.Calls.StopHeldGate);
    }

    [Fact]
    public async Task OutsideInstalledCoverageAndWrongDeviceCannotStartProbeOrCreateStreams()
    {
        using var f = new Fixture();
        var reads = 0;
        f.Network.IncomingOwnershipReader = () => { reads++; return Target(NewWorld); };
        f.Send(NewWorld with { RemoteAddress = "203.0.113.1" }, Login());
        f.Network.ProcessDevicePacket(new object(), Capture(NewWorld, 1000, Login()));
        await f.Wait();
        Assert.Equal(0, reads);
        Assert.Equal(0, f.Network.IncomingOwnershipQueue.Packets);
        Assert.Empty(f.Streams);
        Assert.Null(f.Network.Character);
    }

    private static CaptureTarget Target(params CaptureConnection[] connections) => CaptureTarget.FromRows(
        connections.Select(connection => connection.GamePid).ToHashSet(), connections.Select(Row).ToArray(), _ => "adapter", _ => "Synthetic relay")!;
    private static TcpTable.Row Row(CaptureConnection connection) => new(new IPEndPoint(IPAddress.Parse(connection.LocalAddress), connection.LocalPort),
        new IPEndPoint(IPAddress.Parse(connection.RemoteAddress), connection.RemotePort), connection.GamePid, true);
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
    private static byte[] Clock() => Packet(0x3600, w => w.Write(1791849600000L));
    private static byte[] Candidate() => Packet(0x3620, w =>
    {
        w.Write((ushort)0); V(w, (uint)Own.EntityKey); w.Write(new byte[4]); w.Write(Own.CharacterId);
        w.Write(new byte[2279 - (int)(w.BaseStream.Position - 2)]);
    });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] PetSnapshot() => Packet(0x9000, w =>
    {
        w.Write(0); w.Write((byte)1); w.Write(999); w.Write(1004); w.Write(1);
        w.Write((byte)1); w.Write(1004); w.Write(3); w.Write(new byte[4]);
    });
    private static byte[] Gain() => Packet(0x900D, w => { w.Write((byte)1); w.Write(1004); w.Write(2); });
    private static byte[] OwnMessage() => Packet(0x3633, w =>
    {
        V(w, (uint)Own.EntityKey); w.Write(0);
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes(Own.Name); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)Own.ServerId); w.Write(0); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(Own.CharacterId); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
    });
    private static byte[] Login(bool gain = false) => [.. Frame(Clock()), .. Frame(Candidate()), .. Frame(PetSnapshot()),
        .. Frame(Prepare()), .. Frame(OwnMessage()), .. (gain ? Frame(Gain()) : [])];
    private static byte[] Frame(byte[] message)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        V(writer, (uint)(message.Length + 4)); writer.Write(message); return bytes.ToArray();
    }
    private static void V(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload, byte flags = 0x10)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14)));
        bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence);
        bytes[46] = 0x50; bytes[47] = flags; payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }

    public class DeviceProxy : DispatchProxy
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public object Gate { get; set; } = new();
        public bool StopHeldGate { get; private set; }
        public int Count(string method) { lock (_calls) return _calls.GetValueOrDefault(method); }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            lock (_calls) _calls[method.Name] = Count(method.Name) + 1;
            if (method.Name == "StopCapture") StopHeldGate = Monitor.IsEntered(Gate);
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-incoming-owner-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<CaptureConnection, uint> _sequences = [];
        private long _ticks = At.Ticks;
        public NetworkLootService Network { get; }
        public ProgressService Progress { get; }
        public ILiveDevice Device { get; }
        public DeviceProxy Calls { get; }
        public List<string> Gaps { get; } = [];
        public object Gate => Field("_gate").GetValue(Network)!;
        public object ConnectionGate => Field("_connectionGate").GetValue(Network)!;
        public long Generation => (long)Field("_generation").GetValue(Network)!;
        public CaptureTarget? CurrentTarget => (CaptureTarget?)Field("_target").GetValue(Network);
        public Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress);
            var target = Target(World, Web);
            Device = DispatchProxy.Create<ILiveDevice, DeviceProxy>(); Calls = (DeviceProxy)(object)Device; Calls.Gate = Gate;
            Field("_target").SetValue(Network, target); Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            Field("_device").SetValue(Network, Device);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.IncomingOwnershipUtcNow = () => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            Network.IncomingOwnershipWait = delay => { Advance(delay); return Task.CompletedTask; };
            Network.IncomingOwnershipReader = () => target;
            Network.PeriodicOwnershipReader = () => target;
            Network.BossStreamGap += Gaps.Add;
        }
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        public uint Next(CaptureConnection connection) => _sequences.GetValueOrDefault(connection, 1000u);
        public void Send(CaptureConnection connection, byte[] payload, byte flags = 0x10)
        {
            var sequence = Next(connection); _sequences[connection] = sequence + (uint)payload.Length;
            Network.ProcessDevicePacket(Device, Capture(connection, sequence, payload, flags));
        }
        public void SendSynthetic(CaptureConnection connection, byte[] payload)
        {
            var sequence = Next(connection); _sequences[connection] = sequence + (uint)payload.Length;
            Network.ProcessCapturedPacket(Capture(connection, sequence, payload));
        }
        public void BeginWorld()
        {
            SendSynthetic(World, Frame(Clock())); Network.ObserveIdentity(World.StreamKey, Own, DateTimeOffset.UtcNow);
            SendSynthetic(World, Frame(PetSnapshot())); Gaps.Clear();
        }
        public Task Wait() => Network.IncomingOwnershipTask.WaitAsync(TimeSpan.FromSeconds(5));
        public void AssertUntouched()
        {
            foreach (var method in new[] { "Open", "Close", "StartCapture", "StopCapture", "set_Filter" }) Assert.Equal(0, Calls.Count(method));
        }
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose()
        {
            Network.Dispose(); Directory.Delete(_folder, recursive: true);
        }
    }
}
