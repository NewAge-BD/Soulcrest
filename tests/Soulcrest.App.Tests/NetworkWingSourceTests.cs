using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkWingSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-wing-source-" + Guid.NewGuid().ToString("N"));
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
        internal void Login()
        {
            Send(Candidate(), 0); Send(List((30101100, 7), (30101200, 0)), 1);
            Send(Prepare(), 2); Send(Own(), 3);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public void EntryCollectionIsPrivateUntilFullOwnBindsTheMatchingCharacterAndActiveWingIsIndependent()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(List((30101100, 7), (30101200, 0)), 1);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
        f.Send(Prepare(), 2); f.Send(Own(), 3);
        var state = f.Network.CombatSnapshot;
        Assert.True(state.WingOwnership.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, state.WingOwnership.Status);
        Assert.Equal(At.AddSeconds(1), state.WingOwnership.ObservedAt);
        Assert.Equal("own-wing-ownership-list", state.WingOwnership.Source);
        Assert.Equal((byte)7, state.WingOwnership.Value!.Wings[30101100]);
        Assert.Equal(2, state.WingOwnership.Value.Wings.Count);
        Assert.Equal(new CharacterWingEquipment(30101100, 7, 30109999), state.EquippedWing.Value);
        Assert.True(state.EquippedWing.IsFresh); Assert.Equal(At.AddSeconds(3), state.EquippedWing.ObservedAt);
        Assert.DoesNotContain(30109999, state.WingOwnership.Value.Wings.Keys);
    }

    [Theory]
    [InlineData("character")] [InlineData("entity")] [InlineData("stream")] [InlineData("unprepared")] [InlineData("expired")]
    public void UnmatchedEntryCannotPromoteAProvisionalWingCollection(string variant)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(List((30101100, 7)), 1);
        if (variant != "unprepared") f.Send(Prepare(), 2);
        f.Send(Own(character: variant == "character" ? 102 : 101, entity: variant == "entity" ? 301u : 300u),
            variant == "expired" ? 124 : 3, variant == "stream" ? "synthetic-other" : "synthetic-own");
        Assert.False(f.Network.CombatSnapshot.WingOwnership.IsFresh);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
    }

    [Fact]
    public void ListWithoutAnOwnOrEntryAndChangesBeforeFullOwnNeverCreateAuthority()
    {
        using var f = new Fixture(); f.Send(List((30101100, 7)), 0);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
        f.Send(Candidate(), 1); f.Send(Change(300, false, 30101200), 2); f.Send(Prepare(), 3);
        f.Send(Change(300, true, 30109998), 4); f.Send(Own(), 5);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
        Assert.Equal(new CharacterWingEquipment(30101100, 7, 30109999), f.Network.CombatSnapshot.EquippedWing.Value);
    }

    [Fact]
    public void CosmeticAndStatWingChangesNeverOverwriteEachOthersChannelsOrInferOwnership()
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot;
        f.Send(Change(300, true, 30109998), 4);
        Assert.Equal(new CharacterWingEquipment(30101100, 7, 30109998), f.Network.CombatSnapshot.EquippedWing.Value);
        Assert.Same(original.WingOwnership, f.Network.CombatSnapshot.WingOwnership);
        f.Send(Change(300, false, 30101200), 5);
        Assert.Equal(new CharacterWingEquipment(30101200, null, 30109998), f.Network.CombatSnapshot.EquippedWing.Value);
        f.Send(Change(300, true, 0), 6);
        Assert.Equal(new CharacterWingEquipment(30101200, null, 0), f.Network.CombatSnapshot.EquippedWing.Value);
        f.Send(Change(300, false, 0), 7);
        Assert.Equal(new CharacterWingEquipment(0, null, 0), f.Network.CombatSnapshot.EquippedWing.Value);
        Assert.True(f.Network.CombatSnapshot.EquippedWing.IsFresh);
        Assert.Same(original.WingOwnership, f.Network.CombatSnapshot.WingOwnership);
        Assert.Equal(new CharacterWingEquipment(30101100, 7, 30109999), original.EquippedWing.Value);
    }

    [Fact]
    public void ForeignStreamsEntitiesMalformedForeignTailsAndOlderChangesLeaveTheOwnObservationUntouched()
    {
        using var f = new Fixture(); f.Login(); f.Send(Change(300, false, 30101200), 5);
        var current = f.Network.CombatSnapshot;
        f.Send(Change(301, false, 1), 6); f.Send([.. Change(301, false, 1), 0], 6);
        f.Send(Change(300, false, 1), 6, "synthetic-other");
        f.Send(Change(300, false, 1), 4); f.Send([0x76, 0x56], 4);
        Assert.Same(current, f.Network.CombatSnapshot);
        Assert.Equal(At.AddSeconds(5), current.EquippedWing.ObservedAt);
    }

    [Fact]
    public void MalformedListsInvalidateOnlyOwnershipAndFullEmptyListsReplaceTheCollection()
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot;
        f.Send([0x72, 0x56], 4);
        var rejected = f.Network.CombatSnapshot;
        Assert.Equal(NetworkCombatSourceStatus.Conflict, rejected.WingOwnership.Status);
        Assert.False(rejected.WingOwnership.IsFresh); Assert.Same(original.EquippedWing, rejected.EquippedWing);
        Assert.Equal(2, rejected.WingOwnership.Value!.Wings.Count);
        f.Send(List(), 5);
        Assert.True(f.Network.CombatSnapshot.WingOwnership.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Complete, f.Network.CombatSnapshot.WingOwnership.Status);
        Assert.Empty(f.Network.CombatSnapshot.WingOwnership.Value!.Wings);
        Assert.Equal(2, original.WingOwnership.Value!.Wings.Count);
    }

    [Fact]
    public void MalformedOwnChangesInvalidateOnlyEquipmentAndACosmeticRecoveryCannotInventAStatWing()
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot;
        f.Send([.. Change(300, false, 30101200), 0], 4);
        Assert.False(f.Network.CombatSnapshot.EquippedWing.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.EquippedWing.Status);
        Assert.Same(original.WingOwnership, f.Network.CombatSnapshot.WingOwnership);
        f.Send(Change(300, true, 30109998), 5);
        Assert.Equal(new CharacterWingEquipment(null, null, 30109998), f.Network.CombatSnapshot.EquippedWing.Value);
        Assert.Equal(NetworkCombatSourceStatus.Partial, f.Network.CombatSnapshot.EquippedWing.Status);
        f.Send(Change(300, false, 30101200), 6);
        Assert.Equal(new CharacterWingEquipment(30101200, null, 30109998), f.Network.CombatSnapshot.EquippedWing.Value);
    }

    [Fact]
    public void DuplicateProvisionalCollectionCannotBePromotedAsComplete()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0);
        f.Send(List((30101100, 0)), 1); f.Send(List((30101100, 0)), 1); f.Send(Prepare(), 2); f.Send(Own(), 3);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.WingOwnership.Status);
        Assert.False(f.Network.CombatSnapshot.WingOwnership.IsFresh);
        Assert.True(f.Network.CombatSnapshot.EquippedWing.IsFresh);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GapsAndCharacterChangesCannotReuseAnOldCharactersFreshWings(bool global)
    {
        using var f = new Fixture(); f.Login();
        f.Network.ProcessProgressGap(global ? "*" : "synthetic-own", capturedAtUtc: At.AddSeconds(4));
        Assert.False(f.Network.CombatSnapshot.WingOwnership.IsFresh); Assert.False(f.Network.CombatSnapshot.EquippedWing.IsFresh);
        f.Send(Change(300, false, 1), 5); Assert.False(f.Network.CombatSnapshot.EquippedWing.IsFresh);
        f.Send(Own(character: 102, entity: 301, wing: 40101100, skin: 40109999), 6);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
        Assert.Equal(40101100, f.Network.CombatSnapshot.EquippedWing.Value!.WingId);
        f.Send(List((40101100, 2)), 7);
        Assert.Single(f.Network.CombatSnapshot.WingOwnership.Value!.Wings);
        f.Send(Own(), 8); Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
    }

    [Fact]
    public void SameCharacterLoadsKeepHistoricalOwnershipStaleUntilAnotherFullCollectionAndRefreshEquipmentIndependently()
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot;
        f.Send(Own(wing: 30101200, level: 3, skin: 0), 4);
        Assert.False(f.Network.CombatSnapshot.WingOwnership.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Stale, f.Network.CombatSnapshot.WingOwnership.Status);
        Assert.Same(original.WingOwnership.Value, f.Network.CombatSnapshot.WingOwnership.Value);
        Assert.Equal(new CharacterWingEquipment(30101200, 3, 0), f.Network.CombatSnapshot.EquippedWing.Value);
        f.Send(List((30101100, 7), (30101200, 3)), 5);
        Assert.True(f.Network.CombatSnapshot.WingOwnership.IsFresh);
    }

    [Fact]
    public void MissingOwnWingFlagsRemainUnknownInsteadOfEmptyAndSkinOnlyChangesRemainPartial()
    {
        using var f = new Fixture(); f.Send(Own(wing: null, level: null, skin: null), 0);
        Assert.Null(f.Network.CombatSnapshot.EquippedWing.Value);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, f.Network.CombatSnapshot.EquippedWing.Status);
        f.Send(Change(300, true, 1), 1);
        Assert.Equal(new CharacterWingEquipment(null, null, 1), f.Network.CombatSnapshot.EquippedWing.Value);
        Assert.Equal(NetworkCombatSourceStatus.Partial, f.Network.CombatSnapshot.EquippedWing.Status);
        Assert.Null(f.Network.CombatSnapshot.WingOwnership.Value);
    }

    private static byte[] List(params (int Id, byte Level)[] wings) => Packet(0x5672, w =>
    { V(w, (uint)wings.Length); foreach (var wing in wings) { w.Write(wing.Id); w.Write(wing.Level); } });
    private static byte[] Change(uint entity, bool skin, int id) => Packet(0x5676, w =>
    { V(w, entity); w.Write((byte)(skin ? 1 : 0)); w.Write(id); });
    private static byte[] Candidate(long character = 101, uint entity = 300) => Packet(0x3620, w =>
    { w.Write((ushort)0); V(w, entity); w.Write(0); w.Write(character); });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300, int? wing = 30101100, byte? level = 7, int? skin = 30109999) => Packet(0x3633, w =>
    {
        V(w, entity); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(31); w.Write(45); w.Write(new byte[20]); w.Write(1); w.Write(0L);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        var flags = (ushort)((wing.HasValue ? 4096 : 0) | (level.HasValue ? 8192 : 0) | (skin.HasValue ? 16384 : 0));
        w.Write(flags); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0);
        w.Write(0); w.Write(0); w.Write(new byte[44]);
        if (wing is { } id) w.Write(id); if (level is { } enchant) w.Write(enchant); if (skin is { } appearance) w.Write(appearance);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(character); V(w, 0); w.Write((byte)1);
        w.Write(new byte[16]); w.Write(12345L); w.Write(67890L); V(w, 0); V(w, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter w, uint value)
    { while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; } w.Write((byte)value); }
}
