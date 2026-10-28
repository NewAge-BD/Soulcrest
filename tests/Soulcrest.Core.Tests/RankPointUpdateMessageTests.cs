using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class RankPointUpdateMessageTests
{
    [Theory]
    [InlineData(0L)] [InlineData(952540L)] [InlineData(953190L)]
    [InlineData(0x0123456789abcdefL)] [InlineData(long.MaxValue)]
    public void ReadsOnlyTheCompleteNonnegativeAbsoluteTotal(long total)
    {
        var message = Message(0, total);
        var update = Assert.IsType<AbyssRankPointUpdate>(RankPointUpdateMessage.TryParse(message));
        Assert.Equal(total, update.Total);
        NetworkProgressMessageTests.EveryCutRejected(message, data => RankPointUpdateMessage.TryParse(data));
        Assert.Null(RankPointUpdateMessage.TryParse([.. message, 0]));
    }

    [Theory]
    [InlineData(-1L)] [InlineData(-650L)] [InlineData(long.MinValue)]
    public void RejectsNegativeTotalsRatherThanTreatingThemAsDeltas(long total)
    {
        Assert.Null(RankPointUpdateMessage.TryParse(Message(0, total)));
    }

    [Fact]
    public void RejectsEveryUnsupportedTypeAndOtherOpcodes()
    {
        for (var type = 1; type <= byte.MaxValue; type++)
            Assert.Null(RankPointUpdateMessage.TryParse(Message((byte)type, 1000)));

        var message = Message(0, 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(message, 0x73E2);
        Assert.Null(RankPointUpdateMessage.TryParse(message));
    }

    private static byte[] Message(byte type, long total)
    {
        var message = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(message, 0xE273);
        message[2] = type;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(3), total);
        return message;
    }
}
