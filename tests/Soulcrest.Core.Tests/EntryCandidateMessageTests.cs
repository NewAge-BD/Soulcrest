using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class EntryCandidateMessageTests
{
    private static byte[] Candidate(int bodyLength = 2279, int entityKey = 300, long characterId = 123456789,
        ushort result = 0, ushort opcode = 0x3620)
    {
        // Synthetic envelope only. No captured player fields, names, tokens or private payloads.
        var message = new byte[2 + bodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(message, opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), result);
        var position = 4;
        var key = unchecked((uint)entityKey);
        while (key >= 128) { message[position++] = (byte)(key | 128); key >>= 7; }
        message[position++] = (byte)key;
        message.AsSpan(position, 4).Fill(0xA5); // opaque fixed prefix field
        position += 4;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(position), characterId);
        return message;
    }

    [Theory]
    [InlineData(1361, 1)]
    [InlineData(1362, 300)]
    [InlineData(1373, 300)]
    [InlineData(2279, 300)]
    public void AllObservedEnvelopesReadTheVariablePrefixWithoutFixedCharacterOffsets(int length, int entity)
    {
        var message = Candidate(length, entity);
        Assert.Equal(new OwnEntryCandidate(123456789, entity), OwnIdentityMessage.TryParseEntryCandidate(message));
        Assert.Null(OwnIdentityMessage.TryParse(message));
        Assert.False(OwnIdentityMessage.IsWorldEntryPreparation(message));
    }

    [Theory]
    [InlineData(1, 1L)]
    [InlineData(127, 123456789L)]
    [InlineData(128, 0x0102030405060708L)]
    [InlineData(65536, long.MaxValue)]
    [InlineData(int.MaxValue, long.MaxValue)]
    public void PositiveKeysAndLittleEndianCharacterIdsRemainCandidatesOnly(int entity, long character) =>
        Assert.Equal(new OwnEntryCandidate(character, entity),
            OwnIdentityMessage.TryParseEntryCandidate(Candidate(entityKey: entity, characterId: character)));

    [Theory]
    [InlineData(0, 123L)]
    [InlineData(-1, 123L)]
    [InlineData(int.MinValue, 123L)]
    [InlineData(300, 0L)]
    [InlineData(300, -1L)]
    [InlineData(300, long.MinValue)]
    public void NonpositiveIdentityPartsAreRejected(int entity, long character) =>
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(entityKey: entity, characterId: character)));

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(65535)]
    public void NonzeroResultCannotStartAnInitializationCandidate(int result) =>
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(result: (ushort)result)));

    [Fact]
    public void UnknownLengthsAndForeignOpcodesFailClosed()
    {
        var message = Candidate();
        // The tail is opaque: only the explicitly observed full envelope lengths are accepted.
        for (var length = 0; length < message.Length; length++)
            if (length - 2 is not (1361 or 1362 or 1373))
                Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(message.AsSpan(0, length)));
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate([.. message, 0]));
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate([.. message, 0, 0]));
        foreach (var opcode in new ushort[] { 0x3615, 0x3621, 0x3633, 0x3645, 0x8D09, 0x9000 })
            Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(opcode: opcode)));
    }

    [Fact]
    public void NoncanonicalAndOverflowingEntityVarintsCannotMoveTheCharacterField()
    {
        var overlong = Candidate();
        overlong[4] = 0x81; overlong[5] = 0;
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(overlong));
        var overflow = Candidate();
        overflow.AsSpan(4, 5).Fill(0xFF); overflow[8] = 0x10;
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(overflow));
        var unterminated = Candidate();
        unterminated.AsSpan(4, 5).Fill(0x80);
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(unterminated));
    }

    [Fact]
    public void OpaqueTailDoesNotBecomeWholeOwnValidationAndInputStaysUnchanged()
    {
        var message = Candidate();
        message.AsSpan(18).Fill(0xA5);
        var original = message.ToArray();
        Assert.Equal(new OwnEntryCandidate(123456789, 300), OwnIdentityMessage.TryParseEntryCandidate(message));
        Assert.Equal(original, message);
        Assert.Null(OwnIdentityMessage.TryParse(message));
    }
}
