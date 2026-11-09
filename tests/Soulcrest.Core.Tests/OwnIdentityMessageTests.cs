using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class OwnIdentityMessageTests
{
    [Theory]
    [InlineData(false, 0UL, 300UL, false)] [InlineData(true, 128UL, 300UL, true)]
    [InlineData(true, 300UL, 300UL, true)] [InlineData(false, 301UL, 300UL, null)]
    [InlineData(false, 1UL, 0UL, null)] [InlineData(true, ulong.MaxValue, ulong.MaxValue, null)]
    public void HealthFactUsesOnlyTheCompletelyValidatedOwnAndKeepsInconsistentScalarsUnknown(
        bool optional, ulong health, ulong maximumHealth, bool? expected)
    {
        var message = OwnMessage(optional, health: health, maximumHealth: maximumHealth, appearReason: 4);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(expected, world.HasPositiveHealth);
        Assert.Equal(optional ? (byte?)4 : null, world.AppearReasonCode);
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnIdentityMessage.TryParseWorld(data));
        Assert.Null(OwnIdentityMessage.TryParseWorld([.. message, 0]));
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(255)]
    public void NativeAppearanceReasonIsAnOptionalObservationAndDoesNotInventItsLifeMeaning(byte reason)
    {
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(OwnMessage(true, appearReason: reason)));
        Assert.Equal(reason, world.AppearReasonCode);
        Assert.True(world.HasPositiveHealth);
        Assert.Equal(123456789, world.Identity.CharacterId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsOnlyTheOwnStableIdentityAfterValidatingTheWholeMessage(bool optionalFields)
    {
        var message = OwnMessage(optionalFields);
        var identity = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(message));
        Assert.Equal(new(123456789, 42, 300, "Testchar"), identity);
        Assert.DoesNotContain("SkippedText", identity.ToString());
        NetworkProgressMessageTests.EveryCutRejected(message, d => OwnIdentityMessage.TryParse(d));
    }

    [Theory]
    [InlineData(5, "Gladiator")] [InlineData(8, "Gladiator")]
    [InlineData(9, "Templar")] [InlineData(12, "Templar")]
    [InlineData(13, "Ranger")] [InlineData(16, "Ranger")]
    [InlineData(17, "Assassin")] [InlineData(20, "Assassin")]
    [InlineData(21, "Elementalist")] [InlineData(24, "Elementalist")]
    [InlineData(25, "Sorcerer")] [InlineData(28, "Sorcerer")]
    [InlineData(29, "Cleric")] [InlineData(31, "Cleric")] [InlineData(32, "Cleric")]
    [InlineData(33, "Chanter")] [InlineData(36, "Chanter")]
    public void NativePcVariantsMapToExactBoardClassKeysAfterFullOwnValidation(int pcId, string expected)
    {
        foreach (var optional in new[] { false, true })
        {
            var message = OwnMessage(optional, pcId: pcId);
            var identity = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(message));
            Assert.Equal(expected, identity.ClassId);
            Assert.Equal(123456789, identity.CharacterId);
            NetworkProgressMessageTests.EveryCutRejected(message, d => OwnIdentityMessage.TryParse(d));
            Assert.Null(OwnIdentityMessage.TryParse([.. message, 0]));
        }
    }

    [Theory]
    [InlineData(int.MinValue)] [InlineData(-1)] [InlineData(0)]
    [InlineData(1)] [InlineData(4)] [InlineData(37)] [InlineData(40)]
    [InlineData(41)] [InlineData(44)] [InlineData(45)] [InlineData(48)] [InlineData(int.MaxValue)]
    public void UnknownBaseTestAndFutureClassesKeepAValidOwnIdentity(int pcId)
    {
        var identity = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(OwnMessage(true, pcId: pcId)));
        Assert.Null(identity.ClassId);
        Assert.Equal(new(123456789, 42, 300, "Testchar"), identity);
    }

    [Fact]
    public void ExistingFourArgumentIdentityAndDeconstructionRemainCompatible()
    {
        var identity = new OwnCharacterIdentity(101, 501, 300, "SyntheticAlpha");
        var (character, server, entity, name) = identity;
        Assert.Equal((101L, 501, 300, "SyntheticAlpha"), (character, server, entity, name));
        Assert.Null(identity.ClassId);
        Assert.Equal("Cleric", (identity with { ClassId = "Cleric" }).ClassId);
    }

    [Fact]
    public void RejectsOtherPlayerOpcodesUnknownFlagsAndInvalidOwnNameEncoding()
    {
        var other = OwnMessage(false); other[0] = 0x45;
        Assert.Null(OwnIdentityMessage.TryParse(other));
        var flags = OwnMessage(false); flags[7] |= 0x80;
        Assert.Null(OwnIdentityMessage.TryParse(flags));
        var name = OwnMessage(false); name[10] = 0xff;
        Assert.Null(OwnIdentityMessage.TryParse(name));
        var detail = OwnMessage(false); detail[8] |= 0x80;
        Assert.Null(OwnIdentityMessage.TryParse(detail));
    }

    [Fact]
    public void SameNameOnDifferentServersOrCharacterIdsHasDifferentIdentity()
    {
        var first = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(OwnMessage(false)));
        var otherServer = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(OwnMessage(false, server: 43)));
        var otherCharacter = Assert.IsType<OwnCharacterIdentity>(OwnIdentityMessage.TryParse(OwnMessage(false, characterId: 123456790)));
        Assert.NotEqual(first, otherServer);
        Assert.NotEqual(first, otherCharacter);
        var context = new NetworkProgressContext(first, 7, 3, DateTimeOffset.UnixEpoch);
        Assert.Equal(42, context.ServerId);
        Assert.Equal(123456789, context.CharacterId);
        Assert.Equal("Testchar", context.Name);
    }

    [Fact]
    public void ZeroOrUnavailableIdentityNeverBecomesAProfileContext()
    {
        Assert.Null(OwnIdentityMessage.TryParse(OwnMessage(false, server: 0)));
        Assert.Null(OwnIdentityMessage.TryParse(OwnMessage(false, characterId: 0)));
        Assert.Null(OwnIdentityMessage.TryParse(OwnMessage(false, name: "")));
        Assert.Null(OwnIdentityMessage.TryParse(OwnMessage(false, name: "Test char")));
    }

    [Fact]
    public void EntryPreparationRequiresItsFullSchemaEvenWhenItsMapDoesNotChange()
    {
        var message = Preparation();
        Assert.True(OwnIdentityMessage.IsWorldEntryPreparation(message));
        Assert.True(OwnIdentityMessage.IsWorldEntryPreparation(message));
        for (var cut = 0; cut < message.Length; cut++)
            Assert.False(OwnIdentityMessage.IsWorldEntryPreparation(message[..cut]));
        Assert.False(OwnIdentityMessage.IsWorldEntryPreparation([.. message, 0]));
        message[0] = 0;
        Assert.False(OwnIdentityMessage.IsWorldEntryPreparation(message)); // map-frame clock opcode
    }

    [Theory]
    [InlineData(false, 0L)] [InlineData(true, 0L)]
    [InlineData(false, 952540L)] [InlineData(true, 952540L)]
    [InlineData(false, long.MaxValue)] [InlineData(true, long.MaxValue)]
    public void OptionalAbyssRankTotalRequiresTheCompleteOwnSchema(bool optionalFields, long rankPoints)
    {
        var message = OwnMessage(optionalFields, pcId: 31, abyssRankPoints: rankPoints, abyssRankPointsPresent: true);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(rankPoints, world.AbyssRankPoints);
        Assert.Equal("Cleric", world.Identity.ClassId);
        Assert.Equal(123456789, world.Identity.CharacterId);
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnIdentityMessage.TryParseWorld(data));
        Assert.Null(OwnIdentityMessage.TryParseWorld([.. message, 0]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingAbyssRankFlagLeavesTheTotalUnknown(bool optionalFields)
    {
        var message = OwnMessage(optionalFields, abyssRankPoints: 952540, abyssRankPointsPresent: false);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Null(world.AbyssRankPoints);
        Assert.Equal(123456789, world.Identity.CharacterId);
    }

    [Theory]
    [InlineData(false, -1L)] [InlineData(true, -1L)]
    [InlineData(false, long.MinValue)] [InlineData(true, long.MinValue)]
    public void InvalidAbyssRankTotalPreservesOtherFullyValidatedOwnProgress(bool optionalFields, long rankPoints)
    {
        var message = OwnMessage(optionalFields, gearScore: 17, combatScore: 42,
            abyssRankPoints: rankPoints, abyssRankPointsPresent: true);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Null(world.AbyssRankPoints);
        Assert.Equal(17, world.GearScore);
        Assert.Equal(42, world.CombatScore);
        Assert.Equal(123456789, world.Identity.CharacterId);
    }

    private static byte[] Preparation()
    {
        using var w = new Packet(0x3621);
        w.I(1); w.I(1010); w.L(0); w.Z(12 + 4); w.B(0); w.B(0); w.L(0);
        w.V(1); w.I(1); w.B(0); w.L(0);
        w.V(300); w.S(""); w.B(0);
        return w.Bytes;
    }

    internal static byte[] OwnMessage(bool optional, ushort server = 42, long characterId = 123456789, string name = "Testchar", long mapKey = 0, int mapId = 0, int pcId = 0, int gearScore = 0, int highestGearScore = 0, long combatScore = 0, long highestCombatScore = 0, long? abyssRankPoints = null, bool? abyssRankPointsPresent = null, ulong health = 128, ulong maximumHealth = 300, byte appearReason = 0, int characterLevel = 0, int combatLevel = 0, int conquerorLevel = 0, int maximumMana = 0, int? wingId = null, byte? wingLevel = null, int? wingSkinId = null)
    {
        using var w = new Packet(0x3633);
        var hasAbyssRankPoints = abyssRankPointsPresent ?? optional;
        var flags = optional ? 0x3fffffff : 0;
        flags = hasAbyssRankPoints ? flags | 65536 : flags & ~65536;
        w.V(300); w.I(flags);
        w.B(optional ? (byte)63 : (byte)1); w.S(name); w.U(server); w.I(pcId);
        if (optional) w.B(1);
        w.I(characterLevel); w.I(gearScore); w.I(highestGearScore); w.Z(12); w.I(combatLevel); w.Z(8);
        if (optional) w.B(1);
        w.V(optional ? 1u : 0u); if (optional) w.Z(14);
        w.V(optional ? 1u : 0u); if (optional) w.Z(13);
        w.Z(16); w.B(0);
        if (optional) { w.B(1); Points(w); Points(w); }

        var stateFlags = optional ? ushort.MaxValue : (ushort)0;
        if (wingId is not null) stateFlags |= 4096;
        if (wingLevel is not null) stateFlags |= 8192;
        if (wingSkinId is not null) stateFlags |= 16384;
        w.U(stateFlags); w.Z(18);
        if (optional) w.Z(12 + 4 + 2 + 12 + 12 + 4);
        w.B(1); Var64(w, health); Var64(w, maximumHealth); w.I(0); w.I(maximumMana); w.Z(44);
        if (optional) { w.Z(1 + 4 + 1 + 1); w.V(300); w.Z(4); }
        if ((stateFlags & 4096) != 0) w.I(wingId ?? 0);
        if ((stateFlags & 8192) != 0) w.B(wingLevel ?? 0);
        if ((stateFlags & 16384) != 0) w.I(wingSkinId ?? 0);
        if (optional) w.I(0);
        if (optional) { w.L(1); w.S(""); w.U(0); w.B(0); w.I(0); w.L(0); w.U(0); w.S(""); }
        w.V(optional ? 1u : 0u); if (optional) w.Z(6);
        w.V(optional ? 1u : 0u);
        if (optional)
        {
            w.B(31); w.V(300); w.Z(20); w.V(300); w.B(1); w.I(0); w.Bool(true); w.V(300); w.Z(4 + 12);
        }
        w.V(optional ? 2u : 0u);
        if (optional)
        {
            for (var i = 0; i < 2; i++)
            {
                w.Z(14); w.Bool(i == 0); w.I(0); w.V(1);
                w.Z(4); w.Bool(true); w.Z(11); w.Bool(false); w.Z(9); w.Bool(true); w.Z(4);
                w.V(1); w.I(0); w.Bool(true); w.Bool(false); w.Bool(true); w.V(300);
            }
        }
        if (optional) w.B(appearReason);
        w.L(mapKey); w.I(mapId);
        if (optional) w.Z(4 + 4 + 12 + 2);
        w.L(characterId);
        if (optional) { w.Z(4 + 4 + 1 + 1 + 1); w.Bool(true); w.V(300); }
        w.V(optional ? 3u : 0u); if (optional) w.Z(3);
        w.Bool(true);
        if (optional)
        {
            w.B(1); w.V(300); w.Z(4);
        }
        if (hasAbyssRankPoints) w.L(abyssRankPoints ?? 0);
        if (optional)
        {
            w.V(1); w.B(1); w.B(1); w.S(""); w.Z(4 + 12 + 8 + 8); w.V(1); w.L(0);
            w.Z(4 + 4 + 4 + 8);
            w.Z(10); w.S("SkippedText"); w.Z(5);
            w.U(42);
        }
        w.I(conquerorLevel); w.Z(12);
        if (optional) { w.Bool(true); w.Z(8 + 4 + 16); }
        w.L(combatScore); w.L(highestCombatScore); Points(w); Points(w);
        if (optional) { w.V(1); w.Z(8); w.V(1); w.Z(8); }
        return w.Bytes;
    }

    private static void Points(Packet w) { w.V(1); w.B(1); w.U(2); }
    private static void Var64(Packet w, ulong value)
    {
        while (value >= 128) { w.B((byte)(value | 128)); value >>= 7; }
        w.B((byte)value);
    }
}
