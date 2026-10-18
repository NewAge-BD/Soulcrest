using System.Buffers.Binary;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Anonymous synthetic record bounds only; no capture bytes or game identity fixtures.</summary>
public sealed class ObservedTransportRecordStreamTests
{
    private static byte[] Record(int size = 31, byte fill = 0xa5)
    {
        var bytes = Enumerable.Repeat(fill, size).ToArray();
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1, 2), checked((ushort)(size - 4)));
        return bytes;
    }

    [Fact]
    public void CompleteRecordIncludesItsOpaqueHeaderAndBody()
    {
        var bytes = Record(53);
        var stream = new ObservedTransportRecordStream();
        Assert.Equal(bytes, Assert.Single(stream.Append(bytes)));
        Assert.Equal(0, stream.BufferedBytes);
        Assert.False(stream.IsBlocked);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(127)]
    [InlineData(513)]
    public void EveryFragmentBoundaryRetainsOneIncompleteRecord(int size)
    {
        var bytes = Record(size);
        for (var split = 1; split < bytes.Length; split++)
        {
            var stream = new ObservedTransportRecordStream();
            Assert.Empty(stream.Append(bytes.AsSpan(0, split)));
            Assert.Equal(split, stream.BufferedBytes);
            Assert.Equal(bytes, Assert.Single(stream.Append(bytes.AsSpan(split))));
            Assert.Equal(0, stream.BufferedBytes);
            Assert.False(stream.IsBlocked);
        }
    }

    [Fact]
    public void ByteAtATimeHandlesBothHeaderAndBody()
    {
        var bytes = Record(1024);
        var stream = new ObservedTransportRecordStream();
        for (var i = 0; i < bytes.Length - 1; i++)
            Assert.Empty(stream.Append(bytes.AsSpan(i, 1)));
        Assert.Equal(bytes, Assert.Single(stream.Append(bytes.AsSpan(bytes.Length - 1, 1))));
        Assert.Equal(0, stream.BufferedBytes);
    }

    [Fact]
    public void CoalescedAndArbitrarilySplitRecordsPreserveOrder()
    {
        var expected = new[] { Record(31), Record(65_539, 0x72), Record(41, 0x37) };
        var input = expected.SelectMany(x => x).ToArray();
        var actual = new List<byte[]>();
        var stream = new ObservedTransportRecordStream();
        for (var pos = 0; pos < input.Length; pos += 997)
            actual.AddRange(stream.Append(input.AsSpan(pos, Math.Min(997, input.Length - pos))));
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
        Assert.Equal(0, stream.BufferedBytes);
        Assert.False(stream.IsBlocked);
    }

    [Fact]
    public void ManyCoalescedRecordsDoNotRetainConsumedInput()
    {
        var one = Record();
        var input = Enumerable.Range(0, 10_000).SelectMany(_ => one).ToArray();
        var stream = new ObservedTransportRecordStream();
        var records = stream.Append(input);
        Assert.Equal(10_000, records.Count);
        Assert.Equal(one, records[0]);
        Assert.Equal(one, records[^1]);
        Assert.Equal(0, stream.BufferedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void InvalidTypeBlocksImmediatelyWithoutScanning(byte type)
    {
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append([type]));
        Assert.True(stream.IsBlocked);
        Assert.Equal(0, stream.BufferedBytes);
        Assert.Empty(stream.Append(Record()));
        Assert.Empty(stream.Append(new byte[200_000]));
        Assert.Equal(0, stream.BufferedBytes);
        stream.Break();
        Assert.False(stream.IsBlocked);
        Assert.Single(stream.Append(Record()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(26)]
    public void LengthsBelowObservedMinimumBlockBeforeBody(ushort declaredLength)
    {
        byte[] header = [1, (byte)declaredLength, (byte)(declaredLength >> 8)];
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append(header));
        Assert.True(stream.IsBlocked);
        Assert.Equal(0, stream.BufferedBytes);
        Assert.Empty(stream.Append(Record()));
    }

    [Fact]
    public void MaximumRepresentableRecordIsAcceptedWithoutOverflow()
    {
        var bytes = Record(ObservedTransportRecordStream.MaximumRecordBytes);
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Equal(ObservedTransportRecordStream.MaximumRecordBytes - 1, stream.BufferedBytes);
        Assert.Equal(bytes, Assert.Single(stream.Append(bytes.AsSpan(bytes.Length - 1))));
        Assert.Equal(0, stream.BufferedBytes);
        Assert.False(stream.IsBlocked);
    }

    [Fact]
    public void ExtraByteCannotExtendTheUInt16MaximumRecord()
    {
        var bytes = Record(ObservedTransportRecordStream.MaximumRecordBytes);
        var stream = new ObservedTransportRecordStream();
        // A zero after the largest possible record is an invalid next type, never an oversized body.
        Assert.Empty(stream.Append([.. bytes, 0]));
        Assert.True(stream.IsBlocked);
        Assert.Equal(0, stream.BufferedBytes);
    }

    [Fact]
    public void InvalidLaterHeaderDiscardsTheWholeAppendBatch()
    {
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append([.. Record(), 2, .. Record()]));
        Assert.True(stream.IsBlocked);
        Assert.Empty(stream.Append(Record()));
        stream.Break();
        Assert.Single(stream.Append(Record()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(30)]
    public void BreakDiscardsPartialHeaderOrBody(int partialSize)
    {
        var bytes = Record();
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append(bytes.AsSpan(0, partialSize)));
        stream.Break();
        Assert.Equal(0, stream.BufferedBytes);
        Assert.False(stream.IsBlocked);
        Assert.Equal(bytes, Assert.Single(stream.Append(bytes)));
    }

    [Fact]
    public void EmptyAppendPreservesAnIncompleteRecord()
    {
        var bytes = Record();
        var stream = new ObservedTransportRecordStream();
        Assert.Empty(stream.Append(bytes.AsSpan(0, 2)));
        Assert.Empty(stream.Append([]));
        Assert.Equal(2, stream.BufferedBytes);
        Assert.Equal(bytes, Assert.Single(stream.Append(bytes.AsSpan(2))));
    }

    [Fact]
    public void ReturnedRecordsAreIndependentOfInputAndLaterAppends()
    {
        var input = Record(53);
        var expected = input.ToArray();
        var stream = new ObservedTransportRecordStream();
        var first = Assert.Single(stream.Append(input));
        Array.Fill(input, (byte)0);
        var second = Assert.Single(stream.Append(Record(53, 0x32)));
        stream.Break();
        Assert.Equal(expected, first);
        Assert.NotSame(first, second);
        Assert.Equal(0x32, second[10]);
    }

    [Fact]
    public void TcpRetransmissionAndSequenceWrapProduceOneRecord()
    {
        var bytes = Record(53);
        var tcp = new TcpReassembler();
        var stream = new ObservedTransportRecordStream();
        var actual = new List<byte[]>();
        const uint origin = uint.MaxValue - 15;
        void Feed(uint sequence, ReadOnlySpan<byte> payload)
        {
            foreach (var chunk in tcp.Add(sequence, payload))
            {
                if (chunk.Gap)
                    stream.Break();
                actual.AddRange(stream.Append(chunk.Data));
            }
        }
        Feed(origin, bytes.AsSpan(0, 10));
        Feed(unchecked(origin + 30), bytes.AsSpan(30));
        Feed(origin, bytes.AsSpan(0, 10));
        Feed(unchecked(origin + 10), bytes.AsSpan(10, 20));
        Feed(unchecked(origin + 30), bytes.AsSpan(30));
        Assert.Equal(bytes, Assert.Single(actual));
        Assert.False(stream.IsBlocked);
        Assert.Equal(0, stream.BufferedBytes);
    }
}
