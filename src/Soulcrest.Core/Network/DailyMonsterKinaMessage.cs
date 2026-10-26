namespace Soulcrest.Core.Network;

/// <summary>An observed absolute daily monster-Kina total, never a wallet balance or an income delta.</summary>
public sealed record DailyMonsterKinaObservation(long Total);

/// <summary>
/// The complete observed initial/update envelopes. The final eight bytes stay opaque: they do
/// not establish a reset time, timezone or maximum. Own-entry binding belongs to the caller.
/// </summary>
public static class DailyMonsterKinaMessage
{
    public static DailyMonsterKinaObservation? TryParseInitial(ReadOnlySpan<byte> message) => Parse(message, 0xFFA5);

    public static DailyMonsterKinaObservation? TryParseUpdate(ReadOnlySpan<byte> message) => Parse(message, 0xFFA6);

    private static DailyMonsterKinaObservation? Parse(ReadOnlySpan<byte> message, ushort opcode)
    {
        if (message.Length != 18 || !PetProgressMessage.HasOpcode(message, opcode)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var total = reader.I64();
            reader.Skip(8);
            reader.Exact();
            return total >= 0 ? new(total) : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
