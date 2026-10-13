using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class TcpReassemblerTests
{
    private sealed class Replay
    {
        public TcpReassembler Tcp { get; } = new();
        public List<byte> Bytes { get; } = [];
        public int Gaps { get; private set; }

        public void Feed(uint sequence, ReadOnlySpan<byte> bytes)
        {
            foreach (var chunk in Tcp.Add(sequence, bytes))
            {
                if (chunk.Gap) Gaps++;
                Bytes.AddRange(chunk.Data);
            }
        }
    }

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    [Fact]
    public void ContiguousSegmentsCrossTheSequenceBoundaryWithoutAFalseGap()
    {
        var data = Data(24);
        var replay = new Replay();
        replay.Feed(0xFFFFFFF8, data.AsSpan(0, 16));
        replay.Feed(8, data.AsSpan(16));
        replay.Feed(0xFFFFFFF8, data.AsSpan(0, 16)); // fully retransmitted interval
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(1, replay.Gaps); // initial capture boundary only
    }

    [Fact]
    public void OutOfOrderSegmentsOnBothSidesOfWrapDrainInTcpOrder()
    {
        var data = Data(40);
        var replay = new Replay();
        replay.Feed(0xFFFFFFE0, data.AsSpan(0, 16));
        replay.Feed(4, data.AsSpan(36, 4));
        replay.Feed(0xFFFFFFF4, data.AsSpan(20, 16));
        replay.Feed(0xFFFFFFF0, data.AsSpan(16, 4));
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
        Assert.Empty(replay.Tcp.Add(4, data.AsSpan(36, 4)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameStartRetransmissionsRetainTheWholeLongestInterval(bool longestFirst)
    {
        var data = Data(40);
        var replay = new Replay();
        replay.Feed(100, data.AsSpan(0, 10));
        replay.Feed(120, data.AsSpan(20, longestFirst ? 20 : 5));
        replay.Feed(120, data.AsSpan(20, longestFirst ? 5 : 20));
        replay.Feed(110, data.AsSpan(10, 10));
        replay.Feed(120, data.AsSpan(20));
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
    }

    [Fact]
    public void PendingOverlapsAcrossWrapEmitEachByteOnce()
    {
        var data = Data(32);
        var replay = new Replay();
        replay.Feed(0xFFFFFFF0, data.AsSpan(0, 8));
        replay.Feed(4, data.AsSpan(20, 12));
        replay.Feed(0xFFFFFFFC, data.AsSpan(12, 16));
        replay.Feed(0xFFFFFFF8, data.AsSpan(8, 4));
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
    }

    [Fact]
    public void AnOverlapOfAlreadyDeliveredBytesCanExtendAcrossWrap()
    {
        var data = Data(16);
        var replay = new Replay();
        replay.Feed(0xFFFFFFF8, data.AsSpan(0, 12));
        replay.Feed(0xFFFFFFFC, data.AsSpan(4, 12));
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
    }

    [Fact]
    public void QueueOverflowSkipsOnlyTheRealMissingByteAndDrainsAcrossWrap()
    {
        var data = Data(258);
        var replay = new Replay();
        replay.Feed(0xFFFFFF7F, data.AsSpan(0, 1));
        const uint next = 0xFFFFFF80;
        for (var offset = 1; offset <= 256; offset++)
        {
            replay.Feed(unchecked(next + (uint)offset), data.AsSpan(offset, 1));
            Assert.Single(replay.Bytes);
            Assert.Equal(1, replay.Gaps);
        }
        replay.Feed(unchecked(next + 257), data.AsSpan(257, 1));
        Assert.Equal(data, replay.Bytes);
        Assert.Equal(2, replay.Gaps); // initial boundary and the genuinely missing byte
    }

    [Fact]
    public void RepeatedPendingStartsDoNotConsumeExtraQueueCapacity()
    {
        var replay = new Replay();
        replay.Feed(100, [0]);
        for (var i = 0; i < 256; i++) replay.Feed((uint)(102 + i * 4), [1, 2, 3]);
        for (var i = 0; i < 256; i++) replay.Feed((uint)(102 + i * 4), [1]);
        Assert.Equal(new byte[] { 0 }, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
    }

    [Fact]
    public void TheAmbiguousHalfSequenceBoundaryCannotOverflowOverlapTrimming()
    {
        var replay = new Replay();
        replay.Feed(0, [1]);
        Assert.Empty(replay.Tcp.Add(0x80000001, new byte[] { 99 }));
        replay.Feed(1, [2]);
        Assert.Equal(new byte[] { 1, 2 }, replay.Bytes);
        Assert.Equal(1, replay.Gaps);
    }
}
