using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

/// <summary>Independent presentation metadata. Entries never establish character ownership or an active effect.</summary>
public sealed class CombatWingDisplayCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatWingDisplayCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatWingDisplayEntry> _entries;

    public static CombatWingDisplayCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public int Count => _entries.Count;
    public IReadOnlyDictionary<int, CombatWingDisplayEntry> Entries => _entries;

    private CombatWingDisplayCatalog(int build, IEnumerable<CombatWingDisplayEntry> wings)
    {
        var entries = new Dictionary<int, CombatWingDisplayEntry>();
        foreach (var wing in wings)
        {
            if (wing is null || wing.Id <= 0 || !ValidLabel(wing.NameDe) || !ValidLabel(wing.NameEn)
                || wing.Race is not ("Light" or "Dark")
                || wing.Grade is not ("Common" or "Rare" or "Legend" or "Unique" or "Epic" or "Special")
                || !ValidIcon(wing.IconPath))
                throw new InvalidDataException("Invalid Wing display entry.");
            var copy = wing with { EquippedEffects = Freeze(wing.EquippedEffects), OwnedEffects = Freeze(wing.OwnedEffects) };
            if (!entries.TryAdd(copy.Id, copy)) throw new InvalidDataException("Duplicate Wing display identity.");
        }
        MetadataBuild = build;
        _entries = entries.ToFrozenDictionary();
    }

    public CombatWingDisplayEntry? Find(int wingId) => _entries.GetValueOrDefault(wingId);

    public string? Name(int wingId, string language = "en") => Find(wingId) is { } wing
        ? language == "de" ? wing.NameDe : wing.NameEn : null;

    internal static CombatWingDisplayCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, PresentationOnly: true,
                DoesNotEstablishOwnershipOrEffectApplicability: true, Wings: not null })
                throw new InvalidDataException("Unsupported Wing presentation schema.");
            return new(file.MetadataBuild, file.Wings);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Wing presentation metadata.", error); }
    }

    private static bool ValidLabel(string? label) => !string.IsNullOrWhiteSpace(label)
        && label == label.Trim() && label.Length <= 512
        && !label.Any(character => char.IsControl(character) || character is '<' or '>' or '\uFFFD');

    private static bool ValidIcon(string? path) => path is not null
        && path.StartsWith("icons/combat/wings/", StringComparison.Ordinal)
        && path.EndsWith(".png", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
        && !path.Any(character => char.IsControl(character) || character is '\\' or ':' or '?' or '#');

    private static IReadOnlyList<CombatTitleDisplayEffect>? Freeze(IReadOnlyList<CombatTitleDisplayEffect>? effects)
    {
        if (effects is null) return null;
        if (effects.Any(effect => effect is null || !ValidLabel(effect.StatId) || !ValidLabel(effect.LabelDe)
            || !ValidLabel(effect.LabelEn) || effect.Unit is not (null or "Points" or "Percent"))
            || effects.Select(effect => effect.StatId).Distinct(StringComparer.Ordinal).Count() != effects.Count)
            throw new InvalidDataException("Invalid Wing display effect.");
        return Array.AsReadOnly(effects.ToArray());
    }

    private static CombatWingDisplayCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatWingDisplayCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatWingLabels.json")
            ?? throw new InvalidDataException("Missing embedded Wing presentation metadata.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool PresentationOnly { get; init; }
        public required bool DoesNotEstablishOwnershipOrEffectApplicability { get; init; }
        public required CombatWingDisplayEntry[] Wings { get; init; }
    }
}

public sealed record CombatWingDisplayEntry(int Id, string NameDe, string NameEn, string Race, string Grade, string IconPath)
{
    public IReadOnlyList<CombatTitleDisplayEffect>? EquippedEffects { get; init; }
    public IReadOnlyList<CombatTitleDisplayEffect>? OwnedEffects { get; init; }
}
