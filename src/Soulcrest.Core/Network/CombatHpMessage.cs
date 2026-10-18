using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Soulcrest.Core.Network;

/// <summary>A point update can omit current HP or maximum HP independently. Zero current HP is not a kill proof.</summary>
public sealed record CombatHpObservation(int EntityId, long? Current, long? Maximum);

/// <summary>Reads the observed point-update layout, including both fixed-width tagged lists.</summary>
public static class CombatHpMessage
{
    public const ushort Opcode = 0x8D00; // wire 00 8D

    public static bool TryParse(ReadOnlySpan<byte> message,
        [NotNullWhen(true)] out CombatHpObservation? observation)
    {
        observation = null;
        if (message.Length < 2 || BinaryPrimitives.ReadUInt16LittleEndian(message) != Opcode) return false;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var entity = reader.V32();
            if (entity is 0 or > int.MaxValue) return false;
            var fields = reader.U8();
            if ((fields & ~3) != 0) return false;
            if ((fields & 1) != 0)
            {
                Span<bool> seen = stackalloc bool[256];
                seen.Clear();
                for (var i = reader.Count(5, 256); i > 0; i--)
                {
                    var key = reader.U8();
                    if (seen[key]) return false;
                    seen[key] = true;
                    reader.I32();
                }
            }
            long? current = null, maximum = null;
            if ((fields & 2) != 0)
            {
                Span<bool> seen = stackalloc bool[256];
                seen.Clear();
                for (var i = reader.Count(9, 256); i > 0; i--)
                {
                    var key = reader.U8();
                    if (seen[key]) return false;
                    seen[key] = true;
                    var value = reader.I64();
                    if (key == 0)
                    {
                        if (value < 0) return false;
                        current = value;
                    }
                    else if (key == 7)
                    {
                        if (value <= 0) return false;
                        maximum = value;
                    }
                }
            }
            reader.Exact();
            if (current.HasValue && maximum.HasValue && current.Value > maximum.Value) return false;
            observation = new((int)entity, current, maximum);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
