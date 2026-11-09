using System.Collections.Frozen;

namespace Soulcrest.App.Services;

/// <summary>Observed Genus Insight bindings only. They grant no character-stat, equipment or CP mapping.</summary>
public static class GenusInsightNativeBindings
{
    // User screenshots and the complete current own genus list agree on all 45 active slots (2026-11-09).
    // A captured Cogni/preset-3 roll and the resulting screenshot qualify six additional stat IDs.
    // Keep the exact observed set bounded; unrelated historical EStat ordinals remain unqualified.
    public static IReadOnlyDictionary<ushort, string> Stats { get; } = new Dictionary<ushort, string>
    {
        [19] = "FixingDamage",
        [31] = "WeaponDamage",
        [41] = "CriticalDamageDefense",
        [50] = "BossNpcAddDamage",
        [52] = "Defense",
        [56] = "PvEAddDamage",
        [57] = "PvEDamageDefense",
        [69] = "SealStoneAddDamage",
        [70] = "DecreaseDamage",
        [98] = "BackAttackDamage",
        [99] = "BackAttackDefense",
        [104] = "Accuracy",
        [110] = "PvEAccuracy",
        [128] = "Critical",
        [193] = "HPMax",
        [196] = "MaxHPRatio",
        [199] = "MPMax",
        [255] = "Block",
        [379] = "PvEAmplifyDamage",
        [396] = "IntellectEvasion",
        [397] = "IntellectCritical",
        [406] = "FeralCriticalResist",
        [410] = "NatureDefense",
        [422] = "TransCriticalResist",
        [443] = "HardHit",
        [445] = "IronWall",
        [587] = "FrontAttackDamage",
        [591] = "FrontAttackCritical",
    }.ToFrozenDictionary();

    /// <summary>The observed wire slots 0..8 correspond to visible slots 1..9.</summary>
    public static int? SlotOffset => 1;

    /// <summary>Visible preset 1 matches raw selected panel 1; this does not qualify live preset updates.</summary>
    public static byte? PresetOffset => 0;
}
