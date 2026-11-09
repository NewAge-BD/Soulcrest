using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.Pantheon;

public enum PantheonDecorCategory { Colossus, Statue, Artwork }
public sealed record PantheonGodEffect(string StatId, int Amount);
public sealed record PantheonDecorSlot(int Id, PantheonDecorCategory Category);

/// <summary>Presentation facts only. Collection membership and placement require separate own evidence.</summary>
public sealed record PantheonDecor
{
    public int Id { get; init; }
    public int? ItemId { get; init; }
    public string NameDe { get; init; } = "";
    public string NameEn { get; init; } = "";
    public PantheonDecorCategory Category { get; init; }
    public int Grade { get; init; }
    public string IconPath { get; init; } = "";
    public string? Faction { get; init; }
    public IReadOnlyList<PantheonGodEffect> Effects { get; init; } = [];
    public bool IncludesFaction(string? faction) => faction is "elyos" or "asmodian" && (Faction is null || Faction == faction);
    public string Name(string? language) => language?.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? NameDe : NameEn;
    public int EffectAmount(string statId) => Effects.Where(effect => effect.StatId == statId).Sum(effect => effect.Amount);
}

/// <summary>Finite display collection, independently of runtime ownership and God-to-combat conversions.</summary>
public sealed class PantheonCatalog
{
    private static readonly Lazy<PantheonCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, PantheonDecor> _byId;
    public static PantheonCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<PantheonDecor> Items { get; }
    public IReadOnlyList<PantheonDecorSlot> Slots { get; }
    public PantheonDecor? Find(int id) => _byId.GetValueOrDefault(id);

    private PantheonCatalog(Document document)
    {
        if (document.Schema != 1 || document.MetadataBuild != 26425393 || !document.PresentationOnly
            || document.Items.Length != 120 || document.Items.Select(item => item.Id).Distinct().Count() != 120)
            throw new InvalidDataException("Invalid Pantheon display collection.");
        var gods = new HashSet<string>(["Justice", "Freedom", "Illusion", "Life", "Time", "Destruction", "Death", "Wisdom", "Destiny", "Space"], StringComparer.Ordinal);
        foreach (var item in document.Items)
        {
            if (item.Id <= 0 || item.ItemId is <= 0 || item.Grade is < 1 or > 5 || !Enum.IsDefined(item.Category)
                || item.Faction is not (null or "elyos" or "asmodian") || string.IsNullOrWhiteSpace(item.NameDe)
                || string.IsNullOrWhiteSpace(item.NameEn) || item.NameDe.Contains('\uFFFD') || item.NameEn.Contains('\uFFFD')
                || item.IconPath.Length != 0 && (!item.IconPath.StartsWith("pantheon/p-", StringComparison.Ordinal)
                    || !item.IconPath.EndsWith(".png", StringComparison.Ordinal) || item.IconPath.Contains("..", StringComparison.Ordinal))
                || item.Effects.Count == 0 || item.Effects.Any(effect => !gods.Contains(effect.StatId) || effect.Amount <= 0)
                || item.Effects.Select(effect => effect.StatId).Distinct(StringComparer.Ordinal).Count() != item.Effects.Count)
                throw new InvalidDataException("Invalid Pantheon decoration.");
        }
        var expectedSlots = Enumerable.Range(1, 17).Select(id => new PantheonDecorSlot(id,
            id <= 12 ? PantheonDecorCategory.Artwork : id <= 16 ? PantheonDecorCategory.Statue : PantheonDecorCategory.Colossus)).ToArray();
        if (!document.Slots.OrderBy(slot => slot.Id).SequenceEqual(expectedSlots)
            || new[] { "elyos", "asmodian" }.Any(faction => document.Items.Count(item => item.IncludesFaction(faction)) != 98))
            throw new InvalidDataException("Invalid Pantheon slot or faction scope.");
        MetadataBuild = document.MetadataBuild;
        Items = Array.AsReadOnly(document.Items.Select(item => item with { Effects = Array.AsReadOnly(item.Effects.ToArray()) }).ToArray());
        Slots = Array.AsReadOnly(expectedSlots);
        _byId = Items.ToFrozenDictionary(item => item.Id);
    }

    private static PantheonCatalog LoadEmbedded()
    {
        using var stream = typeof(PantheonCatalog).Assembly.GetManifestResourceStream("Soulcrest.PantheonCatalog.json")
            ?? throw new InvalidDataException("Missing Pantheon display collection.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return new(JsonSerializer.Deserialize<Document>(stream, options) ?? throw new InvalidDataException("Empty Pantheon display collection."));
    }
    private sealed record Document
    {
        public int Schema { get; init; }
        public int MetadataBuild { get; init; }
        public bool PresentationOnly { get; init; }
        public PantheonDecor[] Items { get; init; } = [];
        public PantheonDecorSlot[] Slots { get; init; } = [];
    }
}
