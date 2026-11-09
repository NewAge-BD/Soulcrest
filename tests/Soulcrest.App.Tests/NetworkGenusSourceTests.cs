using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkGenusSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-genus-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])),
                new ExplorationService(Path.Combine(_root, "exploration.json"), []));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] message, int second, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Login() { Send(Own(), 0); Send(Snapshot(), 1); }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public void LockRepliesMatchTheUniqueValueCreatureTypeAndKeepIndependentStorageKeys()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Snapshot(storageKey: 42), 1);
        var before = f.Network.CombatSnapshot.Pets.Value!;
        f.Send(Lock(true), 2);
        var current = f.Network.CombatSnapshot.Pets;
        Assert.True(current.IsFresh); Assert.Equal("own-pet-genus-lock-update", current.Source);
        Assert.Equal((byte)42, Assert.Single(current.Value!.GenusBoards).Key);
        Assert.True(current.Value.GenusBoards[42].Panels[1][0].IsLocked);
        Assert.False(before.GenusBoards[42].Panels[1][0].IsLocked);
        f.Send(Snapshot(storageKey: 42, duplicateType: true), 3);
        var ambiguous = f.Network.CombatSnapshot.Pets.Value;
        Assert.Equal(2, ambiguous!.GenusBoards.Count);
        f.Send(Lock(false), 4);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Pets.Status);
        Assert.Same(ambiguous, f.Network.CombatSnapshot.Pets.Value);
    }

    [Fact]
    public void RollRepliesMatchTheUniqueValueCreatureTypeAndNeverReplaceTheStorageKeyWithItsType()
    {
        using var f = new Fixture(); f.Send(Own(), 0); f.Send(Snapshot(storageKey: 42), 1);
        var before = f.Network.CombatSnapshot.Pets.Value!;
        f.Send(Roll(), 2);
        var current = f.Network.CombatSnapshot.Pets;
        Assert.True(current.IsFresh); Assert.Equal("own-pet-genus-roll-update", current.Source);
        Assert.Equal((byte)42, Assert.Single(current.Value!.GenusBoards).Key);
        Assert.Equal((ushort)591, current.Value.GenusBoards[42].Panels[2][0].StatType);
        Assert.Equal((ushort)193, before.GenusBoards[42].Panels[2][0].StatType);
        Assert.Null(current.Value.SelectedGenusPanel);
        f.Send(Snapshot(storageKey: 42, duplicateType: true), 3);
        var ambiguous = f.Network.CombatSnapshot.Pets.Value;
        f.Send(Roll(), 4);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Pets.Status);
        Assert.Same(ambiguous, f.Network.CombatSnapshot.Pets.Value);
    }

    [Fact]
    public void AQualifiedRollMergesOnlyExplicitSlotsAndCannotDeclareItsTargetPanelEquipped()
    {
        using var f = new Fixture(); f.Login(); var before = f.Network.CombatSnapshot.Pets.Value!;
        f.Send(Roll(), 2);
        var current = f.Network.CombatSnapshot.Pets;
        Assert.True(current.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, current.Status);
        Assert.Equal("own-pet-genus-roll-update", current.Source); Assert.Null(current.Value!.SelectedGenusPanel);
        Assert.Equal(before.GenusBoards[2].Panels[1], current.Value.GenusBoards[2].Panels[1]);
        var changed = current.Value.GenusBoards[2].Panels[2][0];
        Assert.Equal(before.GenusBoards[2].Panels[2][1], current.Value.GenusBoards[2].Panels[2][1]);
        Assert.Equal((ushort)591, changed.StatType); Assert.Equal(30L, changed.Value);
        Assert.False(changed.IsLocked); Assert.True(changed.IsActivated);
        Assert.Equal(999L, current.Value.GenusBoards[2].Experience);
        Assert.Equal(123L, before.GenusBoards[2].Experience);
        Assert.Same(before.Levels, current.Value.Levels); Assert.Same(before.AvailableSouls, current.Value.AvailableSouls);
        f.Send(Lock(true), 3); Assert.Equal("own-pet-genus-lock-update", f.Network.CombatSnapshot.Pets.Source);
        Assert.Null(f.Network.CombatSnapshot.Pets.Value!.SelectedGenusPanel);
        f.Send(Roll(panel: 1), 4); Assert.True(f.Network.CombatSnapshot.Pets.IsFresh);
        Assert.Null(f.Network.CombatSnapshot.Pets.Value!.SelectedGenusPanel);
        f.Send(Snapshot(), 5); Assert.Equal((byte)1, f.Network.CombatSnapshot.Pets.Value!.SelectedGenusPanel);
    }

    [Fact]
    public void RollsRetainAConfirmedSamePanelButCannotCreateListsOrRepairGaps()
    {
        using var f = new Fixture(); f.Send(Roll(panel: 1), 0); Assert.Null(f.Network.CombatSnapshot.Pets.Value);
        f.Login(); f.Send(Roll(panel: 1), 2);
        Assert.Equal((byte)1, f.Network.CombatSnapshot.Pets.Value!.SelectedGenusPanel);
        var current = f.Network.CombatSnapshot;
        f.Send(Roll(), 3, "synthetic-other"); f.Send(Roll(), 1);
        Assert.Same(current, f.Network.CombatSnapshot);
        f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(4));
        var stale = f.Network.CombatSnapshot.Pets;
        f.Send(Roll(), 5); Assert.Same(stale, f.Network.CombatSnapshot.Pets);
    }

    [Fact]
    public void InvalidOrAbsentRollTargetsInvalidateWithoutErasingTheBaseline()
    {
        using var f = new Fixture(); f.Login(); var before = f.Network.CombatSnapshot.Pets.Value;
        f.Send(Roll(panel: 3), 2);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Pets.Status);
        Assert.Same(before, f.Network.CombatSnapshot.Pets.Value);
        f.Send(Roll(), 3); Assert.False(f.Network.CombatSnapshot.Pets.IsFresh);
        f.Send(Snapshot(), 4); f.Send([.. Roll(), 0], 5);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Pets.Status);
    }

    [Fact]
    public void ObservedLockChangesRefineOnlyTheirExactSlotAndRetainTheBaselineAndStatPrecision()
    {
        using var f = new Fixture(); f.Login();
        var before = f.Network.CombatSnapshot.Pets;
        f.Send(Lock(true), 2);
        var current = f.Network.CombatSnapshot.Pets;
        Assert.True(current.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, current.Status);
        Assert.Equal("own-pet-genus-lock-update", current.Source); Assert.Equal(At.AddSeconds(2), current.ObservedAt);
        Assert.Equal((byte)1, current.Value!.SelectedGenusPanel);
        Assert.Same(before.Value!.Levels, current.Value.Levels); Assert.Same(before.Value.AvailableSouls, current.Value.AvailableSouls);
        Assert.Same(before.Value.AcquiredCollections, current.Value.AcquiredCollections);
        Assert.True(current.Value.GenusBoards[2].Panels[1][0].IsLocked);
        Assert.True(current.Value.GenusBoards[2].Panels[1][0].IsActivated);
        Assert.Equal(114L, current.Value.GenusBoards[2].Panels[1][0].Value);
        Assert.Equal(before.Value.GenusBoards[2].Panels[2], current.Value.GenusBoards[2].Panels[2]);
        Assert.False(before.Value.GenusBoards[2].Panels[1][0].IsLocked);
        f.Send(Lock(false), 3);
        Assert.False(f.Network.CombatSnapshot.Pets.Value!.GenusBoards[2].Panels[1][0].IsLocked);
    }

    [Fact]
    public void LockRepliesCannotCreateAnOwnIdentityOrAListFromPreparedPrefixes()
    {
        using var f = new Fixture(); f.Send(Lock(true), 0);
        Assert.Null(f.Network.CombatSnapshot.Pets.Value);
        f.Send(Candidate(), 1); f.Send(Prepare(), 2); f.Send(Snapshot(), 3); f.Send(Lock(true), 4); f.Send(Own(), 5);
        Assert.True(f.Network.CombatSnapshot.Pets.IsFresh);
        Assert.Equal("own-pet-genus-list", f.Network.CombatSnapshot.Pets.Source);
        Assert.False(f.Network.CombatSnapshot.Pets.Value!.GenusBoards[2].Panels[1][0].IsLocked);
        f.Send(Own(character: 102, entity: 301), 6); f.Send(Lock(true), 7);
        Assert.Null(f.Network.CombatSnapshot.Pets.Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GapsAndWorldTransitionsCannotBeRepairedByALockDelta(bool worldTransition)
    {
        using var f = new Fixture(); f.Login();
        if (worldTransition) f.Send(Own(), 2);
        else f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(2));
        var retained = f.Network.CombatSnapshot.Pets;
        Assert.False(retained.IsFresh);
        f.Send(Lock(true), 3);
        Assert.Same(retained, f.Network.CombatSnapshot.Pets);
        Assert.False(retained.Value!.GenusBoards[2].Panels[1][0].IsLocked);
    }

    [Fact]
    public void ForeignStreamsAndOlderRepliesDoNotChangeOrRefreshCurrentFacts()
    {
        using var f = new Fixture(); f.Login(); f.Send(Lock(true), 3);
        var current = f.Network.CombatSnapshot;
        f.Send(Lock(false), 4, "synthetic-other"); f.Send(Lock(false), 2); f.Send([0x1A, 0x90], 2);
        Assert.Same(current, f.Network.CombatSnapshot);
    }

    [Theory]
    [InlineData("malformed")] [InlineData("board")] [InlineData("panel")] [InlineData("slot")]
    public void AmbiguousRepliesDoNotInventEntriesAndAFullListIsRequiredForRecovery(string variant)
    {
        using var f = new Fixture(); f.Login(); var before = f.Network.CombatSnapshot.Pets.Value;
        var message = Lock(true);
        if (variant == "malformed") message = [.. message, 0];
        else message[variant == "board" ? 4 : variant == "panel" ? 5 : 6] = variant == "board" ? (byte)3 : (byte)2;
        if (variant == "panel") message[5] = 3;
        f.Send(message, 2);
        Assert.False(f.Network.CombatSnapshot.Pets.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Pets.Status);
        Assert.Same(before, f.Network.CombatSnapshot.Pets.Value);
        f.Send(Lock(true), 3); Assert.False(f.Network.CombatSnapshot.Pets.IsFresh);
        f.Send(Snapshot(), 4); Assert.True(f.Network.CombatSnapshot.Pets.IsFresh);
    }

    private static byte[] Lock(bool locked) => [0x1A, 0x90, 0, 0, 2, 1, 1, (byte)(locked ? 1 : 0)];
    private static byte[] Roll(byte panel = 2) => Packet(0x9018, w =>
    {
        w.Write((ushort)0); w.Write((byte)2); w.Write(panel); V(w, 1);
        w.Write((byte)1); w.Write((byte)4); w.Write((ushort)591); w.Write(30L); w.Write((byte)2);
        w.Write((byte)2); w.Write(10); w.Write(999L);
    });
    private static byte[] Snapshot(byte storageKey = 2, bool duplicateType = false) => Packet(0x9000, w =>
    {
        var bit = 8; byte bits = 0; long bitPosition = 0;
        void Bool(bool value)
        {
            if (bit == 8) { bit = 0; bits = 0; bitPosition = w.BaseStream.Position; w.Write((byte)0); }
            if (value) bits |= (byte)(1 << bit);
            bit++;
            var cursor = w.BaseStream.Position; w.BaseStream.Position = bitPosition; w.Write(bits); w.BaseStream.Position = cursor;
        }
        w.Write(0); V(w, 1); w.Write(1); w.Write(1135); w.Write(3);
        V(w, 1); w.Write(1135); w.Write(2); V(w, 0); V(w, 1); w.Write(9);
        V(w, duplicateType ? 2u : 1u);
        for (var board = 0; board < (duplicateType ? 2 : 1); board++)
        {
            w.Write((byte)(storageKey + board)); w.Write((byte)2); w.Write(10); w.Write(123L); V(w, 2);
            w.Write((byte)1); V(w, 1); w.Write((byte)1); w.Write((byte)3); w.Write((ushort)57); w.Write(114L); Bool(false); Bool(true);
            w.Write((byte)2); V(w, 2); w.Write((byte)1); w.Write((byte)2); w.Write((ushort)193); w.Write(29L); Bool(true); Bool(false);
            w.Write((byte)7); w.Write((byte)5); w.Write((ushort)19); w.Write(9L); Bool(false); Bool(true);
        }
        w.Write((byte)1);
    });
    private static byte[] Candidate() => Packet(0x3620, w =>
    { w.Write((ushort)0); V(w, 300); w.Write(0); w.Write(101L); });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300) => Packet(0x3633, w =>
    {
        V(w, entity); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(31); w.Write(45); w.Write(new byte[20]); w.Write(1); w.Write(0L);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0);
        w.Write(0); w.Write(0); w.Write(new byte[44]); V(w, 0); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(character); V(w, 0); w.Write((byte)1);
        w.Write(new byte[16]); w.Write(12345L); w.Write(67890L); V(w, 0); V(w, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter w, uint value)
    { while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; } w.Write((byte)value); }
}
