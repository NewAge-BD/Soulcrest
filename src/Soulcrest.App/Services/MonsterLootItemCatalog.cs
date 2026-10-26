using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

public enum LootItemRarity { Unknown, Common, Rare, Legend, Unique, Epic, Mythic, Special }
public enum LootItemExchangeType { Unknown, NonExchangeable, SoulBindOnAcquire, SoulBind, Exchangeable }

/// <summary>Static item-type facts only. They do not prove the binding state of a received instance.</summary>
public sealed record LootItemTradeFacts(LootItemExchangeType ExchangeType, bool? HasTradeCategory)
{
    public static LootItemTradeFacts Unknown { get; } = new(LootItemExchangeType.Unknown, null);
}

/// <summary>Bundled item labels and explicit static classifications; no game files or network access.</summary>
public sealed class MonsterLootItemCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<MonsterLootItemCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, ItemLabel> _items;

    public static MonsterLootItemCatalog Default => Embedded.Value;
    public int Count => _items.Count;

    private MonsterLootItemCatalog(IEnumerable<ItemLabel> items)
    {
        var entries = new Dictionary<int, ItemLabel>();
        foreach (var row in items)
        {
            if (row is null || row.Id <= 0
                || string.IsNullOrWhiteSpace(row.NameDe) && string.IsNullOrWhiteSpace(row.NameEn)
                || row.AutomaticallyConsumedSoul && row.Currency
                || row.Gear && (row.AutomaticallyConsumedSoul || row.Currency)
                || !Enum.IsDefined(row.Rarity) || !Enum.IsDefined(row.ExchangeType)
                || !entries.TryAdd(row.Id, row))
                throw new InvalidDataException("Invalid or duplicate loot item label.");
        }
        _items = entries.ToFrozenDictionary();
    }

    public string Name(int itemId, string language = "en")
    {
        if (_items.TryGetValue(itemId, out var row))
        {
            var preferred = language == "de" ? row.NameDe : row.NameEn;
            var fallback = language == "de" ? row.NameEn : row.NameDe;
            if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        }
        return "Item " + itemId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Only statically proven soul items consumed immediately on acquisition; never a name/ID-range heuristic.</summary>
    public bool IsAutomaticallyConsumedSoul(int itemId) => _items.TryGetValue(itemId, out var row) && row.AutomaticallyConsumedSoul;

    public bool IsCurrency(int itemId) => _items.TryGetValue(itemId, out var row) && row.Currency;

    /// <summary>Exact native Equip classification; common materials and unknown IDs are never gear.</summary>
    public bool IsGear(int itemId) => _items.TryGetValue(itemId, out var row) && row.Gear;

    public LootItemRarity Rarity(int itemId) => _items.TryGetValue(itemId, out var row) ? row.Rarity : LootItemRarity.Unknown;
    public LootItemTradeFacts TradeFacts(int itemId) => _items.TryGetValue(itemId, out var row)
        ? new(row.ExchangeType, row.HasTradeCategory) : LootItemTradeFacts.Unknown;

    // An explicit whitelist avoids treating Unknown or a future enum member as a qualified special drop.
    public static bool IsExceptional(LootItemRarity rarity) => rarity is
        LootItemRarity.Unique or LootItemRarity.Epic or LootItemRarity.Mythic or LootItemRarity.Special;

    public LootItemRarity DropRarity(string? lootKey) => TryItemId(lootKey, out var id) ? Rarity(id) : LootItemRarity.Unknown;
    public static bool TryItemId(string? lootKey, out int id)
    {
        id = 0;
        return lootKey is not null && (lootKey.StartsWith("Item:", StringComparison.Ordinal)
            || lootKey.StartsWith("item:", StringComparison.Ordinal))
            && int.TryParse(lootKey.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    internal static MonsterLootItemCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file?.SchemaVersion != 1 || file.Items is null)
                throw new InvalidDataException("Unsupported loot item catalog schema.");
            return new(file.Items);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid loot item labels.", error); }
    }

    private static MonsterLootItemCatalog LoadEmbedded()
    {
        using var stream = typeof(MonsterLootItemCatalog).Assembly.GetManifestResourceStream("Soulcrest.LootItems.json")
            ?? throw new InvalidDataException("Missing embedded loot item labels.");
        using var reader = new StreamReader(stream);
        // Classification controls duplicate exclusion, so a missing/corrupt table must never silently become empty.
        return FromJson(reader.ReadToEnd());
    }

    private sealed record ItemLabel(int Id, string? NameDe, string? NameEn,
        bool AutomaticallyConsumedSoul = false, bool Currency = false,
        LootItemRarity Rarity = LootItemRarity.Unknown, LootItemExchangeType ExchangeType = LootItemExchangeType.Unknown,
        bool? HasTradeCategory = null, bool Gear = false);
    private sealed record CatalogFile(int SchemaVersion, ItemLabel[]? Items);
}
