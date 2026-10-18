using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic framing only; no private record headers or capture bytes.</summary>
public sealed class ObservedControlFrameTests
{
    [Fact]
    public void ExistingDefaultStillRejectsControlsAndDiscardsAppendBatch()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true);
        Assert.Empty(stream.Append([.. Clock(), 4, 5, 0x7f, .. Clock()]));
        Assert.True(stream.ContinuityBreaks > 0);
    }

    [Fact]
    public void ExplicitProfileConsumesBothControlsWithoutEmittingOrBreakingContinuity()
    {
        var stream = new GameMessageStream(requireVerifiedSync: true, allowObservedControlFrames: true);
        var messages = stream.Append([.. Clock(), 4, 5, 0x7f, .. Clock()]);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.NotNull(BossSpawnMessage.ReadServerClock(m)));
        Assert.Equal(0, stream.ContinuityBreaks);
        Assert.Equal(0, stream.Resyncs);
    }

    [Fact]
    public void EverySplitIncludingSingleByteControlHeadersPreservesTheSameMessages()
    {
        byte[] input = [.. Clock(), 4, 5, 0x7f, .. Clock(), 5, 0x36];
        for (var split = 1; split < input.Length; split++)
        {
            var stream = new GameMessageStream(true, true);
            var messages = stream.Append(input.AsSpan(0, split)).Concat(stream.Append(input.AsSpan(split))).ToArray();
            Assert.Equal(2, messages.Length);
            Assert.All(messages, m => Assert.NotNull(BossSpawnMessage.ReadServerClock(m)));
            Assert.Equal(0, stream.ContinuityBreaks);
        }
    }

    [Fact]
    public void ControlsNeverEstablishVerifiedSyncOrEnableAnUnverifiedProfile()
    {
        byte[] controls = [4, 5, 0x7f, 4, 4, 5, 0x36, 4];
        Assert.Empty(new GameMessageStream(true, true).Append(controls));
        Assert.Throws<ArgumentException>(() => new GameMessageStream(false, true));
    }

    [Fact]
    public void ControlsInsideFullyValidatedBundleDoNotBecomeMessages()
    {
        byte[] inner = [.. Clock(), 4, 5, 0x7f, .. Clock()];
        var stream = new GameMessageStream(true, true);
        var messages = stream.Append(Bundle(inner));
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.NotNull(BossSpawnMessage.ReadServerClock(m)));
        Assert.Equal(0, stream.ContinuityBreaks);
    }

    [Fact]
    public void ControlOnlyBundleCannotEstablishVerifiedSync()
    {
        var stream = new GameMessageStream(true, true);
        Assert.Empty(stream.Append(Bundle([4, 4, 4, 4, 5, 0x7f])));
        Assert.Single(stream.Append(Clock()));
    }

    [Fact]
    public void InvalidLengthStillDiscardsAnOtherwiseValidBatch()
    {
        var stream = new GameMessageStream(true, true);
        Assert.Empty(stream.Append([.. Clock(), 3, .. Clock()]));
        Assert.True(stream.ContinuityBreaks > 0);
    }

    [Fact]
    public void NoncanonicalMultiByteEmptyFrameIsNotAnObservedControl()
    {
        var stream = new GameMessageStream(true, true);
        Assert.Empty(stream.Append([.. Clock(), 0x84, 0, .. Clock()]));
        Assert.True(stream.ContinuityBreaks > 0);
    }

    private static byte[] Clock()
    {
        var body = new byte[10];
        body[1] = 0x36;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(2), 1_792_339_200_000L);
        return Frame(body);
    }

    private static byte[] Frame(byte[] body)
    {
        var result = new List<byte>();
        var length = (uint)body.Length + 4;
        while (length >= 128) { result.Add((byte)((length & 127) | 128)); length >>= 7; }
        result.Add((byte)length);
        result.AddRange(body);
        return result.ToArray();
    }

    private static byte[] Bundle(byte[] inner)
    {
        var body = new List<byte> { 0xff, 0xff };
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, inner.Length);
        body.AddRange(size);
        body.Add((byte)(Math.Min(inner.Length, 15) << 4));
        if (inner.Length >= 15)
        {
            var remaining = inner.Length - 15;
            while (remaining >= 255) { body.Add(255); remaining -= 255; }
            body.Add((byte)remaining);
        }
        body.AddRange(inner);
        return Frame(body.ToArray());
    }
}
