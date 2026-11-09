using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class PantheonMessageTests
{
    [Fact]
    public void CompleteListRetainsOwnedIdsAndIndependentPlacementBytes()
    {
        var message = List((101, 0), (202, 17), (303, 3));
        var state = Assert.IsType<CharacterPantheonState>(CharacterPantheonMessage.TryParseList(message));
        Assert.Equal(3, state.Decorations.Count);
        Assert.Equal((byte)0, state.Decorations[101]);
        Assert.Equal((byte)17, state.Decorations[202]);
        Assert.Equal((byte)3, state.Decorations[303]);
        NetworkProgressMessageTests.EveryCutRejected(message, bytes => CharacterPantheonMessage.TryParseList(bytes));
    }

    [Fact]
    public void FullEmptyListIsDistinctFromMissingOrTruncatedInput()
    {
        Assert.Empty(CharacterPantheonMessage.TryParseList(List())!.Decorations);
        Assert.Null(CharacterPantheonMessage.TryParseList([]));
        Assert.Null(CharacterPantheonMessage.TryParseList([0xaf, 0x8a]));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(1, 1)]
    public void NonpositiveAndDuplicateIdsRejectTheWholeList(int first, int second) =>
        Assert.Null(CharacterPantheonMessage.TryParseList(List((first, 0), (second, 1))));

    [Fact]
    public void FutureIdsSlotsAndAmbiguousPlacementsRemainRawWithoutInventingEquipmentCompatibility()
    {
        var state = CharacterPantheonMessage.TryParseList(List((int.MaxValue, 255), (1234567, 17), (9876543, 17)))!;
        Assert.Equal(3, state.Decorations.Count);
        Assert.Equal((byte)255, state.Decorations[int.MaxValue]);
        Assert.Equal(2, state.Decorations.Values.Count(slot => slot == 17));
    }

    [Fact]
    public void TailWrongOpcodeAndNoncanonicalOrOverflowCountsCannotQualify()
    {
        Assert.Null(CharacterPantheonMessage.TryParseList([.. List((1, 1)), 0]));
        var wrongOpcode = List((1, 1)); wrongOpcode[0]++;
        Assert.Null(CharacterPantheonMessage.TryParseList(wrongOpcode));
        Assert.Null(CharacterPantheonMessage.TryParseList([0xaf, 0x8a, 0x80, 0]));
        Assert.Null(CharacterPantheonMessage.TryParseList([0xaf, 0x8a, 0xff, 0xff, 0xff, 0xff, 0x10]));
        Assert.Null(CharacterPantheonMessage.TryParseList([0xaf, 0x8a, 0x80, 0x80, 0x80, 0x80, 0x80]));
    }

    [Fact]
    public void CountsAndDirectMessageBytesStayBounded()
    {
        using var packet = new Packet(0x8AAF); packet.V(10001); packet.Z(10001 * 5);
        Assert.Null(CharacterPantheonMessage.TryParseList(packet.Bytes));
        var oversized = new byte[(1 << 20) + 1]; oversized[0] = 0xaf; oversized[1] = 0x8a;
        Assert.Null(CharacterPantheonMessage.TryParseList(oversized));
    }

    private static byte[] List(params (int Id, byte Slot)[] rows)
    {
        using var packet = new Packet(0x8AAF); packet.V((uint)rows.Length);
        foreach (var row in rows) { packet.I(row.Id); packet.B(row.Slot); }
        return packet.Bytes;
    }
}
