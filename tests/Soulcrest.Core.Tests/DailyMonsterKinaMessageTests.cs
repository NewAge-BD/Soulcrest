using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic counter envelopes only, without captured timestamps or player identifiers.</summary>
public sealed class DailyMonsterKinaMessageTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(1000001L)]
    [InlineData(long.MaxValue)]
    public void BothEnvelopesPreserveTheAbsoluteInt64TotalWithoutClamping(long total)
    {
        Assert.Equal(total, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseInitial(Packet(0xFFA5, total))).Total);
        Assert.Equal(total, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseUpdate(Packet(0xFFA6, total))).Total);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void NegativeCountersAreRejected(long total)
    {
        Assert.Null(DailyMonsterKinaMessage.TryParseInitial(Packet(0xFFA5, total)));
        Assert.Null(DailyMonsterKinaMessage.TryParseUpdate(Packet(0xFFA6, total)));
    }

    [Fact]
    public void InitialAndUpdateOpcodesAreNotInterchangeable()
    {
        Assert.Null(DailyMonsterKinaMessage.TryParseInitial(Packet(0xFFA6, 17)));
        Assert.Null(DailyMonsterKinaMessage.TryParseUpdate(Packet(0xFFA5, 17)));
        Assert.Null(DailyMonsterKinaMessage.TryParseInitial(Packet(0x561B, 17)));
        Assert.Null(DailyMonsterKinaMessage.TryParseUpdate(Packet(0x5611, 17)));
    }

    [Fact]
    public void EveryTruncationAndAnyTrailingByteAreRejected()
    {
        var initial = Packet(0xFFA5, 17);
        var update = Packet(0xFFA6, 19);
        for (var length = 0; length < 18; length++)
        {
            Assert.Null(DailyMonsterKinaMessage.TryParseInitial(initial.AsSpan(0, length)));
            Assert.Null(DailyMonsterKinaMessage.TryParseUpdate(update.AsSpan(0, length)));
        }
        Assert.Null(DailyMonsterKinaMessage.TryParseInitial([.. initial, 0]));
        Assert.Null(DailyMonsterKinaMessage.TryParseUpdate([.. update, 0]));
    }

    [Fact]
    public void TheEightByteTailHasNoTimeOrResetValidation()
    {
        foreach (var tail in new long[] { 0, -1, long.MinValue, long.MaxValue })
        {
            Assert.Equal(17, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseInitial(Packet(0xFFA5, 17, tail))).Total);
            Assert.Equal(19, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseUpdate(Packet(0xFFA6, 19, tail))).Total);
        }
    }

    [Fact]
    public void CounterUsesLittleEndianAndAllEightBytes()
    {
        const long total = 0x0102030405060708;
        Assert.Equal(total, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseInitial(Packet(0xFFA5, total))).Total);
        Assert.Equal(total, Assert.IsType<DailyMonsterKinaObservation>(DailyMonsterKinaMessage.TryParseUpdate(Packet(0xFFA6, total))).Total);
    }

    private static byte[] Packet(ushort opcode, long total, long opaqueTail = 0)
    {
        var bytes = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), total);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(10), opaqueTail);
        return bytes;
    }
}
