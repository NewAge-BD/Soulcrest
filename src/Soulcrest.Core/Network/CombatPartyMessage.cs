using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>
/// Observed members of one party snapshot. This is wire data, not own-connection authority.
/// Match a confirmed own character in this list before applying it; never add own again by convention.
/// Snapshot evidence does not carry across a transport gap or generation change.
/// </summary>
public sealed record CombatPartySnapshot(long PartyKey, long LeaderCharacterId, int MemberLimit,
    IReadOnlyList<CombatPartyMember> Members, byte Reason);

public sealed record CombatPartyMember(byte Slot, int EntityKey, int BirthServerId, int CurrentServerId,
    long CharacterId, string Name, int ClassDataId, long CombatPower, long CurrentHp, long MaximumHp,
    bool IsAlive, bool IsLoggedIn);

/// <summary>Apply only to an already confirmed roster identity; this update has no server or name.</summary>
public sealed record CombatPartyMemberStatus(long CharacterId, int ClassDataId, long CombatPower);

/// <summary>Apply only to an entity in the current confirmed party and source generation.</summary>
public sealed record CombatPartyMemberVitals(int EntityKey, long CurrentHp, long MaximumHp, bool IsAlive);

/// <summary>Only a matching current party key may invalidate its confirmed roster.</summary>
public sealed record CombatPartyDeparture(long PartyKey, byte Reason);

/// <summary>
/// Bounded Global/EU party forms independently checked against the 2026-10-17 private captures.
/// No account strings, guild names, coordinates or unrelated character appearances are returned.
/// Class data IDs are preserved without inventing a localized class mapping.
/// </summary>
public static class CombatPartyMessage
{
    private const int MaximumMessageBytes = 1 << 20;

    public static CombatPartySnapshot? TryParseSnapshot(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x9200)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var flags = r.U8();
            // Only the no-marker and focus-target forms were present in the Global/EU captures.
            // Unobserved marker, scan and map-pin extensions must not grant a partial roster.
            if ((flags & ~1) != 0) return null;
            var partyKey = r.I64();
            var leader = r.I64();
            var limit = r.I32();
            r.Skip(4); // Distribution and join settings.
            if ((flags & 1) != 0)
            {
                var focusFlags = r.U8();
                if ((focusFlags & ~1) != 0) return null;
                r.I32();
                if ((focusFlags & 1) != 0) r.I32();
                r.I32();
            }
            var count = r.Count(90, 5);
            var members = new List<CombatPartyMember>(count);
            var ids = new HashSet<(int Server, long Character)>();
            var entities = new HashSet<int>();
            var slots = new HashSet<byte>();
            for (var i = 0; i < count; i++)
            {
                var member = ReadMember(ref r);
                if (!ids.Add((member.BirthServerId, member.CharacterId))
                    || !entities.Add(member.EntityKey) || !slots.Add(member.Slot)) return null;
                members.Add(member);
            }
            var reason = r.U8();
            r.Exact();
            return partyKey > 0 && leader > 0 && limit is >= 1 and <= 5 && count <= limit
                ? new(partyKey, leader, limit, members, reason) : null;
        }
        catch (InvalidDataException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    public static CombatPartyMemberStatus? TryParseMemberStatus(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x921C)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var id = r.I64();
            var classId = r.I32();
            r.Skip(8 + 4 + 12 + 4 + 4 + 4); // Map, position and level fields are not retained.
            r.SkipString();
            r.I32();
            r.Bool();
            var power = r.I64();
            r.U8();
            r.Exact();
            return id > 0 && classId > 0 && power >= 0 ? new(id, classId, power) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CombatPartyMemberVitals? TryParseMemberVitals(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x921B)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            var hp = ReadNonnegativeHp(ref r);
            var maximum = ReadNonnegativeHp(ref r);
            r.Skip(24); // MP, AP and DP current/maximum.
            var alive = r.Bool();
            r.Exact();
            return entity > 0 && hp <= maximum ? new(entity, hp, maximum, alive) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CombatPartyDeparture? TryParseDeparture(ReadOnlySpan<byte> message)
    {
        if (!Matches(message, 0x931F)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var key = r.I64();
            var reason = r.U8();
            r.Exact();
            return key > 0 ? new(key, reason) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static CombatPartyMember ReadMember(ref ProgressPacketReader r)
    {
        var flags = r.U8();
        if (flags is not (0 or 3 or 7)) throw new InvalidDataException("Unconfirmed party member flags.");
        var slot = r.U8();
        var entity = r.I32();
        var birthServer = r.U16();
        var currentServer = r.U16();
        r.SkipString(); // Account identifier is deliberately never decoded or exposed.
        var id = r.I64();
        var name = r.String(128);
        var classId = r.I32();
        var hp = ReadNonnegativeHp(ref r);
        var maximum = ReadNonnegativeHp(ref r);
        r.Skip(24 + 8 + 4 + 12); // Other resources, map and position.
        var alive = r.Bool();
        if ((flags & 1) != 0) r.SkipString();
        if ((flags & 2) != 0) r.I32();
        r.Skip(12);
        r.Bool();
        var loggedIn = r.Bool();
        if ((flags & 4) != 0)
        {
            for (var i = r.Count(6, 256); i > 0; i--)
            {
                var skillFlags = r.U8();
                if ((skillFlags & ~1) != 0) throw new InvalidDataException("Unknown party skill flags.");
                r.I32(); r.U8();
                if ((skillFlags & 1) != 0) r.Vi32();
            }
        }
        var power = r.I64();
        r.U8();
        if (entity <= 0 || birthServer <= 0 || currentServer <= 0 || id <= 0 || classId <= 0
            || hp > maximum || power < 0 || name.Length is < 1 or > 32 || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid party member.");
        return new(slot, entity, birthServer, currentServer, id, name, classId, power, hp, maximum, alive, loggedIn);
    }

    private static long ReadNonnegativeHp(ref ProgressPacketReader r)
    {
        var value = r.V64();
        if (value > long.MaxValue) throw new InvalidDataException("Negative party HP.");
        return (long)value;
    }

    private static bool Matches(ReadOnlySpan<byte> message, ushort opcode) =>
        message.Length is >= 2 and <= MaximumMessageBytes && PetProgressMessage.HasOpcode(message, opcode);
}
