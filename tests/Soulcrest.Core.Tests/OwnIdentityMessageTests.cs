using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class OwnIdentityMessageTests
{
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

    private static byte[] Preparation()
    {
        using var w = new Packet(0x3621);
        w.I(1); w.I(1010); w.L(0); w.Z(12 + 4); w.B(0); w.B(0); w.L(0);
        w.V(1); w.I(1); w.B(0); w.L(0);
        w.V(300); w.S(""); w.B(0);
        return w.Bytes;
    }

    internal static byte[] OwnMessage(bool optional, ushort server = 42, long characterId = 123456789, string name = "Testchar", long mapKey = 0, int mapId = 0)
    {
        using var w = new Packet(0x3633);
        w.V(300); w.I(optional ? 0x3fffffff : 0);
        w.B(optional ? (byte)63 : (byte)1); w.S(name); w.U(server); w.I(0);
        if (optional) w.B(1);
        w.Z(36);
        if (optional) w.B(1);
        w.V(optional ? 1u : 0u); if (optional) w.Z(14);
        w.V(optional ? 1u : 0u); if (optional) w.Z(13);
        w.Z(16); w.B(0);
        if (optional) { w.B(1); Points(w); Points(w); }

        w.U(optional ? ushort.MaxValue : (ushort)0); w.Z(18);
        if (optional) w.Z(12 + 4 + 2 + 12 + 12 + 4);
        w.B(1); w.V(128); w.V(300); w.Z(52);
        if (optional) { w.Z(1 + 4 + 1 + 1); w.V(300); w.Z(4 + 4 + 1 + 4 + 4); }
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
        if (optional) w.B(0);
        w.L(mapKey); w.I(mapId);
        if (optional) w.Z(4 + 4 + 12 + 2);
        w.L(characterId);
        if (optional) { w.Z(4 + 4 + 1 + 1 + 1); w.Bool(true); w.V(300); }
        w.V(optional ? 3u : 0u); if (optional) w.Z(3);
        w.Bool(true);
        if (optional)
        {
            w.B(1); w.V(300); w.Z(4 + 8);
            w.V(1); w.B(1); w.B(1); w.S(""); w.Z(4 + 12 + 8 + 8); w.V(1); w.L(0);
            w.Z(4 + 4 + 4 + 8);
            w.Z(10); w.S("SkippedText"); w.Z(5);
            w.U(42);
        }
        w.Z(16);
        if (optional) { w.Bool(true); w.Z(8 + 4 + 16); }
        w.Z(16); Points(w); Points(w);
        if (optional) { w.V(1); w.Z(8); w.V(1); w.Z(8); }
        return w.Bytes;
    }

    private static void Points(Packet w) { w.V(1); w.B(1); w.U(2); }
}
