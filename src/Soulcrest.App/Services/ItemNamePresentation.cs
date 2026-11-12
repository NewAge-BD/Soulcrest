using System.Collections.Frozen;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

/// <summary>Immutable foreground treatment only; never changes item labels or loot classifications.</summary>
public sealed record ItemNameStyle(string? Grade, string? CssColor, Color? Color)
{
    public bool IsKnown => CssColor is not null;
    public static ItemNameStyle Neutral { get; } = new(null, null, null);
}

/// <summary>Shared bundled item-name foregrounds for Blazor, map labels and native overlays.</summary>
public static class ItemNamePresentation
{
    private static readonly Lazy<Catalog> Embedded = new(LoadEmbedded);
    public static int ItemCount => Embedded.Value.ItemCount;
    public static int GradeCount => Embedded.Value.GradeCount;
    public static IReadOnlyDictionary<int, string> CssColorsByItemId => Embedded.Value.CssColorsByItemId;

    /// <summary>An unknown exact item ID remains neutral; no name, ID-range or older-catalog fallback.</summary>
    public static ItemNameStyle ForItem(int itemId) => Embedded.Value.ForItem(itemId);
    public static ItemNameStyle ForGrade(string? grade) => Embedded.Value.ForGrade(grade);
    public static ItemNameStyle ForGrade(LootItemRarity rarity) => Enum.IsDefined(rarity)
        && rarity != LootItemRarity.Unknown ? ForGrade(rarity.ToString()) : ItemNameStyle.Neutral;

    /// <summary>Canonical persisted keys only. Crystal-type aliases affect presentation alone.</summary>
    public static ItemNameStyle ForDropKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return ItemNameStyle.Neutral;
        var separator = key.IndexOf(':');
        if (separator <= 0 || !int.TryParse(key.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id <= 0) return ItemNameStyle.Neutral;
        return key.AsSpan(0, separator) switch
        {
            "Item" or "item" => ForItem(id),
            "SoulCrystal" => ForItem(id switch
            {
                1 => 531900005, 2 => 531900004, 3 => 531900006, 4 => 531900007, 5 => 531900008, _ => 0
            }),
            "UniversalSoulCrystal" when id == Network.NetworkFarmDrop.UniversalSoulCrystalItemId => ForItem(id),
            _ => ItemNameStyle.Neutral
        };
    }

    private static Catalog LoadEmbedded()
    {
        // Foregrounds are optional presentation; a damaged package must not interrupt counting or plans.
        try
        {
            using var stream = typeof(ItemNamePresentation).Assembly.GetManifestResourceStream("Soulcrest.ItemNameAppearance.json")
                ?? throw new InvalidDataException("Missing item-name appearance catalog.");
            using var reader = new StreamReader(stream);
            return FromJson(reader.ReadToEnd());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            return Catalog.Empty;
        }
    }

    internal static Catalog FromJson(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            };
            var file = JsonSerializer.Deserialize<CatalogFile>(json, options);
            if (file?.SchemaVersion != 1 || file.Palette is null || file.Items is null
                || file.Palette.Length > 16 || file.Items.Length > 100000)
                throw new InvalidDataException("Unsupported item-name appearance catalog.");
            return new(file.Palette, file.Items);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid item-name appearance catalog.", error); }
    }

    internal sealed class Catalog
    {
        private readonly FrozenDictionary<string, ItemNameStyle> _grades;
        private readonly FrozenDictionary<int, ItemNameStyle> _items;
        internal static Catalog Empty { get; } = new([], []);
        internal int ItemCount => _items.Count;
        internal int GradeCount => _grades.Count;
        internal IReadOnlyDictionary<int, string> CssColorsByItemId { get; }

        internal Catalog(IEnumerable<PaletteRow> palette, IEnumerable<ItemRow> items)
        {
            var grades = new Dictionary<string, ItemNameStyle>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in palette)
            {
                if (row is null || !IsGrade(row.Grade) || !TryColor(row.CssColor, out var color)
                    || !grades.TryAdd(row.Grade!, new(row.Grade, row.CssColor, color)))
                    throw new InvalidDataException("Invalid or duplicate item-name grade.");
            }
            var entries = new Dictionary<int, ItemNameStyle>();
            foreach (var row in items)
                if (row is null || row.Id <= 0 || row.Grade is null || !grades.TryGetValue(row.Grade, out var style)
                    || !entries.TryAdd(row.Id, style))
                    throw new InvalidDataException("Invalid or duplicate item-name item.");
            _grades = grades.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _items = entries.ToFrozenDictionary();
            CssColorsByItemId = entries.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.CssColor!);
        }

        internal ItemNameStyle ForItem(int id) => _items.GetValueOrDefault(id, ItemNameStyle.Neutral);
        internal ItemNameStyle ForGrade(string? grade)
        {
            if (grade is null) return ItemNameStyle.Neutral;
            if (grade.StartsWith("EItemGrade::", StringComparison.Ordinal)) grade = grade[12..];
            return _grades.GetValueOrDefault(grade, ItemNameStyle.Neutral);
        }

        private static bool IsGrade(string? grade) => grade is "Common" or "Rare" or "Legend" or "Unique"
            or "Epic" or "Mythic" or "Special";
        private static bool TryColor(string? css, out Color color)
        {
            color = default;
            if (css is not { Length: 7 } || css[0] != '#'
                || css.AsSpan(1).ContainsAnyExcept("0123456789ABCDEF".AsSpan())
                || !int.TryParse(css.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
            color = System.Drawing.Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }
    }

    internal sealed record PaletteRow(string? Grade, string? CssColor);
    internal sealed record ItemRow(int Id, string? Grade);
    private sealed record CatalogFile(int SchemaVersion, PaletteRow[]? Palette, ItemRow[]? Items);
}
