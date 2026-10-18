using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic protocol structures only; no capture bytes or real identities.</summary>
public sealed class CombatWireMessageTests
{
    [Fact]
    public void DirectAmountPreservesUnclassifiedWireValuesAndNullableMetadata()
    {
        var packet = Direct(4, 123456);
        Assert.True(CombatHitMessage.TryParse(packet, out var observation));
        Assert.Equal(101, observation.TargetEntityId);
        Assert.Equal(202, observation.SourceEntityId);
        Assert.Equal((uint)404, observation.SkillDataId);
        Assert.Equal((uint)505, observation.EffectDataId);
        Assert.Equal(123456, observation.Amount);
        Assert.False(observation.IsPeriodic);
        Assert.Null(observation.RawHitFlags);
        Assert.Equal((byte)2, observation.ResultCode);
        Assert.Equal((ushort)4, observation.RawPresenceFlags);
        RejectEveryCutAndTail(packet, p => CombatHitMessage.TryParse(p, out _));
    }

    [Fact]
    public void DirectOptionalListsAndPackedBooleansAreConsumedAcrossScalarReads()
    {
        var packet = Direct(0x1576, 987654321);
        Assert.True(CombatHitMessage.TryParse(packet, out var observation));
        Assert.Equal(0b1010101, observation.RawHitFlags);
        Assert.Equal(987654321, observation.Amount);
        RejectEveryCutAndTail(packet, p => CombatHitMessage.TryParse(p, out _));
    }

    [Fact]
    public void OmittedAmountStaysUnknownAndObservedZeroRemainsZero()
    {
        Assert.True(CombatHitMessage.TryParse(Direct(0), out var unknown));
        Assert.Null(unknown.Amount);
        Assert.True(CombatHitMessage.TryParse(Direct(4, 0), out var zero));
        Assert.Equal(0, zero.Amount);
    }

