namespace Soulcrest.Core.Network;

/// <summary>One soul gain of a loot message: pet id (as on aion2.gaming.tools/pets/&lt;id&gt;) and souls.</summary>
public readonly record struct SoulGain(int PetNumber, int Souls);

/// <summary>
/// The server's soul message <c>0d 90 | n | n × (pet id u32 LE, souls u32 LE)</c>, one per pickup group
/// (docs/NETWORK_LOOT.md). Pets on MAX get none.
/// </summary>
public static class LootMessage
{
    public static IReadOnlyList<SoulGain>? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length < 3 || message[0] != 0x0d || message[1] != 0x90)
            return null;
        var count = message[2];
        if (count == 0 || message.Length < 3 + count * 8)
            return null;
        var gains = new List<SoulGain>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = message.Slice(3 + i * 8, 8);
            var pet = entry[0] | (entry[1] << 8) | (entry[2] << 16) | (entry[3] << 24);
            var souls = entry[4] | (entry[5] << 8) | (entry[6] << 16) | (entry[7] << 24);
            // Pet ids are 4-digit, a pickup brings a few souls: anything else is not this message.
            if (pet is < 1000 or > 9999 || souls is < 1 or > 100)
                return null;
            gains.Add(new SoulGain(pet, souls));
        }
        return gains;
    }
}
