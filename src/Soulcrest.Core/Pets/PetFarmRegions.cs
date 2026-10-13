using System.Collections.ObjectModel;
using System.Text.Json;

namespace Soulcrest.Core.Pets;

/// <summary>Public farm-source joins for the merchant's user-defined map labels, available offline.</summary>
public static class PetFarmRegions
{
    private static readonly string[] Order = ["Abyss", "Altgard", "Verteron", "Special"];
    private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<string>>> Table = new(Load);
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> All => Table.Value;
    public static IReadOnlyList<string>? Find(int petNumber) => Table.Value.GetValueOrDefault(petNumber);

    private static IReadOnlyDictionary<int, IReadOnlyList<string>> Load()
    {
        using var stream = typeof(PetFarmRegions).Assembly.GetManifestResourceStream("Soulcrest.PetFarmRegions.json");
        if (stream is null) throw new InvalidDataException("Missing embedded pet farm regions.");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported embedded pet farm regions.");
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var seen = new HashSet<int>();
        foreach (var pet in document.RootElement.GetProperty("pets").EnumerateArray())
        {
            var number = pet.GetProperty("petNumber").GetInt32();
            var regions = pet.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (number <= 0 || !seen.Add(number) || regions.Any(region => !Order.Contains(region)) ||
                regions.Distinct().Count() != regions.Length || regions.Contains("Special") && regions.Length != 1)
                throw new InvalidDataException("Invalid embedded pet farm regions.");
            if (!pet.GetProperty("known").GetBoolean() || regions.Length == 0) continue;
            result.Add(number, Array.AsReadOnly(Order.Where(regions.Contains).ToArray()));
        }
        return new ReadOnlyDictionary<int, IReadOnlyList<string>>(result);
    }
}
