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

/// <summary>Live-handle lifecycle with a synthetic ILiveDevice; no Npcap, sockets or user files.</summary>
[Collection("Settings file")]
public sealed class NetworkCaptureFilterExpansionTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
    private static readonly CaptureConnection Web = new(42, "192.0.2.2", 50001, "198.51.100.1", 443);
    private static readonly CaptureConnection Added = new(42, "192.0.2.2", 50002, "203.0.113.1", 443);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticFilterProfile");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewRemoteExpandsOnlyProvenCoverageWithoutInterruptingOwnOrAPartialWorldMessage()
    {
        using var f = new Fixture();
        f.Bind();
        var before = f.Network.ProgressSources;
        var generation = f.Generation;
        var original = f.Streams[World];
        var gain = Frame(Gain());
        f.Send(World, gain[..5]);
        var oldTarget = f.Target;
        var oldCoverage = f.Coverage;
        f.Handle.BeforeFilter = _ =>
        {
            Assert.Same(oldTarget, f.Target);
            Assert.Same(oldCoverage, f.Coverage);
            Assert.Equal(Own.Name, f.Network.Character);
        };

        f.Network.UpdateCaptureTarget(Target(World, Added));
        f.Send(World, gain[5..]);

        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(before.PetBaselineAt, f.Network.PetBaselineAt);
        Assert.Equal(before.QuestBaselineAt, f.Network.QuestBaselineAt);
        Assert.Equal(before.TeleportBaselineAt, f.Network.TeleportBaselineAt);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(generation, f.Generation);
        Assert.Same(original.Tcp, f.Streams[World].Tcp);
        Assert.Same(original.Messages, f.Streams[World].Messages);
        Assert.True(f.Coverage.SetEquals(new[] { World.RemoteEndpoint, Web.RemoteEndpoint, Added.RemoteEndpoint }));
        Assert.Equal("tcp and ((host 192.0.2.1 and port 9000) or (host 198.51.100.1 and port 443) or (host 203.0.113.1 and port 443))", Assert.Single(f.Handle.Filters));
        Assert.Equal(new[] { Web.StreamKey }, f.Gaps);
        Assert.Contains(f.Network.ProgressDiagnostics.Events, e => e.Kind == NetworkProgressEventKind.FilterExpanded);
        AssertHandleNeverRestarted(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterFailureKeepsOldOwnershipAndCanRetryWithoutClosingTheCapture(bool invalidOperation)
    {
        using var f = new Fixture();
        f.Bind();
        var originalTarget = f.Target;
        var originalCoverage = f.Coverage;
        var originalStream = f.Streams[World];
        f.Handle.FilterError = invalidOperation ? new InvalidOperationException("Synthetic filter failure") : PcapFailure();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));

        Assert.True(f.Network.Running);
        Assert.True(f.Network.StatusIsError);
        Assert.Same(originalTarget, f.Target);
        Assert.Same(originalCoverage, f.Coverage);
        Assert.Same(originalStream.Tcp, f.Streams[World].Tcp);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal("Mitschnittfilter konnte nicht erweitert werden. Bestehende Spielverbindungen werden weiter gelesen; ein neuer Versuch folgt.", f.Network.Status);
        f.Send(Added, Frame(Gain()));
        Assert.Equal(8, f.Progress.Souls("fossa")); // Uncovered new ownership was not adopted.
        f.Send(World, Frame(Gain()));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        AssertHandleNeverRestarted(f);

        f.Handle.FilterError = null;
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.False(f.Network.StatusIsError);
        Assert.Equal(2, f.Handle.Filters.Count);
        Assert.Contains(Added, f.Target.Connections!);
        Assert.Contains(Added.RemoteEndpoint, f.Coverage);
        Assert.Equal(new[] { NetworkProgressEventKind.FilterFailed, NetworkProgressEventKind.FilterExpanded },
            f.Network.ProgressDiagnostics.Events.Where(e => e.Kind is NetworkProgressEventKind.FilterFailed or NetworkProgressEventKind.FilterExpanded).Select(e => e.Kind));
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void FailedExpansionStillRevokesAKnownRemovedWorldTupleAndRejectsItsQueuedData()
    {
        using var f = new Fixture();
        f.Bind();
        var coverage = f.Coverage;
        f.Handle.FilterError = PcapFailure();
        f.Network.UpdateCaptureTarget(Target(Web, Added));
        Assert.True(f.Network.StatusIsError);
        Assert.Same(coverage, f.Coverage);
        Assert.True(f.Target.Connections!.SetEquals(new[] { Web }));
        Assert.False(f.Streams.ContainsKey(World));
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.Equal(new[] { World.StreamKey }, f.Gaps);
        f.Send(World, [.. Frame(Clock()), .. Frame(Gain())]);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Gains);
        Assert.False(f.Streams.ContainsKey(World));
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void FailedExpansionWarningClearsWhenTheUncoveredEndpointDisappearsAgain()
    {
        using var f = new Fixture();
        f.Bind();
        f.Handle.FilterError = PcapFailure();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        Assert.True(f.Network.StatusIsError);
        f.Network.UpdateCaptureTarget(Target(World, Web));
        Assert.False(f.Network.StatusIsError);
        Assert.Single(f.Handle.Filters); // No retry is necessary for the unchanged installed coverage.
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void ShrinkingAndReaddingCoveredEndpointsNeverShrinkOrRewriteTheInstalledFilter()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        var expanded = f.Coverage;
        f.Network.UpdateCaptureTarget(Target(World));
        f.Network.UpdateCaptureTarget(Target(World, Web with { LocalPort = 50003 }, Added with { LocalPort = 50004 }));
        Assert.Single(f.Handle.Filters);
        Assert.Same(expanded, f.Coverage);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertHandleNeverRestarted(f);
    }

    [Fact]
    public void ARealDetectedGapAfterFilterExpansionStillInvalidatesOwnInsteadOfClaimingLosslessTransition()
    {
        using var f = new Fixture();
        f.Bind();
        f.Network.UpdateCaptureTarget(Target(World, Web, Added));
        f.Network.ProcessProgressGap(World.StreamKey);
        f.Send(World, Frame(Gain()));
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Equal(1, f.Network.ProgressDiagnostics.UnboundSoulMessages);
        Assert.Equal(2, f.Network.ProgressDiagnostics.UnboundSoulPoints);
        Assert.Empty(f.Gains);
        AssertHandleNeverRestarted(f);
    }

    // SharpPcap 6.3.1 exposes no public PcapException constructors.
    private static PcapException PcapFailure() => (PcapException)Activator.CreateInstance(typeof(PcapException), nonPublic: true)!;

    private static void AssertHandleNeverRestarted(Fixture f)
    {
        Assert.Equal(0, f.Handle.Count("Open"));
        Assert.Equal(0, f.Handle.Count("StartCapture"));
        Assert.Equal(0, f.Handle.Count("StopCapture"));
        Assert.Equal(0, f.Handle.Count("Close"));
    }

    public class DeviceProxy : DispatchProxy
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public List<string> Filters { get; } = [];
        public Exception? FilterError { get; set; }
        public Action<string>? BeforeFilter { get; set; }
        public int Count(string method) => _calls.GetValueOrDefault(method);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            _calls[method.Name] = Count(method.Name) + 1;
            if (method.Name == "set_Filter")
            {
                var filter = (string)args![0]!;
                Filters.Add(filter); BeforeFilter?.Invoke(filter);
                if (FilterError is { } error) throw error;
            }
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-filter-expand-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<CaptureConnection, uint> _sequences = [];
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public DeviceProxy Handle { get; }
        public List<NetworkSoul> Gains { get; } = [];
        public List<string> Gaps { get; } = [];
        public long Generation => (long)Field("_generation").GetValue(Network)!;
        public CaptureTarget Target => (CaptureTarget)Field("_target").GetValue(Network)!;
        public IReadOnlySet<CaptureEndpoint> Coverage => (IReadOnlySet<CaptureEndpoint>)Field("_installedCoverage").GetValue(Network)!;
        public Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            var exploration = new ExplorationService(Path.Combine(_folder, "exploration.json"), []);
            Network = new(Progress, exploration);
            var target = NetworkCaptureFilterExpansionTests.Target(World, Web);
            var device = DispatchProxy.Create<ILiveDevice, DeviceProxy>();
            Handle = (DeviceProxy)(object)device;
            Field("_device").SetValue(Network, device); Field("_target").SetValue(Network, target);
            Field("_installedCoverage").SetValue(Network, target.RemoteEndpoints);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.SoulGained += Gains.Add; Network.BossStreamGap += Gaps.Add;
        }
        public void Bind()
        {
            Send(World, Frame(Clock()));
            Network.ObserveIdentity(World.StreamKey, Own, At);
            Send(World, Frame(Pets()));
            Send(World, Frame([0x09, 0x8D, 0]));
            Send(World, Frame([0x32, 0x92, 0]));
            Gaps.Clear();
            Assert.True(Network.ProgressSources.InitialProgressConfirmed);
        }
        public void Send(CaptureConnection connection, byte[] payload)
        {
            var sequence = _sequences.GetValueOrDefault(connection, 1000u);
            Network.ProcessCapturedPacket(Capture(connection, sequence, payload));
            _sequences[connection] = sequence + (uint)payload.Length;
        }
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }

    private static CaptureTarget Target(params CaptureConnection[] connections) => CaptureTarget.FromRows(
        connections.Select(c => c.GamePid).ToHashSet(), connections.Select(c => new TcpTable.Row(
            new IPEndPoint(IPAddress.Parse(c.LocalAddress), c.LocalPort), new IPEndPoint(IPAddress.Parse(c.RemoteAddress), c.RemotePort), c.GamePid, true)).ToArray(),
        _ => "adapter", _ => "Synthetic relay")!;
    private static byte[] Clock() => [0, 0x36, .. BitConverter.GetBytes(1791849600000L)];
    private static byte[] Gain() => [0x0D, 0x90, 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(2)];
    private static byte[] Pets() => [0, 0x90, .. BitConverter.GetBytes(0), 1, .. BitConverter.GetBytes(999), .. BitConverter.GetBytes(1004),
        .. BitConverter.GetBytes(1), 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(3), 0, 0, 0, 0];
    private static byte[] Frame(byte[] message) => [checked((byte)(message.Length + 4)), .. message];
    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14))); bytes[22] = 64; bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence); bytes[46] = 0x50; bytes[47] = 0x10;
        payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791849600UL, 0UL), bytes, null);
    }
}
