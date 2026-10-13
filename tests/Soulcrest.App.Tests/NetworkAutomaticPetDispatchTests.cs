using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkAutomaticPetDispatchTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic upgrade profile");
    private const int PetNumber = 1062;
    private const string PetId = "contaminated-slime";

    [Fact]
    public void UnboundExplicitUpgradeAndSeparateSpendingMatchTheObservedSixGainSequence()
    {
        using var f = new Fixture();
        Assert.Null(f.Network.Character);
        f.Send(Snapshot(1, 21), 1);
        for (var i = 0; i < 4; i++) f.Send(Points(0x900D, 1), i + 2);
        Assert.Equal("25/25", f.Progress.SoulCount(PetId));
        Assert.Equal(1, f.Progress.ObservedLevel(PetId));
        f.Send(Upgrade(), 6);
        Assert.Equal(2, f.Progress.ObservedLevel(PetId));
        Assert.Equal(25, f.Progress.AvailableSouls(PetId));
        Assert.Equal(4, f.Gains.Count);
        f.Send(Points(0x900E, 25), 6);
        for (var i = 0; i < 2; i++) f.Send(Points(0x900D, 1), i + 7);

        Assert.Equal("2/75", f.Progress.SoulCount(PetId));
        Assert.Equal(32, f.Progress.Souls(PetId));
        Assert.Equal(6, f.Gains.Count);
        Assert.All(f.Gains, gain => Assert.Equal(1, gain.Souls));
        Assert.Equal(6, f.Progress.Recent().Count);
        Assert.Equal(6, f.Network.Recent.Count);
        Assert.Equal(6, File.ReadAllLines(f.EventsPath).Length);
        var diagnostics = f.Network.ProgressDiagnostics;
        Assert.Equal(6, diagnostics.AcceptedSoulMessages);
        Assert.Equal(6, diagnostics.AcceptedSoulPoints);
        var upgraded = Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Upgraded);
        Assert.Equal((ushort)0x9014, upgraded.Opcode);
        Assert.Equal(PetNumber, upgraded.PetNumber);
        Assert.Equal(2, upgraded.Level);
        Assert.Null(upgraded.Souls);
        Assert.Equal(25, Assert.Single(diagnostics.Events, e => e.Kind == NetworkProgressEventKind.Spent).Souls);
    }

    [Fact]
    public void UnboundAndOtherNativeOwnedStreamUpgradeResultsApplyWithoutCountingLoot()
    {
        using var f = new Fixture();
        f.Send(Upgrade(), 1);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(2, f.Progress.ObservedLevel(PetId));
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        f.Network.ObserveIdentity("own", Own, At.AddSeconds(2));
        Assert.Equal(2, f.Progress.ObservedLevel(PetId));
        f.Send(Snapshot(1, 21), 3);
        f.Network.ProcessProgressMessage("another-owned-stream", Upgrade(), At.AddSeconds(4));
        Assert.Equal(2, f.Progress.ObservedLevel(PetId));
        Assert.Equal(21, f.Progress.AvailableSouls(PetId));
        Assert.False(f.Network.ProgressSources.PetFormatRejected);
        Assert.Equal(2, f.Network.ProgressDiagnostics.Events.Count(e => e.Kind == NetworkProgressEventKind.Upgraded));
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void CompleteFailedResultDoesNotWriteALevelDebitBalanceOrRejectThePetSource()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("own", Own, At);
        f.Send(Snapshot(1, 21), 1);
        var before = File.ReadAllBytes(f.ProgressPath);
        f.Send(Upgrade(result: 1, pet: 0, level: 0), 2);
        Assert.Equal(before, File.ReadAllBytes(f.ProgressPath));
        Assert.Equal(1, f.Progress.ObservedLevel(PetId));
        Assert.Equal(21, f.Progress.AvailableSouls(PetId));
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.False(f.Network.ProgressSources.PetFormatRejected);
        Assert.Equal(0, f.Network.ProgressDiagnostics.RejectedProgressMessages);
        Assert.DoesNotContain(f.Network.ProgressDiagnostics.Events, e => e.Kind == NetworkProgressEventKind.Upgraded);
        Assert.Empty(f.Gains);
        f.Send(Points(0x900D, 2), 3);
        Assert.Equal(23, f.Progress.AvailableSouls(PetId));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
    }

    [Fact]
    public void DuplicateAbsoluteResultCannotSpendAgainOrBecomeAnotherDrop()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("own", Own, At);
        f.Send(Snapshot(1, 25), 1);
        f.Send(Upgrade(), 2);
        f.Send(Points(0x900E, 25), 3);
        f.Send(Points(0x900D, 2), 4);
        f.Send(Upgrade(), 5);
        f.Send(Upgrade(), 6);
        Assert.Equal(2, f.Progress.ObservedLevel(PetId));
        Assert.Equal(2, f.Progress.AvailableSouls(PetId));
        Assert.Equal("2/75", f.Progress.SoulCount(PetId));
        Assert.Equal(32, f.Progress.Souls(PetId));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Single(f.Progress.Recent());
        Assert.Single(f.Network.Recent);
        Assert.Single(File.ReadAllLines(f.EventsPath));
        Assert.Equal(1, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Single(f.Network.ProgressDiagnostics.Events, e => e.Kind == NetworkProgressEventKind.Spent);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("invalid-pet")]
    [InlineData("invalid-level")]
    public void MalformedUpgradeRejectsOnlyPetConfirmationAndLaterSoulsContinueOnTheBoundStream(string invalid)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("own", Own, At);
        f.Send(Snapshot(2, 2), 1);
        f.Send(Packet(0x8D09, writer => writer.Write((byte)0)), 1);
        f.Send(Packet(0x9232, writer => writer.Write((byte)0)), 1);
        var resets = f.Network.ProgressDiagnostics.ContextResets;
        byte[] malformed = invalid switch
        {
            "truncated" => Upgrade()[..^1],
            "trailing" => [.. Upgrade(), 0],
            "invalid-pet" => Upgrade(pet: 0),
            _ => Upgrade(level: 4),
        };
        f.Send(malformed, 2);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.True(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Equal(resets, f.Network.ProgressDiagnostics.ContextResets);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Network.QuestBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Network.TeleportBaselineAt);
        Assert.True(f.Network.ProgressSources.PetFormatRejected);
        Assert.False(f.Network.ProgressSources.QuestFormatRejected);
        Assert.False(f.Network.ProgressSources.TeleportFormatRejected);
        Assert.Equal((ushort)0x9014, f.Network.ProgressDiagnostics.LastRejectedOpcode);
        Assert.Equal(1, f.Network.ProgressDiagnostics.RejectedProgressMessages);
        Assert.Equal(2, f.Progress.SoulsInLevel(PetId));
        f.Send(Points(0x900D, 1), 3);
        Assert.Equal("3/75", f.Progress.SoulCount(PetId));
        Assert.Equal(33, f.Progress.Souls(PetId));
        Assert.Equal(1, Assert.Single(f.Gains).Souls);
        Assert.True(f.Network.ProgressSources.PetFormatRejected);
        Assert.Null(f.Network.PetBaselineAt);
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] Snapshot(int level, int balance) => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(PetNumber); writer.Write(level);
        writer.Write((byte)1); writer.Write(PetNumber); writer.Write(balance); writer.Write(new byte[4]);
    });
    private static byte[] Points(ushort opcode, int souls) => Packet(opcode, writer =>
    { writer.Write((byte)1); writer.Write(PetNumber); writer.Write(souls); });
    private static byte[] Upgrade(ushort result = 0, int pet = PetNumber, int level = 2) => Packet(0x9014, writer =>
    { writer.Write(result); writer.Write(pet); writer.Write(level); writer.Write(new byte[13]); });

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-upgrade-dispatch-" + Guid.NewGuid().ToString("N"));
        internal string ProgressPath => Path.Combine(_folder, "progress.json");
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal ProgressService Progress { get; }
        internal NetworkLootService Network { get; }
        internal List<NetworkSoul> Gains { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(PetNumber));
            Assert.Equal(PetId, source.CatalogId);
            Progress = new(ProgressPath, new PetCatalog([new PetDefinition { Id = PetId, En = source.En,
                Genus = source.Genus.ToLowerInvariant(), NumericId = "1062" }]));
            var exploration = new ExplorationService(Path.Combine(_folder, "exploration.json"), []);
            Network = new(Progress, exploration);
            Network.SoulGained += Gains.Add;
        }
        internal void Send(byte[] message, int seconds) => Network.ProcessProgressMessage("own", message, At.AddSeconds(seconds));
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }
}
