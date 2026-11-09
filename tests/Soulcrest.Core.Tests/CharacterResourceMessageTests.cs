using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class CharacterResourceMessageTests
{
    [Theory]
    [InlineData(1)] [InlineData(300)] [InlineData(301)] [InlineData(int.MaxValue)]
    public void EntityPrefixOnlyFiltersAndNeverRequiresOrAuthorizesAValidBody(int entity)
    {
        using var w = new Packet(0x8D00); w.V((uint)entity);
        Assert.Equal(entity, CharacterResourceMessage.TryReadEntityKeyPrefix(w.Bytes));
        Assert.Null(CharacterResourceMessage.TryParseUpdate(w.Bytes));
        Assert.Equal(entity, CharacterResourceMessage.TryReadEntityKeyPrefix([.. w.Bytes, 255, 1, 2, 3]));
        Assert.Null(CharacterResourceMessage.TryParseUpdate([.. w.Bytes, 255, 1, 2, 3]));
        Assert.Equal(entity, CharacterResourceMessage.TryReadEntityKeyPrefix(Update(false, [(7, 100)], (uint)entity)));
    }

    [Fact]
    public void EntityPrefixRejectsOtherOpcodeTruncationNoncanonicalNegativeAndOutOfBoundsPrefixes()
    {
        foreach (var message in new byte[][]
        {
            [], [0], [0, 0x8D], [0, 0x8D, 0x80], [0, 0x8D, 0x80, 0], [1, 0x8D, 1],
            [0, 0x8D, 0], [0, 0x8D, 0xff, 0xff, 0xff, 0xff, 0x0f],
            [0, 0x8D, 0x80, 0x80, 0x80, 0x80, 0x08], [0, 0x8D, 0xff, 0xff, 0xff, 0xff, 0x1f]
        }) Assert.Null(CharacterResourceMessage.TryReadEntityKeyPrefix(message));
        var large = new byte[(1 << 20) + 1]; large[1] = 0x8D; large[2] = 1;
        Assert.Null(CharacterResourceMessage.TryReadEntityKeyPrefix(large));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteUpdateReturnsOnlyExplicitMaximaAndRejectsEveryTruncation(bool wide)
    {
        var message = Update(wide, [(1, 123), (7, 25543), (8, 4945), (255, -17)]);
        var value = Assert.IsType<CharacterResourceUpdate>(CharacterResourceMessage.TryParseUpdate(message));
        Assert.Equal(300, value.EntityKey);
        Assert.Equal(new(25543, 4945), value.Maximums);
        Assert.Equal(new[] { "MaxHp", "MaxMp" }, typeof(CharacterResourceMaximums).GetProperties().Select(p => p.Name));
        NetworkProgressMessageTests.EveryCutRejected(message, data => CharacterResourceMessage.TryParseUpdate(data));
    }

    [Fact]
    public void BothNativeMapWidthsCanSupplyDifferentMaximaWithoutRetainingOtherPoints()
    {
        using var w = new Packet(0x8D00);
        w.V(300); w.B(3); w.V(2); w.B(1); w.I(100); w.B(8); w.I(4945);
        w.V(2); w.B(2); w.L(200); w.B(7); w.L(long.MaxValue);
        var value = Assert.IsType<CharacterResourceUpdate>(CharacterResourceMessage.TryParseUpdate(w.Bytes));
        Assert.Equal(new(long.MaxValue, 4945), value.Maximums);
        NetworkProgressMessageTests.EveryCutRejected(w.Bytes, data => CharacterResourceMessage.TryParseUpdate(data));
    }

    [Theory]
    [InlineData(7)] [InlineData(8)]
    public void ExplicitZeroMaximumIsRetainedAndAbsentCounterpartIsUnknown(byte key)
    {
        var value = Assert.IsType<CharacterResourceUpdate>(CharacterResourceMessage.TryParseUpdate(Update(false, [(key, 0)])));
        Assert.Equal(key == 7 ? new(0, null) : new(null, 0), value.Maximums);
    }

    [Fact]
    public void ValidUpdateWithoutMaximumKeysHasNoImplicitMaximumZeroes()
    {
        var value = Assert.IsType<CharacterResourceUpdate>(CharacterResourceMessage.TryParseUpdate(Update(true, [(1, 500), (2, 100)])));
        Assert.Equal(new(null, null), value.Maximums);
        using var empty = new Packet(0x8D00); empty.V(300); empty.B(0);
        Assert.Equal(new(null, null), CharacterResourceMessage.TryParseUpdate(empty.Bytes)!.Maximums);
    }

    [Theory]
    [InlineData(false, 7)] [InlineData(false, 8)] [InlineData(true, 7)] [InlineData(true, 8)]
    public void NegativeMaximumInvalidatesWholeUpdateInsteadOfLookingAbsent(bool wide, byte key)
        => Assert.Null(CharacterResourceMessage.TryParseUpdate(Update(wide, [(key, -1)])));

    [Fact]
    public void DuplicatesInEitherMapOrAcrossBothMapsAreRejectedIncludingIgnoredKeys()
    {
        foreach (var key in new byte[] { 1, 7, 8 })
        {
            Assert.Null(CharacterResourceMessage.TryParseUpdate(Update(false, [(key, 1), (key, 1)])));
            Assert.Null(CharacterResourceMessage.TryParseUpdate(Update(true, [(key, 1), (key, 1)])));
            using var w = new Packet(0x8D00);
            w.V(300); w.B(3); w.V(1); w.B(key); w.I(1); w.V(1); w.B(key); w.L(1);
            Assert.Null(CharacterResourceMessage.TryParseUpdate(w.Bytes));
        }
    }

    [Fact]
    public void UnknownFlagsInvalidEntityCountOverflowOtherOpcodeAndOversizeAreRejected()
    {
        var valid = Update(false, [(7, 25543)]);
        var wrong = valid.ToArray(); wrong[0] = 1;
        Assert.Null(CharacterResourceMessage.TryParseUpdate(wrong));
        foreach (var entity in new uint[] { 0, uint.MaxValue, 0x80000000 })
        {
            using var w = new Packet(0x8D00); w.V(entity); w.B(0);
            Assert.Null(CharacterResourceMessage.TryParseUpdate(w.Bytes));
        }
        using var flags = new Packet(0x8D00); flags.V(300); flags.B(4);
        Assert.Null(CharacterResourceMessage.TryParseUpdate(flags.Bytes));
        using var count = new Packet(0x8D00); count.V(300); count.B(1); count.V(257); count.Z(257 * 5);
        Assert.Null(CharacterResourceMessage.TryParseUpdate(count.Bytes));
        Assert.Null(CharacterResourceMessage.TryParseUpdate([0, 0x8D, 0x80, 0, 0]));
        Assert.Null(CharacterResourceMessage.TryParseUpdate(new byte[(1 << 20) + 1]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FullOwnPublishesNativeMaximaWithoutChangingExistingLifeSemantics(bool optional)
    {
        var message = OwnIdentityMessageTests.OwnMessage(optional, health: 0, maximumHealth: 25543, maximumMana: 4945);
        var own = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(new(25543, 4945), own.ResourceMaximums);
        Assert.False(own.HasPositiveHealth);
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnIdentityMessage.TryParseWorld(data));
    }

    [Theory]
    [InlineData(0UL, 0, 0L, 0L)]
    [InlineData(300UL, -1, 300L, null)]
    [InlineData(ulong.MaxValue, 100, null, 100L)]
    public void InvalidOwnMaximaRemainUnknownWhileExplicitZeroIsRetained(ulong hp, int mp, long? expectedHp, long? expectedMp)
    {
        var own = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(true, health: 0, maximumHealth: hp, maximumMana: mp)));
        Assert.Equal(new(expectedHp, expectedMp), own.ResourceMaximums);
    }

    private static byte[] Update(bool wide, (byte Key, long Value)[] values, uint entity = 300)
    {
        using var w = new Packet(0x8D00); w.V(entity); w.B(wide ? (byte)2 : (byte)1); w.V((uint)values.Length);
        foreach (var (key, value) in values) { w.B(key); if (wide) w.L(value); else w.I(checked((int)value)); }
        return w.Bytes;
    }
}
