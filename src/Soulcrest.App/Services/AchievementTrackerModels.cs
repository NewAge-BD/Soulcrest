using System.Collections.Immutable;
using Soulcrest.App.Components;

namespace Soulcrest.App.Services;

public enum AchievementTrackerRowState { Unknown, InProgress, Celebrating, AwaitingClaim }
public sealed record AchievementTrackerReward(string NameDe, string NameEn, string? IconPath, long? Quantity)
{
    public int? ItemId { get; init; }
}

/// <summary>One frozen family row. Catalogue labels do not qualify a goal or live completion.</summary>
public sealed record AchievementTrackerRow
{
    public int FamilyId { get; init; }
    public int? StageId { get; init; }
    public int? TrackedTitleId { get; init; }
    public string? TitleNameDe { get; init; }
    public string? TitleNameEn { get; init; }
    public bool IsTitlePlaceholder { get; init; }
    public string NameDe { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string ConditionDe { get; init; } = "";
    public string ConditionEn { get; init; } = "";
    public string? IconPath { get; init; }
    public bool IsFavorite { get; init; }
    /// <summary>Last known progress for the confirmed own character, awaiting fresh network evidence.</summary>
    public bool IsHistorical { get; init; }
    public AchievementTrackerRowState State { get; init; }
    public long? Current { get; init; }
    public long? Target { get; init; }
    public decimal? DisplayCurrent { get; init; }
    public decimal? DisplayTarget { get; init; }
    public int? RawValue { get; init; }
    public CombatAchievementProgressUnit Unit { get; init; } = CombatAchievementProgressUnit.Unknown;
    public ImmutableArray<AchievementTrackerReward> Rewards { get; init; } = [];
    /// <summary>Open rewards may belong to earlier stages than the currently displayed progress goal.</summary>
    public bool HasUnclaimedRewards { get; init; }
    /// <summary>Animation provenance only; never replaces the current stage's labels, goal or rewards.</summary>
    public int? CelebratedStageId { get; init; }
    /// <summary>Elapsed fraction of the fixed 60-second completion animation, not stat progress.</summary>
    public double CompletionProgress { get; init; }
    public double AnimationProgress => CompletionProgress;
    public bool HasValidDisplayPair => IsValidDisplayPair(DisplayCurrent, DisplayTarget);
    public decimal? PresentationCurrent => HasQualifiedGoal ? HasValidDisplayPair ? DisplayCurrent : Current : null;
    public decimal? PresentationTarget => HasQualifiedGoal ? HasValidDisplayPair ? DisplayTarget : Target : null;
    public double? ProgressFraction => PresentationCurrent is { } current && PresentationTarget is { } target
        ? Math.Clamp((double)current / (double)target, 0, 1) : null;
    public bool IsAnimating => !IsHistorical && State == AchievementTrackerRowState.Celebrating;
    public bool IsCompleted => !IsHistorical && (State == AchievementTrackerRowState.AwaitingClaim
        || State == AchievementTrackerRowState.Celebrating && (CelebratedStageId is null
            || HasQualifiedGoal && Current!.Value >= Target!.Value));
    public bool HasQualifiedGoal => Current is >= 0 && Target is > 0 && Unit != CombatAchievementProgressUnit.Unknown;
    public static bool IsValidDisplayPair(decimal? current, decimal? target) => current is >= 0 && target is > 0;
}

/// <summary>Current-own presentation only. Timers and rows are transient and are never persisted.</summary>
public sealed record AchievementTrackerSnapshot(AchievementProfileBinding? Binding,
    ImmutableArray<AchievementTrackerRow> Rows)
{
    public bool IsLive { get; init; }
    public bool HasContent => !Rows.IsDefaultOrEmpty;
    public static AchievementTrackerSnapshot Empty { get; } = new(null, []);
}
