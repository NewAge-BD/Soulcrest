namespace Soulcrest.Core.Network;

/// <summary>An absolute Abyss rank total. Parsing supplies no own identity and never supplies a delta.</summary>
public sealed record AbyssRankPointUpdate(long Total);

/// <summary>
/// Complete observed E273 notification: supported type zero followed by one signed I64 LE total.
/// A caller must independently establish current own authority on the same uninterrupted stream.
/// This decoder alone does not authorize a live producer or arithmetic gain events.
/// </summary>
public static class RankPointUpdateMessage
{
    public static AbyssRankPointUpdate? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length != 11 || !PetProgressMessage.HasOpcode(message, 0xE273)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            if (reader.U8() != 0) return null;
            var total = reader.I64();
            reader.Exact();
            return total >= 0 ? new(total) : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
