using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.Network;

/// <summary>A pet by its numeric id: English name, genus and the Soulcrest catalog id (null if not in the map data).</summary>
public sealed record PetIdEntry(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("genus")] string Genus,
    [property: JsonPropertyName("catalogId")] string? CatalogId);

/// <summary>
/// The 207 pets with their numeric id (data/pets/pet-ids.json, from aion2.gaming.tools, checked against
/// own captures). Embedded, so the app needs no network to resolve a loot message.
/// </summary>
public static class PetIdTable
{
    private static readonly Lazy<IReadOnlyDictionary<int, PetIdEntry>> Table = new(Load);

    public static IReadOnlyDictionary<int, PetIdEntry> All => Table.Value;

    public static PetIdEntry? Find(int id) => Table.Value.GetValueOrDefault(id);

    private static IReadOnlyDictionary<int, PetIdEntry> Load()
    {
        using var stream = typeof(PetIdTable).Assembly.GetManifestResourceStream("Soulcrest.PetIds.json");
        if (stream is null)
            return new Dictionary<int, PetIdEntry>();
        using var document = JsonDocument.Parse(stream);
        var pets = document.RootElement.GetProperty("pets").Deserialize<List<PetIdEntry>>() ?? [];
        return pets.ToDictionary(p => p.Id);
    }
}
