namespace Soulcrest.Core.Network;

/// <summary>Explicit native resource maxima, distinct from raw character-stat components. Missing is unknown.</summary>
public sealed record CharacterResourceMaximums(long? MaxHp, long? MaxMp);

/// <summary>No own authority is supplied by this message. Bind EntityKey to the current own transport and generation.</summary>
public sealed record CharacterResourceUpdate(int EntityKey, CharacterResourceMaximums Maximums);

/// <summary>Bounded reader for the qualified CharPointUpdate (0x8D00) map variant. Current resources are not retained.</summary>
public static class CharacterResourceMessage
{
    /// <summary>
    /// Reads only the canonical positive entity prefix of a bounded 0x8D00 message for filtering.
    /// This does not validate the body or supply a complete observation or any own authority.
    /// A missing, malformed or truncated body may still have a readable prefix.
    /// </summary>
    public static int? TryReadEntityKeyPrefix(ReadOnlySpan<byte> message)
    {
        if (message.Length > 1 << 20 || !PetProgressMessage.HasOpcode(message, 0x8D00)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            return entity > 0 ? entity : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterResourceUpdate? TryParseUpdate(ReadOnlySpan<byte> message)
    {
        if (message.Length > 1 << 20 || !PetProgressMessage.HasOpcode(message, 0x8D00)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            var flags = r.U8();
            if (entity <= 0 || (flags & ~3) != 0) return null;
            var seen = new HashSet<byte>();
            long? hp = null, mp = null;
            for (var width = 0; width < 2; width++)
            {
                if ((flags & (1 << width)) == 0) continue;
                var count = r.Count(width == 0 ? 5 : 9, 256);
                for (var i = 0; i < count; i++)
                {
                    var key = r.U8();
                    var value = width == 0 ? r.I32() : r.I64();
                    if (!seen.Add(key)) return null;
                    // Controlled HP/MP node tests qualify these native resource-map keys.
                    // All other keys are consumed without exposing current HP/MP or guessing their meaning.
                    if (key is not (7 or 8)) continue;
                    if (value < 0) return null;
                    if (key == 7) hp = value;
                    else mp = value;
                }
            }
            r.Exact();
            return new(entity, new(hp, mp));
        }
        catch (InvalidDataException) { return null; }
    }
}
