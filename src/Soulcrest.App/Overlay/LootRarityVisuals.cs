using System.Drawing.Drawing2D;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Original item-card-inspired lighting; the exact item grade decides which treatment is used.</summary>
internal static class LootRarityVisuals
{
    internal static Color Accent(LootItemRarity rarity) => rarity switch
    {
        LootItemRarity.Common => Color.FromArgb(235, 233, 222),
        LootItemRarity.Rare => Color.FromArgb(102, 220, 104),
        LootItemRarity.Legend => Color.FromArgb(100, 183, 250),
        LootItemRarity.Unique => Color.FromArgb(251, 202, 71),
        LootItemRarity.Epic => Color.FromArgb(195, 142, 251),
        LootItemRarity.Mythic => Color.FromArgb(255, 136, 170),
        LootItemRarity.Special => Color.FromArgb(117, 226, 236),
        _ => Color.FromArgb(235, 233, 222)
    };

    // Item grades come from the exact catalog ID, including non-exceptional materials.
    // Both production surfaces use this palette; unknown entries keep their normal theme text.
    internal static Color? NameColor(LootItemRarity rarity) => rarity != LootItemRarity.Unknown && Enum.IsDefined(rarity)
        ? Accent(rarity) : null;

    internal static string? NameCssColor(LootItemRarity rarity) => NameColor(rarity) is { } color
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null;

    internal static void PaintBackground(Graphics graphics, RectangleF bounds, LootItemRarity rarity,
        double seconds, bool animated)
    {
        if (!HasAnimatedBackground(rarity)) return;
        var strength = LightingStrength(rarity);
        PaintBackground(graphics, bounds, Accent(rarity), seconds, animated, strength,
            .4 + strength * .6, accentLine: true, particleCount: ParticleCount(rarity),
            ribbonCount: rarity == LootItemRarity.Rare ? 1 : rarity == LootItemRarity.Legend ? 2 : 3);
    }

    internal static bool HasAnimatedBackground(LootItemRarity rarity) => rarity is LootItemRarity.Rare
        or LootItemRarity.Legend or LootItemRarity.Unique or LootItemRarity.Epic or LootItemRarity.Mythic or LootItemRarity.Special;
    internal static int ParticleCount(LootItemRarity rarity) => rarity switch
    {
        LootItemRarity.Unique => 4, LootItemRarity.Epic => 8, LootItemRarity.Mythic => 13,
        LootItemRarity.Special => 18, _ => 0
    };
    internal static double LightingStrength(LootItemRarity rarity) => rarity switch
    {
        LootItemRarity.Rare => .23, LootItemRarity.Legend => .34, LootItemRarity.Unique => .52,
        LootItemRarity.Epic => .68, LootItemRarity.Mythic => .84, LootItemRarity.Special => 1, _ => 0
    };

    // Shared lighting only; callers choose their own semantics and treatment. Unit parameters
    // preserve the existing item-card pixels exactly, including the dust alpha truncation.
    internal static void PaintBackground(Graphics graphics, RectangleF bounds, Color accent,
        double seconds, bool animated, double strength = 1, double motionIntensity = 1, bool accentLine = true,
        int particleCount = 13, int ribbonCount = 3)
    {
        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0;
        motionIntensity = double.IsFinite(motionIntensity) ? Math.Clamp(motionIntensity, 0, 1) : 0;
        particleCount = Math.Clamp(particleCount, 0, 64);
        ribbonCount = Math.Clamp(ribbonCount, 0, 5);
        if (strength <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        int Alpha(int value) => (int)Math.Round(value * strength);
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            using var ground = new LinearGradientBrush(bounds, Color.FromArgb(Alpha(83), accent), Color.FromArgb(Alpha(10), accent), 0f);
            graphics.FillRectangle(ground, bounds);
            var phase = animated && double.IsFinite(seconds) ? seconds * motionIntensity : 0;
            var cycle = ((phase / 4.2) % 1 + 1) % 1;
            var start = bounds.Left - bounds.Width * .5f + (float)cycle * bounds.Width * 1.7f;
            // Three sloping, gently moving light ribbons refer to the flowing gold item-card background.
            for (var band = 0; band < ribbonCount; band++)
            {
                var offset = band * bounds.Height * .22f;
                using var ribbon = new Pen(Color.FromArgb(Alpha(40 - band * 9), accent), band == 0 ? 3f : 1.1f);
                graphics.DrawBezier(ribbon, start - 80, bounds.Bottom + offset,
                    start + bounds.Width * .22f, bounds.Top + bounds.Height * .65f + offset,
                    start + bounds.Width * .47f, bounds.Top + bounds.Height * .92f + offset,
                    start + bounds.Width + 70, bounds.Top - bounds.Height * .6f + offset);
            }
            for (var particle = 0; particle < particleCount; particle++)
            {
                var px = ((particle * .371 + phase * (.017 + particle % 3 * .002)) % 1 + 1) % 1;
                var py = ((particle * .617 - phase * .025) % 1 + 1) % 1;
                var light = .55 + .45 * Math.Sin(particle * 2.1 + phase * 1.6);
                using var dust = new SolidBrush(Color.FromArgb(Alpha((int)(70 * light)), accent));
                var radius = particle % 4 == 0 ? 1.5f : .85f;
                graphics.FillEllipse(dust, bounds.Left + (float)px * bounds.Width,
                    bounds.Top + (float)py * bounds.Height, radius, radius);
            }
            if (accentLine)
            {
                using var left = new Pen(Color.FromArgb(Alpha(160), accent), 2);
                graphics.DrawLine(left, bounds.Left + 1, bounds.Top + 3, bounds.Left + 1, bounds.Bottom - 3);
            }
        }
        finally { graphics.Restore(state); }
    }
}
