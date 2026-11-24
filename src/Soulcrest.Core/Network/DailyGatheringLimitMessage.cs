using System.Buffers.Binary;

namespace Soulcrest.Core.Network;

/// <summary>Absolute gathering usage from the initial and update envelopes.</summary>
public static class DailyGatheringLimitMessage
{
    public const int Limit = 1_000;
    public const ushort InitialOpcode = 0xFFA7;
    public const ushort UpdateOpcode = 0xFFA8;

    public static int? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length != 18 || BinaryPrimitives.ReadUInt16LittleEndian(message) is not (InitialOpcode or UpdateOpcode))
            return null;

        // The last eight bytes are opaque. The observed total fits the supported daily range.
        var count = BinaryPrimitives.ReadInt64LittleEndian(message[2..]);
        return count is >= 0 and <= Limit ? (int)count : null;
    }
}
