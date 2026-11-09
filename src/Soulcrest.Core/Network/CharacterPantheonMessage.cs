using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

/// <summary>Strict readers for the complete own decoration list; no preset or delta format is inferred.</summary>
public static class CharacterPantheonMessage
{
    private const int MaximumMessageBytes = 1 << 20;

    public static CharacterPantheonState? TryParseList(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x8AAF)) return null;
        try
        {
            var reader = new ProgressPacketReader(message[2..]);
            var decorations = ImmutableDictionary.CreateBuilder<int, byte>();
            var count = reader.Count(5);
            for (var index = 0; index < count; index++)
            {
                var id = reader.I32();
                var slot = reader.U8();
                // Unknown positive IDs and future byte slots stay raw observations. Catalogue
                // compatibility and ambiguous placements cannot supply equipped-effect authority.
                if (id <= 0 || !decorations.TryAdd(id, slot)) return null;
            }
            reader.Exact();
            return new(decorations.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }
}
