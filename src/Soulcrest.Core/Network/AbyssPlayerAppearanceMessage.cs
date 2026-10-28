using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>
/// One completely validated PC appearance. Native entity/server keys and names are transient RAM
/// data, never diagnostics or journal fields. Class and ordinal grade are independent metadata;
/// an unknown value does not turn a known PC into an unknown actor type.
/// </summary>
public sealed record AbyssPlayerAppearanceObservation(int EntityKey, ushort ServerId, string Name)
{
    public string? ClassId { get; init; }
    public int? AbyssRankGrade { get; init; }
    public override string ToString() => nameof(AbyssPlayerAppearanceObservation);
}

/// <summary>
/// Complete current CharAppear PC form (0x3645), using the same State/Guild/Abnormal/Equip/Stats
/// subforms as the own identity decoder. No prefix fallback or alternate equipment-layout probe.
/// This establishes a PC identity, not life state, damage participation, or a kill/assist credit.
/// </summary>
public static class AbyssPlayerAppearanceMessage
{
    // A direct-call resource bound, matching the bounded transport framer; not a wire length field.
    private const int MaximumMessageBytes = 1 << 20;

    public static AbyssPlayerAppearanceObservation? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x3645)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var entity = reader.Vi32();
            var flags = reader.U32();
            if ((flags & 0xF8000000) != 0 || (flags & 1) == 0) return null;
            var (name, classId) = Descriptor(ref reader);
            OwnIdentityMessage.State(ref reader); // _live/HP and movement stay uninterpreted.
            var server = reader.U16();
            if ((flags & 2) != 0) OwnIdentityMessage.Guild(ref reader);
            var abnormals = reader.Count(23);
            for (var i = 0; i < abnormals; i++) OwnIdentityMessage.Abnormal(ref reader);
            var equipment = reader.Count(21);
            for (var i = 0; i < equipment; i++) OwnIdentityMessage.Equip(ref reader);

            Movement(ref reader, flags);
            reader.I64(); // Native database identifier; no stable-identity/life meaning claimed.
            OwnIdentityMessage.Stats(ref reader);
            reader.Skip(12); // Required opaque scalars, not AP/rank totals.
            Skip(ref reader, flags, 0x8000, 4);
            Skip(ref reader, flags, 0x10000, 4);
            Skip(ref reader, flags, 0x20000, 4);
            reader.Skip(reader.Count(1, 65536)); // Customization bytes.
            Skip(ref reader, flags, 0x40000, 1);
            Skip(ref reader, flags, 0x80000, 4);
            if ((flags & 0x100000) != 0) OpaqueOptionalStructure(ref reader);
            Skip(ref reader, flags, 0x200000, 4);
            Skip(ref reader, flags, 0x400000, 4);
            Skip(ref reader, flags, 0x800000, 4);
            var grade = (flags & 0x1000000) != 0 ? reader.I32() : (int?)null;
            Bool(ref reader, flags, 0x2000000);
            Skip(ref reader, flags, 0x4000000, 4);
            var opaqueTotal = reader.I64();
            reader.U8();
            reader.Bool();
            reader.Exact();

            return entity > 0 && server > 0 && opaqueTotal >= 0
                && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
                ? new(entity, server, name) { ClassId = classId, AbyssRankGrade = grade is >= 1 and <= 19 ? grade : null }
                : null;
        }
        catch (InvalidDataException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static (string Name, string? ClassId) Descriptor(ref ProgressPacketReader reader)
    {
        var flags = reader.U8();
        if ((flags & 0xE0) != 0 || (flags & 1) == 0)
            throw new InvalidDataException("Unsupported PC descriptor.");
        var name = reader.String(128);
        var classId = OwnIdentityMessage.ClassIdForPcId(reader.I32());
        Skip(ref reader, flags, 2, 1);
        Skip(ref reader, flags, 4, 1);
        Skip(ref reader, flags, 8, 5);
        Skip(ref reader, flags, 16, 1); // Current opaque descriptor extension.
        return (name, classId);
    }

    private static void Movement(ref ProgressPacketReader reader, uint flags)
    {
        Skip(ref reader, flags, 0x4, 1);
        Skip(ref reader, flags, 0x8, 1);
        Bool(ref reader, flags, 0x10);
        Bool(ref reader, flags, 0x20);
        Skip(ref reader, flags, 0x40, 1);
        Skip(ref reader, flags, 0x80, 12);
        Skip(ref reader, flags, 0x100, 8);
        Skip(ref reader, flags, 0x200, 2);
        Skip(ref reader, flags, 0x400, 2);
        Skip(ref reader, flags, 0x800, 8);
        Bool(ref reader, flags, 0x1000);
        Skip(ref reader, flags, 0x2000, 1);
        Bool(ref reader, flags, 0x4000);
    }

    private static void OpaqueOptionalStructure(ref ProgressPacketReader reader)
    {
        var flags = reader.U8();
        if ((flags & 0xF0) != 0) throw new InvalidDataException("Unsupported PC appearance extension.");
        reader.Skip(7);
        reader.Bool();
        reader.U32();
        Skip(ref reader, flags, 1, 8);
        Skip(ref reader, flags, 2, 4);
        Skip(ref reader, flags, 4, 8);
        Skip(ref reader, flags, 8, 4);
    }

    private static void Skip(ref ProgressPacketReader reader, uint flags, uint mask, int count)
    { if ((flags & mask) != 0) reader.Skip(count); }

    private static void Bool(ref ProgressPacketReader reader, uint flags, uint mask)
    { if ((flags & mask) != 0) reader.Bool(); }
}
