using System.Collections.Immutable;

namespace Soulcrest.Core.Achievements;

public enum AchievementTruth { Unknown, False, True }

public enum AchievementSemanticKind
{
    Unknown, CumulativeCount, Quantity, CurrentState, BestValue, Duration, Experience, ResultCount,
    Membership, Dependency
}

public enum AchievementTargetComparison { AtLeast, Exactly, AtMost }
public enum AchievementDurationUnit { Unknown, Milliseconds, Seconds, Minutes, Hours }

public enum AchievementEvaluationIssue
{
    UnqualifiedPeriod, UnqualifiedRule, UnknownSemantics, InvalidContract, MissingCounter, NegativeCounter,
    InvalidTarget, MissingDurationContract, InvalidExperienceCurve, ExperienceOutsideQualifiedCurve,
    CounterBelowClaimedTarget, CounterBelowEarnedTarget, ContradictoryCompletionFact
}

public sealed record AchievementDurationContract(AchievementDurationUnit WireUnit, AchievementDurationUnit DisplayUnit);
public sealed record AchievementExperienceThreshold(int Level, long MinimumExperience);

/// <summary>
/// Explicit cumulative XP thresholds. A non-terminal final threshold proves only its exact boundary;
/// higher XP cannot be assigned to that last level without the next threshold or a qualified cap.
/// </summary>
public sealed record AchievementExperienceCurve(ImmutableArray<AchievementExperienceThreshold> Thresholds)
{
    public bool FinalLevelIsQualifiedMaximum { get; init; }
}

/// <summary>
/// Concrete family/stage/context binding belongs to the caller's versioned catalogue. No kind, token,
/// text, target or default instance grants qualification. Membership means a separately qualified
/// distinct-member counter, never an inferred pet/title collection or repeated acquisition count.
/// </summary>
public sealed record AchievementSemanticRule(string ContractId, int Version, AchievementSemanticKind Kind, decimal Target)
{
    public bool IsQualified { get; init; }
    public AchievementTargetComparison Comparison { get; init; } = AchievementTargetComparison.AtLeast;
    public bool CounterCanConfirmCompletion { get; init; }
    public bool MonotoneCounterQualified { get; init; }
    public bool ClaimConfirmsCompletion { get; init; }
    public AchievementDurationContract? Duration { get; init; }
    public AchievementExperienceCurve? Experience { get; init; }
}

/// <summary>
/// Claimed and explicit/past earned facts must already be independently qualified for the same
/// stage and content period. Neither CompleteTime nor a raw reward ordinal is interpreted here.
/// Missing counter is unknown, including when a complete wire list omitted that family.
/// </summary>
public sealed record AchievementSemanticInput(long? RawCounter)
{
    public long? CompleteTime { get; init; }
    public bool PeriodQualified { get; init; }
    public AchievementTruth Claimed { get; init; }
    public AchievementTruth PreviouslyEarned { get; init; }
    public AchievementTruth ExplicitEarned { get; init; }
    public AchievementTruth Availability { get; init; }
    public AchievementTruth DependencyRequirements { get; init; }
}

public sealed record AchievementSemanticEvaluation(AchievementSemanticInput Raw,
    AchievementTruth RequirementsMetNow, AchievementTruth EarnedCompletion, AchievementTruth Claimed,
    AchievementTruth Available, decimal? ProgressValue, decimal? TargetValue, string? DisplayUnit,
    ImmutableArray<AchievementEvaluationIssue> Issues)
{
    public bool HasConflict => Issues.Any(issue => issue is AchievementEvaluationIssue.CounterBelowClaimedTarget
        or AchievementEvaluationIssue.CounterBelowEarnedTarget or AchievementEvaluationIssue.ContradictoryCompletionFact);
}
