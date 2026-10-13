namespace Soulcrest.Core.Network;

/// <summary>
/// Passive exploration packet decoders. Quest decoding deliberately accepts only the observed
/// variant; status values remain raw. Neither presence nor absence is translated into completion here.
/// </summary>
public static class ExplorationProgressMessage
{
    public static TeleportProgressSnapshot? TryParseTeleports(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x9232)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var count = r.Count(4);
            var ids = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var id = r.I32();
                if (id <= 0 || !ids.Add(id)) return null;
            }
            r.Exact();
            return new(ids);
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Observed own-bound Kibelisk binding add: exactly one positive little-endian teleport ID.</summary>
    public static int? TryParseTeleportBinding(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x9233)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var id = r.I32();
            r.Exact();
            return id > 0 ? id : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static QuestProgressSnapshot? TryParseQuests(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x8D09)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var count = r.Count(22);
            var records = new List<QuestProgressRecord>(count);
            var ids = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var id = r.I32();
                var status = r.U8();
                var stage = r.U8();
                // Status 3 was observed in the same complete bounded layout (2026-10-13).
                // Keep wire values raw; exact catalogue/own/freshness completion policy belongs to the service.
                if (id <= 0 || !ids.Add(id) || status is not (2 or 3 or 4) || stage >= 128) return null;
                var counters = new int[6];
                for (var j = 0; j < counters.Length; j++) counters[j] = r.Vi32();
                var ordinal = r.I64();
                // Could be an empty list count or another optional scalar; reject unseen variants.
                if (r.V32() != 0) return null;
                var rewards = r.Count(14, 1000);
                for (var j = 0; j < rewards; j++)
                {
                    r.I32(); r.Vi32();
                    if (r.U8() != 0) return null; // u8/v32 remain equivalent only while this is zero
                    r.I64();
                }
                records.Add(new(id, status, stage, counters, ordinal));
            }
            r.Exact();
            return new(records);
        }
        catch (InvalidDataException) { return null; }
    }
}
