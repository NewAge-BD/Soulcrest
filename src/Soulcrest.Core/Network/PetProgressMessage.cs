namespace Soulcrest.Core.Network;

/// <summary>Passive decoding of complete ride-init, acquired, and spent packets. No game code is loaded.</summary>
public static class PetProgressMessage
{
    public static PetProgressSnapshot? TryParseSnapshot(ReadOnlySpan<byte> message)
    {
        if (!HasOpcode(message, 0x9000)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            r.I32(); // default ride; it is not an acquired-level map key
            var levels = new Dictionary<int, int>();
            var mapKeys = new HashSet<int>();
            var acquired = r.Count(12);
            for (var i = 0; i < acquired; i++)
            {
                if (!mapKeys.Add(r.I32())) return null;
                var pet = r.I32();
                var level = r.I32();
                if (pet <= 0 || level is < 0 or > 3 || !levels.TryAdd(pet, level)) return null;
            }
            var balances = new Dictionary<int, int>();
            var souls = r.Count(8);
            for (var i = 0; i < souls; i++)
            {
                var pet = r.I32();
                var balance = r.I32();
                if (pet <= 0 || balance < 0 || !balances.TryAdd(pet, balance)) return null;
            }
            var crystals = r.Count(5);
            var crystalKeys = new HashSet<byte>();
            for (var i = 0; i < crystals; i++)
            {
                if (!crystalKeys.Add(r.U8()) || r.I32() < 0) return null;
            }
            r.Skip(r.Count(4) * 4); // acquired collections, distinct from individual levels
            var boards = r.Count(15, 256);
            var boardKeys = new HashSet<byte>();
            for (var i = 0; i < boards; i++)
            {
                if (!boardKeys.Add(r.U8())) return null;
                r.U8(); r.I32(); r.I64();
                var panels = r.Count(2, 256);
                var panelKeys = new HashSet<byte>();
                for (var j = 0; j < panels; j++)
                {
                    if (!panelKeys.Add(r.U8())) return null;
                    var slots = r.Count(12, 256);
                    for (var k = 0; k < slots; k++)
                    {
                        r.U8(); r.U8(); r.U16(); r.I64();
                        r.Bool(); r.Bool();
                    }
                }
            }
            r.U8(); // selected panel
            r.Exact();
            return new(levels, balances);
        }
        catch (InvalidDataException) { return null; }
    }

    public static PetAcquisition? TryParseAcquired(ReadOnlySpan<byte> message)
    {
        if (!HasOpcode(message, 0x9009)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            var pet = r.I32();
            var level = r.I32();
            r.U8(); r.I32(); r.I64(); // creature-board experience follows the acquisition
            r.Exact();
            return pet > 0 && level is >= 1 and <= 3 ? new(entity, pet, level) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Reads the independently captured 0x9014 result. Its unused 13-byte tail remains opaque.</summary>
    public static PetUpgradeResult? TryParseUpgrade(ReadOnlySpan<byte> message)
    {
        if (!HasOpcode(message, 0x9014)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var result = r.U16();
            var pet = r.I32();
            var level = r.I32();
            r.Skip(13);
            r.Exact();
            if (result == 0 && (pet <= 0 || level is < 1 or > 3)) return null;
            return new(result, pet, level);
        }
        catch (InvalidDataException) { return null; }
    }

    public static IReadOnlyList<SoulGain>? TryParseSpent(ReadOnlySpan<byte> message) => ParsePoints(message, 0x900E);

    internal static IReadOnlyList<SoulGain>? ParsePoints(ReadOnlySpan<byte> message, ushort opcode)
    {
        if (!HasOpcode(message, opcode)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var count = r.Count(8);
            var result = new List<SoulGain>(count);
            for (var i = 0; i < count; i++)
            {
                var pet = r.I32();
                var points = r.I32();
                if (pet <= 0 || points <= 0) return null;
                result.Add(new(pet, points));
            }
            r.Exact();
            return result;
        }
        catch (InvalidDataException) { return null; }
    }

    internal static bool HasOpcode(ReadOnlySpan<byte> message, ushort opcode) =>
        message.Length >= 2 && message[0] == (byte)(opcode & 255) && message[1] == (byte)(opcode >> 8);
}