    [Theory]
    [InlineData(1)] [InlineData(8)] [InlineData(128)] [InlineData(512)]
    [InlineData(2048)] [InlineData(8192)] [InlineData(16384)] [InlineData(32768)]
    public void DirectRejectsUnobservedOptionalFieldBits(int fields)
    {
        var packet = Direct(0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3), (ushort)fields);
        Assert.False(CombatHitMessage.TryParse(packet, out var observation));
        Assert.Null(observation);
    }

    [Fact]
    public void PeriodicHasEffectButDoesNotInventASkillOrHitFlags()
    {
        var packet = Periodic(11, 40000);
        Assert.True(CombatHitMessage.TryParse(packet, out var observation));
        Assert.Equal(101, observation.TargetEntityId);
        Assert.Equal(202, observation.SourceEntityId);
        Assert.Null(observation.SkillDataId);
        Assert.Equal((uint)505, observation.EffectDataId);
        Assert.Equal(40000, observation.Amount);
        Assert.True(observation.IsPeriodic);
        Assert.Null(observation.RawHitFlags);
        Assert.Null(observation.ResultCode);
        RejectEveryCutAndTail(packet, p => CombatHitMessage.TryParse(p, out _));
        Assert.True(CombatHitMessage.TryParse(Periodic(48), out var noAmount));
        Assert.Null(noAmount.Amount);
    }

    [Theory]
    [InlineData(4)] [InlineData(64)] [InlineData(128)]
    public void PeriodicRejectsUnobservedOptionalFieldBits(int fields)
    {
        var packet = Periodic(0);
        packet[3] = (byte)fields;
        Assert.False(CombatHitMessage.TryParse(packet, out _));
    }

    [Fact]
    public void HitRejectsUnknownByteOrderInvalidEntityAndOverflowingAmounts()
    {
        var swapped = Direct(4); (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        Assert.False(CombatHitMessage.TryParse(swapped, out _));
        var zeroEntity = Direct(4); zeroEntity[2] = 0;
        Assert.False(CombatHitMessage.TryParse(zeroEntity, out _));
        Assert.False(CombatHitMessage.TryParse(Direct(4, ulong.MaxValue), out _));
        Assert.False(CombatHitMessage.TryParse(Periodic(2, ulong.MaxValue), out _));
        var overlong = Direct(4).ToList(); overlong[2] = 0xE5; overlong.Insert(3, 0);
        Assert.False(CombatHitMessage.TryParse(overlong.ToArray(), out _));
    }

    [Fact]
    public void HitRejectsAnUnboundedOrTruncatedOptionalList()
    {
        var packet = Direct(32);
        // Fixed prefix ends at offset 22 for this synthetic one-byte entity encoding.
        packet[22] = 0x7F;
        Assert.False(CombatHitMessage.TryParse(packet, out _));
    }

    [Fact]
    public void HpListsPreserveCurrentAndMaximumAndIgnoreOtherFixedWidthPoints()
    {
        var packet = Hp(3, [(3, -123)], [(9, -1), (0, 400), (7, 1000)]);
        Assert.True(CombatHpMessage.TryParse(packet, out var observation));
        Assert.Equal(101, observation.EntityId);
        Assert.Equal(400, observation.Current);
        Assert.Equal(1000, observation.Maximum);
        RejectEveryCutAndTail(packet, p => CombatHpMessage.TryParse(p, out _));
    }

    [Fact]
    public void HpCurrentMaximumAndZeroAreIndependentObservations()
    {
        Assert.True(CombatHpMessage.TryParse(Hp(2, [], [(7, 1000)]), out var maximum));
        Assert.Null(maximum.Current); Assert.Equal(1000, maximum.Maximum);
        Assert.True(CombatHpMessage.TryParse(Hp(2, [], [(0, 0)]), out var zero));
        Assert.Equal(0, zero.Current); Assert.Null(zero.Maximum);
        Assert.True(CombatHpMessage.TryParse(Hp(0, [], []), out var omitted));
        Assert.Null(omitted.Current); Assert.Null(omitted.Maximum);
    }

    [Fact]
    public void HpRejectsDuplicateTagsNegativeHealthInvalidMaximumAndUnknownFlags()
    {
        Assert.False(CombatHpMessage.TryParse(Hp(1, [(1, 1), (1, 2)], []), out _));
        Assert.False(CombatHpMessage.TryParse(Hp(2, [], [(0, 100), (0, 200)]), out _));
        Assert.False(CombatHpMessage.TryParse(Hp(2, [], [(0, -1)]), out _));
        Assert.False(CombatHpMessage.TryParse(Hp(2, [], [(7, 0)]), out _));
        Assert.False(CombatHpMessage.TryParse(Hp(2, [], [(0, 1001), (7, 1000)]), out _));
        Assert.False(CombatHpMessage.TryParse(Hp(4, [], []), out _));
        var zeroEntity = Hp(0, [], []); zeroEntity[2] = 0;
        Assert.False(CombatHpMessage.TryParse(zeroEntity, out _));
    }

    private static byte[] Direct(ushort fields, ulong amount = 10)
    {
        var p = new Packet(CombatHitMessage.DirectOpcode);
        p.V(101); p.U16(fields); p.V(202); p.U32(404); p.B(0); p.B(2);
        if ((fields & 2) != 0)
        {
            for (var i = 0; i < 6; i++) p.Bool(i % 2 == 0);
            p.V(300); p.Bool(true); p.B(0);
        }
        p.U32(505); p.U32(0); p.V(0);
        if ((fields & 4) != 0) p.V(amount);
        if ((fields & 16) != 0) p.Bool(false);
        if ((fields & 32) != 0) { p.V(2); p.V(1); p.V(500); }
        if ((fields & 64) != 0) { p.V(1); p.U32(42); }
        if ((fields & 256) != 0) p.V(0);
        p.U16(0);
        if ((fields & 1024) != 0) p.V(0);
        if ((fields & 4096) != 0) p.Bool(true);
        return p.Bytes;
    }

    private static byte[] Periodic(byte fields, ulong amount = 10)
    {
        var p = new Packet(CombatHitMessage.PeriodicOpcode);
        p.V(101); p.B(fields); p.V(202); p.V(303); p.U32(505);
        if ((fields & 1) != 0) p.V(20);
        if ((fields & 2) != 0) p.V(amount);
        if ((fields & 8) != 0) p.U32(42);
        if ((fields & 16) != 0) p.V(606);
        if ((fields & 32) != 0) p.U32(42);
        return p.Bytes;
    }

    private static byte[] Hp(byte fields, (byte Key, int Value)[] integers, (byte Key, long Value)[] longs)
    {
        var p = new Packet(CombatHpMessage.Opcode); p.V(101); p.B(fields);
        if ((fields & 1) != 0) { p.V((ulong)integers.Length); foreach (var (key, value) in integers) { p.B(key); p.U32(unchecked((uint)value)); } }
        if ((fields & 2) != 0) { p.V((ulong)longs.Length); foreach (var (key, value) in longs) { p.B(key); p.I64(value); } }
        return p.Bytes;
    }

    private static void RejectEveryCutAndTail(byte[] bytes, Func<byte[], bool> parse)
    {
        for (var n = 0; n < bytes.Length; n++) Assert.False(parse(bytes[..n]));
        Assert.False(parse([.. bytes, 0]));
    }

    private sealed class Packet(ushort opcode)
    {
        private readonly List<byte> _bytes = [(byte)opcode, (byte)(opcode >> 8)];
        private int _booleanIndex, _booleanBit = 8;
        public byte[] Bytes => _bytes.ToArray();
        public void B(byte value) => _bytes.Add(value);
        public void U16(ushort value) { B((byte)value); B((byte)(value >> 8)); }
        public void U32(uint value) { for (var i = 0; i < 4; i++) B((byte)(value >> (i * 8))); }
        public void I64(long value) { for (var i = 0; i < 8; i++) B((byte)(value >> (i * 8))); }
        public void V(ulong value) { while (value >= 128) { B((byte)((value & 127) | 128)); value >>= 7; } B((byte)value); }
        public void Bool(bool value)
        {
            if (_booleanBit == 8) { _booleanIndex = _bytes.Count; B(0); _booleanBit = 0; }
            if (value) _bytes[_booleanIndex] |= (byte)(1 << _booleanBit);
            _booleanBit++;
        }
    }
}
