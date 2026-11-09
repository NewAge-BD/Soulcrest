using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class CharacterWingMessageTests
{
    [Fact]
    public void FullCollectionRetainsIndependentEnhancementObservationsAndEveryByteMustBePresent()
    {
        var message = List((30101100, 0), (30101101, 255));
        var owned = Assert.IsType<CharacterWingOwnership>(CharacterWingMessage.TryParseList(message));
        Assert.Equal((byte)0, owned.Wings[30101100]); Assert.Equal((byte)255, owned.Wings[30101101]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, byte>)owned.Wings).Add(1, 0));
        NetworkProgressMessageTests.EveryCutRejected(message, data => CharacterWingMessage.TryParseList(data));
        Assert.Empty(CharacterWingMessage.TryParseList(List())!.Wings);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 1)] [InlineData(1, 1)]
    public void InvalidAndDuplicateIdsAreRejectedWithoutInventingOwnership(int first, int second) =>
        Assert.Null(CharacterWingMessage.TryParseList(List((first, 0), (second, 1))));

    [Fact]
    public void OversizedNoncanonicalAndTruncatedListsAreRejected()
    {
        Assert.Null(CharacterWingMessage.TryParseList([0x72, 0x56, 0x80, 0]));
        Assert.Null(CharacterWingMessage.TryParseList([0x72, 0x56, 0xff, 0xff, 0xff, 0xff, 0x0f]));
        using var packet = new Packet(0x5672); packet.V(10001); packet.Z(10001 * 5);
        Assert.Null(CharacterWingMessage.TryParseList(packet.Bytes));
        var oversized = new byte[(1 << 20) + 1]; oversized[0] = 0x72; oversized[1] = 0x56;
        Assert.Null(CharacterWingMessage.TryParseList(oversized));
    }

    [Theory]
    [InlineData(false, 30101100)] [InlineData(true, 30109999)] [InlineData(false, 0)] [InlineData(true, 0)]
    public void ChangesKeepSkinSeparatePermitExplicitEmptyAndRequireTheFullEnvelope(bool skin, int id)
    {
        var message = Change(300, skin, id);
        Assert.Equal(new CharacterWingChange(300, skin, id), CharacterWingMessage.TryParseChange(message));
        Assert.Equal(300, CharacterWingMessage.TryReadEntityKeyPrefix(message));
        NetworkProgressMessageTests.EveryCutRejected(message, data => CharacterWingMessage.TryParseChange(data));
        Assert.Equal(300, CharacterWingMessage.TryReadEntityKeyPrefix(message[..4]));
    }

    [Fact]
    public void InvalidEntitiesIdsOpcodesAndNoncanonicalKeysCannotBecomeChanges()
    {
        Assert.Null(CharacterWingMessage.TryParseChange(Change(0, false, 1)));
        Assert.Null(CharacterWingMessage.TryParseChange(Change(uint.MaxValue, false, 1)));
        Assert.Null(CharacterWingMessage.TryParseChange(Change(300, false, -1)));
        Assert.Null(CharacterWingMessage.TryParseChange([0x76, 0x56, 0x80, 0, 0, 1, 0, 0, 0]));
        Assert.Null(CharacterWingMessage.TryParseChange(List((1, 0))));
        Assert.Null(CharacterWingMessage.TryParseList(Change(300, false, 1)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnWingFieldsAreReturnedOnlyAfterFullValidationAndDoNotClaimCollectionOwnership(bool optional)
    {
        var message = OwnIdentityMessageTests.OwnMessage(optional, wingId: 30101100, wingLevel: 7, wingSkinId: 30109999);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(new CharacterWingEquipment(30101100, 7, 30109999), world.EquippedWing);
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnIdentityMessage.TryParseWorld(data));
    }

    [Fact]
    public void MissingZeroAndInvalidOwnWingFieldsRemainDistinctWithoutBreakingOtherOwnFacts()
    {
        Assert.Null(OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false))!.EquippedWing);
        var empty = OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false, wingId: 0, wingSkinId: 0))!;
        Assert.Equal(new CharacterWingEquipment(0, null, 0), empty.EquippedWing);
        var invalid = OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false, wingId: -1, wingLevel: 255, wingSkinId: -2))!;
        Assert.Equal(123456789, invalid.Identity.CharacterId);
        Assert.Equal(new CharacterWingEquipment(null, 255, null), invalid.EquippedWing);
        var skinOnly = OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false, wingSkinId: 123))!;
        Assert.Equal(new CharacterWingEquipment(null, null, 123), skinOnly.EquippedWing);
    }

    private static byte[] List(params (int Id, byte Level)[] wings)
    {
        using var packet = new Packet(0x5672); packet.V((uint)wings.Length);
        foreach (var wing in wings) { packet.I(wing.Id); packet.B(wing.Level); }
        return packet.Bytes;
    }
    private static byte[] Change(uint entity, bool skin, int id)
    {
        using var packet = new Packet(0x5676); packet.V(entity); packet.Bool(skin); packet.I(id);
        return packet.Bytes;
    }
}
