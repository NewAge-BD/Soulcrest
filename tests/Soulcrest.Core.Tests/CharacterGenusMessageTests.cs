using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CharacterGenusMessageTests
{
    private static byte[] CurrentRoll() => Convert.FromHexString("18900000020309000362000c00000000000000aa010463006e0000000000000002028d0107000000000000000301c700050000000000000004034b020700000000000000aa05044f021e000000000000000601c70005000000000000000701c100100000000000000008046800180000000000000002020a0000000000000000000000");

    [Fact]
    public void CurrentNineSlotRollDecodesU16StatsI64ValuesAndBooleanBitsAcrossScalars()
    {
        var roll = Assert.IsType<CharacterGenusRoll>(CharacterGenusMessage.TryParseRoll(CurrentRoll()));
        Assert.Equal((byte)2, roll.CreatureType); Assert.Equal((byte)3, roll.Panel);
        Assert.Equal(10, roll.Level); Assert.Equal(0L, roll.Experience);
        Assert.Equal(Enumerable.Range(0, 9).Select(index => (byte)index), roll.Slots.Select(slot => slot.Slot));
        Assert.Equal(new ushort[] { 98, 99, 397, 199, 587, 591, 199, 193, 104 }, roll.Slots.Select(slot => slot.StatType));
        Assert.Equal(new long[] { 12, 110, 7, 5, 7, 30, 5, 16, 24 }, roll.Slots.Select(slot => slot.Value));
        Assert.Equal(new byte[] { 3, 4, 2, 1, 3, 4, 1, 1, 4 }, roll.Slots.Select(slot => slot.Grade));
        Assert.All(roll.Slots, slot => { Assert.False(slot.IsLocked); Assert.True(slot.IsActivated); });
        var extreme = CurrentRoll(); BitConverter.GetBytes(long.MinValue).CopyTo(extreme, 11);
        Assert.Equal(long.MinValue, CharacterGenusMessage.TryParseRoll(extreme)!.Slots[0].Value);
    }

    [Fact]
    public void RollRejectsTruncationTrailingDataDuplicateSlotsUnknownFieldsAndMismatchedTail()
    {
        var valid = CurrentRoll();
        for (var length = 0; length < valid.Length; length++)
            Assert.Null(CharacterGenusMessage.TryParseRoll(valid.AsSpan(0, length)));
        Assert.Null(CharacterGenusMessage.TryParseRoll([.. valid, 0]));
        foreach (var (index, value) in new (int, byte)[] { (0, 0x19), (2, 1), (4, 1), (5, 4), (6, 0), (6, 10), (7, 9), (8, 6), (20, 0), (118, 3), (119, 11) })
        {
            var invalid = CurrentRoll(); invalid[index] = value;
            Assert.Null(CharacterGenusMessage.TryParseRoll(invalid));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CurrentLockReplyPreservesTheNativeSlotAndExplicitBoolean(bool locked)
    {
        Assert.Equal(new CharacterGenusSlotLock(2, 1, 1, locked),
            CharacterGenusMessage.TryParseSlotLock([0x1A, 0x90, 0, 0, 2, 1, 1, (byte)(locked ? 1 : 0)]));
    }

    [Fact]
    public void TruncationTrailingDataFailuresAndUnqualifiedVariantsAreRejected()
    {
        byte[] valid = [0x1A, 0x90, 0, 0, 2, 1, 1, 1];
        for (var length = 0; length < valid.Length; length++)
            Assert.Null(CharacterGenusMessage.TryParseSlotLock(valid.AsSpan(0, length)));
        Assert.Null(CharacterGenusMessage.TryParseSlotLock([.. valid, 0]));
        foreach (var (index, value) in new (int, byte)[] { (0, 0x19), (2, 1), (4, 1), (4, 7), (5, 0), (5, 4), (6, 9), (7, 2), (7, 0x81) })
        {
            var invalid = (byte[])valid.Clone(); invalid[index] = value;
            Assert.Null(CharacterGenusMessage.TryParseSlotLock(invalid));
        }
    }
}
