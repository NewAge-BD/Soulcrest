using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

/// <summary>Successful slot-lock reply observed on the current own stream. Native slot numbering is retained.</summary>
public sealed record CharacterGenusSlotLock(byte CreatureType, byte Panel, byte Slot, bool IsLocked);

/// <summary>Only the explicitly returned slots changed; other slots and the equipped panel remain independent.</summary>
public sealed record CharacterGenusRoll(byte CreatureType, byte Panel, ImmutableArray<CharacterGenusEffect> Slots,
    int Level, long Experience);

/// <summary>Only independently observed current reply variants are decoded here.</summary>
public static class CharacterGenusMessage
{
    public static CharacterGenusRoll? TryParseRoll(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x9018)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            if (r.U16() != 0) return null;
            var type = r.U8(); var panel = r.U8();
            if (type is < 2 or > 6 || panel is < 1 or > 3) return null;
            var count = r.Count(12, 9);
            if (count == 0) return null;
            var slots = ImmutableArray.CreateBuilder<CharacterGenusEffect>(count);
            var indices = new HashSet<byte>();
            for (var i = 0; i < count; i++)
            {
                var slot = r.U8(); var grade = r.U8(); var stat = r.U16(); var value = r.I64();
                var locked = r.Bool(); var activated = r.Bool();
                if (slot > 8 || !indices.Add(slot) || grade is < 1 or > 5) return null;
                slots.Add(new(slot, grade, stat, value, locked, activated));
            }
            var boardType = r.U8(); var level = r.I32(); var experience = r.I64();
            r.Exact();
            return boardType == type && level is >= 1 and <= 10 && experience >= 0
                ? new(type, panel, slots.MoveToImmutable(), level, experience) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterGenusSlotLock? TryParseSlotLock(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x901A) || message.Length != 8) return null;
        var r = new ProgressPacketReader(message[2..]);
        if (r.U16() != 0) return null;
        var type = r.U8(); var panel = r.U8(); var slot = r.U8(); var locked = r.U8();
        // The successful six-byte payload was captured for lock-on and lock-off. Extra
        // flag bits and unknown board/panel/slot variants must not become current facts.
        return type is >= 2 and <= 6 && panel is >= 1 and <= 3 && slot <= 8 && locked <= 1
            ? new(type, panel, slot, locked == 1) : null;
    }
}
