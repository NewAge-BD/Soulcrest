using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.Pets;

public sealed record PetDefinition
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("en")] public required string En { get; init; }
    [JsonPropertyName("de")] public string? De { get; init; }
    [JsonPropertyName("genus")] public required string Genus { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("iconName")] public string? IconName { get; init; }
    [JsonPropertyName("numericId")] public string? NumericId { get; init; }
    [JsonPropertyName("legacyDuplicate")] public bool LegacyDuplicate { get; init; }
    [JsonPropertyName("spawns")] public Dictionary<string, int> Spawns { get; init; } = [];
    [JsonPropertyName("monsters")] public Dictionary<string, int> Monsters { get; init; } = [];

    public string DisplayName(string language) =>
        language == "de" && !string.IsNullOrWhiteSpace(De) ? De! : En;

    /// <summary>Maps where the pet can be found: its own spawns or the monsters that drop its soul.</summary>
    public IEnumerable<string> Maps =>
        Spawns.Where(s => s.Value > 0).Select(s => s.Key).Union(Monsters.Where(m => m.Value > 0).Select(m => m.Key));

    /// <summary>Only on this map (map-exclusive pet).</summary>
    public bool IsExclusiveTo(string mapId) => Maps.All(m => m == mapId) && Maps.Any();
}

public sealed class PetCatalog
{
    private readonly Dictionary<string, PetDefinition> _byId;

    public PetCatalog(IEnumerable<PetDefinition> pets, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var input = pets.ToList();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        // User report 2026-10-11: old map IDs appeared as separate pets and made identical
        // portraits/names ambiguous. Only the sourced numeric ID plus an explicit legacy flag
        // proves an alias; shared names or shared icons alone never merge different pets.
        foreach (var group in input.Where(p => !string.IsNullOrWhiteSpace(p.NumericId)).GroupBy(p => p.NumericId))
        {
            var primary = group.Where(p => !p.LegacyDuplicate).DistinctBy(p => p.Id).ToArray();
            if (primary.Length != 1) continue;
            foreach (var legacy in group.Where(p => p.LegacyDuplicate && p.Id != primary[0].Id))
                resolved[legacy.Id] = primary[0].Id;
        }
        if (aliases is not null)
            foreach (var (alias, target) in aliases)
                if (alias != target && !resolved.ContainsKey(target) && !aliases.ContainsKey(target)
                    && input.Any(p => p.Id == target)) resolved[alias] = target;
        Pets = input.Where(p => !resolved.ContainsKey(p.Id)).OrderBy(p => p.Genus, StringComparer.Ordinal)
            .ThenBy(p => p.En, StringComparer.OrdinalIgnoreCase).ToList();
        _byId = Pets.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (alias, target) in resolved)
            _byId[alias] = _byId[target];
        Aliases = resolved;
    }

    public IReadOnlyList<PetDefinition> Pets { get; }

    /// <summary>Compatibility IDs resolve to a single displayed and recognised pet.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; }

    public PetDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public static PetCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        var pets = JsonSerializer.Deserialize<List<PetDefinition>>(stream) ?? [];
        return new PetCatalog(pets);
    }

    public static PetCatalog FromNames(IEnumerable<string> names) =>
        new(names.Select(n => new PetDefinition { Id = Slug(n), En = n, Genus = "unknown" }));

    public static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
