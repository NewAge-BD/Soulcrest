using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>
/// One complete supported CharDie form with an explicit PC caster and no NPC caster. The victim
/// can still be an NPC; only the caller's current Own can establish an own PvP death. Native keys
/// and the caster name are transient RAM data and must never be logged or persisted.
/// </summary>
public sealed record AbyssPlayerCasterDeathObservation(int VictimEntityKey, int CasterEntityKey,
    ushort CasterServerId, string CasterName)
{
    public override string ToString() => nameof(AbyssPlayerCasterDeathObservation);
}

/// <summary>
/// Complete bounded PC-caster CharDie variant. PC identity is established by the explicit PC
/// fields and zero NPC field, never by a name pattern, AP/rank change, or the rebirth flag.
/// Other reasons, NPC forms and unqualified trailing flags remain unsupported.
/// </summary>
public static class AbyssPlayerCasterDeathMessage
{
    public static AbyssPlayerCasterDeathObservation? TryParse(ReadOnlySpan<byte> message)
    {
        var form = ReadKnownForm(message);
        // Preserve the independently qualified PC-only contract. The victim can still be an NPC.
        return form is not null && form.Victim > 0 && form.Caster > 0 && form.Caster != form.Victim && form.SkillId >= 0
            && form.Server > 0 && !string.IsNullOrWhiteSpace(form.Name) && !form.Name.Any(char.IsControl) && form.NpcCasterDataId == 0
            && form.NpcDieActionId == 0 && form.Reason == 1 && form.RebirthFlags <= 1
            ? new(form.Victim, form.Caster, form.Server, form.Name) : null;
    }

    /// <summary>
    /// Revocation only for the complete compared neutral empty-caster form. This does not identify
    /// a player, NPC, killer, life, or own result; a caller can only destroy the victim's evidence.
    /// Other PC reasons, flag variants and mixed forms remain unsupported.
    /// </summary>
    public static int? TryParseRevocationVictim(ReadOnlySpan<byte> message)
    {
        var form = ReadKnownForm(message);
        return form is not null && form.Victim > 0 && form.SkillId == 0 && form.Caster == 0 && form.Server == 0
            && form.Name.Length == 0 && form.Guild.Length == 0 && form.Emblem == 0 && form.NpcCasterDataId == 0
            && form.NpcDieActionId == 0 && form.Reason == 0 && form.RebirthFlags == 0
            ? form.Victim : null;
    }

    // Shared structural reader only: exact current scalar widths and bounded strings, with no
    // caster classification or fallback based on a partial prefix. Native details stay in RAM.
    private static KnownForm? ReadKnownForm(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x8D04)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var victim = reader.Vi32();
            var skillId = reader.I32();
            var caster = reader.Vi32();
            var server = reader.U16();
            var name = reader.String(128);
            var guild = reader.String(1024);
            var emblem = reader.U16(); // Same narrow representation as own guild metadata
            var npcCasterDataId = reader.V32();
            var npcDieActionId = reader.I32();
            var reason = reader.U8();
            var rebirthFlags = reader.U8();
            reader.Exact();
            return new(victim, skillId, caster, server, name, guild, emblem, npcCasterDataId,
                npcDieActionId, reason, rebirthFlags);
        }
        catch (InvalidDataException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private sealed record KnownForm(int Victim, int SkillId, int Caster, ushort Server, string Name,
        string Guild, ushort Emblem, uint NpcCasterDataId, int NpcDieActionId, byte Reason, byte RebirthFlags)
    {
        public override string ToString() => nameof(KnownForm);
    }
}
