using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatEffectMessageTests
{
    [Theory]
    [InlineData(17)] [InlineData(19)]
    public void AddsPreserveOptionalWireValuesWithoutInventingStacksOrCoordinates(int flags)
    {
        using var p = new Packet(0x382A); p.V(301); p.V(1); Entry(p, (byte)flags, 7, 1001, true);
        var update = Assert.IsType<CombatEffectUpdate>(CombatEffectMessage.TryParse(p.Bytes));
        Assert.Equal(CombatEffectOperation.Add, update.Operation);
        Assert.Equal(301, update.TargetEntityKey);
        var entry = Assert.Single(update.Entries);
        Assert.Equal(new CombatEffectEntry(7, 1001, 15000, 1700000015000,
            (flags & 1) != 0 ? 302 : null, 4, (flags & 2) != 0 ? 2001 : null, true,
            (flags & 4) != 0 ? 303 : null, (flags & 8) != 0 ? 9 : null), entry);
        EveryCutRejected(p.Bytes);
        Assert.Null(CombatEffectMessage.TryParse([.. p.Bytes, 0]));
    }

    [Fact]
    public void InterleavedScalarReadsKeepPackedBooleansAcrossManyEntries()
    {
        using var p = new Packet(0x382A); p.V(301); p.V(20);
        for (var i = 0; i < 20; i++) Entry(p, 19, i, 1001 + i, i % 2 == 0);
        var update = Assert.IsType<CombatEffectUpdate>(CombatEffectMessage.TryParse(p.Bytes));
        Assert.Equal(20, update.Entries.Count);
        for (var i = 0; i < 20; i++) Assert.Equal(i % 2 == 0, update.Entries[i].ReadyToRemove);
        EveryCutRejected(p.Bytes);
    }

    [Fact]
    public void ChangesMayReplaceEffectDataForTheSameUidAndDoNotImplyACast()
    {
        using var p = new Packet(0x382B); p.V(301); Entry(p, 19, 7, 1002, false); p.Bool(true);
        var update = Assert.IsType<CombatEffectUpdate>(CombatEffectMessage.TryParse(p.Bytes));
        Assert.Equal(CombatEffectOperation.Change, update.Operation);
        Assert.Equal(7, Assert.Single(update.Entries).Uid);
        Assert.Equal(1002, update.Entries[0].EffectDataId);
        Assert.False(update.Entries[0].ReadyToRemove);
        EveryCutRejected(p.Bytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(7)]
    public void RemovalPreservesReasonAndOptionalDispelSource(int flags)
    {
        using var p = new Packet(0x382C); p.V(301); p.V(1); p.B((byte)flags); p.V(7); p.B(3);
        if ((flags & 1) != 0) p.V(302); if ((flags & 2) != 0) p.I(2001); if ((flags & 4) != 0) p.I(1001);
        var update = Assert.IsType<CombatEffectUpdate>(CombatEffectMessage.TryParse(p.Bytes));
        Assert.Equal(CombatEffectOperation.Remove, update.Operation);
        Assert.Empty(update.Entries);
        Assert.Equal(new CombatEffectRemoval(7, 3, (flags & 1) != 0 ? 302 : null,
            (flags & 2) != 0 ? 2001 : null, (flags & 4) != 0 ? 1001 : null), Assert.Single(update.Removals));
        EveryCutRejected(p.Bytes);
    }

    [Fact]
    public void RejectsUnknownFlagsCountsNoncanonicalIdentifiersAndWrongOpcodes()
    {
        using var add = new Packet(0x382A); add.V(301); add.V(1); Entry(add, 32, 7, 1001, false);
        Assert.Null(CombatEffectMessage.TryParse(add.Bytes));
        using var remove = new Packet(0x382C); remove.V(301); remove.V(1); remove.B(8); remove.V(7); remove.B(0);
        Assert.Null(CombatEffectMessage.TryParse(remove.Bytes));
        using var count = new Packet(0x382A); count.V(301); count.V(4097); count.Zero(4097 * 23);
        Assert.Null(CombatEffectMessage.TryParse(count.Bytes));
        Assert.Null(CombatEffectMessage.TryParse([0x2A, 0x38, 0x81, 0x00, 0]));
        Assert.Null(CombatEffectMessage.TryParse([0x2B, 0x37]));
        foreach (var flags in new byte[] { 0, 1, 2, 4, 8, 16, 18, 20, 31 })
        {
            using var p = new Packet(0x382A); p.V(301); p.V(1); Entry(p, flags, 7, 1001, false);
            Assert.Null(CombatEffectMessage.TryParse(p.Bytes));
        }
    }

    [Fact]
    public void DuplicateInstanceIdsWithinOneMessageAreRejectedAsAmbiguous()
    {
        using var add = new Packet(0x382A); add.V(301); add.V(2);
        Entry(add, 19, 7, 1001, false); Entry(add, 19, 7, 1002, false);
        Assert.Null(CombatEffectMessage.TryParse(add.Bytes));
        using var remove = new Packet(0x382C); remove.V(301); remove.V(2);
        remove.B(0); remove.V(7); remove.B(1); remove.B(0); remove.V(7); remove.B(2);
        Assert.Null(CombatEffectMessage.TryParse(remove.Bytes));
    }

    private static void Entry(Packet p, byte flags, int uid, int id, bool ready)
    {
        p.B(flags); p.V((ulong)uid); p.I(id); p.L(15000); p.L(1700000015000);
        if ((flags & 1) != 0) p.V(302); p.B(4); if ((flags & 2) != 0) p.I(2001);
        p.Bool(ready); if ((flags & 4) != 0) p.V(303); if ((flags & 8) != 0) p.I(9); if ((flags & 16) != 0) p.Zero(12);
    }
    private static void EveryCutRejected(byte[] bytes) { for (var i = 0; i < bytes.Length; i++) Assert.Null(CombatEffectMessage.TryParse(bytes[..i])); }
    private sealed class Packet(ushort opcode) : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private BinaryWriter? _writer;
        private int _boolIndex = -1, _boolBit;
        private BinaryWriter Writer { get { if (_writer is null) { _writer = new(_stream, Encoding.UTF8, true); _writer.Write(opcode); } return _writer; } }
        public byte[] Bytes { get { _ = Writer; return _stream.ToArray(); } }
        public void B(byte value) => Writer.Write(value);
        public void I(int value) => Writer.Write(value);
        public void L(long value) => Writer.Write(value);
        public void Zero(int count) => Writer.Write(new byte[count]);
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
