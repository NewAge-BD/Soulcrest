using System.Text.Json;

namespace Soulcrest.Core.Pets;

/// <summary>
/// Real names for catalog pets that only carry an icon name ("Crestlich 01"): the loot feed shows
/// "Soul: Crestlich (Bound)", which never matched (user report 2026-10-03). Sourced entries ship in
/// data/pets/name-overrides.json; names learned from the in-game pet window are added at runtime.
/// </summary>
public static class PetNameOverrides
{
    /// <summary>The sourced overrides embedded from data/pets/name-overrides.json (id -> English name).</summary>
    public static IReadOnlyDictionary<string, string> Shipped { get; } = LoadShipped();

    /// <summary>A pet named after its icon ("Crestlich 01", "KrallWar 01 V01"): the real name is unknown.</summary>
    public static bool IsPlaceholder(PetDefinition pet) =>
        pet.IconName is { Length: > 0 } icon && string.Equals(pet.En, icon, StringComparison.Ordinal);

    public static IEnumerable<PetDefinition> Apply(IEnumerable<PetDefinition> pets, IReadOnlyDictionary<string, string> names) =>
        pets.Select(p => names.TryGetValue(p.Id, out var name) && !string.IsNullOrWhiteSpace(name) ? p with { En = name } : p);

    private static Dictionary<string, string> LoadShipped()
    {
        using var stream = typeof(PetNameOverrides).Assembly.GetManifestResourceStream("Soulcrest.PetNameOverrides.json");
        if (stream is null)
            return [];
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("overrides").EnumerateArray()
            .Where(e => e.TryGetProperty("sourceRefs", out var refs) && refs.GetArrayLength() > 0)
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("en").GetString()!, StringComparer.Ordinal);
    }
}
