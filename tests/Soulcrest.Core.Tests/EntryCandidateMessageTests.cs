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
    [InlineData(2283, 300)]
    [InlineData(2286, 300)]
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

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16384)]
    [InlineData(2097152)]
    [InlineData(int.MaxValue)]
    public void EveryCutInsideTheVariablePrefixIsRejectedWithoutInventingATailSchema(int entity)
    {
        var width = 1;
        for (var value = entity; (value >>= 7) != 0;) width++;
        var prefixBytes = 16 + width;
        var message = Candidate(prefixBytes - 2, entity);
        for (var length = 0; length < prefixBytes; length++)
            Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(message.AsSpan(0, length)));
        Assert.Equal(new OwnEntryCandidate(123456789, entity), OwnIdentityMessage.TryParseEntryCandidate(message));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void ArbitraryOpaqueTailsAreEligiblePrefixesOnlyAndAreNeverChanged(int tailBytes)
    {
        var message = Candidate(16 + tailBytes); // two-byte canonical entity: 18-byte prefix including opcode
        message.AsSpan(18).Fill(0xFF); // deliberately arbitrary; no tail schema is assumed
        var original = message.ToArray();
        Assert.Equal(new OwnEntryCandidate(123456789, 300), OwnIdentityMessage.TryParseEntryCandidate(message));
        Assert.Null(OwnIdentityMessage.TryParse(message));
        Assert.False(OwnIdentityMessage.IsWorldEntryPreparation(message));
        Assert.Equal(original, message);
    }

    [Fact]
    public void CandidateResourceCapIsIndependentOfAnyObservedGameEnvelopeLength()
    {
        var maximum = Candidate(OwnIdentityMessage.MaximumEntryCandidateMessageBytes - 2);
        Assert.Equal(1 << 20, maximum.Length);
        Assert.Equal(new OwnEntryCandidate(123456789, 300), OwnIdentityMessage.TryParseEntryCandidate(maximum));
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate([.. maximum, 0]));
    }

    [Theory]
    [InlineData(0x3615)]
    [InlineData(0x3621)]
    [InlineData(0x3633)]
    [InlineData(0x3645)]
    [InlineData(0x8D09)]
    [InlineData(0x9000)]
    public void ForeignOpcodesCannotSupplyAnEntryPrefix(int opcode) =>
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(opcode: (ushort)opcode)));

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
    public void NewlyObserved2286EnvelopeStillRequiresAnIndependentFullOwnAndKeepsItsTailOpaque()
    {
        var message = Candidate(bodyLength: 2286);
        message.AsSpan(18).Fill(0x5A);
        var original = message.ToArray();
        Assert.Equal(new OwnEntryCandidate(123456789, 300), OwnIdentityMessage.TryParseEntryCandidate(message));
        Assert.Null(OwnIdentityMessage.TryParse(message));
        Assert.False(OwnIdentityMessage.IsWorldEntryPreparation(message));
        Assert.Equal(original, message);
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(bodyLength: 2286, result: 1)));
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(bodyLength: 2286, entityKey: 0)));
        Assert.Null(OwnIdentityMessage.TryParseEntryCandidate(Candidate(bodyLength: 2286, characterId: 0)));
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
