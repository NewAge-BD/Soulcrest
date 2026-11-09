using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

/// <summary>Explicit wire components. An absent component block remains unknown, never five zeroes.</summary>
public sealed record OwnSkillLevelBonuses(int Daevanion, int Abnormal, int Equipment, int Arcana, int Fixed);

/// <summary>Observed total and original skill levels; OriginalLevel is not total minus Daevanion alone.</summary>
public sealed record OwnSkillLevel(int SkillId, int TotalLevel, int OriginalLevel, OwnSkillLevelBonuses? AdditionalLevels);

/// <summary>A completely consumed own skill list. Missing IDs do not imply learned level zero.</summary>
public sealed record OwnSkillList(ImmutableDictionary<int, OwnSkillLevel> Skills);

/// <summary>Explicit absolute changes, usable only with a previously confirmed complete own list.</summary>
public sealed record OwnSkillChanges(ImmutableDictionary<int, OwnSkillLevel> Skills);

/// <summary>
/// Bounded independent reader for MySkillList (5100), SkillAdd (380F), and SkillLevelUpdate (3813).
/// The schema and original/total/component distinction are supported by existing offline own captures.
/// No actor identity is present: the caller must supply the current own transport or a matched login.
/// </summary>
public static class OwnSkillMessage
{
    public static OwnSkillList? TryParseList(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x5100)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var count = r.Count(11, 4096);
            var values = ImmutableDictionary.CreateBuilder<int, OwnSkillLevel>();
            for (var i = 0; i < count; i++)
            {
                var flags = r.U8();
                if ((flags & 0xF0) != 0) return null;
                var skill = ReadLevel(ref r, flags);
                if (!values.TryAdd(skill.SkillId, skill)) return null;
                if ((flags & 2) != 0) r.Vi32(); // cooldown, not retained
                r.I32(); // automatic-use cooldown, not retained
                if ((flags & 4) != 0) r.U8();
                if ((flags & 8) != 0) r.Vi32();
            }
            r.Exact();
            return new(values.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    public static OwnSkillChanges? TryParseChanges(ReadOnlySpan<byte> message)
    {
        var add = PetProgressMessage.HasOpcode(message, 0x380F);
        if (!add && !PetProgressMessage.HasOpcode(message, 0x3813)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var count = add ? 1 : r.Count(11, 4096);
            var values = ImmutableDictionary.CreateBuilder<int, OwnSkillLevel>();
            var storageKeys = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                // Map keys are opaque storage keys, never substituted for the explicit skill ID.
                if (!add && !storageKeys.Add(r.I32())) return null;
                var flags = r.U8();
                if ((flags & 0xFE) != 0) return null;
                var skill = ReadLevel(ref r, flags);
                if (!values.TryAdd(skill.SkillId, skill)) return null;
            }
            r.U8(); // reason has no qualified enum semantics here
            r.Exact();
            return new(values.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    private static OwnSkillLevel ReadLevel(ref ProgressPacketReader r, byte flags)
    {
        var id = r.I32();
        var total = r.U8();
        var original = r.U8();
        OwnSkillLevelBonuses? bonus = (flags & 1) == 0 ? null
            : new(r.U8(), r.U8(), r.U8(), r.U8(), r.U8());
        if (id <= 0 || original > total || bonus is { } b
            && total != original + b.Daevanion + b.Abnormal + b.Equipment + b.Arcana + b.Fixed)
            throw new InvalidDataException("Inconsistent own skill level.");
        return new(id, total, original, bonus);
    }
}
