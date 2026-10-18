using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class ObservedGameTransportRecordTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(255, 255)]
    [InlineData(256, 1)]
    [InlineData(1234, 256)]
    public void DataLengthAndCounterWidthsAreIndependent(int counter, int length)
    {
        var raw = Data(counter, length);
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Equal(ObservedGameTransportRecordKind.Data, result.Kind);
        Assert.Equal(length, result.BodyLength);
        Assert.Equal(34 + (counter <= 255 ? 1 : 2) + 2 + (length <= 255 ? 1 : 2), result.BodyOffset);
        Assert.True(result.Body.Span.SequenceEqual(raw.AsSpan(result.BodyOffset, length)));
    }

    [Fact]
    public void BodyReferencesTheCallersMemoryWithoutCopyingOrExposingTheCounter()
    {
        var raw = Data(1234, 16);
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.True(MemoryMarshal.TryGetArray(result.Body, out var memory));
        Assert.Same(raw, memory.Array);
        Assert.Equal(result.BodyOffset, memory.Offset);
        Assert.Equal(16, memory.Count);
        Assert.Null(typeof(ObservedGameTransportRecord).GetProperty("Counter"));
    }

    [Fact]
    public void ArbitraryOpaqueHeaderContextDoesNotBecomeIdentityOrAFilter()
    {
        var raw = Data(1234, 16);
        for (var at = 3; at <= 30; at++) if (at != 7) raw[at] = (byte)(at * 7);
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Equal(ObservedGameTransportRecordKind.Data, result.Kind);
        Assert.Null(typeof(ObservedGameTransportRecord).GetProperty("Identity"));
    }

    [Fact]
    public void EveryTruncatedPrefixAndTrailingByteIsRejected()
    {
        var raw = Data(256, 256);
        for (var count = 0; count < raw.Length; count++)
        {
            Assert.False(ObservedGameTransportRecord.TryRead(raw.AsMemory(0, count), out var result));
            Assert.Null(result);
        }
        Assert.False(ObservedGameTransportRecord.TryRead(raw.Concat(new byte[] { 0 }).ToArray(), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void WrongOuterTypesAreRejected(byte value)
    {
        var raw = Data(1, 12); raw[0] = value;
        Assert.False(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(31, 0)]
    [InlineData(32, 0)]
    [InlineData(33, 0)]
    [InlineData(33, 3)]
    [InlineData(33, 4)]
    [InlineData(35, 0)]
    [InlineData(36, 0)]
    public void UnknownDataTagsOrFieldWidthsExposeNoBody(int at, byte value)
    {
        var raw = Data(1, 12); raw[at] = value;
        Assert.False(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void NoncanonicalCounterAndBodyLengthAreRejected()
    {
        var counter = Data(256, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(counter.AsSpan(34), 255);
        Assert.False(ObservedGameTransportRecord.TryRead(counter, out _));
        var length = Data(1, 256);
        BinaryPrimitives.WriteUInt16LittleEndian(length.AsSpan(37), 255);
        Assert.False(ObservedGameTransportRecord.TryRead(length, out _));
    }

    [Fact]
    public void BodyLengthMustConsumeExactlyTheAreaBeforeTheFormatFooter()
    {
        foreach (var delta in new[] { -1, 1 })
        {
            var raw = Data(1, 12); raw[37] = (byte)(raw[37] + delta);
            Assert.False(ObservedGameTransportRecord.TryRead(raw, out _));
        }
        var empty = Data(1, 1); empty[37] = 0;
        Assert.False(ObservedGameTransportRecord.TryRead(empty, out _));
    }

    [Fact]
    public void EveryChangedFooterFieldIsRejected()
    {
        for (var at = 1; at <= 4; at++)
        {
            var raw = Data(1, 12); raw[^at] ^= 1;
            Assert.False(ObservedGameTransportRecord.TryRead(raw, out var result));
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(255)]
    public void NonDataKindsRemainUnknownAndNeverExposeDataOrLifecycleAuthority(byte kind)
    {
        var raw = Data(1, 12); raw[7] = kind;
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Equal(ObservedGameTransportRecordKind.Unknown, result.Kind);
        Assert.Equal(0, result.BodyOffset);
        Assert.True(result.Body.IsEmpty);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(255, -1)]
    [InlineData(256, -1)]
    [InlineData(1, 2)]
    [InlineData(256, 2)]
    [InlineData(1, 256)]
    [InlineData(256, 300)]
    public void BothControlFieldLayoutsHaveNoBodyOrGameLifecycleMeaning(int last, int first)
    {
        var raw = Control(last, first);
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Equal(ObservedGameTransportRecordKind.Control, result.Kind);
        Assert.Equal(10, result.RawKind);
        Assert.True(result.Body.IsEmpty);
        Assert.Equal(0, result.BodyOffset);
    }

    [Fact]
    public void BareTypeElevenIsOnlyANeutralControlRecord()
    {
        var raw = new byte[31]; raw[0] = 1; raw[7] = 11;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1), 27);
        Assert.True(ObservedGameTransportRecord.TryRead(raw, out var result));
        Assert.Equal(ObservedGameTransportRecordKind.Control, result.Kind);
        Assert.Equal(11, result.RawKind);
        Assert.True(result.Body.IsEmpty);
        var extra = new byte[32]; raw.CopyTo(extra, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(1), 28);
        Assert.False(ObservedGameTransportRecord.TryRead(extra, out _));
    }

    [Fact]
    public void EveryTruncatedControlAndInvalidControlFieldIsRejected()
    {
        foreach (var source in new[] { Control(0), Control(300, 256) })
        {
            for (var cut = 0; cut < source.Length; cut++)
                Assert.False(ObservedGameTransportRecord.TryRead(source.AsMemory(0, cut), out _));
        }
        foreach (var (at, value) in new[] { (31, 0), (32, 42), (33, 0), (33, 3), (35, 0) })
        {
            var raw = Control(300); raw[at] = (byte)value;
            Assert.False(ObservedGameTransportRecord.TryRead(raw, out _));
        }
        var missingSecond = Control(0); missingSecond[32] = 44;
        Assert.False(ObservedGameTransportRecord.TryRead(missingSecond, out _));
        var wrongOrder = Control(300, 256); wrongOrder[32] = 43;
        Assert.False(ObservedGameTransportRecord.TryRead(wrongOrder, out _));
    }

    private static byte[] Control(int last, int first = -1)
    {
        var fields = new List<byte>();
        void Field(byte key, int value)
        {
            fields.Add(5); fields.Add(key); fields.Add((byte)(value <= 255 ? 1 : 2));
            fields.Add((byte)value);
            if (value > 255) fields.Add((byte)(value >> 8));
        }
        if (first >= 0) Field(44, first);
        Field(43, last);
        var raw = new byte[31 + fields.Count]; raw[0] = 1; raw[7] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1), (ushort)(raw.Length - 4));
        fields.CopyTo(raw, 31);
        return raw;
    }

    private static byte[] Data(int counter, int bodyLength)
    {
        var counterWidth = counter <= 255 ? 1 : 2;
        var lengthWidth = bodyLength <= 255 ? 1 : 2;
        var position = 34 + counterWidth + 2 + lengthWidth;
        var raw = new byte[position + bodyLength + 4];
        raw[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1), (ushort)(raw.Length - 4));
        raw[7] = 9; raw[31] = 5; raw[32] = 34; raw[33] = (byte)counterWidth;
        if (counterWidth == 1) raw[34] = (byte)counter;
        else BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(34), (ushort)counter);
        var at = 34 + counterWidth;
        raw[at++] = (byte)(lengthWidth == 1 ? 5 : 9); raw[at++] = 35;
        if (lengthWidth == 1) raw[at++] = (byte)bodyLength;
        else { BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at), (ushort)bodyLength); at += 2; }
        for (var i = 0; i < bodyLength; i++) raw[at + i] = (byte)(i * 13);
        new byte[] { 5, 37, 1, 1 }.CopyTo(raw, raw.Length - 4);
        return raw;
    }
}
