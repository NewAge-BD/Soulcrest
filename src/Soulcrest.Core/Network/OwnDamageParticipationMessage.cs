namespace Soulcrest.Core.Network;

/// <summary>
/// Transient own participation only. Native actor keys are RAM data; neither keys nor damage
/// amounts belong in session records, diagnostic text or the killfeed's persisted data.
/// </summary>
public sealed record OwnDamageParticipationObservation(int CasterEntityKey, int VictimEntityKey,
    bool HasPositiveDamage)
{
    public override string ToString() => nameof(OwnDamageParticipationObservation);
}

/// <summary>
/// Complete, narrow direct-result forms compared with the controlled own PvP recordings.
/// This is participation evidence for an explicitly estimated assist, never server assist credit.
/// Positive amounts in this protocol also occur for healing; unknown effects therefore remain
/// unsupported. Only the individually compared damaging effects are admitted. Periodic results,
/// zero-damage hits, healing, support, reflected and pet damage are deliberately not inferred.
/// </summary>
public static class OwnDamageParticipationMessage
{
    public static OwnDamageParticipationObservation? TryParse(ReadOnlySpan<byte> message, int ownEntityKey)
    {
        if (ownEntityKey <= 0 || message.Length < 2 || message[0] != 0x04 || message[1] != 0x38) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var victim = reader.Vi32();
            var fields = reader.U16();
            // Finite forms positively matched to own PC hits in the closed 2026-10-30 corpus.
            // Optional scalar/list/packed-Boolean order was independently read from static IL.
            // A previously unseen combination does not acquire meaning through a broad bitmask.
            if (fields is not (0x0004 or 0x0006 or 0x0024 or 0x0026 or 0x0046 or 0x0066
                or 0x1044 or 0x1046 or 0x1066)) return null;
            var caster = reader.Vi32();
            reader.U32(); // Skill reference, not used to infer participation or classify a target.
            reader.U8();
            var resultKind = reader.U8();
            byte packedResultFlags = 0;
            if ((fields & 0x0002) != 0)
            {
                packedResultFlags = reader.U8(); // Six Booleans, scalar, seventh Boolean.
                ReadNonnegativeInt64(ref reader);
                reader.U8();
            }
            var effectId = reader.U32();
            reader.U32();
            reader.V32();
            var positive = ReadNonnegativeInt64(ref reader) > 0;
            if ((fields & 0x0020) != 0)
            {
                var count = reader.Count(1, 64);
                for (var index = 0; index < count; index++) ReadNonnegativeInt64(ref reader);
            }
            if ((fields & 0x0040) != 0) reader.Skip(reader.Count(4, 64) * 4);
            var resultLoop = reader.U16();
            if ((fields & 0x1000) != 0)
            {
                // With the seven preceding bits this consumes bit seven of the original byte,
                // despite the intervening scalars/lists. Without that block it starts a byte.
                if ((fields & 0x0002) == 0 && (reader.U8() & 0xFE) != 0) return null;
            }
            else if ((fields & 0x0002) != 0 && (packedResultFlags & 0x80) != 0) return null;
            reader.Exact();

            return caster == ownEntityKey && victim > 0 && victim != caster && positive
                && resultKind is 2 or 3 && resultLoop == 1
                && IsComparedDamagingEffect(effectId)
                ? new(caster, victim, true) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    // Each literal has a positive identity/time/raw-skill/amount match to a saved outgoing
    // NotMeter PC hit, plus a matching Damage entry in the existing static client reference.
    // That historical projection is not a complete native effect catalogue. No ranges, negative
    // healing-list inference or unknown-effect fallback; this is deliberately incomplete.
    private static bool IsComparedDamagingEffect(uint effectId) => effectId is
        300_001_911 or 1_701_000_011 or 1_702_000_011 or 1_703_004_011 or 1_704_000_011
        or 1_705_034_011 or 1_705_034_012 or 1_706_000_012 or 1_706_001_311 or 1_706_001_312
        or 1_707_034_011 or 1_707_034_012 or 1_708_023_011 or 1_708_023_012 or 1_719_004_011
        or 1_730_004_011 or 1_730_004_013 or 1_730_004_014 or 1_735_345_011 or 1_735_345_012
        or 1_735_345_013 or 1_773_000_111;

    private static long ReadNonnegativeInt64(ref ProgressPacketReader reader)
    {
        var value = reader.V64();
        if (value > long.MaxValue) throw new InvalidDataException("Participation scalar exceeds signed range.");
        return (long)value;
    }
}
