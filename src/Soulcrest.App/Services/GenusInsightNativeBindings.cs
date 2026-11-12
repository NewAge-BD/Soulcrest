using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

/// <summary>Genus display bindings. Shared names grant no equipment or combat-score formula.</summary>
public static class GenusInsightNativeBindings
{
    // User request 2026-11-12: share table-checked name recognition with the Stats tab.
    // The Genus projection still requires the exact slot, grade and raw value to fit its roll pool.
    public static IReadOnlyDictionary<ushort, string> Stats => NativeStatDisplayBindings.Stats;

    /// <summary>The observed wire slots 0..8 correspond to visible slots 1..9.</summary>
    public static int? SlotOffset => 1;

    /// <summary>Visible preset 1 matches raw selected panel 1; this does not qualify live preset updates.</summary>
    public static byte? PresetOffset => 0;
}
