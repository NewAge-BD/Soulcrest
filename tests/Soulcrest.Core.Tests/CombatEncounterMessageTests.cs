using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Invented packet values only; no capture data, identities or real player names.</summary>
public sealed class CombatEncounterMessageTests
{
    [Theory]
    [InlineData(0x001C, 0x0200)] [InlineData(0x001F, 0x0240)]
    [InlineData(0x002C, 0x0200)] [InlineData(0x005F, 0x0240)]
    [InlineData(0x200C, 0x0200)] [InlineData(0x200D, 0x0240)]
    [InlineData(0x2C0C, 0x0200)] [InlineData(0x2C0D, 0x0240)]
    [InlineData(0x2004, 0x0200)] [InlineData(0x2005, 0x0200)]
    [InlineData(0x101D, 0x0208)] [InlineData(0x105D, 0x0208)]
    [InlineData(0x0014, 0x0200)] [InlineData(0x1014, 0x0200)]
    [InlineData(0x200F, 0x0200)] [InlineData(0x220C, 0x0200)]
    [InlineData(0x101F, 0x0200)] [InlineData(0x101F, 0x0240)]
    [InlineData(0x1024, 0x0200)] [InlineData(0x101D, 0x0248)]
    public void AppearanceConsumesEveryObservedFlagFormAndReturnsNeutralIdentityAndHp(int flags, int state)
    {
        var bytes = Spawn((ushort)flags, (ushort)state);
        var npc = Assert.IsType<CombatNpcAppearance>(CombatEncounterMessage.TryParseNpcAppearance(bytes));
        Assert.Equal((ushort)flags, npc.RawFlags);
        Assert.Equal((ushort)state, npc.RawStateFlags);
        Assert.Equal(101, npc.EntityKey);
        Assert.Equal(1001, npc.NpcDataId);
        Assert.Equal(700, npc.CurrentHp);
        Assert.Equal(1000, npc.MaximumHp);
        Assert.Equal((byte)1, npc.RawLifeState);
        Assert.Equal((state & 64) != 0 ? (byte)1 : (byte?)null, npc.RawCombatState);
        Assert.Equal((flags & 16) != 0 ? 202 : (int?)null, npc.ParentEntityKey);
        Assert.Equal((flags & 32) != 0 ? 10 : (int?)null, npc.ParentDescriptionDataId);
        Assert.Equal((flags & 8192) != 0 ? 678 : (int?)null, npc.SpawnUniqueKey);
        Assert.Equal((byte)3, npc.RawSpawnReason);
        RejectEveryCutAndTail(bytes, d => CombatEncounterMessage.TryParseNpcAppearance(d));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void DescriptionAndManyAbnormalsDoNotDisplaceHpOrTrailingSpawnFields(int descriptionFlags)
    {
        var npc = Assert.IsType<CombatNpcAppearance>(CombatEncounterMessage.TryParseNpcAppearance(
            Spawn(0x2C0D, 0x0240, (byte)descriptionFlags, 12)));
        Assert.Equal(678, npc.SpawnUniqueKey);
        Assert.Equal(1000, npc.MaximumHp);
        Assert.Null(npc.ParentEntityKey);
    }

    [Fact]
    public void ParentDescriptionCanBeAClassAndNeitherZeroHpNorAppearanceProvesABoss()
    {
        var npc = Assert.IsType<CombatNpcAppearance>(CombatEncounterMessage.TryParseNpcAppearance(
            Spawn(0x002C, 0x0200, hp: 0)));
        Assert.Equal(10, npc.ParentDescriptionDataId);
        Assert.Null(npc.ParentEntityKey);
        Assert.Equal(0, npc.CurrentHp);
        Assert.Equal((byte)1, npc.RawLifeState);
    }

    [Fact]
    public void RejectsUnobservedPresenceFlagsInvalidHealthAndNoncanonicalEntity()
    {
        foreach (var flags in new ushort[] { 0, 4, 0x004C, 0x204C, 0xFFFF })
            Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(flags, 0x0200)));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(0x200C, 0)));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(0x200C, 0x0200, 2)));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(0x200C, 0x0200, hp: 1001)));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(0x200C, 0x0200, hp: ulong.MaxValue)));
        var overlong = Spawn(0x200C, 0x0200).ToList(); overlong[2] = 0xE5; overlong.Insert(3, 0);
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(overlong.ToArray()));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn(0x220C, 0x0200, groggyFlags: 1)));
    }

    [Theory]
    [InlineData(0x101F, 0x0200)] [InlineData(0x101F, 0x0240)]
    [InlineData(0x1024, 0x0200)] [InlineData(0x101D, 0x0248)]
    public void CompositeAppearanceFormsRejectUnknownPresenceAndStateBits(int flags, int state)
    {
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn((ushort)(flags | 0x8000), (ushort)state)));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(Spawn((ushort)flags, (ushort)(state | 0x8000))));
    }

    [Fact]
    public void DeparturePreservesReasonWithoutClaimingDeath()
    {
        using var p = new Packet(0x3642); p.V(101); p.B(0); p.B(7);
        Assert.Equal(new CombatNpcDeparture(101, 7, null), CombatEncounterMessage.TryParseNpcDeparture(p.Bytes));
        RejectEveryCutAndTail(p.Bytes, d => CombatEncounterMessage.TryParseNpcDeparture(d));
        var extension = p.Bytes; extension[3] = 1;
        Assert.Null(CombatEncounterMessage.TryParseNpcDeparture(extension));
    }

    [Fact]
    public void DeathReturnsEntityEvidenceAndSkipsAllKillerNamesAndServerFields()
    {
        using var p = new Packet(0x8D04); p.V(101); p.I(2001); p.V(202); p.U(1);
        p.S("UnusedSyntheticKiller"); p.S("UnusedSyntheticGuild"); p.U(2); p.V(1002); p.I(33); p.B(1); p.B(2);
        Assert.Equal(new CombatEntityDeath(101, 202, 2001, 1002, 1, 2), CombatEncounterMessage.TryParseDeath(p.Bytes));
        RejectEveryCutAndTail(p.Bytes, d => CombatEncounterMessage.TryParseDeath(d));
        Assert.Null(CombatEncounterMessage.TryParseNpcAppearance(p.Bytes));
    }

    [Fact]
    public void DeathCanHaveNoCasterWithoutInventingOne()
    {
        using var p = new Packet(0x8D04); p.V(101); p.I(0); p.V(0); p.U(0); p.S(""); p.S(""); p.U(0); p.V(0); p.I(0); p.B(0); p.B(0);
        Assert.Equal(new CombatEntityDeath(101, 0, 0, 0, 0, 0), CombatEncounterMessage.TryParseDeath(p.Bytes));
    }

    [Fact]
    public void MiddleBossRewardContainsOnlyAnNpcDataIdAndRequiresExactBody()
    {
        using var p = new Packet(0x572E); p.I(1001);
        Assert.Equal(new CombatMiddleBossReward(1001), CombatEncounterMessage.TryParseMiddleBossReward(p.Bytes));
        RejectEveryCutAndTail(p.Bytes, d => CombatEncounterMessage.TryParseMiddleBossReward(d));
        using var zero = new Packet(0x572E); zero.I(0);
        Assert.Null(CombatEncounterMessage.TryParseMiddleBossReward(zero.Bytes));
        Assert.Null(CombatEncounterMessage.TryParseDeath(p.Bytes));
    }

    private static byte[] Spawn(ushort flags, ushort state, byte desc = 0, int abnormals = 1, ulong hp = 700, byte groggyFlags = 3)
    {
        using var p = new Packet(0x3641); p.V(101); p.U(flags); p.B(desc);
        if ((desc & 1) != 0) p.S("UnusedSyntheticNpc"); p.I(1001);
        p.U(state); p.Zero(18); if ((state & 8) != 0) p.Zero(12); p.B(1); p.V(hp); p.V(1000); p.Zero(52);
        if ((state & 64) != 0) p.B(1); p.B(0);
        p.V((ulong)abnormals);
        for (var i = 0; i < abnormals; i++)
        {
            var f = i % 2 == 0 ? (byte)0x11 : (byte)0x13;
            p.B(f); p.V((ulong)i); p.I(3001); p.L(15000); p.L(1700000015000);
            if ((f & 1) != 0) p.V(202); p.B(4); if ((f & 2) != 0) p.I(2001);
            p.Bool(i % 2 == 0); p.Zero(12);
        }
        if ((flags & 1) != 0) p.B(2); if ((flags & 2) != 0) p.B(3);
        if ((flags & 4) != 0) p.B(3); if ((flags & 8) != 0) p.Bool(true);
        if ((flags & 16) != 0) p.I(202); if ((flags & 32) != 0) { p.B(0); p.I(10); }
        if ((flags & 64) != 0) { p.L(1); p.S("UnusedGuild"); p.Zero(17); p.S("UnusedUnion"); }
        p.V(1); p.U(1); p.I(55); p.I(50);
        if ((flags & 512) != 0) { p.B(groggyFlags); p.B(1); p.I(100); p.I(20); p.Bool(false); p.Bool(true); }
        if ((flags & 1024) != 0) p.Zero(9); if ((flags & 2048) != 0) p.L(1700000000000);
        if ((flags & 4096) != 0) p.I(445);
        if ((flags & 8192) != 0) p.I(678); p.V(2); p.I(1); p.I(2);
        return p.Bytes;
    }
    private static void RejectEveryCutAndTail(byte[] bytes, Func<byte[], object?> parse)
    {
        for (var i = 0; i < bytes.Length; i++) Assert.Null(parse(bytes[..i]));
        Assert.Null(parse([.. bytes, 0]));
    }
    private sealed class Packet(ushort opcode) : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private BinaryWriter? _writer;
        private int _boolIndex = -1, _boolBit;
        private BinaryWriter Writer { get { if (_writer is null) { _writer = new(_stream, Encoding.UTF8, true); _writer.Write(opcode); } return _writer; } }
        public byte[] Bytes { get { _ = Writer; return _stream.ToArray(); } }
        public void B(byte value) => Writer.Write(value);
        public void U(ushort value) => Writer.Write(value);
        public void I(int value) => Writer.Write(value);
        public void L(long value) => Writer.Write(value);
        public void Zero(int count) => Writer.Write(new byte[count]);
        public void S(string value) { var bytes = Encoding.UTF8.GetBytes(value); V((ulong)bytes.Length); Writer.Write(bytes); }
        public void V(ulong value) { do { var b = (byte)(value & 127); value >>= 7; B((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0); }
        public void Bool(bool value)
        {
            _ = Writer;
            if (_boolIndex < 0 || _boolBit == 8) { _boolIndex = (int)_stream.Position; _boolBit = 0; B(0); }
            if (value) _stream.GetBuffer()[_boolIndex] |= (byte)(1 << _boolBit);
            _boolBit++;
        }
        public void Dispose() { _writer?.Dispose(); _stream.Dispose(); }
    }
}
