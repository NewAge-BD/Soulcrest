using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

/// <summary>Presentation metadata only. Labels do not establish ownership, slot semantics or stat effects.</summary>
public sealed class CombatTitleDisplayCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatTitleDisplayCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatTitleDisplayDefinition> _titles;

    public static CombatTitleDisplayCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public int Count => _titles.Count;
    public IReadOnlyDictionary<int, CombatTitleDisplayDefinition> Titles => _titles;

    private CombatTitleDisplayCatalog(int metadataBuild, IEnumerable<CombatTitleDisplayDefinition> titles)
    {
        var entries = new Dictionary<int, CombatTitleDisplayDefinition>();
        foreach (var title in titles)
        {
            if (title is null || title.Id <= 0 || !ValidLabel(title.NameDe) || !ValidLabel(title.NameEn)
                || title.EquipCategory is not ("Attack" or "Defense" or "Etc" or "None")
                || title.Origin is not (null or "Quest" or "Growth" or "Challenge" or "Adventure" or "Dungeon" or "Item" or "None")
                || title.Grade is not (null or "Common" or "Rare" or "Legend" or "Unique" or "Epic" or "Special")
                || !ValidIcon(title.IconPath))
                throw new InvalidDataException("Invalid or duplicate title label.");
            var copy = title with { EquippedEffects = Freeze(title.EquippedEffects), OwnedEffects = Freeze(title.OwnedEffects) };
            if (!entries.TryAdd(copy.Id, copy)) throw new InvalidDataException("Invalid or duplicate title label.");
        }
        MetadataBuild = metadataBuild;
        _titles = entries.ToFrozenDictionary();
    }

    public CombatTitleDisplayDefinition? Find(int titleId) => _titles.GetValueOrDefault(titleId);

    public string? Name(int titleId, string language = "en") => Find(titleId) is { } title
        ? language == "de" ? title.NameDe : title.NameEn : null;

    internal static CombatTitleDisplayCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 2, MetadataBuild: > 0, PresentationOnly: true,
                DoesNotEstablishOwnershipOrEffectApplicability: true, Titles: not null })
                throw new InvalidDataException("Unsupported title label schema.");
            return new(file.MetadataBuild, file.Titles);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid title labels.", error); }
    }

    private static bool ValidLabel(string? label) => !string.IsNullOrWhiteSpace(label)
        && label == label.Trim() && label.Length <= 512
        && !label.Any(character => char.IsControl(character) || character is '<' or '>' or '\uFFFD');

    private static bool ValidIcon(string? path) => path is null || path.StartsWith("icons/combat/titles/", StringComparison.Ordinal)
        && path.EndsWith(".png", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
        && !path.Any(character => char.IsControl(character) || character is '\\' or ':' or '?' or '#');

    private static IReadOnlyList<CombatTitleDisplayEffect>? Freeze(IReadOnlyList<CombatTitleDisplayEffect>? effects)
    {
        if (effects is null) return null;
        if (effects.Any(effect => effect is null || !ValidLabel(effect.StatId) || !ValidLabel(effect.LabelDe)
            || !ValidLabel(effect.LabelEn) || effect.Unit is not (null or "Points" or "Percent")))
            throw new InvalidDataException("Invalid title display effect.");
        return Array.AsReadOnly(effects.ToArray());
    }

    private static CombatTitleDisplayCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatTitleDisplayCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatTitleLabels.json")
            ?? throw new InvalidDataException("Missing embedded title labels.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool PresentationOnly { get; init; }
        public required bool DoesNotEstablishOwnershipOrEffectApplicability { get; init; }
        public required CombatTitleDisplayDefinition[] Titles { get; init; }
    }
}

public sealed record CombatTitleDisplayDefinition(int Id, string NameDe, string NameEn, string EquipCategory)
{
    public string? Origin { get; init; }
    public string? Grade { get; init; }
    public string? IconPath { get; init; }
    public IReadOnlyList<CombatTitleDisplayEffect>? EquippedEffects { get; init; }
    public IReadOnlyList<CombatTitleDisplayEffect>? OwnedEffects { get; init; }
}

public sealed record CombatTitleDisplayEffect(string StatId, string LabelDe, string LabelEn, decimal? Value, string? Unit);
