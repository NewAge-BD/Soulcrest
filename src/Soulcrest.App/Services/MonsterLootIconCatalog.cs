using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>Exact bundled item/pet image bindings. No file discovery, game access or downloads at runtime.</summary>
public sealed class MonsterLootIconCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex AssetPath = new(@"\Aloot-icons/[a-z0-9_-]{1,160}\.png\z", RegexOptions.CultureInvariant);
    private static readonly Lazy<MonsterLootIconCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, string> _items, _pets;

    public static MonsterLootIconCatalog Default => Embedded.Value;
    public int ItemCount => _items.Count;
    public int PetCount => _pets.Count;

    private MonsterLootIconCatalog(IEnumerable<IconBinding> items, IEnumerable<IconBinding> pets)
    {
        _items = Bind(items); _pets = Bind(pets);
    }

    /// <summary>Null preserves an explicit neutral fallback when no original image was available.</summary>
    public string? ItemAsset(int itemId) => _items.GetValueOrDefault(itemId);
    public string? PetSoulAsset(int petNumber) => _pets.GetValueOrDefault(petNumber);

    /// <summary>Canonical persisted drop keys only; labels and numeric ranges never select an image.</summary>
    public string? DropAsset(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var separator = key.IndexOf(':');
        if (separator <= 0 || !int.TryParse(key.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        return key.AsSpan(0, separator) switch
        {
            "Item" or "item" => ItemAsset(id),
            "SoulCrystal" => SoulCrystalAsset(id),
            // Exact own-build currency/item pair shares Icon_Item_Soulcrystal_All.
            "UniversalSoulCrystal" when id == Network.NetworkFarmDrop.UniversalSoulCrystalItemId =>
                ItemAsset(id) ?? ItemAsset(531900009),
            "PetSoul" => PetSoulAsset(id),
            _ => null
        };
    }

    /// <summary>Raw native crystal types, explicitly mapped independently of creature genus numbering.</summary>
    public string? SoulCrystalAsset(int nativeType) => ItemAsset(nativeType switch
    {
        1 => 531900005, 2 => 531900004, 3 => 531900006, 4 => 531900007, 5 => 531900008, _ => 0
    });

    internal static MonsterLootIconCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file?.SchemaVersion != 1 || file.Items is null || file.Pets is null
                || file.Items.Length > 20000 || file.Pets.Length > 10000)
                throw new InvalidDataException("Unsupported loot icon catalog schema.");
            return new(file.Items, file.Pets);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid loot icon catalog.", error); }
    }

    private static FrozenDictionary<int, string> Bind(IEnumerable<IconBinding> bindings)
    {
        var result = new Dictionary<int, string>();
        foreach (var binding in bindings)
            if (binding is null || binding.Id <= 0 || binding.Asset is null || !AssetPath.IsMatch(binding.Asset)
                || !result.TryAdd(binding.Id, binding.Asset))
                throw new InvalidDataException("Invalid or duplicate loot icon binding.");
        return result.ToFrozenDictionary();
    }

    private static MonsterLootIconCatalog LoadEmbedded()
    {
        // Icons are optional presentation; an incomplete package must never interrupt counting.
        try
        {
            using var stream = typeof(MonsterLootIconCatalog).Assembly.GetManifestResourceStream("Soulcrest.LootIcons.json")
                ?? throw new InvalidDataException("Missing bundled loot icon metadata.");
            using var reader = new StreamReader(stream);
            return FromJson(reader.ReadToEnd());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            // Lazy initialization logs at most once; the renderer retains its neutral fallback.
            LogFile.Error("Loot-Iconkatalog", error);
            return new([], []);
        }
    }

    private sealed record IconBinding(int Id, string? Asset);
    private sealed record CatalogFile(int SchemaVersion, IconBinding[]? Items, IconBinding[]? Pets);
}
