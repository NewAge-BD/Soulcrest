namespace Soulcrest.Core.Network;

/// <summary>A positive crystal gain with its raw native type; it contains no pet or entity identity.</summary>
public readonly record struct SoulCrystalGain(byte Type, int Amount);

/// <summary>
/// Complete observed item-origin crystal gains. Types 1..5 are raw native crystal categories,
/// distinct from creature-type numbering. Only the observed reason 2 is qualified; this reader
/// does not accept crystallization or establish ownership, a pet mapping, or double-count policy.
/// </summary>
public static class SoulCrystalMessage
{
    public static IReadOnlyList<SoulCrystalGain>? TryParseGains(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x9011)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var count = reader.Count(5, 5);
            if (count == 0) return null;
            var gains = new List<SoulCrystalGain>(count);
            var seen = 0;
            for (var index = 0; index < count; index++)
            {
                var type = reader.U8();
                var amount = reader.I32();
                if (type is < 1 or > 5 || amount <= 0 || (seen & (1 << type)) != 0) return null;
                seen |= 1 << type;
                gains.Add(new(type, amount));
            }
            if (reader.U8() != 2) return null;
            reader.Exact();
            return gains.AsReadOnly();
        }
        catch (InvalidDataException) { return null; }
    }
}
