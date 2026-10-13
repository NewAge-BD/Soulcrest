using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>
/// Reads only MyCharAppear (0x3633), validates its whole known schema, and returns the own identity.
/// Other strings and coordinates are skipped without decoding or retaining them. Unknown flag
/// variants fail closed, so a partial name prefix cannot bind subsequent progress to a profile.
/// </summary>
public static class OwnIdentityMessage
{
    /// <summary>
    /// Reads only an untrusted entry candidate from the observed 0x3620 envelope. Its opaque tail
    /// is not schema-validated. This never supplies own authority, a server or a profile context;
    /// a subsequent completely validated own message must independently confirm the candidate.
    /// </summary>
    public static OwnEntryCandidate? TryParseEntryCandidate(ReadOnlySpan<byte> message)
    {
        // These are the complete observed envelope lengths, not a recovered tail schema.
        // The entity varint moves the character field; a fixed offset would misread one variant.
        if (message.Length - 2 is not (1361 or 1362 or 1373 or 2279)
            || !PetProgressMessage.HasOpcode(message, 0x3620)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            if (r.U16() != 0) return null;
            var entityKey = r.Vi32();
            r.Skip(4); // Opaque prefix field; no server or flags semantics are established.
            var characterId = r.I64();
            return entityKey > 0 && characterId > 0 ? new(characterId, entityKey) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Invalidate live identity before any zone-entry snapshots, including same-map entries.</summary>
    public static bool IsWorldEntryPreparation(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x3621)) return false;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            r.Skip(4 + 4 + 8 + 12 + 4 + 1 + 1 + 8);
            r.Skip(r.Count(13) * 13);
            r.Vi32(); r.SkipString(); r.U8();
            r.Exact();
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    public static OwnCharacterIdentity? TryParse(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x3633)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            var flags = r.U32();
            if ((flags & 0xC0000000) != 0) return null;
            var (name, server) = Detail(ref r);
            State(ref r);
            if ((flags & 1) != 0) Guild(ref r);
            Stats(ref r);
            var abnormals = r.Count(23);
            for (var i = 0; i < abnormals; i++) Abnormal(ref r);
            var equips = r.Count(21);
            for (var i = 0; i < equips; i++) Equip(ref r);
            SkipIf(ref r, flags, 2, 1); // appear reason
            r.Skip(12); // map key and map id, not used as player position
            SkipIf(ref r, flags, 4, 4);
            SkipIf(ref r, flags, 8, 4);
            SkipIf(ref r, flags, 16, 12);
            SkipIf(ref r, flags, 32, 2);
            var characterId = r.I64();
            SkipIf(ref r, flags, 64, 4);
            SkipIf(ref r, flags, 128, 4);
            SkipIf(ref r, flags, 256, 1);
            SkipIf(ref r, flags, 512, 1);
            SkipIf(ref r, flags, 1024, 1);
            if ((flags & 2048) != 0) r.Bool();
            if ((flags & 4096) != 0) r.Vi32();
            r.Skip(r.Count(1, 65536)); // own customization bytes
            r.Bool();
            SkipIf(ref r, flags, 8192, 1);
            if ((flags & 16384) != 0) r.Vi32();
            SkipIf(ref r, flags, 32768, 4);
            SkipIf(ref r, flags, 65536, 8);
            if ((flags & 131072) != 0)
            {
                var pins = r.Count(31);
                for (var i = 0; i < pins; i++) MapPin(ref r);
            }
            SkipIf(ref r, flags, 262144, 4);
            SkipIf(ref r, flags, 524288, 4);
            SkipIf(ref r, flags, 1048576, 4);
            SkipIf(ref r, flags, 2097152, 8);
            if ((flags & 4194304) != 0)
            {
                r.Skip(10);
                r.SkipString(); // optional killer name: never decode, store, or expose it
                r.Skip(5);
            }
            SkipIf(ref r, flags, 8388608, 2);
            r.Skip(16); // conqueror level, maximum, experience
            if ((flags & 16777216) != 0) r.Bool();
            SkipIf(ref r, flags, 33554432, 8);
            SkipIf(ref r, flags, 67108864, 4);
            SkipIf(ref r, flags, 134217728, 16);
            r.Skip(16); // combat power and highest combat power
            Points(ref r);
            Points(ref r);
            if ((flags & 268435456) != 0) r.Skip(r.Count(8) * 8);
            if ((flags & 536870912) != 0) r.Skip(r.Count(8) * 8);
            r.Exact();
            return entity > 0 && server > 0 && characterId > 0 && name.Length is >= 2 and <= 32
                && name.All(char.IsLetterOrDigit) ? new(characterId, server, entity, name) : null;
        }
        catch (InvalidDataException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static (string Name, int Server) Detail(ref ProgressPacketReader r)
    {
        var flags = r.U8();
        if ((flags & 0xC0) != 0) throw new InvalidDataException("Unknown own detail flags.");
        var name = (flags & 1) != 0 ? r.String(128) : "";
        var server = r.U16();
        r.I32();
        SkipIf(ref r, flags, 2, 1);
        r.Skip(4 * 4 + 8 + 4 + 8); // levels and experience
        SkipIf(ref r, flags, 4, 1);
        r.Skip(r.Count(14) * 14); // gathering stats
        r.Skip(r.Count(13) * 13); // crafting stats
        r.Skip(16); // cooldowns and reputation
        r.U8();
        SkipIf(ref r, flags, 8, 1);
        if ((flags & 16) != 0) Points(ref r);
        if ((flags & 32) != 0) Points(ref r);
        return (name, server);
    }

    private static void State(ref ProgressPacketReader r)
    {
        var flags = r.U16();
        r.Skip(18); // position, direction and movement direction; deliberately not returned
        SkipIf(ref r, flags, 1, 12);
        SkipIf(ref r, flags, 2, 4);
        SkipIf(ref r, flags, 4, 2);
        SkipIf(ref r, flags, 8, 12);
        SkipIf(ref r, flags, 16, 12);
        SkipIf(ref r, flags, 32, 4);
        r.U8();
        r.V64(); r.V64();
        r.Skip(13 * 4);
        SkipIf(ref r, flags, 64, 1);
        SkipIf(ref r, flags, 128, 4);
        SkipIf(ref r, flags, 256, 1);
        SkipIf(ref r, flags, 512, 1);
        if ((flags & 1024) != 0) r.Vi32();
        SkipIf(ref r, flags, 2048, 4);
        SkipIf(ref r, flags, 4096, 4);
        SkipIf(ref r, flags, 8192, 1);
        SkipIf(ref r, flags, 16384, 4);
        SkipIf(ref r, flags, 32768, 4);
    }

    private static void Guild(ref ProgressPacketReader r)
    {
        r.I64(); r.SkipString(); r.Skip(2 + 1 + 4 + 8 + 2); r.SkipString();
    }

    private static void Abnormal(ref ProgressPacketReader r)
    {
        var flags = r.U8();
        if ((flags & 0xE0) != 0) throw new InvalidDataException("Unknown abnormal flags.");
        r.Vi32(); r.Skip(20);
        if ((flags & 1) != 0) r.Vi32();
        r.U8();
        SkipIf(ref r, flags, 2, 4);
        r.Bool();
        if ((flags & 4) != 0) r.Vi32();
        SkipIf(ref r, flags, 8, 4);
        SkipIf(ref r, flags, 16, 12);
    }

    private static void Equip(ref ProgressPacketReader r)
    {
        r.Skip(14); r.Bool(); r.I32();
        var decorations = r.Count(28);
        for (var i = 0; i < decorations; i++)
        {
            r.Skip(4); r.Bool(); r.Skip(11); r.Bool(); r.Skip(9); r.Bool(); r.Skip(4);
        }
        r.Skip(r.Count(4) * 4);
        r.Bool(); r.Bool(); r.Bool(); r.Vi32();
    }

    private static void MapPin(ref ProgressPacketReader r)
    {
        var flags = r.U8();
        if ((flags & 0xFE) != 0) throw new InvalidDataException("Unknown own map pin flags.");
        r.U8(); r.SkipString(); r.Skip(4 + 12 + 8 + 8);
        if ((flags & 1) != 0) r.Skip(r.Count(8) * 8);
    }

    private static void Stats(ref ProgressPacketReader r) => r.Skip(r.Count(6) * 6);
    private static void Points(ref ProgressPacketReader r) => r.Skip(r.Count(3) * 3);
    private static void SkipIf(ref ProgressPacketReader r, uint flags, uint mask, int length)
    {
        if ((flags & mask) != 0) r.Skip(length);
    }
}
