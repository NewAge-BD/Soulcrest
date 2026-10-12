namespace Soulcrest.Core.Network;

/// <summary>One soul gain of a loot message: pet id (as on aion2.gaming.tools/pets/&lt;id&gt;) and souls.</summary>
public readonly record struct SoulGain(int PetNumber, int Souls);

/// <summary>
/// The server's soul message <c>0d 90 | n:v32 | n × (pet id i32 LE, souls i32 LE)</c>, one per pickup group
/// (docs/NETWORK_LOOT.md). Pets on MAX get none.
/// </summary>
public static class LootMessage
{
    public static IReadOnlyList<SoulGain>? TryParse(ReadOnlySpan<byte> message) => PetProgressMessage.ParsePoints(message, 0x900D);
}
