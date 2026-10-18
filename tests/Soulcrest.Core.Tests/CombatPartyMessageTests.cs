using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Invented values only; capture payloads and identities never enter fixtures.</summary>
public sealed class CombatPartyMessageTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void FullSnapshotIncludesOwnAsAnOrdinaryMemberAndPreservesStableIdentity(int flags, int focusFlags)
    {
        var bytes = Snapshot((byte)flags, (byte)focusFlags);
        var result = Assert.IsType<CombatPartySnapshot>(CombatPartyMessage.TryParseSnapshot(bytes));
        Assert.Equal(5, result.MemberLimit);
        Assert.Equal(2, result.Members.Count);
        Assert.Equal(101, result.LeaderCharacterId);
        Assert.Equal(new CombatPartyMember(0, 301, 1, 2, 101, "SyntheticOne", 10, 123456, 70000, 90000, true, true), result.Members[0]);
        Assert.Equal(102, result.Members[1].CharacterId);
        Assert.Equal(11, result.Members[1].ClassDataId);
        EveryCutRejected(bytes, d => CombatPartyMessage.TryParseSnapshot(d));
        Assert.Null(CombatPartyMessage.TryParseSnapshot([.. bytes, 0]));
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(7)]
    public void MemberOptionalsAndPackedBooleansDoNotDisplaceCombatPower(int memberFlags)
    {
        using var p = new Packet(0x9200);
        Header(p, 0, 0); p.V(1); Member(p, (byte)memberFlags, 0, 301, 101); p.B(0);
        var member = Assert.Single(Assert.IsType<CombatPartySnapshot>(CombatPartyMessage.TryParseSnapshot(p.Bytes)).Members);
        Assert.Equal(123456, member.CombatPower);
        Assert.True(member.IsLoggedIn);
    }

    [Fact]
    public void RejectsUnobservedSnapshotExtensionsAndDuplicateIdentitiesEntitiesOrSlots()
    {
        foreach (var flag in new byte[] { 2, 4, 8, 16, 32, 128 })
        {
            var invalid = Snapshot(0, 0); invalid[2] = flag;
            Assert.Null(CombatPartyMessage.TryParseSnapshot(invalid));
        }
        foreach (var (slot, entity, id) in new[] { ((byte)0, 302, 102L), ((byte)1, 301, 102L), ((byte)1, 302, 101L) })
        {
            using var p = new Packet(0x9200); Header(p, 0, 0); p.V(2);
            Member(p, 0, 0, 301, 101); Member(p, 0, slot, entity, id); p.B(0);
            Assert.Null(CombatPartyMessage.TryParseSnapshot(p.Bytes));
        }
        foreach (var flags in new byte[] { 1, 2, 4, 5, 6, 8, 128 })
        {
            using var p = new Packet(0x9200); Header(p, 0, 0); p.V(1); Member(p, flags, 0, 301, 101); p.B(0);
            Assert.Null(CombatPartyMessage.TryParseSnapshot(p.Bytes));
        }
    }

    [Fact]
    public void StatusAndVitalsRemainUpdatesWithoutNameOrServerAuthority()
    {
        using var status = new Packet(0x921C);
        status.L(102); status.I(11); status.Zero(36); status.S("UnusedGuild");
        status.I(1); status.Bool(true); status.L(234567); status.B(0);
        Assert.Equal(new CombatPartyMemberStatus(102, 11, 234567), CombatPartyMessage.TryParseMemberStatus(status.Bytes));
        EveryCutRejected(status.Bytes, d => CombatPartyMessage.TryParseMemberStatus(d));
        using var vitals = new Packet(0x921B);
        vitals.V(302); vitals.V(0); vitals.V(90000); vitals.Zero(24); vitals.Bool(false);
        Assert.Equal(new CombatPartyMemberVitals(302, 0, 90000, false), CombatPartyMessage.TryParseMemberVitals(vitals.Bytes));
        EveryCutRejected(vitals.Bytes, d => CombatPartyMessage.TryParseMemberVitals(d));
        Assert.Null(CombatPartyMessage.TryParseMemberStatus(vitals.Bytes));
        Assert.Null(CombatPartyMessage.TryParseMemberVitals(status.Bytes));
    }

    [Fact]
    public void VitalsRejectOverflowNoncanonicalEncodingAndImpossibleHp()
    {
        foreach (var hp in new[] { 101UL, ulong.MaxValue })
        {
            using var p = new Packet(0x921B); p.V(301); p.V(hp); p.V(100); p.Zero(24); p.Bool(true);
            Assert.Null(CombatPartyMessage.TryParseMemberVitals(p.Bytes));
        }
        using var invalid = new Packet(0x921B); invalid.B(0x81); invalid.B(0); invalid.V(1); invalid.V(2); invalid.Zero(24); invalid.Bool(true);
        Assert.Null(CombatPartyMessage.TryParseMemberVitals(invalid.Bytes));
    }

    [Fact]
    public void DepartureIdentifiesOnlyThePartyToInvalidate()
    {
        using var p = new Packet(0x931F); p.L(55); p.B(3);
        Assert.Equal(new CombatPartyDeparture(55, 3), CombatPartyMessage.TryParseDeparture(p.Bytes));
        EveryCutRejected(p.Bytes, d => CombatPartyMessage.TryParseDeparture(d));
        Assert.Null(CombatPartyMessage.TryParseDeparture([.. p.Bytes, 0]));
        Assert.Null(CombatPartyMessage.TryParseSnapshot(p.Bytes));
    }

    private static byte[] Snapshot(byte flags, byte focusFlags)
    {
        using var p = new Packet(0x9200); Header(p, flags, focusFlags); p.V(2);
        Member(p, 7, 0, 301, 101); Member(p, 0, 1, 302, 102); p.B(2); return p.Bytes;
    }
    private static void Header(Packet p, byte flags, byte focusFlags)
    {
        p.B(flags); p.L(55); p.L(101); p.I(5); p.Zero(4);
        if ((flags & 1) != 0) { p.B(focusFlags); p.I(999); if ((focusFlags & 1) != 0) p.I(7); p.I(1); }
    }
    private static void Member(Packet p, byte flags, byte slot, int entity, long id)
    {
        p.B(flags); p.B(slot); p.I(entity); p.U(1); p.U(2); p.S("UnusedAccount"); p.L(id);
        p.S(id == 101 ? "SyntheticOne" : "SyntheticTwo"); p.I(id == 101 ? 10 : 11);
        p.V(70000); p.V(90000); p.Zero(48); p.Bool(true);
        if ((flags & 1) != 0) p.S("UnusedGuild"); if ((flags & 2) != 0) p.I(7);
        p.Zero(12); p.Bool(false); p.Bool(true);
        if ((flags & 4) != 0) { p.V(2); p.B(0); p.I(2001); p.B(1); p.B(1); p.I(2002); p.B(2); p.V(1000); }
        p.L(123456); p.B(0);
    }
    private static void EveryCutRejected(byte[] bytes, Func<byte[], object?> parse)
    {
        for (var i = 0; i < bytes.Length; i++) Assert.Null(parse(bytes[..i]));
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
