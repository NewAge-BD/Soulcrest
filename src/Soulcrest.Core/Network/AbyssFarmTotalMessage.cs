namespace Soulcrest.Core.Network;

/// <summary>An absolute cumulative own AP farm total, never an acquisition delta or wallet.</summary>
public sealed record AbyssFarmTotalObservation(long Total);

/// <summary>
/// Complete empirically observed FFA9 envelope. The final eight bytes remain opaque and
/// supply no reset, time, identity or maximum semantics. Own-stream binding is the caller's job.
/// </summary>
public static class AbyssFarmTotalMessage
{
    public static AbyssFarmTotalObservation? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length != 18 || !PetProgressMessage.HasOpcode(message, 0xFFA9)) return null;
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
