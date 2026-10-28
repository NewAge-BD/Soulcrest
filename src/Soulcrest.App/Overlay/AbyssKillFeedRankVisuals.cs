using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Overlay lighting chosen from an explicitly observed opponent grade, independent of own point tiers.</summary>
internal static class AbyssKillFeedRankVisuals
{
    internal readonly record struct Treatment(Color Accent, double Strength, double MotionIntensity);

    internal static Treatment? ForGrade(int? grade)
    {
        if (grade is not (>= 1 and <= 19)) return null;
        var prominence = (19 - grade.Value) / 18d;
        // These are design colors borrowed from the item palette, not item rarities or game rank categories.
        var palette = grade switch
        {
            1 => LootItemRarity.Special,
            <= 4 => LootItemRarity.Mythic,
            <= 9 => LootItemRarity.Epic,
            _ => LootItemRarity.Unique
        };
        return new(LootRarityVisuals.Accent(palette), .26 + .74 * prominence, .3 + .7 * prominence);
    }

    internal static void PaintBackground(Graphics graphics, RectangleF bounds, int? grade,
        double seconds, bool animated)
    {
        if (ForGrade(grade) is not { } treatment) return;
        LootRarityVisuals.PaintBackground(graphics, bounds, treatment.Accent, seconds, animated,
            treatment.Strength, treatment.MotionIntensity, accentLine: false);
    }

    internal static bool NeedsAnimation(AbyssRankOverlaySnapshot snapshot, bool reducedMotion,
        bool presentationVisible = true) => presentationVisible && !reducedMotion
        && AbyssRankOverlayRenderer.KillFeedEntries(snapshot).Any(entry => ForGrade(entry.OpponentAbyssRankGrade) is not null);
}
