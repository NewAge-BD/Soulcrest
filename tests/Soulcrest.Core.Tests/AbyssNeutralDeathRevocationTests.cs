using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AbyssNeutralDeathRevocationTests
{
    [Fact]
    public void WholeNeutralEmptyCasterFormOnlyReturnsTheVictimAndNeverBecomesAPcDeath()
    {
        var message = Message();
        Assert.Equal(50_000, AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(message));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParse(message));
        for (var length = 0; length < message.Length; length++)
            Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(message.AsSpan(0, length)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim([.. message, 0]));
    }

    [Theory]
    [InlineData("victim-zero")] [InlineData("victim-negative")] [InlineData("skill-positive")]
    [InlineData("skill-negative")] [InlineData("caster")] [InlineData("server")]
    [InlineData("name")] [InlineData("guild")] [InlineData("emblem")] [InlineData("npc")]
    [InlineData("action")] [InlineData("action-negative")] [InlineData("reason")] [InlineData("flags")]
    public void EveryUncomparedSemanticFieldStaysOutsideTheNeutralRevocationContract(string field)
    {
        var message = field switch
        {
            "victim-zero" => Message(victim: 0),
            "victim-negative" => Message(victim: uint.MaxValue),
            "skill-positive" => Message(skill: 1),
            "skill-negative" => Message(skill: -1),
            "caster" => Message(caster: 400),
            "server" => Message(server: 501),
            "name" => Message(name: "SyntheticCaster"u8.ToArray()),
            "guild" => Message(guild: "Synthetic guild"u8.ToArray()),
            "emblem" => Message(emblem: 17),
            "npc" => Message(npc: 12_345_678),
            "action" => Message(action: 1),
            "action-negative" => Message(action: -1),
            "reason" => Message(reason: 1),
            "flags" => Message(rebirth: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(message));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void TheExistingPcContractRemainsSeparateFromTheNeutralVictimContract(int rebirth)
    {
        var message = Message(caster: 400, server: 501, name: "SyntheticCaster"u8.ToArray(),
            guild: "Synthetic guild"u8.ToArray(), skill: 123, emblem: 17, reason: 1, rebirth: (byte)rebirth);
        var pc = Assert.IsType<AbyssPlayerCasterDeathObservation>(AbyssPlayerCasterDeathMessage.TryParse(message));
        Assert.Equal(50_000, pc.VictimEntityKey);
        Assert.Equal(400, pc.CasterEntityKey);
        Assert.Equal(nameof(AbyssPlayerCasterDeathObservation), pc.ToString());
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(message));
    }

    [Theory]
    [InlineData("8000")] [InlineData("FFFFFFFF10")] [InlineData("8080808080")]
    public void TheWholeNeutralFormRejectsNoncanonicalOverflowingAndUnterminatedVarints(string hex)
    {
        var invalid = Convert.FromHexString(hex);
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(victimVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(casterVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(npcVar: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(nameLength: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(guildLength: invalid)));
    }

    [Theory]
    [InlineData("FF")] [InlineData("C080")] [InlineData("EDA080")]
    public void InvalidUtf8RemainsAStructuralRejectionRatherThanARevocationPrefix(string hex)
    {
        var invalid = Convert.FromHexString(hex);
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(name: invalid)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(guild: invalid)));
    }

    [Theory]
    [InlineData((ushort)0x3647)] [InlineData((ushort)0x3645)] [InlineData((ushort)0xE273)]
    public void OtherFramedOpcodesNeverBorrowTheNeutralVictimContract(ushort opcode) =>
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(opcode: opcode)));

    [Fact]
    public void UnknownReasonsFlagsAndNonemptyWhitespaceAreNotNeutral()
    {
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(reason: byte.MaxValue)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(rebirth: byte.MaxValue)));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(name: " "u8.ToArray())));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(guild: " "u8.ToArray())));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(name: Encoding.UTF8.GetBytes(new string('x', 129)))));
        Assert.Null(AbyssPlayerCasterDeathMessage.TryParseRevocationVictim(Message(guild: Encoding.UTF8.GetBytes(new string('x', 1025)))));
    }

    private static byte[] Message(uint victim = 50_000, uint caster = 0, ushort server = 0,
        uint npc = 0, int skill = 0, int action = 0, byte reason = 0, byte rebirth = 0, ushort emblem = 0,
        ushort opcode = 0x8D04, byte[]? name = null, byte[]? guild = null,
        byte[]? victimVar = null, byte[]? casterVar = null, byte[]? npcVar = null,
        byte[]? nameLength = null, byte[]? guildLength = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(opcode); Scalar(writer, victim, victimVar); writer.Write(skill); Scalar(writer, caster, casterVar);
        writer.Write(server); Text(writer, name ?? [], nameLength); Text(writer, guild ?? [], guildLength);
        writer.Write(emblem); Scalar(writer, npc, npcVar); writer.Write(action); writer.Write(reason); writer.Write(rebirth);
        return stream.ToArray();
    }

    private static void Text(BinaryWriter writer, byte[] value, byte[]? encodedLength)
    { Scalar(writer, (uint)value.Length, encodedLength); writer.Write(value); }
    private static void Scalar(BinaryWriter writer, uint value, byte[]? encoded)
    {
        if (encoded is not null) { writer.Write(encoded); return; }
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
