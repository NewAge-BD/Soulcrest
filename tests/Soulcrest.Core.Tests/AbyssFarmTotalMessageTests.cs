using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AbyssFarmTotalMessageTests
{
    [Theory]
    [InlineData(0L)] [InlineData(676_730L)] [InlineData(717_422L)] [InlineData(717_427L)]
    [InlineData(0x0123456789abcdefL)] [InlineData(long.MaxValue)]
    public void ReadsTheCompleteNonnegativeAbsoluteTotalOnly(long total)
    {
        var message = Message(total);
        Assert.Equal(total, Assert.IsType<AbyssFarmTotalObservation>(AbyssFarmTotalMessage.TryParse(message)).Total);
        NetworkProgressMessageTests.EveryCutRejected(message, data => AbyssFarmTotalMessage.TryParse(data));
        Assert.Null(AbyssFarmTotalMessage.TryParse([.. message, 0]));
    }

    [Theory]
    [InlineData(-1L)] [InlineData(long.MinValue)]
    public void RejectsNegativeTotals(long total) => Assert.Null(AbyssFarmTotalMessage.TryParse(Message(total)));

    [Theory]
    [InlineData(0)] [InlineData(0xFFA8)] [InlineData(0xFFAA)] [InlineData(0xFFA5)] [InlineData(0xFFA6)] [InlineData(0xE273)]
    public void NeitherNeighborsNorOtherExistingMessagesAreInitialSources(ushort opcode)
    {
        var message = Message(100); BinaryPrimitives.WriteUInt16LittleEndian(message, opcode);
        Assert.Null(AbyssFarmTotalMessage.TryParse(message));
    }

    [Theory]
    [InlineData((byte)0)] [InlineData((byte)0x7F)] [InlineData(byte.MaxValue)]
    public void ReadsEveryOpaqueTailByteWithoutAssigningSemantics(byte fill)
    {
        var message = Message(100); message.AsSpan(10, 8).Fill(fill);
        Assert.Equal(100, Assert.IsType<AbyssFarmTotalObservation>(AbyssFarmTotalMessage.TryParse(message)).Total);
    }

    private static byte[] Message(long total)
    {
        var message = new byte[18]; BinaryPrimitives.WriteUInt16LittleEndian(message, 0xFFA9);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(2), total);
        return message;
    }
}
