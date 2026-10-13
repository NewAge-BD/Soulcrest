using System.Text.Json;

namespace Soulcrest.Core.Network;

/// <summary>Explicit public item-to-pet joins, embedded for offline merchant counters.</summary>
public static class MerchantPetItems
{
    private static readonly Lazy<IReadOnlyDictionary<int, int>> Table = new(Load);
    public static IReadOnlyDictionary<int, int> All => Table.Value;
    public static int? Find(int itemId) => Table.Value.TryGetValue(itemId, out var petNumber) ? petNumber : null;

    private static IReadOnlyDictionary<int, int> Load()
    {
        using var stream = typeof(MerchantPetItems).Assembly.GetManifestResourceStream("Soulcrest.MerchantPetItems.json");
        if (stream is null) return new Dictionary<int, int>();
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) return new Dictionary<int, int>();
        var result = new Dictionary<int, int>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var itemId = item.GetProperty("itemId").GetInt32();
            var pet = item.GetProperty("petNumber").GetInt32();
            if (itemId <= 0 || pet <= 0 || !result.TryAdd(itemId, pet))
                throw new InvalidDataException("Invalid embedded merchant item mapping.");
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(result);
    }
}
