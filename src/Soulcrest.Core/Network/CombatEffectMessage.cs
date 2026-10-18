namespace Soulcrest.Core.Network;

/// <summary>Wire operations only: neither addition nor change proves an actual skill cast.</summary>
public enum CombatEffectOperation { Add, Change, Remove }

/// <summary>
/// Effect instance data. Uid belongs to this target and source generation, not a global buff ID.
/// MultiplicationValue has no confirmed stack semantics. Negative timing sentinels stay raw.
/// </summary>
public sealed record CombatEffectEntry(int Uid, int EffectDataId, long DurationMilliseconds,
    long ExpireTimeMilliseconds, int? SourceEntityKey, byte Level, int? SkillId, bool ReadyToRemove,
    int? MultiplicationSourceEntityKey, int? MultiplicationValue);

public sealed record CombatEffectRemoval(int Uid, byte Reason, int? DispelSourceEntityKey,
    int? DispelSkillId, int? DispelEffectId);

/// <summary>
/// A completely consumed wire message. Restrict targets/sources to the confirmed own group or boss
/// before retaining it. This does not prove a complete initial buff state, uptime, or buff/debuff kind.
/// </summary>
public sealed record CombatEffectUpdate(int TargetEntityKey, CombatEffectOperation Operation,
    IReadOnlyList<CombatEffectEntry> Entries, IReadOnlyList<CombatEffectRemoval> Removals);

public static class CombatEffectMessage
{
    public static CombatEffectUpdate? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length is < 2 or > (1 << 20)) return null;
        CombatEffectOperation operation;
        if (PetProgressMessage.HasOpcode(message, 0x382A)) operation = CombatEffectOperation.Add;
        else if (PetProgressMessage.HasOpcode(message, 0x382B)) operation = CombatEffectOperation.Change;
        else if (PetProgressMessage.HasOpcode(message, 0x382C)) operation = CombatEffectOperation.Remove;
        else return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var target = r.Vi32();
            if (target <= 0) return null;
            if (operation == CombatEffectOperation.Remove)
            {
                var count = r.Count(3, 4096);
                var removals = new List<CombatEffectRemoval>(count);
                var uids = new HashSet<int>();
                for (var i = 0; i < count; i++)
                {
                    var flags = r.U8();
                    if (flags is not (0 or 7)) return null;
                    var uid = r.Vi32();
                    var reason = r.U8();
                    int? caster = (flags & 1) != 0 ? r.Vi32() : null;
                    int? skill = (flags & 2) != 0 ? r.I32() : null;
                    int? effect = (flags & 4) != 0 ? r.I32() : null;
                    if (uid < 0 || !uids.Add(uid)) return null;
                    removals.Add(new(uid, reason, caster, skill, effect));
                }
                r.Exact();
                return new(target, operation, [], removals);
            }
            var entryCount = operation == CombatEffectOperation.Add ? r.Count(23, 4096) : 1;
            var entries = new List<CombatEffectEntry>(entryCount);
            var entryUids = new HashSet<int>();
            for (var i = 0; i < entryCount; i++)
            {
                var flags = r.U8();
                // Only these complete Global/EU forms were independently observed. In particular,
                // multiplication fields have not been established by these captures.
                if (flags is not (0x11 or 0x13)) return null;
                var uid = r.Vi32();
                var effectId = r.I32();
                var duration = r.I64();
                var expiry = r.I64();
                int? caster = (flags & 1) != 0 ? r.Vi32() : null;
                var level = r.U8();
                int? skill = (flags & 2) != 0 ? r.I32() : null;
                var ready = r.Bool();
                int? multiplicationCaster = (flags & 4) != 0 ? r.Vi32() : null;
                int? multiplication = (flags & 8) != 0 ? r.I32() : null;
                if ((flags & 16) != 0) r.Skip(12); // Range-base coordinates are not exposed or retained.
                if (uid < 0 || effectId <= 0 || !entryUids.Add(uid)) return null;
                entries.Add(new(uid, effectId, duration, expiry, caster, level, skill, ready,
                    multiplicationCaster, multiplication));
            }
            if (operation == CombatEffectOperation.Change) r.Bool(); // Show-message bit shares the boolean stream.
            r.Exact();
            return new(target, operation, entries, []);
        }
        catch (InvalidDataException) { return null; }
    }
}
