using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Overlay-only grouping and ordering. Stored item identities and accepted-drop notifications stay unchanged.</summary>
internal static class LootOverlayDropPresentation
{
    internal const string CommonGearKey = "CommonGear", RareGearKey = "RareGear", LegendGearKey = "LegendGear";

    // Keep the existing entry point for every loot-overlay source. Only explicit Equip rows of these
    // three grades combine; materials, unknown keys and exceptional items retain their exact rows.
    internal static IReadOnlyList<LootOverlayDrop> GroupCommonGear(IReadOnlyList<LootOverlayDrop> drops)
    {
        var result = new List<LootOverlayDrop>(drops.Count);
        var gear = new Dictionary<LootItemRarity, List<LootOverlayDrop>>();
        foreach (var row in drops)
        {
            if (GearRarity(row) is not { } rarity) { result.Add(row); continue; }
            if (!gear.TryGetValue(rarity, out var rows)) gear.Add(rarity, rows = []);
            rows.Add(row);
        }
        foreach (var (rarity, rows) in gear)
        {
            if (CombineGear(rarity, rows) is { } combined) result.Add(combined);
            else result.AddRange(rows); // Never truncate quantities or row values on overflow.
        }
        return LootTrackerPresentation.OrderDrops(result, row => row.Rarity == LootItemRarity.Unknown
                ? MonsterLootItemCatalog.Default.DropRarity(row.Key) : row.Rarity,
            row => row.Count, row => row.Name, row => row.Key).ToArray();
    }

    private static LootOverlayDrop? CombineGear(LootItemRarity rarity, IReadOnlyList<LootOverlayDrop> rows)
    {
        long count = 0;
        try
        {
            foreach (var row in rows)
            {
                count = checked(count + row.Count);
            }
        }
        catch (OverflowException) { return null; }
        var (key, label) = rarity switch
        {
            LootItemRarity.Rare => (RareGearKey, "Seltene Ausrüstung"),
            LootItemRarity.Legend => (LegendGearKey, "Legendäre Ausrüstung"),
            _ => (CommonGearKey, "Gewöhnliche Ausrüstung")
        };
        return new(key, UiText.T(label), count)
        {
            Rarity = rarity,
            IconPath = rows.OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => row.IconPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
        };
    }

    private static LootItemRarity? GearRarity(LootOverlayDrop row)
    {
        if (row.Kind != LootOverlayDropKind.Item || row.Count <= 0
            || !MonsterLootItemCatalog.TryItemId(row.Key, out var id)
            || !MonsterLootItemCatalog.Default.IsGear(id)) return null;
        var rarity = MonsterLootItemCatalog.Default.Rarity(id);
        return rarity is LootItemRarity.Common or LootItemRarity.Rare or LootItemRarity.Legend ? rarity : null;
    }
}
