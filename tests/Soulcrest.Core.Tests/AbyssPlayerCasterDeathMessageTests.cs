using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AbyssPlayerCasterDeathMessageTests
{
    [Theory]
    [InlineData((byte)0)] [InlineData((byte)1)]
    public void WholePcCasterFormDoesNotUseRebirthToDecideWhetherTheVictimIsAPlayer(byte rebirth)
    {
        var message = Message(rebirth: rebirth);
        var result = Assert.IsType<AbyssPlayerCasterDeathObservation>(AbyssPlayerCasterDeathMessage.TryParse(message));
        Assert.Equal(300, result.VictimEntityKey); Assert.Equal(400, result.CasterEntityKey);
        Assert.Equal((ushort)501, result.CasterServerId); Assert.Equal("SyntheticCaster", result.CasterName);
        Assert.Equal(nameof(AbyssPlayerCasterDeathObservation), result.ToString());
        NetworkProgressMessageTests.EveryCutRejected(message, data => AbyssPlayerCasterDeathMessage.TryParse(data));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse([.. message, 0]));
    }

    [Theory]
    [InlineData(0U, 400U)] [InlineData(300U, 0U)] [InlineData(300U, 300U)]
    [InlineData(uint.MaxValue, 400U)] [InlineData(300U, uint.MaxValue)]
    public void MissingOutOfRangeAndSelfCasterKeysDoNotDescribeThisSupportedPvpForm(uint victim, uint caster) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(victim: victim, caster: caster)));

    [Theory]
    [InlineData(0, "SyntheticCaster")] [InlineData(501, "")] [InlineData(501, " ")]
    public void PcCasterRequiresBothExplicitServerAndNonemptyPcName(int server, string name) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(server: (ushort)server, name: Encoding.UTF8.GetBytes(name))));

    [Theory]
    [InlineData("Synthetic\0Caster")] [InlineData("Synthetic\nCaster")] [InlineData("Synthetic\tCaster")]
    public void ExplicitPcNamesCannotContainControlCharacters(string name) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(name: Encoding.UTF8.GetBytes(name))));

    [Theory]
    [InlineData(1U)] [InlineData(16_384U)] [InlineData(uint.MaxValue)]
    public void APositiveNpcCasterIsNeverPromotedEvenIfThePcFieldsLookPlausible(uint npc) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(npc: npc)));

    [Fact]
    public void ExplicitNpcCasterWithNoPcIdentityIsUnsupportedRatherThanOwnPvp()
    {
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(server: 0, name: [], npc: 12_345_678)));
    }

    [Theory]
    [InlineData(-1, 0, 1, 0)] [InlineData(1, 1, 1, 0)] [InlineData(1, -1, 1, 0)]
    [InlineData(1, 0, 0, 0)] [InlineData(1, 0, 2, 0)] [InlineData(1, 0, 255, 0)]
    [InlineData(1, 0, 1, 2)] [InlineData(1, 0, 1, 255)]
    public void UncomparedActionsReasonsAndFlagVariantsRemainUnsupported(int skill, int action, int reason, int flags) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(skill: skill, action: action,
            reason: (byte)reason, rebirth: (byte)flags)));

    [Theory]
    [InlineData("8000")] [InlineData("FFFFFFFF10")] [InlineData("8080808080")]
    public void ActorNpcAndStringVarintsRequireCanonicalBoundedEncoding(string hex)
    {
        var invalid = Convert.FromHexString(hex);
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(victimVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(casterVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(npcVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(nameLength: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(guildLength: invalid)));
    }

    [Theory]
    [InlineData("FF")] [InlineData("C080")] [InlineData("EDA080")]
    public void BothExplicitPcTextsRejectInvalidUtf8(string hex)
    {
        var invalid = Convert.FromHexString(hex);
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(name: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(guild: invalid)));
    }

    [Fact]
    public void PcAndGuildTextsHaveSeparateByteResourceBounds()
    {
        Assert.NotNull(AbyssPlayerCasterDeathMessage.TryParse(Message(name: Encoding.UTF8.GetBytes(new string('x', 128)),
            guild: Encoding.UTF8.GetBytes(new string('x', 1024)))));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(name: Encoding.UTF8.GetBytes(new string('x', 129)))));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(guild: Encoding.UTF8.GetBytes(new string('x', 1025)))));
    }

    [Theory]
    [InlineData((ushort)0x3642)] [InlineData((ushort)0x3645)] [InlineData((ushort)0xE273)] [InlineData((ushort)0x561B)]
    public void OtherFramedMessagesNeverBorrowDeathSemantics(ushort opcode) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(Message(opcode: opcode)));

    private static byte[] Message(uint victim = 300, uint caster = 400, ushort server = 501, uint npc = 0,
        int skill = 1, int action = 0, byte reason = 1, byte rebirth = 1, ushort opcode = 0x8D04,
        byte[]? name = null, byte[]? guild = null, byte[]? victimVar = null, byte[]? casterVar = null,
        byte[]? npcVar = null, byte[]? nameLength = null, byte[]? guildLength = null)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(opcode); Scalar(writer, victim, victimVar); writer.Write(skill); Scalar(writer, caster, casterVar);
        writer.Write(server); Text(writer, name ?? Encoding.UTF8.GetBytes("SyntheticCaster"), nameLength);
        Text(writer, guild ?? Encoding.UTF8.GetBytes("Synthetic guild"), guildLength);
        writer.Write((ushort)17); Scalar(writer, npc, npcVar); writer.Write(action); writer.Write(reason); writer.Write(rebirth);
        return stream.ToArray();
    }
    private static void Text(BinaryWriter writer, byte[] bytes, byte[]? length)
    { Scalar(writer, (uint)bytes.Length, length); writer.Write(bytes); }
    private static void Scalar(BinaryWriter writer, uint value, byte[]? encoded)
    {
        if (encoded is not null) { writer.Write(encoded); return; }
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
