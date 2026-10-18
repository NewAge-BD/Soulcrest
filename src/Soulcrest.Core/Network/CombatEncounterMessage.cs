namespace Soulcrest.Core.Network;

/// <summary>
/// A fully consumed NPC appearance. NPC classification is not implied by HP, life/combat bytes,
/// parent fields, or this packet's presence. Entity and parent keys belong to the source generation.
/// ParentDescriptionDataId may describe a player class; it is not necessarily an NPC or an owner.
/// </summary>
public sealed record CombatNpcAppearance(int EntityKey, int NpcDataId, long CurrentHp, long MaximumHp,
    byte RawLifeState, byte? RawCombatState, int? ParentEntityKey, int? ParentDescriptionDataId,
    byte? RawSpawnReason, int? SpawnUniqueKey, ushort RawFlags, ushort RawStateFlags);

/// <summary>Disappearance invalidates a matching current entity; its reason alone does not prove death.</summary>
public sealed record CombatNpcDeparture(int EntityKey, byte Reason, int? DespawnActionId);

/// <summary>
/// Explicit entity-death wire observation. It does not identify a boss, prove a wipe or bind a caster
/// to the own party. Player killer strings and server identifiers are deliberately not returned.
/// </summary>
public sealed record CombatEntityDeath(int EntityKey, int CasterEntityKey, int SkillDataId,
    int NpcCasterDataId, byte Reason, byte DeathType);

/// <summary>
/// A retrospective middle-boss reward notification. It carries a public NPC data ID, no entity key.
/// Match to one current, independently identified encounter; do not create a missing hit timeline.
/// </summary>
public sealed record CombatMiddleBossReward(int NpcDataId);

/// <summary>
/// Strict Global/EU forms checked independently against the 2026-10-17 private captures.
/// No coordinates, names, guild strings, server IDs or raw payloads are retained.
/// </summary>
public static class CombatEncounterMessage
{
    private const int MaximumMessageBytes = 1 << 20;

    public static CombatNpcAppearance? TryParseNpcAppearance(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x3641)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = Positive(ref r);
            var flags = r.U16();
            if (flags is not (0x0014 or 0x001C or 0x001F or 0x002C or 0x005F or 0x1014 or 0x101D or 0x105D
                or 0x101F or 0x1024 or 0x2004 or 0x2005 or 0x200C or 0x200D or 0x200F or 0x220C or 0x2C0C or 0x2C0D)) return null;
            var dataId = ReadDescription(ref r, false);
            // Composite presence/state forms are independently checked in both manual Krao replays.
            var stateFlags = r.U16();
            if (stateFlags is not (0x0200 or 0x0208 or 0x0240 or 0x0248)) return null;
            r.Skip(18); // Position and directions are deliberately not exposed.
            if ((stateFlags & 8) != 0) r.Skip(12); // Velocity vector in the completed Krao captures.
            var life = r.U8();
            var hp = Hp(ref r);
            var maximum = Hp(ref r);
            r.Skip(52); // Non-HP resources.
            byte? combat = (stateFlags & 64) != 0 ? r.U8() : null;
            r.U8(); // Relationship entity byte, not a boss or ownership flag.
            for (var i = r.Count(23, 4096); i > 0; i--) SkipAbnormal(ref r);
            if ((flags & 1) != 0) r.U8();
            if ((flags & 2) != 0) r.U8();
            byte? reason = (flags & 4) != 0 ? r.U8() : null;
            if ((flags & 8) != 0) r.Bool();
            int? parent = (flags & 16) != 0 ? r.I32() : null;
            int? parentDescription = (flags & 32) != 0 ? ReadDescription(ref r, true) : null;
            if ((flags & 64) != 0) SkipGuild(ref r);
            r.Skip(r.Count(6, 4096) * 6);
            r.I32(); // Level does not establish NPC rank or boss kind.
            if ((flags & 512) != 0) SkipGroggy(ref r);
            if ((flags & 1024) != 0) r.Skip(9);
            if ((flags & 2048) != 0) r.I64();
            if ((flags & 4096) != 0) r.I32(); // Spawn-action identifier, not an encounter/result code.
            int? uniqueKey = (flags & 8192) != 0 ? r.I32() : null;
            r.Skip(r.Count(4, 4096) * 4); // Unclassified tags; never treat them as a boss marker.
            r.Exact();
            return hp <= maximum ? new(entity, dataId, hp, maximum, life, combat, parent,
                parentDescription, reason, uniqueKey, flags, stateFlags) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CombatNpcDeparture? TryParseNpcDeparture(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x3642)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = Positive(ref r);
            if (r.U8() != 0) return null; // Only the form without despawn-action extension was observed.
            var reason = r.U8();
            r.Exact();
            return new(entity, reason, null);
        }
        catch (InvalidDataException) { return null; }
    }

    public static CombatEntityDeath? TryParseDeath(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x8D04)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = Positive(ref r);
            var skill = r.I32();
            var caster = r.Vi32();
            r.U16();
            r.SkipString();
            r.SkipString();
            r.U16();
            var npcCaster = r.Vi32();
            r.I32();
            var reason = r.U8();
            var deathType = r.U8();
            r.Exact();
            return skill >= 0 && caster >= 0 && npcCaster >= 0
                ? new(entity, caster, skill, npcCaster, reason, deathType) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CombatMiddleBossReward? TryParseMiddleBossReward(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x572E)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var data = r.I32();
            r.Exact();
            return data > 0 ? new(data) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static int ReadDescription(ref ProgressPacketReader r, bool parent)
    {
        var flags = r.U8();
        if (parent ? flags != 0 : flags is not (0 or 1))
            throw new InvalidDataException("Unconfirmed NPC description flags.");
        if ((flags & 1) != 0) r.SkipString();
        var data = r.I32();
        if (data <= 0) throw new InvalidDataException("Invalid NPC description data ID.");
        return data;
    }

    private static void SkipAbnormal(ref ProgressPacketReader r)
    {
        var flags = r.U8();
        if (flags is not (0x11 or 0x13)) throw new InvalidDataException("Unconfirmed NPC abnormal flags.");
        r.Vi32(); r.Skip(20);
        if ((flags & 1) != 0) r.Vi32();
        r.U8();
        if ((flags & 2) != 0) r.I32();
        r.Bool();
        r.Skip(12);
    }

    private static void SkipGuild(ref ProgressPacketReader r)
    {
        r.I64(); r.SkipString(); r.Skip(17); r.SkipString();
    }

    private static void SkipGroggy(ref ProgressPacketReader r)
    {
        if (r.U8() != 3) throw new InvalidDataException("Unconfirmed NPC groggy flags.");
        r.U8(); r.I32(); r.I32();
        r.Bool(); r.Bool();
    }

    private static int Positive(ref ProgressPacketReader r)
    {
        var entity = r.Vi32();
        if (entity <= 0) throw new InvalidDataException("Invalid NPC entity key.");
        return entity;
    }

    private static long Hp(ref ProgressPacketReader r)
    {
        var value = r.V64();
        if (value > long.MaxValue) throw new InvalidDataException("Invalid NPC HP.");
        return (long)value;
    }

    private static bool Matches(ReadOnlySpan<byte> message, ushort opcode) =>
        message.Length is >= 2 and <= MaximumMessageBytes && PetProgressMessage.HasOpcode(message, opcode);
}
