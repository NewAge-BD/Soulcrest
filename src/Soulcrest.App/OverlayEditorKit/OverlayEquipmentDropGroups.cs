using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Display-only summaries of proven ordinary equipment; source receipts remain exact.</summary>
internal static class OverlayEquipmentDropGroups
{
    internal static IReadOnlyList<OverlayLootItem> Group(IEnumerable<OverlayLootItem> source, string? language)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rows = source.ToArray();
        var catalog = MonsterLootItemCatalog.Default;
        var groups = new Dictionary<LootItemRarity, List<(int Index, OverlayLootItem Item)>>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row.Quantity <= 0 || !MonsterLootItemCatalog.TryItemId(row.Key, out var id) || !catalog.IsGear(id)) continue;
            var rarity = catalog.Rarity(id);
            if (rarity is not (LootItemRarity.Common or LootItemRarity.Rare or LootItemRarity.Legend)) continue;
            if (!groups.TryGetValue(rarity, out var members)) groups[rarity] = members = [];
            members.Add((index, row));
        }

        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        var replacements = new Dictionary<int, OverlayLootItem>();
        var combined = new HashSet<int>();
        foreach (var (rarity, members) in groups)
        {
            long quantity = 0;
            try
            {
                foreach (var member in members) quantity = checked(quantity + member.Item.Quantity);
            }
            catch (OverflowException)
            {
                // No clipping or partial group: every exact member stays at its original position.
                continue;
            }
            var (suffix, label) = Label(rarity, language == "de");
            var quantityText = quantity.ToString("N0", culture);
            var icon = members.OrderBy(member => member.Item.Key, StringComparer.Ordinal)
                .ThenBy(member => member.Item.IconPath, StringComparer.Ordinal).Select(member => member.Item.IconPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            replacements[members[0].Index] = new("equipment-group:" + suffix, label, quantityText,
                IconPath: icon, IsRare: false, Quantity: quantity, Tooltip: label + " ×" + quantityText,
                Rarity: rarity, IsEquipmentGroup: true);
            foreach (var member in members) combined.Add(member.Index);
        }

        var result = new List<OverlayLootItem>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            if (replacements.TryGetValue(index, out var group)) result.Add(group);
            else if (!combined.Contains(index)) result.Add(rows[index]);
        }
        return result.AsReadOnly();
    }

    private static (string Key, string Label) Label(LootItemRarity rarity, bool german) => rarity switch
    {
        LootItemRarity.Common => ("common", german ? "Gewöhnliche Ausrüstung" : "Common Gear"),
        LootItemRarity.Rare => ("rare", german ? "Seltene Ausrüstung" : "Rare Gear"),
        // The blue grade is named Epic in game; its existing catalog enum remains Legend.
        LootItemRarity.Legend => ("legend", german ? "Epische Ausrüstung" : "Epic Gear"),
        _ => throw new ArgumentOutOfRangeException(nameof(rarity)),
    };
}
