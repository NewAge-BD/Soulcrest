using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class GameMessageVerifiedSyncTests
{
    private static byte[] Frame(byte[] message) => GameMessageContinuityTests.Frame(message);
    private static byte[] Bundle(byte[] inner) => GameMessageContinuityTests.Bundle(inner);
    private static readonly byte[] Unknown = [0x20, 0x28];
    private static readonly byte[] Gain = [0x0d, 0x90, 1, 0xec, 3, 0, 0, 2, 0, 0, 0];
    private static byte[] FalseChain() => [.. Frame(Unknown), .. Frame(Unknown), .. Frame(Unknown), .. Frame(Unknown)];
    private static byte[] Clock() => [0, 0x36, .. BitConverter.GetBytes(1760000000000L)];
    private static byte[] Snapshot()
    {
        using var w = new Packet(0x9000);
        w.I(0); w.Z(6);
        return w.Bytes;
    }

    [Fact]
    public void FalseFourFrameChainCannotConsumeTheFollowingFragmentedOwnAndBaselineBundle()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Empty(stream.Append(FalseChain())); // generic framing would bind and consume these bytes
        // An unconfirmed ordinary frame also claims to cover the real bundle, as in the replay regression.
        var claimedFrame = Frame(new byte[31000]);
        Assert.Empty(stream.Append(claimedFrame.AsSpan(0, 5)));
        var own = OwnIdentityMessageTests.OwnMessage(false);
        var bundled = Frame(Bundle([.. Frame(own), .. Frame(Snapshot()), .. Frame(Gain), .. Frame(Unknown)]));
        Assert.Empty(stream.Append(bundled.AsSpan(0, bundled.Length / 2)));
        var messages = stream.Append(bundled.AsSpan(bundled.Length / 2));
        Assert.Equal(4, messages.Count);
        Assert.Single(messages, m => OwnIdentityMessage.TryParse(m) is not null);
        Assert.Single(messages, m => PetProgressMessage.TryParseSnapshot(m) is not null);
        Assert.Equal(2, messages.Select(m => LootMessage.TryParse(m)).OfType<IReadOnlyList<SoulGain>>().SelectMany(g => g).Sum(g => g.Souls));
        Assert.Equal(1, stream.Resyncs);
        Assert.Empty(stream.Append([]));
    }

    [Fact]
    public void ACompleteOwnMessageOrSingleOwnBundleDoesNotNeedFourOuterFrames()
    {
        var own = OwnIdentityMessageTests.OwnMessage(false);
        var plain = new GameMessageStream(requireVerifiedSync: true);
        Assert.Equal(own, Assert.Single(plain.Append([.. FalseChain(), .. Frame(own)])));
        var bundled = new GameMessageStream(requireVerifiedSync: true);
        Assert.Equal(own, Assert.Single(bundled.Append(Frame(Bundle(Frame(own))))));
    }

    [Fact]
    public void AnIncompleteBundleDoesNotExposeAClockAnchorInsideItsLiteralBytes()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        var bundled = Frame(Bundle([.. Frame(Clock()), .. Frame(Unknown), .. Frame(Unknown), .. Frame(Unknown)]));
        Assert.Empty(stream.Append(bundled.AsSpan(0, bundled.Length - 1)));
        Assert.Equal(4, stream.Append(bundled.AsSpan(bundled.Length - 1)).Count);
        Assert.Equal(0, stream.Resyncs);
        Assert.Equal(0, stream.ContinuityBreaks);
    }

    [Fact]
    public void AValidatedLargeBundleCanSynchronizeAboveTheGeneric64KiBLimit()
    {
        var unknown = new byte[20000]; unknown[0] = 0x20; unknown[1] = 0x28;
        var bundled = Frame(Bundle([.. Frame(unknown), .. Frame(unknown), .. Frame(unknown), .. Frame(unknown)]));
        Assert.True(bundled.Length > 65536);
        var stream = new GameMessageStream(requireVerifiedSync: true);
        for (var i = 0; i < bundled.Length - 1; i += 1000)
            Assert.Empty(stream.Append(bundled.AsSpan(i, Math.Min(1000, bundled.Length - 1 - i))));
        var messages = stream.Append(bundled.AsSpan(bundled.Length - 1));
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.Equal(unknown, m));
        Assert.Equal(0, stream.ContinuityBreaks);
    }

    [Fact]
    public void ClockAnchorRequiresTheExactOpcodeLengthAndValidatedTimestamp()
    {
        var wrongOpcode = Clock(); wrongOpcode[0] = 1;
        byte[] wrongTime = [0, 0x36, .. BitConverter.GetBytes(0L)];
        var clock = Clock();
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Empty(stream.Append([.. Frame(wrongOpcode), .. Frame(wrongTime), .. Frame(clock[..^1]), .. Frame([.. clock, 0])]));
        Assert.Equal(clock, Assert.Single(stream.Append(Frame(clock))));
    }

    [Fact]
    public void EveryOwnHeaderAndBodySplitPreservesTheVerifiedAnchor()
    {
        var own = OwnIdentityMessageTests.OwnMessage(false);
        var framed = Frame(own);
        for (var cut = 1; cut < framed.Length; cut++)
        {
            var stream = new GameMessageStream(requireVerifiedSync: true);
            Assert.Empty(stream.Append(framed.AsSpan(0, cut)));
            Assert.Equal(own, Assert.Single(stream.Append(framed.AsSpan(cut))));
            Assert.Equal(0, stream.ContinuityBreaks);
        }
    }

    [Fact]
    public void TinyUnconfirmedOrMalformedBundlesDoNotSynchronize()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Empty(stream.Append(Frame(Bundle(Frame(Unknown)))));
        Assert.Empty(stream.Append(Frame(Bundle([.. Frame(Unknown), .. Frame(Unknown), .. Frame(Unknown), 0]))));
        var own = OwnIdentityMessageTests.OwnMessage(false);
        Assert.Equal(own, Assert.Single(stream.Append(Frame(own))));
    }

    [Fact]
    public void BufferTrimmingKeepsTheIncrementalSearchCursorValid()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Empty(stream.Append(new byte[2 * 1024 * 1024 + 100]));
        Assert.Equal(1, stream.ContinuityBreaks);
        var own = OwnIdentityMessageTests.OwnMessage(false);
        Assert.Equal(own, Assert.Single(stream.Append(Frame(own))));
    }
}
