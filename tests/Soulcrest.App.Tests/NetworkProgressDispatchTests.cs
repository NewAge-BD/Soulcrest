using System.Buffers.Binary;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkProgressDispatchTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 100, "SyntheticAlpha");

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-dispatch-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Progress = new(Path.Combine(_root, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_root, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810001),
                new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052026)]);
            Network = new(Progress, Exploration);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }

    private static byte[] EntryCandidate(OwnCharacterIdentity identity) => Packet(0x3620, writer =>
    {
        var bodyStart = writer.BaseStream.Position;
        writer.Write((ushort)0);
        var entity = (uint)identity.EntityKey;
        while (entity >= 0x80)
        {
            writer.Write((byte)(entity | 0x80));
            entity >>= 7;
        }
        writer.Write((byte)entity);
        writer.Write(new byte[4]);
        writer.Write(identity.CharacterId);
        writer.Write(new byte[2279 - (int)(writer.BaseStream.Position - bodyStart)]);
    });
    private static byte[] Preparation() => Packet(0x3621, writer => writer.Write(new byte[46]));

    private static byte[] PetSnapshot() => Packet(0x9000, w =>
    {
        w.Write(0); w.Write((byte)1); w.Write(999); w.Write(1004); w.Write(1);
        w.Write((byte)1); w.Write(1004); w.Write(3); w.Write(new byte[4]);
    });
    private static byte[] Points(ushort opcode) => Packet(opcode, w =>
    { w.Write((byte)1); w.Write(1004); w.Write(2); });
    private static byte[] Quests() => Packet(0x8D09, w =>
    {
        w.Write((byte)1); w.Write(2810001); w.Write((byte)2); w.Write((byte)1);
        w.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); w.Write(1L); w.Write(new byte[2]);
    });

    [Fact]
    public void AnchoredBaselinesWaitForMatchingPreparationAndOwnIdentityAndNeverProduceSessionLoot()
    {
        using var f = new Fixture();
        var gains = new List<NetworkSoul>(); f.Network.SoulGained += gains.Add;
        f.Network.ProcessProgressMessage("game", EntryCandidate(Identity), At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", Quests(), At);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        f.Network.ProcessProgressMessage("game", Preparation(), At.AddMilliseconds(500));
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        f.Network.ObserveIdentity("game", Identity, At.AddSeconds(1));
        Assert.Equal(At, f.Network.PetBaselineAt);
        Assert.Equal(At, f.Network.QuestBaselineAt);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(gains);
        Assert.Empty(f.Progress.Recent());
        Assert.Equal((byte)2, f.Exploration.Snapshot().Progress["cave"].QuestStatus);
        Assert.Null(f.Exploration.Snapshot().Progress["cave"].State);
    }

    [Fact]
    public void OnlyTheBoundStreamCountsLootAndSpendingIsNotNewLoot()
    {
        using var f = new Fixture();
        var gains = new List<NetworkSoul>(); f.Network.SoulGained += gains.Add;
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("other", Points(0x900D), At);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        f.Network.ProcessProgressMessage("game", Points(0x900D), At);
        f.Network.ProcessProgressMessage("game", Points(0x900E), At);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(gains).Souls);
        Assert.Single(f.Progress.Recent());
        f.Network.ResetProgressContext();
        f.Network.ProcessProgressMessage("game", Points(0x900D), At);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(gains);
        f.Network.ObserveIdentity("new-game", Identity with { CharacterId = 202, ServerId = 502 }, At);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.Single(gains);
    }

    [Theory]
    [InlineData((ushort)0x9000)]
    [InlineData((ushort)0x9009)]
    [InlineData((ushort)0x900D)]
    [InlineData((ushort)0x900E)]
    [InlineData((ushort)0x9232)]
    [InlineData((ushort)0x8D09)]
    public void UnsupportedProgressRejectsOnlyItsSourceAndLaterSoulsStillCountOnce(ushort opcode)
    {
        using var f = new Fixture();
        var gains = new List<NetworkSoul>(); f.Network.SoulGained += gains.Add;
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", Quests(), At);
        f.Network.ProcessProgressMessage("game", Packet(0x9232, w => { w.Write((byte)1); w.Write(1052026); }), At);
        f.Network.ProcessProgressMessage("game", Points(0x900D), At.AddSeconds(1));
        var generation = f.Network.ProgressDiagnostics.ContextResets;
        byte[] malformed = opcode switch
        {
            0x9000 => [.. PetSnapshot(), 0],
            0x900D or 0x900E => [.. Points(opcode), 0],
            0x8D09 => [.. Quests(), 0],
            _ => Packet(opcode, w => w.Write(new byte[] { 0, 0 })),
        };
        f.Network.ProcessProgressMessage("game", malformed, At.AddSeconds(2));
        Assert.Equal(Identity.Name, f.Network.Character);
        Assert.Equal(generation, f.Network.ProgressDiagnostics.ContextResets);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Contains("nicht unterstützt", f.Network.ProgressStatus);
        Assert.Equal(opcode is 0x9000 or 0x9009 or 0x900D or 0x900E ? null : At, f.Network.PetBaselineAt);
        Assert.Equal(opcode == 0x8D09 ? null : At, f.Network.QuestBaselineAt);
        Assert.Equal(opcode == 0x9232 ? null : At, f.Network.TeleportBaselineAt);

        f.Network.ProcessProgressMessage("other", Points(0x900D), At.AddSeconds(3));
        f.Network.ProcessProgressMessage("game", Points(0x900D), At.AddSeconds(3));
        Assert.Equal(12, f.Progress.Souls("fossa"));
        Assert.Equal(2, gains.Count);
        Assert.All(gains, gain => Assert.Equal(2, gain.Souls));
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(4, f.Progress.Recent().Sum(row => row.Quantity));
        Assert.Contains("nicht unterstützt", f.Network.ProgressStatus); // an unrelated/delta message cannot re-confirm the rejected source
        var diagnostics = f.Network.ProgressDiagnostics;
        Assert.True(diagnostics.IdentityBound);
        Assert.Equal(1, diagnostics.RejectedProgressMessages);
        Assert.Equal(opcode, diagnostics.LastRejectedOpcode);
        Assert.Equal(2, diagnostics.AcceptedSoulMessages);
        Assert.Equal(4, diagnostics.AcceptedSoulPoints);
        Assert.Equal(At.AddSeconds(3), diagnostics.LastSoulAt);
        Assert.Equal(0, diagnostics.PendingMessages);
        if (opcode is 0x9000 or 0x9009 or 0x900D or 0x900E)
            Assert.Null(f.Progress.AvailableSouls("fossa"));

        // Only a complete valid baseline of the affected source restores confirmation.
        f.Network.ProcessProgressMessage("game", opcode switch
        {
            0x8D09 => Quests(),
            0x9232 => Packet(0x9232, w => { w.Write((byte)1); w.Write(1052026); }),
            _ => PetSnapshot(),
        }, At.AddSeconds(4));
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.NotNull(f.Network.QuestBaselineAt);
        Assert.NotNull(f.Network.TeleportBaselineAt);
        Assert.DoesNotContain("nicht unterstützt", f.Network.ProgressStatus);
        Assert.Equal(2, gains.Count); // full snapshots never create pickup events
    }
    [Theory]
    [InlineData((ushort)0x3633)]
    [InlineData((ushort)0x3621)]
    public void UnsupportedOwnWorldEntryCannotAttributeLaterPacketsToThePreviousCharacter(ushort opcode)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("other", Packet(opcode, _ => { }), At);
        Assert.NotNull(f.Network.Character);
        f.Network.ProcessProgressMessage("game", Packet(opcode, _ => { }), At);
        Assert.Null(f.Network.Character);
        f.Network.ProcessProgressMessage("game", Points(0x900D), At);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void PendingOverflowDoesNotReplayAnArbitraryTailAsACompleteBaseline()
    {
        using var f = new Fixture();
        f.Network.ProcessProgressMessage("game", EntryCandidate(Identity), At);
        f.Network.ProcessProgressMessage("game", Quests(), At);
        Assert.Equal(1, f.Network.ProgressDiagnostics.PendingMessages);
        // This must be a different source: a second 9000 is rejected as a duplicate before the byte-cap check.
        var oversizedSnapshot = new byte[2 * 1024 * 1024 + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(oversizedSnapshot, 0x9000);
        f.Network.ProcessProgressMessage("game", oversizedSnapshot, At);
        Assert.True(f.Network.ProgressDiagnostics.PendingOverflow);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", Preparation(), At);
        f.Network.ObserveIdentity("game", Identity, At);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.Empty(f.Progress.Recent());
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        Assert.Equal(8, f.Progress.Souls("fossa"));
    }

    [Fact]
    public void AWorldPreparationOnAnotherStreamCannotInvalidateTheBoundCharacter()
    {
        using var f = new Fixture();
        var prepare = Packet(0x3621, w => w.Write(new byte[46]));
        Assert.True(OwnIdentityMessage.IsWorldEntryPreparation(prepare));
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("other", prepare, At);
        Assert.NotNull(f.Network.PetBaselineAt);
        f.Network.ProcessProgressMessage("game", Points(0x900D), At);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(f.Progress.Recent());
        f.Network.ProcessProgressMessage("game", prepare, At);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
    }

    [Theory]
    [InlineData("game")]
    [InlineData("*")]
    public void BoundOrGlobalGapPublishesTheInvalidatedBaselineImmediately(string stream)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        var changes = 0;
        f.Network.Changed += () =>
        {
            changes++;
            Assert.Null(f.Network.PetBaselineAt);
            Assert.Null(f.Network.Character);
        };
        f.Network.ProcessProgressGap("other");
        Assert.Equal(0, changes);
        Assert.NotNull(f.Network.PetBaselineAt);
        f.Network.ProcessProgressGap(stream);
        Assert.Equal(1, changes);
        Assert.Equal(8, f.Progress.Souls("fossa"));
    }

    [Fact]
    public void UnanchoredStreamsNeverQueueProgressOrProduceABaseline()
    {
        using var f = new Fixture();
        for (var i = 0; i < 9; i++) f.Network.ProcessProgressMessage("stream-" + i, PetSnapshot(), At);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Network.ObserveIdentity("stream-0", Identity, At);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Progress.Souls("fossa"));
    }

}
