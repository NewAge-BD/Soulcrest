namespace Soulcrest.App.Overlay;

public sealed partial class PetOverlayForm
{
    /// <summary>Places a transient level-up on existing rows without changing any progress or loot data.</summary>
    internal static (IReadOnlyList<LootRow> Focus, IReadOnlyList<LootRow> Drops) PlaceLevelUpRows(
        IReadOnlyList<LootRow> focus, IReadOnlyList<LootRow> drops, LevelUpRow? levelUp, LootRow? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(focus);
        ArgumentNullException.ThrowIfNull(drops);
        static LootRow Plain(LootRow row) => row.LevelUp is null ? row : row with { LevelUp = null };
        static bool SameId(string? first, string? second) => !string.IsNullOrWhiteSpace(first) &&
            string.Equals(first, second, StringComparison.Ordinal);

        var placedFocus = focus.Select(Plain).ToArray();
        var focusIds = placedFocus.Select(row => row.PetId).OfType<string>()
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var maxId = levelUp is { IsMax: true } ? levelUp.PetId : null;
        // Filter before the five-row limit so the next eligible pet can move up. An active MAX
        // result also excludes an older row snapshot that has not acquired IsMax yet.
        var placedDrops = drops.Where(row => !row.IsMax && !SameId(maxId, row.PetId) &&
                (row.PetId is null || !focusIds.Contains(row.PetId)))
            .Select(Plain).ToList();

        if (levelUp is { IsMax: false, Level: 1 or 2 } && !string.IsNullOrWhiteSpace(levelUp.PetId))
        {
            var focusIndex = Array.FindIndex(placedFocus, row => SameId(row.PetId, levelUp.PetId));
            if (focusIndex >= 0)
            {
                placedFocus[focusIndex] = placedFocus[focusIndex] with { LevelUp = levelUp, Alpha = 255 };
            }
            else
            {
                var dropIndex = placedDrops.FindIndex(row => SameId(row.PetId, levelUp.PetId));
                if (dropIndex >= 0)
                {
                    var row = placedDrops[dropIndex] with { LevelUp = levelUp, Alpha = 255 };
                    placedDrops.RemoveAt(dropIndex);
                    placedDrops.Insert(0, row);
                }
                else if (fallback is { IsMax: false, Quantity: 0 } && SameId(fallback.PetId, levelUp.PetId))
                {
                    // This row is presentation only; its real counters come from the caller.
                    placedDrops.Insert(0, fallback with { LevelUp = levelUp, Alpha = 255 });
                }
            }
        }
        return (Array.AsReadOnly(placedFocus), Array.AsReadOnly(placedDrops.Take(5).ToArray()));
    }
}
