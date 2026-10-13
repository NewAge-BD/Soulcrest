using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class TeleportBindingMessageTests
{
    private static byte[] Packet(int id, ushort opcode = 0x9233)
    {
        var bytes = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), id);
        return bytes;
    }

    [Fact]
    public void ObservedSingleIdLayoutUsesTheEntireLittleEndianBody()
    {
        // Public catalogue ID from the observed format, with no identity or captured private data.
        Assert.Equal(1052020, ExplorationProgressMessage.TryParseTeleportBinding([0x33, 0x92, 0x74, 0x0D, 0x10, 0x00]));
        Assert.Null(ExplorationProgressMessage.TryParseTeleports(Packet(1052020)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1052020)]
    [InlineData(int.MaxValue)]
    public void AcceptsOnlyPositiveSignedInt32Ids(int id) =>
        Assert.Equal(id, ExplorationProgressMessage.TryParseTeleportBinding(Packet(id)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RejectsNonpositiveIds(int id) =>
        Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding(Packet(id)));

    [Fact]
    public void RejectsEveryTruncationTrailingDataAndForeignOpcodes()
    {
        var message = Packet(1052020);
        for (var length = 0; length < message.Length; length++)
            Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding(message.AsSpan(0, length)));
        Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding([.. message, 0]));
        foreach (var opcode in new ushort[] { 0x9232, 0x9234, 0x900D, 0x6101 })
            Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding(Packet(1052020, opcode)));
        // Neither a varint scalar nor a count-prefixed list is this observed fixed-width format.
        Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding([0x33, 0x92, 0xF4, 0x9A, 0x40]));
        Assert.Null(ExplorationProgressMessage.TryParseTeleportBinding([0x33, 0x92, 1, 0x74, 0x0D, 0x10, 0]));
    }
}
