using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.GenusInsight;

public sealed record GenusInsightStatDefinition(string StatId, string LabelDe, string LabelEn, string Unit, decimal RawDivisor);
public sealed record GenusInsightOption(byte Rank, string StatId, decimal Minimum, decimal Maximum);
public sealed record GenusInsightSlotDefinition(string Genus, byte Slot, IReadOnlyList<GenusInsightOption> Options);
public sealed record GenusInsightRange(decimal Minimum, decimal Maximum);

/// <summary>Presentation ranges requested 2026-11-09. Entries establish neither a current roll nor a network stat binding.</summary>
public sealed class GenusInsightCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<GenusInsightCatalog> Embedded = new(LoadEmbedded);
    private static readonly IReadOnlyList<GenusInsightOption> EmptyOptions = Array.AsReadOnly(Array.Empty<GenusInsightOption>());
    private readonly FrozenDictionary<string, GenusInsightStatDefinition> _stats;
    private readonly FrozenDictionary<(string Genus, int Slot), IReadOnlyList<GenusInsightOption>> _options;

    public static GenusInsightCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyDictionary<string, GenusInsightStatDefinition> Stats => _stats;
    public IReadOnlyList<GenusInsightSlotDefinition> Slots { get; }

    private GenusInsightCatalog(int metadataBuild, IEnumerable<GenusInsightStatDefinition> stats,
        IEnumerable<GenusInsightSlotDefinition> slots)
    {
        var definitions = new Dictionary<string, GenusInsightStatDefinition>(StringComparer.Ordinal);
        foreach (var stat in stats)
        {
            if (stat is null || !ValidLabel(stat.StatId) || !ValidLabel(stat.LabelDe) || !ValidLabel(stat.LabelEn)
                || stat.Unit is not ("Points" or "Percent") || stat.RawDivisor is not (1m or 100m)
                || !definitions.TryAdd(stat.StatId, stat))
                throw new InvalidDataException("Invalid or duplicate Genus Insight stat.");
        }
        var options = new Dictionary<(string Genus, int Slot), IReadOnlyList<GenusInsightOption>>();
        var frozenSlots = new List<GenusInsightSlotDefinition>();
        foreach (var slot in slots)
        {
            if (slot is null || !KnownGenus(slot.Genus) || slot.Slot is < 1 or > 9 || slot.Options is null)
                throw new InvalidDataException("Invalid Genus Insight slot.");
            var seen = new HashSet<(byte Rank, string StatId)>();
            foreach (var option in slot.Options)
                if (option is null || option.Rank is < 1 or > 5 || option.Minimum > option.Maximum
                    || !definitions.ContainsKey(option.StatId) || !seen.Add((option.Rank, option.StatId)))
                    throw new InvalidDataException("Invalid or duplicate Genus Insight option.");
            var copy = slot with { Options = Array.AsReadOnly(slot.Options.ToArray()) };
            if (!options.TryAdd((copy.Genus, copy.Slot), copy.Options))
                throw new InvalidDataException("Duplicate Genus Insight slot.");
            frozenSlots.Add(copy);
        }
        MetadataBuild = metadataBuild;
        _stats = definitions.ToFrozenDictionary(StringComparer.Ordinal);
        _options = options.ToFrozenDictionary();
        Slots = Array.AsReadOnly(frozenSlots.ToArray());
    }

    public GenusInsightStatDefinition? FindStat(string statId) => statId is null ? null : _stats.GetValueOrDefault(statId);

    public IReadOnlyList<GenusInsightOption> Options(string genus, int slot) => genus is null ? EmptyOptions
        : _options.GetValueOrDefault((genus, slot)) ?? EmptyOptions;

    public GenusInsightRange? Range(string genus, int slot, string statId, byte? rank = null)
    {
        var options = Options(genus, slot).Where(option => option.StatId == statId && (rank is null || option.Rank == rank)).ToArray();
        return options.Length == 0 ? null : new(options.Min(option => option.Minimum), options.Max(option => option.Maximum));
    }

    internal static GenusInsightCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions);
            if (file is not { Schema: 1, MetadataBuild: > 0, PresentationOnly: true,
                DoesNotEstablishNetworkStatBindings: true, Stats: not null, Slots: not null })
                throw new InvalidDataException("Unsupported Genus Insight presentation schema.");
            return new(file.MetadataBuild, file.Stats, file.Slots);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Genus Insight presentation metadata.", error); }
    }

    private static bool KnownGenus(string? genus) => genus is "cogni" or "fera" or "natura" or "varian" or "special";
    private static bool ValidLabel(string? text) => !string.IsNullOrWhiteSpace(text) && text == text.Trim() && text.Length <= 512
        && !text.Any(character => char.IsControl(character) || character is '<' or '>' or '\uFFFD');

    private static GenusInsightCatalog LoadEmbedded()
    {
        using var stream = typeof(GenusInsightCatalog).Assembly.GetManifestResourceStream("Soulcrest.GenusInsightCatalog.json")
            ?? throw new InvalidDataException("Missing embedded Genus Insight presentation metadata.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool PresentationOnly { get; init; }
        public required bool DoesNotEstablishNetworkStatBindings { get; init; }
        public required GenusInsightStatDefinition[] Stats { get; init; }
        public required GenusInsightSlotDefinition[] Slots { get; init; }
    }
}
