using System.Globalization;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

// Adapted from overlay-editor-kit 1.15.0: presentation-only OverlaySnapshot/OverlayMetric.
public sealed record OverlaySnapshot
{
    public string UiLanguage { get; init; } = "en";
    public string NameLanguage { get; init; } = "en";
    public string ThemeId { get; init; } = OverlayThemeDefinition.DefaultId;
    public DateTimeOffset ClockUtcNow { get; init; }
    public string? SessionId { get; init; }
    public bool SessionActive { get; init; }
    public bool TrackingAvailable { get; init; }
    public bool WaitingForWorldContext { get; init; }
    public TimeSpan SessionElapsed { get; init; }
    public bool ItemAnimationsEnabled { get; init; } = true;
    public IReadOnlyList<OverlayLootItem> Drops { get; init; } = [];
    public IReadOnlyList<OverlayLootItem> ItemCatalog { get; init; } = [];
    public long? DailyKina { get; init; }
    public bool DailyConfirmed { get; init; }
    public long DailyLimit { get; init; } = 1_000_000;
    public OverlayDailyKinaProgress DailyProgress { get; init; } = new("—", "", "—", null);
    public OverlayRareNotice? RareNotice { get; init; }
    public GenusInsightSnapshot GenusInsight { get; init; } = GenusInsightSnapshot.Empty;
    public AbyssRankOverlaySnapshot Abyss { get; init; } = AbyssRankOverlaySnapshot.Empty;
    public IReadOnlyDictionary<string, OverlayMetric> Metrics { get; init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, OverlayMetric>(new Dictionary<string, OverlayMetric>());
    public static OverlaySnapshot Demo => DemoFor("en");
    /// <summary>Deterministic examples for pure UI rendering; no service, capture or online request is created.</summary>
    public static OverlaySnapshot DemoFor(string? language) => OverlayEditorPresentation.Examples(
        new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, language == "de" ? "de" : "en");

    internal static OverlayMetric LegacyExampleMetric(string? language)
    {
        var german = language == "de";
        var culture = CultureInfo.GetCultureInfo(german ? "de-DE" : "en-US");
        return new(german ? "Beispielwert" : "Example value", 1234.ToString("N0", culture),
            german ? "Beispieldaten" : "Sample data");
    }
}

public enum OverlayMetricTone { Default, Muted, Positive, Accent }
public sealed record OverlayMetric(string Label, string Value, string? Detail = null, bool IsWarning = false,
    OverlayMetricTone Tone = OverlayMetricTone.Default, string? Tooltip = null, string? HistoricalAge = null);

/// <summary>Read-only exact drop or list-only equipment summary. Zero catalog choices are not receipts.</summary>
public sealed record OverlayLootItem(string Key, string Name, string QuantityText, string? IconPath = null,
    bool IsRare = false, long Quantity = 0, string? Tooltip = null, LootItemRarity Rarity = LootItemRarity.Unknown,
    bool IsEquipmentGroup = false);

public sealed record OverlayDailyKinaProgress(string Value, string Detail, string Percentage, double? Fraction,
    double? SessionStartFraction = null, double? SessionFraction = null);

/// <summary>Read-only visual receipt. Reading or rendering it cannot trigger audio.</summary>
public sealed record OverlayRareNotice(string Id, string Name, long Quantity, string QuantityText,
    string? IconPath, LootItemRarity Rarity, double ElapsedSeconds, string? Tooltip = null)
{
    public string? LootKey { get; init; }
}
