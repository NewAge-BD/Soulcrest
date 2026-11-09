using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

public sealed record GenusInsightSlotView
{
    public byte Slot { get; init; }
    public string? StatId { get; init; }
    public string LabelDe { get; init; } = "Unbekannter Stat";
    public string LabelEn { get; init; } = "Unknown stat";
    public decimal? Value { get; init; }
    public string? Unit { get; init; }
    public byte? Rank { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public double? RollFraction { get; init; }
    public bool IsMaxRoll { get; init; }
    public bool IsFavorite { get; init; }
    public bool? IsLocked { get; init; }
    public bool? IsActivated { get; init; }
    public bool IsCurrent { get; init; }
}

public sealed record GenusInsightBoardView(string Genus, int? Level, IReadOnlyList<GenusInsightSlotView> Slots);
public sealed record GenusInsightAppliedEffect(string StatId, string LabelDe, string LabelEn, string Unit, decimal? Value);

/// <summary>Shared roll display with a character-specific preset and separate live or saved authority.</summary>
public sealed record GenusInsightSnapshot
{
    public string? ProfileId { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsHistorical { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public byte? Preset { get; init; }
    public IReadOnlyList<GenusInsightBoardView> Boards { get; init; } = [];
    public IReadOnlyList<GenusInsightAppliedEffect> AppliedEffects { get; init; } = [];
    public CombatKnowledgeStatus AppliedEffectsStatus { get; init; } = CombatKnowledgeStatus.Unknown;
    public bool? MenuVisible { get; init; }
    public string? SelectedGenus { get; init; }
    public byte? ViewedPreset { get; init; }
    public GenusInsightBoardView? ViewedBoard { get; init; }
    public long MenuSourceGeneration { get; init; }
    public static GenusInsightSnapshot Empty { get; } = new();
}

public readonly record struct GenusInsightFavoriteKey(string Genus, byte Slot, string StatId);

public static class GenusInsightPresentation
{
    public static IReadOnlyList<string> Genera { get; } = Array.AsReadOnly(new[] { "cogni", "fera", "natura", "varian", "special" });
    public static bool KnownGenus(string? genus) => genus is "cogni" or "fera" or "natura" or "varian" or "special";
    public static string GenusLabel(string genus) => genus switch
    {
        "cogni" => "Cogni", "fera" => "Fera", "natura" => "Natura", "varian" => "Varian", "special" => "Special", _ => "—"
    };
    public static string RankColor(byte? rank) => rank switch
    {
        1 => "#f4f4f1", 2 => "#39db63", 3 => "#2fa9ff", 4 => "#f4cf35", 5 => "#ff8b22", _ => "#99a8bc"
    };
    public static string RankName(byte? rank) => rank switch
    {
        1 => "Common", 2 => "Rare", 3 => "Epic", 4 => "Unique", 5 => "Heroic", _ => "—"
    };
    public static string IconPath(string genus) => KnownGenus(genus) ? "icons/combat/genus-insight/" + genus + ".png" : "";
}
