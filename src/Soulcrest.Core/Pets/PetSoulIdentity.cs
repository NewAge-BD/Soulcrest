using System.Globalization;
using Soulcrest.Core.Network;

namespace Soulcrest.Core.Pets;

/// <summary>User request 2026-10-11: list pets only with a proven, unambiguous Soul identity.</summary>
public static class PetSoulIdentity
{
    public static int? Resolve(PetDefinition pet, PetCatalog catalog)
    {
        if (pet.NumericId is { } number)
            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && PetIdTable.Find(id) is not null ? id : null;
        // Compatibility with older map packages: an exact sourced catalog ID is also proof.
        // A learned display name or a matching portrait alone cannot establish a Soul ID.
        var matches = PetIdTable.All.Values.Where(source => source.CatalogId is { } catalogId
            && catalog.Find(catalogId)?.Id == pet.Id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    public static IReadOnlyList<PetDefinition> Verified(PetCatalog catalog) => catalog.Pets
        .Select(pet => (Pet: pet, SoulId: Resolve(pet, catalog)))
        .Where(row => row.SoulId is not null)
        .GroupBy(row => row.SoulId)
        .Where(group => group.Count() == 1)
        .Select(group => group.Single().Pet).ToArray();
}
