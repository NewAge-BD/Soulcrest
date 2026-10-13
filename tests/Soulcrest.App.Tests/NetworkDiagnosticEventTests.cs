using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkDiagnosticEventTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(1001, 501, 100, "Synthetic profile");

    [Fact]
    public void LevelSpendAndGainAreRecordedSeparatelyWithoutBecomingExtraDrops()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("synthetic-world", Own, At);
        f.Send(Snapshot(), 1);
        f.Send(Acquired(), 2);
        f.Send(Points(0x900E, 25), 3);
        f.Send(Points(0x900D, 2), 4);

        var diagnostics = f.Network.ProgressDiagnostics;
        Assert.Equal(1, diagnostics.AcceptedSoulMessages);
        Assert.Equal(2, diagnostics.AcceptedSoulPoints);
        Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Snapshot);
        var acquired = Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Acquired);
        Assert.Equal(1135, acquired.PetNumber);
        Assert.Equal(2, acquired.Level);
        Assert.Equal(25, Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Spent).Souls);
        Assert.Equal(2, Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Gained).Souls);
        Assert.Equal(2, f.Progress.AvailableSouls("ashen-dionae"));
        Assert.Equal(2, f.Progress.Level("ashen-dionae"));
        Assert.Single(f.Progress.Recent());
    }

    [Fact]
    public void GainsWithoutOwnCountGloballyAndKeepOnlyABoundedDiagnosticHistory()
    {
        using var f = new Fixture();
        for (var i = 0; i < 200; i++) f.Send(Points(0x900D, 2), i);
        var diagnostics = f.Network.ProgressDiagnostics;
        Assert.False(diagnostics.IdentityBound);
        Assert.Equal(0, diagnostics.UnboundSoulMessages);
        Assert.Equal(0, diagnostics.UnboundSoulPoints);
        Assert.Equal(200, diagnostics.AcceptedSoulMessages);
        Assert.Equal(400, diagnostics.AcceptedSoulPoints);
        Assert.Equal(128, diagnostics.Events.Count);
        Assert.All(diagnostics.Events, entry =>
        {
            Assert.Equal(NetworkProgressEventKind.Gained, entry.Kind);
            Assert.Equal((ushort)0x900D, entry.Opcode);
            Assert.Equal(1135, entry.PetNumber);
            Assert.Equal(2, entry.Souls);
        });
        Assert.Equal(At.AddSeconds(72), diagnostics.Events[0].At);
        Assert.Equal(At.AddSeconds(199), diagnostics.Events[^1].At);
        Assert.Equal(200, f.Progress.Recent().Count);
        Assert.All(f.Progress.Recent(), soul => Assert.Equal(2, soul.Quantity));
        Assert.Equal(100, f.Network.Recent.Count);
        Assert.Equal(400, f.Progress.Souls("ashen-dionae"));
        Assert.Null(f.Progress.ObservedLevel("ashen-dionae"));
        Assert.Null(f.Progress.AvailableSouls("ashen-dionae"));
        Assert.Null(f.Network.PetBaselineAt);

        f.Network.ObserveIdentity("synthetic-world", Own, At.AddSeconds(200));
        Assert.Equal(400, f.Progress.Souls("ashen-dionae"));
        Assert.Equal(200, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(400, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Equal(200, f.Progress.Recent().Count);
    }

    [Fact]
    public void EventHistoryIsBoundedAndSnapshotsAreIndependentCopies()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("synthetic-world", Own, At);
        f.Send(Snapshot(), 1);
        var before = f.Network.ProgressDiagnostics;
        var beforeEvents = before.Events.ToArray();
        for (var i = 0; i < 200; i++) f.Send(Points(0x900D, 1), i + 2);
        var after = f.Network.ProgressDiagnostics;
        Assert.Equal(128, after.Events.Count);
        Assert.All(after.Events, e => Assert.Equal(NetworkProgressEventKind.Gained, e.Kind));
        Assert.Equal(beforeEvents, before.Events);
        Assert.Equal(200, after.AcceptedSoulPoints);
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode);
        write(writer);
        return bytes.ToArray();
    }

    private static byte[] Points(ushort opcode, int souls) => Packet(opcode,
        w => { w.Write((byte)1); w.Write(1135); w.Write(souls); });

    private static byte[] Acquired() => Packet(0x9009,
        w => { w.Write((byte)100); w.Write(1135); w.Write(2); w.Write((byte)0); w.Write(0); w.Write(0L); });

    private static byte[] Snapshot() => Packet(0x9000, w =>
    {
        w.Write(0); w.Write((byte)1); w.Write(1); w.Write(1135); w.Write(1);
        w.Write((byte)1); w.Write(1135); w.Write(25); w.Write(new byte[4]);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-event-" + Guid.NewGuid().ToString("N"));
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "ashen-dionae", En = "Ashen Dionae", Genus = "Natura", NumericId = "1135" }]));
            Network = new(Progress);
        }

        public void Send(byte[] message, int seconds) => Network.ProcessProgressMessage("synthetic-world", message, At.AddSeconds(seconds));
        public void Dispose()
        {
            Network.Dispose();
            Directory.Delete(_folder, true);
        }
    }
}
