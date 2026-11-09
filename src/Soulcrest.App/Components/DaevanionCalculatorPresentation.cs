using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Components;

public sealed record DaevanionPriorityDisplayRow(string Key, string Label, int Rank,
    decimal Weight, bool Ignored, int NodeCount, string? IconPath = null)
{
    public int? CurrentLevel { get; init; }
    public int? CurrentBoardBonus { get; init; }
    public int? TargetLevel { get; init; }
    public bool Required { get; init; }
    public int RequiredNodeCount { get; init; }
    public IReadOnlyList<DaevanionCostVariant> CostVariants { get; init; } = [];
    public string CostColorClass => CostVariants.Select(variant => variant.Cost).Distinct().ToArray() is [var cost]
        ? "dc-cost-" + cost : "";
}

public sealed record DaevanionCostVariant(int Cost, string DisplayAmount, int NodeCount);

public sealed record DaevanionPriorityMove(DaevanionPriorityKind Kind, string Key,
    string? BeforeKey, bool Ignored, string? AfterKey = null, bool Required = false);

public enum DaevanionSkillEditField { CurrentLevel, CurrentBoardBonus, TargetLevel }

public sealed record DaevanionSkillEdit(string Key, DaevanionSkillEditField Field, int? Value);
