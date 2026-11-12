using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The globally accepted pet transaction retains Genus boards without borrowing character authority.</summary>
public sealed class NetworkGenusInsightSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
    private const string OwnStream = "synthetic-own";
    private const int PetNumber = 1062;

    [Theory]
    [InlineData("before-own")]
    [InlineData("before-prepare")]
    [InlineData("other-owned-stream")]
    [InlineData("prepared-control")]
    public void EveryAcceptedGlobalFullListRetainsItsGenusBoardsWithoutSupplyingAnActivePreset(string ordering)
    {
        using var f = new Fixture();
        if (ordering == "before-own") { f.Send(Full(), 0); f.Send(Own(), 1); }
        else if (ordering == "before-prepare") { f.Send(Candidate(), 0); f.Send(Full(), 1); f.Send(Prepare(), 2); f.Send(Own(), 3); }
        else if (ordering == "other-owned-stream") { f.Send(Own(), 0); f.Send(Full(), 1, "synthetic-other-owned"); }
        else { f.Send(Candidate(), 0); f.Send(Prepare(), 1); f.Send(Full(), 2); f.Send(Own(), 3); }
        var snapshot = AssertCurrent(f);
        Assert.Equal(5, snapshot.Domain.Value!.GenusBoards.Count);
        Assert.Equal(135, snapshot.Domain.Value.GenusBoards.Values.Sum(board => board.Panels.Values.Sum(panel => panel.Length)));
        Assert.Null(snapshot.Domain.Value.SelectedGenusPanel);
        Assert.NotNull(f.Network.CombatSnapshot.Character);
        Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void AListWithoutAnyOwnRemainsOnlySharedEvidenceAndCannotInventACharacterOrPreset()
    {
        using var f = new Fixture(); f.Send(Full(selected: 3), 0);
        var snapshot = AssertCurrent(f);
        Assert.Null(f.Network.OwnCharacter); Assert.Null(f.Network.CombatSnapshot.Character);
        Assert.Null(snapshot.Domain.Value!.SelectedGenusPanel);
    }

    [Fact]
    public void OwnTransitionsKeepTheGlobalBoardsButNeverPublishAPresetFromAnotherCharacter()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(selected: 3), 1);
        var before = AssertCurrent(f);
        f.Send(Prepare(), 2); Assert.Same(before, AssertCurrent(f));
        f.Send(Own(character: 202, entity: 301), 3);
        Assert.Same(before, AssertCurrent(f)); Assert.Null(before.Domain.Value!.SelectedGenusPanel);
    }

    [Fact]
    public void IdenticalListsAndNewTransportBaselinesHaveMonotoneSharedAndDomainSequences()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(), 1);
        var first = AssertCurrent(f);
        Assert.True(first.Sequence > f.Network.CombatSnapshot.Pets.Sequence);
        f.Send(Full(), 2); var second = AssertCurrent(f);
        Assert.Equal(first.Generation, second.Generation); Assert.True(second.Sequence > first.Sequence);
        f.Send(Full(), 3, "synthetic-other-owned"); var third = AssertCurrent(f);
        Assert.True(third.Generation > second.Generation); Assert.True(third.Sequence > second.Sequence);
        f.Network.ProcessProgressGap("synthetic-other-owned"); var gap = f.Network.GenusInsightSnapshot;
        Assert.False(gap.Domain.IsFresh); Assert.True(gap.Generation > third.Generation); Assert.True(gap.Sequence > third.Sequence);
        f.Send(Full(), 4); Assert.True(AssertCurrent(f).Sequence > gap.Sequence);
    }

    [Fact]
    public void SharedOrderingRemainsMonotoneAfterTheCharacterProgressCounterResets()
    {
        using var f = new Fixture(); f.Send(Own(), 0);
        for (var second = 1; second <= 8; second++) f.Send(Full(), second);
        var before = AssertCurrent(f);
        f.Send(Own(), 9); f.Send(Full(), 10);
        var after = AssertCurrent(f); Assert.True(after.Sequence > before.Sequence);
        Assert.True(after.Sequence > f.Network.CombatSnapshot.Pets.Sequence);
    }

    [Fact]
    public void ExactOwnLockAndRollRepliesRefineOnlyTheirQualifiedCurrentBaseline()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(), 1);
        var before = AssertCurrent(f);
        f.Send(Lock(true), 2); var locked = AssertCurrent(f);
        Assert.Equal(before.Generation, locked.Generation); Assert.True(locked.Sequence > before.Sequence);
        Assert.Equal("own-pet-genus-lock-update", locked.Domain.Source);
        Assert.True(locked.Domain.Value!.GenusBoards[2].Panels[1][0].IsLocked);
        Assert.False(before.Domain.Value!.GenusBoards[2].Panels[1][0].IsLocked);
        f.Send(Roll(), 3); var rolled = AssertCurrent(f);
        Assert.Equal("own-pet-genus-roll-update", rolled.Domain.Source);
        Assert.Equal(16, rolled.Domain.Value!.GenusBoards[2].Panels[1][0].Value);
        Assert.Equal((ushort)19, rolled.Domain.Value.GenusBoards[2].Panels[1][0].StatType);
        Assert.Equal(before.Domain.Value.GenusBoards[2].Panels[1][1], rolled.Domain.Value.GenusBoards[2].Panels[1][1]);
        Assert.Null(rolled.Domain.Value.SelectedGenusPanel);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId)); Assert.Empty(f.Gains);
    }

    [Fact]
    public void AReplyOnTheBaselineWithoutAFullOwnRevokesOldRollsInsteadOfLeavingThemLive()
    {
        using var f = new Fixture(); f.Send(Full(), 0); var initial = AssertCurrent(f);
        f.Send(Roll(), 1); f.Send(Lock(true), 2);
        Assert.False(f.Network.GenusInsightSnapshot.Domain.IsFresh);
        Assert.Same(initial.Domain.Value, f.Network.GenusInsightSnapshot.Domain.Value);
    }

    [Fact]
    public void ForeignOrOlderRepliesCannotChangeSharedData()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(), 1); var initial = AssertCurrent(f);
        f.Send(Roll(), 4, "synthetic-foreign"); f.Send(Lock(true), 5, "synthetic-foreign");
        Assert.Same(initial, AssertCurrent(f));
        f.Send(Roll(), -1); Assert.Same(initial, AssertCurrent(f));
    }

    [Fact]
    public void RepliesOnTheExactSharedTransportUseASeparateCurrentFullOwnWithoutRequiringItsConnection()
    {
        using var f = new Fixture(); f.Send(Own(), 0); const string shared = "synthetic-other-owned";
        f.Send(Full(), 1, shared); var before = AssertCurrent(f);
        Assert.Null(f.Network.CombatSnapshot.Pets.Value);
        f.Send(Lock(true), 2, shared); var locked = AssertCurrent(f);
        Assert.True(locked.Domain.Value!.GenusBoards[2].Panels[1][0].IsLocked);
        f.Send(Roll(), 3, shared); var rolled = AssertCurrent(f);
        Assert.Equal(16, rolled.Domain.Value!.GenusBoards[2].Panels[1][0].Value);
        Assert.Equal(before.Generation, rolled.Generation); Assert.True(rolled.Sequence > before.Sequence);
        Assert.Null(rolled.Domain.Value.SelectedGenusPanel); Assert.Null(f.Network.CombatSnapshot.Pets.Value);
        f.Send(Roll(), 4, "synthetic-unrelated"); Assert.Same(rolled, AssertCurrent(f));
    }

    [Fact]
    public void AQualifiedOwnChangeCannotMergeAnUnmatchedGlobalTransportOrKeepItsOldRollsLive()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(), 1, "synthetic-other-owned");
        var before = AssertCurrent(f); f.Send(Roll(), 2);
        var after = f.Network.GenusInsightSnapshot;
        Assert.False(after.Domain.IsFresh); Assert.True(after.Generation > before.Generation);
        f.Send(Lock(true), 3); Assert.False(f.Network.GenusInsightSnapshot.Domain.IsFresh);
        f.Send(Full(), 4); AssertCurrent(f);
    }

    [Theory]
    [InlineData("malformed-roll")]
    [InlineData("malformed-lock")]
    [InlineData("unknown-slot")]
    public void InvalidOwnChangesRevokeAuthorityAndOnlyAFullListRestoresIt(string invalid)
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Full(), 1); var before = AssertCurrent(f);
        f.Send(invalid switch { "malformed-roll" => [.. Roll(), 0], "malformed-lock" => [.. Lock(true), 0], _ => Lock(true, slot: 9) }, 2);
        var rejected = f.Network.GenusInsightSnapshot;
        Assert.False(rejected.Domain.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Conflict, rejected.Domain.Status);
        Assert.True(rejected.Generation > before.Generation);
        f.Send(Roll(), 3); f.Send(Lock(false), 4); Assert.False(f.Network.GenusInsightSnapshot.Domain.IsFresh);
        f.Send(Full(), 5); AssertCurrent(f);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("capture-gap")]
    [InlineData("malformed-full")]
    [InlineData("malformed-gain")]
    [InlineData("stop")]
    public void RealGlobalPetBoundariesInvalidateSharedEvidence(string boundary)
    {
        using var f = new Fixture(); f.Send(Full(), 0); var before = AssertCurrent(f);
        switch (boundary)
        {
            case "gap": f.Network.ProcessProgressGap(OwnStream); break;
            case "capture-gap": f.Network.ProcessProgressGap("*"); break;
            case "malformed-full": f.Send([0, 0x90], 1); break;
            case "malformed-gain": f.Send([0x0d, 0x90, 0xff], 1); break;
            default: f.Network.Stop(); break;
        }
        var after = f.Network.GenusInsightSnapshot;
        Assert.False(after.Domain.IsFresh); Assert.True(after.Generation > before.Generation);
        Assert.True(after.Sequence > before.Sequence); Assert.Null(after.Domain.Value!.SelectedGenusPanel);
    }

    [Fact]
    public void UntrackedForeignGapsAndOrdinarySoulGainSpendDoNotRewriteGenusOrDuplicatePickups()
    {
        using var f = new Fixture(); f.Send(Full(), 0); var before = AssertCurrent(f);
        f.Network.ProcessProgressGap("synthetic-untracked-foreign");
        f.Send(Points(0x900d, 3), 1); f.Send(Points(0x900e, 1), 2);
        Assert.Same(before, AssertCurrent(f)); Assert.Equal(23, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(3, Assert.Single(f.Gains).Souls);
        Assert.Equal(1, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(3, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
    }

    [Fact]
    public void AProgressCallbackGapCannotPublishTheOuterAcceptedListAfterItsWitnessWasRevoked()
    {
        using var f = new Fixture(); f.Send(Full(), 0); var before = AssertCurrent(f); var reset = false;
        f.Progress.Changed += () =>
        {
            if (reset || f.Progress.NetworkBaselineAt != At.AddSeconds(1)) return;
            reset = true; f.Network.ProcessProgressGap(OwnStream);
        };
        f.Send(Full(value: 77), 1);
        Assert.True(reset); Assert.False(f.Network.GenusInsightSnapshot.Domain.IsFresh);
        Assert.Same(before.Domain.Value, f.Network.GenusInsightSnapshot.Domain.Value);
    }

    [Fact]
    public void ASubscriberGapDuringPublicationCannotBeResurrectedByRemainingOuterCallbacks()
    {
        using var f = new Fixture(); var reset = false;
        f.Network.GenusInsightChanged += () =>
        {
            if (reset || !f.Network.GenusInsightSnapshot.Domain.IsFresh) return;
            reset = true; f.Network.ProcessProgressGap(OwnStream);
        };
        f.Send(Full(), 0);
        Assert.True(reset); Assert.False(f.Network.GenusInsightSnapshot.Domain.IsFresh);
        Assert.Null(f.Network.PetBaselineAt);
    }

    [Fact]
    public void ReentrantFreshListAfterInvalidationRemainsTheCurrentSource()
    {
        using var f = new Fixture(); f.Send(Full(), 0); var restored = false;
        f.Network.GenusInsightChanged += () =>
        {
            if (restored || f.Network.GenusInsightSnapshot.Domain.IsFresh) return;
            restored = true; f.Send(Full(value: 77), 2);
        };
        f.Network.ProcessProgressGap(OwnStream);
        Assert.True(restored); var current = AssertCurrent(f);
        Assert.Equal(77, current.Domain.Value!.GenusBoards[2].Panels[1][0].Value);
        Assert.Equal(At.AddSeconds(2), f.Network.PetBaselineAt);
    }

    [Fact]
    public void ReloadingPersistedPetCountsCannotCreateAnyLiveGenusSource()
    {
        using var f = new Fixture(); f.Send(Full(), 0);
        using var other = new NetworkLootService(new ProgressService(f.ProgressPath, f.Catalog));
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(other, true);
        Assert.Same(NetworkGenusInsightSnapshot.Empty, other.GenusInsightSnapshot);
        Assert.Null(other.GenusInsightSnapshot.Domain.Value); Assert.False(other.GenusInsightSnapshot.Domain.IsFresh);
    }

    private static NetworkGenusInsightSnapshot AssertCurrent(Fixture fixture)
    {
        var snapshot = fixture.Network.GenusInsightSnapshot;
        Assert.True(snapshot.Domain.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, snapshot.Domain.Status);
        Assert.True(snapshot.Generation > 0); Assert.True(snapshot.Sequence > 0); Assert.Equal(snapshot.Sequence, snapshot.Domain.Sequence);
        Assert.NotNull(snapshot.Domain.Value); Assert.Null(snapshot.Domain.Value.SelectedGenusPanel);
        return snapshot;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-shared-genus-" + Guid.NewGuid().ToString("N"));
        internal string ProgressPath => Path.Combine(_folder, "progress.json");
        internal PetCatalog Catalog { get; }
        internal string PetId { get; }
        internal ProgressService Progress { get; }
        internal NetworkLootService Network { get; }
        internal List<NetworkSoul> Gains { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            var source = PetIdTable.Find(PetNumber)!; PetId = source.CatalogId!;
            Catalog = new([new PetDefinition { Id = PetId, En = source.En, Genus = source.Genus, NumericId = PetNumber.ToString() }]);
            Progress = new(ProgressPath, Catalog);
            Network = new(Progress, new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.SoulGained += Gains.Add;
        }
        internal void Send(byte[] message, int seconds, string stream = OwnStream) => Network.ProcessProgressMessage(stream, message, At.AddSeconds(seconds));
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, true); }
    }

    private static byte[] Full(long value = 20, byte selected = 1) => Packet(0x9000, writer =>
    {
        var bit = 8; byte bits = 0; long position = 0;
        void Bool(bool value)
        {
            if (bit == 8) { bit = 0; bits = 0; position = writer.BaseStream.Position; writer.Write((byte)0); }
            if (value) bits |= (byte)(1 << bit); bit++;
            var cursor = writer.BaseStream.Position; writer.BaseStream.Position = position; writer.Write(bits); writer.BaseStream.Position = cursor;
        }
        writer.Write(0); V(writer, 1); writer.Write(1); writer.Write(PetNumber); writer.Write(1);
        V(writer, 1); writer.Write(PetNumber); writer.Write(21); V(writer, 0); V(writer, 0); V(writer, 5);
        for (byte creature = 2; creature <= 6; creature++)
        {
            writer.Write(creature); writer.Write(creature); writer.Write(10); writer.Write(0L); V(writer, 3);
            for (byte panel = 1; panel <= 3; panel++)
            {
                writer.Write(panel); V(writer, 9);
                for (byte slot = 0; slot <= 8; slot++)
                { writer.Write(slot); writer.Write((byte)1); writer.Write((ushort)193); writer.Write(value); Bool(false); Bool(true); }
            }
        }
        writer.Write(selected);
    });
    private static byte[] Points(ushort opcode, int amount) => Packet(opcode, writer => { V(writer, 1); writer.Write(PetNumber); writer.Write(amount); });
    private static byte[] Lock(bool value, byte slot = 0) => [0x1a, 0x90, 0, 0, 2, 1, slot, (byte)(value ? 1 : 0)];
    private static byte[] Roll() => Packet(0x9018, writer =>
    { writer.Write((ushort)0); writer.Write((byte)2); writer.Write((byte)1); V(writer, 1); writer.Write((byte)0); writer.Write((byte)5); writer.Write((ushort)19); writer.Write(16L); writer.Write((byte)2); writer.Write((byte)2); writer.Write(10); writer.Write(999L); });
    private static byte[] Candidate() => Packet(0x3620, writer => { writer.Write((ushort)0); V(writer, 300); writer.Write(0); writer.Write(101L); });
    private static byte[] Prepare() => Packet(0x3621, writer => writer.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300) => Packet(0x3633, writer =>
    {
        V(writer, entity); writer.Write(0); writer.Write((byte)1); var name = Encoding.UTF8.GetBytes("SyntheticGenus"); V(writer, (uint)name.Length); writer.Write(name);
        writer.Write((ushort)501); writer.Write(31); writer.Write(45); writer.Write(new byte[20]); writer.Write(1); writer.Write(0L);
        V(writer, 0); V(writer, 0); writer.Write(new byte[16]); writer.Write((byte)0); writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1); V(writer, 0); V(writer, 0);
        writer.Write(0); writer.Write(0); writer.Write(new byte[44]); V(writer, 0); V(writer, 0); V(writer, 0); writer.Write(new byte[12]); writer.Write(character); V(writer, 0); writer.Write((byte)1);
        writer.Write(new byte[16]); writer.Write(12345L); writer.Write(67890L); V(writer, 0); V(writer, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter writer, uint value)
    { while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; } writer.Write((byte)value); }
}
