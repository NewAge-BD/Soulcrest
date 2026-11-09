using System.Collections.Immutable;

namespace Soulcrest.Core.Achievements;

/// <summary>Pure semantic evaluation. It observes no clock, character store, transport or game state.</summary>
public static class AchievementSemanticEngine
{
    public static AchievementSemanticEvaluation Evaluate(AchievementSemanticRule? rule, AchievementSemanticInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = ImmutableArray.CreateBuilder<AchievementEvaluationIssue>();
        if (!input.PeriodQualified)
        {
            issues.Add(AchievementEvaluationIssue.UnqualifiedPeriod);
            return new(input, AchievementTruth.Unknown, AchievementTruth.Unknown, AchievementTruth.Unknown,
                AchievementTruth.Unknown, null, null, null, issues.ToImmutable());
        }

        decimal? progress = null;
        decimal? target = null;
        string? unit = null;
        var requirements = AchievementTruth.Unknown;
        var qualified = rule is { IsQualified: true };
        if (!qualified) issues.Add(AchievementEvaluationIssue.UnqualifiedRule);
        else if (string.IsNullOrWhiteSpace(rule!.ContractId) || rule.Version <= 0)
        {
            qualified = false;
            issues.Add(AchievementEvaluationIssue.InvalidContract);
        }
        else if (!Enum.IsDefined(rule.Kind) || rule.Kind == AchievementSemanticKind.Unknown)
        {
            qualified = false;
            issues.Add(AchievementEvaluationIssue.UnknownSemantics);
        }
        else if (rule.Kind == AchievementSemanticKind.Dependency)
        {
            // DependencyRequirements must come from separately qualified explicit edges, not a
            // comma-separated argument, matching text or a guessed reference inside another row.
            requirements = input.DependencyRequirements;
        }
        else if (rule.Target < 0 || !Enum.IsDefined(rule.Comparison)
            || rule.Kind == AchievementSemanticKind.Experience && decimal.Truncate(rule.Target) != rule.Target)
        {
            issues.Add(AchievementEvaluationIssue.InvalidTarget);
        }
        else if (input.RawCounter is not { } counter) issues.Add(AchievementEvaluationIssue.MissingCounter);
        else if (counter < 0) issues.Add(AchievementEvaluationIssue.NegativeCounter);
        else
        {
            progress = counter;
            target = rule.Target;
            if (rule.Kind == AchievementSemanticKind.Duration)
            {
                if (rule.Duration is not { } duration || Milliseconds(duration.WireUnit) is not { } wire
                    || Milliseconds(duration.DisplayUnit) is not { } display)
                {
                    progress = target = null;
                    issues.Add(AchievementEvaluationIssue.MissingDurationContract);
                }
                else
                {
                    progress = counter * wire / display;
                    unit = duration.DisplayUnit.ToString();
                }
            }
            else if (rule.Kind == AchievementSemanticKind.Experience)
            {
                progress = ExperienceLevel(rule.Experience, counter, issues);
                unit = "Level";
                if (progress is null) target = null;
            }
            if (progress is { } value) requirements = Compare(value, rule.Target, rule.Comparison);
        }

        // Earned completion is durable in this qualified period. A reversible current value can
        // establish requirements now, but can neither erase past completion nor imply never earned.
        var counterEarned = qualified && rule!.CounterCanConfirmCompletion
            && requirements == AchievementTruth.True;
        var claimEarned = qualified && rule!.ClaimConfirmsCompletion && input.Claimed == AchievementTruth.True;
        var positive = input.PreviouslyEarned == AchievementTruth.True || input.ExplicitEarned == AchievementTruth.True
            || counterEarned || claimEarned;
        var monotone = qualified && rule!.MonotoneCounterQualified && rule.Kind is not
            (AchievementSemanticKind.Unknown or AchievementSemanticKind.CurrentState or AchievementSemanticKind.Dependency);
        var qualifiedDeficit = monotone && rule!.Comparison == AchievementTargetComparison.AtLeast
            && requirements == AchievementTruth.False;
        var counterNeverEarned = qualifiedDeficit && rule!.CounterCanConfirmCompletion;
        var earned = positive ? AchievementTruth.True : input.ExplicitEarned == AchievementTruth.False
            || input.PreviouslyEarned == AchievementTruth.False || counterNeverEarned
                ? AchievementTruth.False : AchievementTruth.Unknown;

        if (qualifiedDeficit)
        {
            if (input.Claimed == AchievementTruth.True) issues.Add(AchievementEvaluationIssue.CounterBelowClaimedTarget);
            if (input.PreviouslyEarned == AchievementTruth.True || input.ExplicitEarned == AchievementTruth.True)
                issues.Add(AchievementEvaluationIssue.CounterBelowEarnedTarget);
        }
        if (input.ExplicitEarned == AchievementTruth.False && positive)
            issues.Add(AchievementEvaluationIssue.ContradictoryCompletionFact);

        return new(input, requirements, earned, input.Claimed, input.Availability, progress, target, unit, issues.ToImmutable());
    }

    private static AchievementTruth Compare(decimal value, decimal target, AchievementTargetComparison comparison) =>
        (comparison switch
        {
            AchievementTargetComparison.AtLeast => value >= target,
            AchievementTargetComparison.Exactly => value == target,
            AchievementTargetComparison.AtMost => value <= target,
            _ => false
        }) ? AchievementTruth.True : AchievementTruth.False;

    private static decimal? Milliseconds(AchievementDurationUnit unit) => unit switch
    {
        AchievementDurationUnit.Milliseconds => 1m,
        AchievementDurationUnit.Seconds => 1000m,
        AchievementDurationUnit.Minutes => 60_000m,
        AchievementDurationUnit.Hours => 3_600_000m,
        _ => null
    };

    private static decimal? ExperienceLevel(AchievementExperienceCurve? curve, long experience,
        ImmutableArray<AchievementEvaluationIssue>.Builder issues)
    {
        if (curve is null || curve.Thresholds.IsDefaultOrEmpty)
        {
            issues.Add(AchievementEvaluationIssue.InvalidExperienceCurve);
            return null;
        }
        var previousLevel = -1;
        var previousExperience = -1L;
        foreach (var threshold in curve.Thresholds)
        {
            if (threshold is null || threshold.Level < 0 || threshold.Level <= previousLevel
                || previousLevel >= 0 && threshold.Level != previousLevel + 1
                || threshold.MinimumExperience < 0 || threshold.MinimumExperience <= previousExperience)
            {
                issues.Add(AchievementEvaluationIssue.InvalidExperienceCurve);
                return null;
            }
            previousLevel = threshold.Level;
            previousExperience = threshold.MinimumExperience;
        }
        if (experience < curve.Thresholds[0].MinimumExperience
            || experience > curve.Thresholds[^1].MinimumExperience && !curve.FinalLevelIsQualifiedMaximum)
        {
            issues.Add(AchievementEvaluationIssue.ExperienceOutsideQualifiedCurve);
            return null;
        }
        for (var index = curve.Thresholds.Length - 1; index >= 0; index--)
            if (experience >= curve.Thresholds[index].MinimumExperience) return curve.Thresholds[index].Level;
        return null;
    }
}
