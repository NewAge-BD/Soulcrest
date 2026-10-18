using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Soulcrest.Core.Network;

/// <summary>
/// A completely consumed Global/EU hit-shaped wire message. Amount is deliberately unclassified:
/// these messages also carry healing. No damage, critical-hit or summon-owner claim follows from it.
/// Data identifiers refer to the local game catalog; entity identifiers are transient source-scoped keys.
/// </summary>
public sealed record CombatHitObservation(int TargetEntityId, int SourceEntityId, uint? SkillDataId,
    uint EffectDataId, long? Amount, bool IsPeriodic, int? RawHitFlags, byte? ResultCode,
    ushort RawPresenceFlags);

/// <summary>Bounded decoder for the observed Global/EU message shapes, with no attribution policy.</summary>
public static class CombatHitMessage
{
    // Opcode constants use the existing Core convention: little-endian value of the two wire bytes.
    public const ushort DirectOpcode = 0x3804; // wire 04 38
    public const ushort PeriodicOpcode = 0x3805; // wire 05 38
    private const ushort DirectSupportedFields = 0x1576;
    private const byte PeriodicSupportedFields = 0x3B;

    public static bool TryParse(ReadOnlySpan<byte> message,
        [NotNullWhen(true)] out CombatHitObservation? observation)
    {
        observation = null;
        if (message.Length < 2) return false;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(message);
        if (opcode is not (DirectOpcode or PeriodicOpcode)) return false;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            observation = opcode == DirectOpcode ? ReadDirect(ref reader) : ReadPeriodic(ref reader);
            reader.Exact();
            return true;
        }
        catch (InvalidDataException)
        {
            observation = null;
            return false;
        }
    }

    private static CombatHitObservation ReadDirect(ref ProgressPacketReader reader)
    {
        var target = Entity(ref reader);
        var fields = reader.U16();
        if ((fields & ~DirectSupportedFields) != 0) throw new InvalidDataException();
        var source = Entity(ref reader);
        var skill = reader.U32();
        reader.U8(); // Meaning of this scalar is not established by the recordings.
        var result = reader.U8();
        int? hitFlags = null;
        if ((fields & 2) != 0)
        {
            var bits = 0;
            for (var i = 0; i < 6; i++) if (reader.Bool()) bits |= 1 << i;
            reader.V64();
            if (reader.Bool()) bits |= 64;
            reader.U8();
            hitFlags = bits;
        }
        var effect = reader.U32();
        reader.U32();
        reader.V32();
        long? amount = (fields & 4) != 0 ? Amount(ref reader) : null;
        if ((fields & 16) != 0) reader.Bool();
        if ((fields & 32) != 0)
            for (var i = reader.Count(); i > 0; i--) reader.V64();
        if ((fields & 64) != 0) reader.Skip(reader.Count(4) * 4);
        if ((fields & 256) != 0) reader.Skip(reader.Count(4) * 4);
        reader.U16();
        if ((fields & 1024) != 0) reader.V64();
        if ((fields & 4096) != 0) reader.Bool();
        return new(target, source, skill, effect, amount, false, hitFlags, result, fields);
    }

    private static CombatHitObservation ReadPeriodic(ref ProgressPacketReader reader)
    {
        var target = Entity(ref reader);
        var fields = reader.U8();
        if ((fields & ~PeriodicSupportedFields) != 0) throw new InvalidDataException();
        var source = Entity(ref reader);
        Entity(ref reader); // A separate transient key; not evidence of a summon owner or skill id.
        var effect = reader.U32();
        if ((fields & 1) != 0) reader.V64();
        long? amount = (fields & 2) != 0 ? Amount(ref reader) : null;
        if ((fields & 8) != 0) reader.U32();
        if ((fields & 16) != 0) Entity(ref reader);
        if ((fields & 32) != 0) reader.U32();
        return new(target, source, null, effect, amount, true, null, null, fields);
    }

    private static int Entity(ref ProgressPacketReader reader)
    {
        var value = reader.V32();
        if (value is 0 or > int.MaxValue) throw new InvalidDataException();
        return (int)value;
    }

    private static long Amount(ref ProgressPacketReader reader)
    {
        var value = reader.V64();
        if (value > long.MaxValue) throw new InvalidDataException();
        return (long)value;
    }
}
