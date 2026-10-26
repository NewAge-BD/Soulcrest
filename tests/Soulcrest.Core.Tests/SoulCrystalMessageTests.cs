using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic crystal envelopes only, without captures, pet identities or entity keys.</summary>
public sealed class SoulCrystalMessageTests
{
    [Fact]
    public void ItemOriginGainsKeepNativeTypesAmountsAndWireOrder()
    {
        var message = Packet([(5, 9), (1, 7), (4, 6), (2, int.MaxValue), (3, 1)]);
        var gains = Assert.IsAssignableFrom<IReadOnlyList<SoulCrystalGain>>(SoulCrystalMessage.TryParseGains(message));
        Assert.Equal(new SoulCrystalGain[] { new(5, 9), new(1, 7), new(4, 6), new(2, int.MaxValue), new(3, 1) }, gains);
        Assert.Throws<NotSupportedException>(() => ((IList<SoulCrystalGain>)gains).Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(255)]
    public void UnknownOrAggregateTypesAreNotQualified(byte type) =>
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([(type, 1)])));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveAmountsAreRejected(int amount) =>
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([(2, amount)])));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(255)]
    public void OnlyTheObservedItemReasonIsQualified(byte reason) =>
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([(2, 1)], reason)));

    [Fact]
    public void EmptyAndOverLimitListsAreRejected()
    {
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([])));
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([(1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (1, 1)])));
    }

    [Fact]
    public void DuplicateTypesDoNotBecomeMultipleGains() =>
        Assert.Null(SoulCrystalMessage.TryParseGains(Packet([(2, 1), (2, 1)])));

    [Fact]
    public void EveryTruncationAndTrailingByteAreRejected()
    {
        var message = Packet([(2, 1), (4, 7)]);
        for (var length = 0; length < message.Length; length++)
            Assert.Null(SoulCrystalMessage.TryParseGains(message.AsSpan(0, length)));
        Assert.Null(SoulCrystalMessage.TryParseGains([.. message, 0]));
    }

    [Fact]
    public void WrongOpcodeCannotBeUsedAsCrystalIncome()
    {
        var message = Packet([(2, 1)]);
        message[0] = 0x0d;
        Assert.Null(SoulCrystalMessage.TryParseGains(message));
        Assert.Null(SoulCrystalMessage.TryParseGains([]));
        Assert.Null(SoulCrystalMessage.TryParseGains([0x11]));
    }

    [Fact]
    public void NoncanonicalOrOverflowingCountsAreRejected()
    {
        foreach (var count in new byte[][] { [0x81, 0], [0xff, 0xff, 0xff, 0xff, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80] })
            Assert.Null(SoulCrystalMessage.TryParseGains([0x11, 0x90, .. count, 2, 1, 0, 0, 0, 2]));
    }

    private static byte[] Packet((byte Type, int Amount)[] gains, byte reason = 2)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x9011);
        writer.Write((byte)gains.Length);
        foreach (var gain in gains) { writer.Write(gain.Type); writer.Write(gain.Amount); }
        writer.Write(reason);
        return stream.ToArray();
    }
}
