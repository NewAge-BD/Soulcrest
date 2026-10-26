using System.Collections.Frozen;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>Item labels loaded once from the bundled name list; no game files or network access.</summary>
public sealed class LifeskillItemNameCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<LifeskillItemNameCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, ItemLabel> _items;
    public static LifeskillItemNameCatalog Empty { get; } = new([]);
    public static LifeskillItemNameCatalog Default => Embedded.Value;
    public int Count => _items.Count;

    private LifeskillItemNameCatalog(IEnumerable<ItemLabel> items)
    {
        var names = new Dictionary<int, ItemLabel>();
        foreach (var row in items)
        {
            if (row is null || row.Id <= 0
                || string.IsNullOrWhiteSpace(row.NameDe) && string.IsNullOrWhiteSpace(row.NameEn)
                || !names.TryAdd(row.Id, row))
                throw new InvalidDataException("Invalid or duplicate item label.");
        }
        _items = names.ToFrozenDictionary();
    }

    public string Name(int itemId) => Name(itemId, UiText.Language);

    public string Name(int itemId, string? language)
    {
        if (_items.TryGetValue(itemId, out var row))
        {
            var name = language == "de"
                ? First(row.NameDe, row.NameEn) : First(row.NameEn, row.NameDe);
            if (name is not null) return name;
        }
        return $"Item #{itemId}";
    }

    internal static LifeskillItemNameCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file?.SchemaVersion != 1 || file.Items is null)
                throw new InvalidDataException("Unknown item label schema.");
            return new(file.Items);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid item labels.", error); }
    }

    private static string? First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;

    private static LifeskillItemNameCatalog LoadEmbedded()
    {
        using var stream = typeof(LifeskillItemNameCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.LifeskillItemNames.json");
        if (stream is null) return Empty;
        using var reader = new StreamReader(stream);
        try { return FromJson(reader.ReadToEnd()); }
        catch (InvalidDataException) { return Empty; }
    }

    private sealed record ItemLabel(int Id, string? NameDe, string? NameEn);
    private sealed record CatalogFile(int SchemaVersion, ItemLabel[]? Items);
}
