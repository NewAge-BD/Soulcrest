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
    public void BaselinesWaitForTheirOwnStreamIdentityAndNeverProduceSessionLoot()
    {
        using var f = new Fixture();
        var gains = new List<NetworkSoul>(); f.Network.SoulGained += gains.Add;
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", Quests(), At);
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

    [Fact]
    public void UnsupportedVariantMakesTheContextStaleWithoutOverwritingSavedProgress()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Identity, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", [.. Quests(), 0], At);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Null(f.Network.Character);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Contains("nicht unterstützt", f.Network.ProgressStatus);
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
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        for (var i = 0; i < 256; i++) f.Network.ProcessProgressMessage("game", Points(0x900D), At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
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
    public void PendingStreamCountIsBoundedGlobally()
    {
        using var f = new Fixture();
        for (var i = 0; i < 9; i++) f.Network.ProcessProgressMessage("stream-" + i, PetSnapshot(), At);
        f.Network.ObserveIdentity("stream-0", Identity, At);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Progress.Souls("fossa"));
    }

}
