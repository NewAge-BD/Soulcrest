using System.Collections.Immutable;

namespace Soulcrest.Core.Network;

/// <summary>A complete own collection. Enhancement levels are retained only as internal observations.</summary>
public sealed record CharacterWingOwnership(ImmutableDictionary<int, byte> Wings);

/// <summary>Active stat wing and cosmetic skin are independent. Null means unobserved; zero means explicitly empty.</summary>
public sealed record CharacterWingEquipment(int? WingId, byte? EnchantLevel, int? SkinId);

/// <summary>Entity-scoped active-wing or cosmetic change; decoding alone supplies no own authority.</summary>
public sealed record CharacterWingChange(int EntityKey, bool IsWingSkin, int WingId);

/// <summary>Strict bounded readers over the passive own stream. No acquisition or enhancement opcode is inferred.</summary>
public static class CharacterWingMessage
{
    private const int MaximumMessageBytes = 1 << 20;

    public static CharacterWingOwnership? TryParseList(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x5672)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var wings = ImmutableDictionary.CreateBuilder<int, byte>();
            var count = r.Count(5);
            for (var index = 0; index < count; index++)
            {
                var id = r.I32(); var level = r.U8();
                if (id <= 0 || !wings.TryAdd(id, level)) return null;
            }
            r.Exact();
            return new(wings.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>A prefix can exclude a foreign entity but never qualify a complete update.</summary>
    public static int? TryReadEntityKeyPrefix(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x5676)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32();
            return entity > 0 ? entity : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterWingChange? TryParseChange(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x5676)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var entity = r.Vi32(); var skin = r.Bool(); var id = r.I32();
            r.Exact();
            return entity > 0 && id >= 0 ? new(entity, skin, id) : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
