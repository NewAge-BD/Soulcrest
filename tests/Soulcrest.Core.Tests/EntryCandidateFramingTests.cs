using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class EntryCandidateFramingTests
{
    private static byte[] Frame(byte[] message) => GameMessageContinuityTests.Frame(message);
    private static byte[] Clock() => [0x00, 0x36, .. BitConverter.GetBytes(1760000000000L)];

    private static byte[] Candidate(int tailBytes = 257)
    {
        var message = new byte[18 + tailBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(message, 0x3620);
        message[4] = 0xAC; message[5] = 0x02; // canonical synthetic EntityKey 300
        message.AsSpan(6, 4).Fill(0xA5); // opaque prefix field
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(10, 8), 123456789);
        message.AsSpan(18).Fill(0xA5); // opaque tail, with no framing/identity meaning
        return message;
    }

    [Fact]
    public void EvenFourCompleteEntryCandidatesCannotEstablishVerifiedFraming()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        var candidate = Candidate();
        Assert.NotNull(OwnIdentityMessage.TryParseEntryCandidate(candidate));
        var frame = Frame(candidate);
        Assert.Empty(stream.Append([.. frame, .. frame, .. frame, .. frame]));
        Assert.Equal(Clock(), Assert.Single(stream.Append(Frame(Clock()))));
    }

    [Fact]
    public void ClockSyncWithholdsEveryIncompleteOuterCandidateFrameUntilItsOpaqueTailIsComplete()
    {
        var candidate = Candidate();
        var framed = Frame(candidate);
        for (var cut = 1; cut < framed.Length; cut++)
        {
            var stream = new GameMessageStream(requireVerifiedSync: true);
            Assert.Equal(Clock(), Assert.Single(stream.Append(Frame(Clock()))));
            Assert.Empty(stream.Append(framed.AsSpan(0, cut)));
            var complete = Assert.Single(stream.Append(framed.AsSpan(cut)));
            Assert.Equal(candidate, complete);
            Assert.Equal(new OwnEntryCandidate(123456789, 300), OwnIdentityMessage.TryParseEntryCandidate(complete));
            Assert.Null(OwnIdentityMessage.TryParse(complete));
            Assert.Equal(0, stream.ContinuityBreaks);
            Assert.Equal(0, stream.Resyncs);
        }
    }

    [Fact]
    public void AGapDropsTheIncompleteCandidateAndItsRemainingTailCannotSupplyAuthorityOrSync()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Single(stream.Append(Frame(Clock())));
        var framed = Frame(Candidate());
        Assert.Empty(stream.Append(framed.AsSpan(0, framed.Length - 1)));
        stream.Break();
        Assert.Empty(stream.Append(framed.AsSpan(framed.Length - 1)));
        Assert.Empty(stream.Append(framed));
        Assert.Equal(Clock(), Assert.Single(stream.Append(Frame(Clock()))));
    }

    [Fact]
    public void PrefixEligibilityCannotBypassTheOuterTransportFrameSizeBound()
    {
        // The direct-call resource guard is conservative. A decoded message of exactly 1 MiB
        // already exceeds the transport frame cap once its length header is added.
        var candidate = Candidate(OwnIdentityMessage.MaximumEntryCandidateMessageBytes - 18);
        Assert.NotNull(OwnIdentityMessage.TryParseEntryCandidate(candidate));
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Single(stream.Append(Frame(Clock())));
        Assert.Empty(stream.Append(Frame(candidate)));
        Assert.Equal(1, stream.ContinuityBreaks);
    }
}
