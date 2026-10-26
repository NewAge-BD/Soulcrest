using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Overlay-only grouping. Stored item identities and accepted-drop notifications stay unchanged.</summary>
internal static class LootOverlayDropPresentation
{
    internal const string CommonGearKey = "CommonGear";

    internal static IReadOnlyList<LootOverlayDrop> GroupCommonGear(IReadOnlyList<LootOverlayDrop> drops)
    {
        var gear = drops.Where(IsCommonGear).ToArray();
        if (gear.Length == 0) return drops;
        long count = 0;
        try
        {
            foreach (var row in gear)
            {
                count = checked(count + row.Count);
            }
        }
        catch (OverflowException) { return drops; }
        var result = new List<LootOverlayDrop>(drops.Count - gear.Length + 1);
        var inserted = false;
        foreach (var row in drops)
        {
            if (!IsCommonGear(row)) { result.Add(row); continue; }
            if (inserted) continue;
            result.Add(new(CommonGearKey, "Common Gear", count)
            {
                Rarity = LootItemRarity.Common, IconPath = row.IconPath,
            });
            inserted = true;
        }
        return result.AsReadOnly();
    }

    private static bool IsCommonGear(LootOverlayDrop row) => row.Kind == LootOverlayDropKind.Item && row.Count > 0
        && MonsterLootItemCatalog.TryItemId(row.Key, out var id)
        && MonsterLootItemCatalog.Default.Rarity(id) == LootItemRarity.Common
        && MonsterLootItemCatalog.Default.IsGear(id);
}
