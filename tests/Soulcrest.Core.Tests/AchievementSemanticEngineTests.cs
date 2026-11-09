using Soulcrest.Core.Achievements;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AchievementSemanticEngineTests
{
    [Fact]
    public void QualificationAndCompletionEvidenceAreIndependentExplicitRequirements()
    {
        var rule = Rule() with { IsQualified = false };
        var raw = Input(5);
        var unknown = AchievementSemanticEngine.Evaluate(rule, raw);
        Assert.Equal(AchievementTruth.Unknown, unknown.RequirementsMetNow);
        Assert.Equal(AchievementTruth.Unknown, unknown.EarnedCompletion);
        Assert.Null(unknown.ProgressValue);
        Assert.Contains(AchievementEvaluationIssue.UnqualifiedRule, unknown.Issues);

        var conditionOnly = AchievementSemanticEngine.Evaluate(rule with { IsQualified = true }, raw);
        Assert.Equal(AchievementTruth.True, conditionOnly.RequirementsMetNow);
        Assert.Equal(AchievementTruth.Unknown, conditionOnly.EarnedCompletion);
        var completed = AchievementSemanticEngine.Evaluate(rule with { IsQualified = true, CounterCanConfirmCompletion = true }, raw);
        Assert.Equal(AchievementTruth.True, completed.EarnedCompletion);
        Assert.Equal(AchievementTruth.Unknown, completed.Claimed);
    }

    [Fact]
    public void IndependentClaimIsPreservedButClaimToCompletionNeedsAQualifiedRelation()
    {
        var raw = Input(null) with { Claimed = AchievementTruth.True };
        var withoutCounterRule = AchievementSemanticEngine.Evaluate(null, raw);
        Assert.Equal(AchievementTruth.True, withoutCounterRule.Claimed);
        Assert.Equal(AchievementTruth.Unknown, withoutCounterRule.EarnedCompletion);
        var claimOnly = AchievementSemanticEngine.Evaluate(Rule() with
        {
            IsQualified = false, Kind = AchievementSemanticKind.Unknown, ClaimConfirmsCompletion = true
        }, raw);
        Assert.Equal(AchievementTruth.Unknown, claimOnly.EarnedCompletion);
        Assert.Equal(AchievementTruth.Unknown, claimOnly.RequirementsMetNow);
        Assert.False(claimOnly.HasConflict);
        var qualifiedClaimRelation = AchievementSemanticEngine.Evaluate(Rule() with { ClaimConfirmsCompletion = true }, raw);
        Assert.Equal(AchievementTruth.True, qualifiedClaimRelation.EarnedCompletion);
        Assert.Equal(AchievementTruth.Unknown, qualifiedClaimRelation.RequirementsMetNow);
    }

    [Theory]
    [InlineData(null, AchievementEvaluationIssue.MissingCounter)]
    [InlineData(-1L, AchievementEvaluationIssue.NegativeCounter)]
    [InlineData(long.MinValue, AchievementEvaluationIssue.NegativeCounter)]
    public void MissingAndNegativeCountersRemainUnknownWhileRawAndIndependentClaimsAreRetained(long? counter,
        AchievementEvaluationIssue issue)
    {
        var raw = Input(counter) with { CompleteTime = long.MaxValue, Claimed = AchievementTruth.True };
        var result = AchievementSemanticEngine.Evaluate(Rule() with
        {
            CounterCanConfirmCompletion = true, MonotoneCounterQualified = true, ClaimConfirmsCompletion = true
        }, raw);
        Assert.Same(raw, result.Raw);
        Assert.Equal(counter, result.Raw.RawCounter);
        Assert.Equal(long.MaxValue, result.Raw.CompleteTime);
        Assert.Equal(AchievementTruth.Unknown, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
        Assert.Contains(issue, result.Issues);
        Assert.False(result.HasConflict);
    }

    [Fact]
    public void ConfirmedZeroIsDistinctFromAMissingRowAndOpaqueCompleteTimeNeverProvesCompletion()
    {
        var rule = Rule() with { CounterCanConfirmCompletion = true, MonotoneCounterQualified = true };
        var zero = AchievementSemanticEngine.Evaluate(rule, Input(0) with { CompleteTime = long.MaxValue });
        var absent = AchievementSemanticEngine.Evaluate(rule, Input(null) with { CompleteTime = long.MaxValue });
        Assert.Equal(AchievementTruth.False, zero.RequirementsMetNow);
        Assert.Equal(AchievementTruth.False, zero.EarnedCompletion);
        Assert.Equal(AchievementTruth.Unknown, absent.RequirementsMetNow);
        Assert.Equal(AchievementTruth.Unknown, absent.EarnedCompletion);
    }

    [Theory]
    [InlineData(AchievementSemanticKind.CumulativeCount)]
    [InlineData(AchievementSemanticKind.Quantity)]
    [InlineData(AchievementSemanticKind.BestValue)]
    [InlineData(AchievementSemanticKind.ResultCount)]
    [InlineData(AchievementSemanticKind.Membership)]
    public void CumulativeDeficitConflictsOnlyWhenMonotonicityWasSeparatelyQualified(AchievementSemanticKind kind)
    {
        var rule = Rule() with { Kind = kind, CounterCanConfirmCompletion = true, ClaimConfirmsCompletion = true };
        var raw = Input(4) with { Claimed = AchievementTruth.True, PreviouslyEarned = AchievementTruth.True };
        var ordinary = AchievementSemanticEngine.Evaluate(rule, raw);
        Assert.False(ordinary.HasConflict);
        var monotone = AchievementSemanticEngine.Evaluate(rule with { MonotoneCounterQualified = true }, raw);
        Assert.True(monotone.HasConflict);
        Assert.Contains(AchievementEvaluationIssue.CounterBelowClaimedTarget, monotone.Issues);
        Assert.Contains(AchievementEvaluationIssue.CounterBelowEarnedTarget, monotone.Issues);
        Assert.Equal(AchievementTruth.True, monotone.EarnedCompletion);
    }

    [Fact]
    public void AReversibleStateCannotEraseEarnedCompletionOrTurnAClaimIntoACounterConflict()
    {
        var rule = Rule() with { Kind = AchievementSemanticKind.CurrentState, CounterCanConfirmCompletion = true,
            MonotoneCounterQualified = true, ClaimConfirmsCompletion = true };
        var earned = Input(7) with { Claimed = AchievementTruth.True, PreviouslyEarned = AchievementTruth.True };
        Assert.Equal(AchievementTruth.True, AchievementSemanticEngine.Evaluate(rule, earned).RequirementsMetNow);
        var result = AchievementSemanticEngine.Evaluate(rule, earned with { RawCounter = 2 });
        Assert.Equal(AchievementTruth.False, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
        Assert.Equal(AchievementTruth.True, result.Claimed);
        Assert.False(result.HasConflict);
        Assert.Equal(AchievementTruth.Unknown, AchievementSemanticEngine.Evaluate(rule, Input(2)).EarnedCompletion);
    }

    [Fact]
    public void ExactTargetOvershootDoesNotContradictADurableCompletion()
    {
        var result = AchievementSemanticEngine.Evaluate(Rule() with
        {
            Comparison = AchievementTargetComparison.Exactly, CounterCanConfirmCompletion = true, MonotoneCounterQualified = true
        }, Input(6) with { PreviouslyEarned = AchievementTruth.True, Claimed = AchievementTruth.True });
        Assert.Equal(AchievementTruth.False, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
        Assert.False(result.HasConflict);
    }

    [Theory]
    [InlineData(AchievementDurationUnit.Minutes, 10L, 10, AchievementTruth.True)]
    [InlineData(AchievementDurationUnit.Seconds, 600L, 10, AchievementTruth.True)]
    [InlineData(AchievementDurationUnit.Milliseconds, 599999L, 10, AchievementTruth.False)]
    public void DurationUsesOnlyExplicitUnitsWithoutThresholdRounding(AchievementDurationUnit wireUnit, long raw,
        int target, AchievementTruth met)
    {
        var result = AchievementSemanticEngine.Evaluate(Rule() with
        {
            Kind = AchievementSemanticKind.Duration, Target = target,
            Duration = new(wireUnit, AchievementDurationUnit.Minutes)
        }, Input(raw));
        Assert.Equal(met, result.RequirementsMetNow);
        Assert.Equal("Minutes", result.DisplayUnit);
        Assert.Equal(raw, result.Raw.RawCounter);
        if (met == AchievementTruth.True) Assert.Equal(10m, result.ProgressValue);
        else Assert.True(result.ProgressValue!.Value < 10m);
    }

    [Theory]
    [InlineData(AchievementDurationUnit.Unknown, AchievementDurationUnit.Minutes)]
    [InlineData(AchievementDurationUnit.Seconds, AchievementDurationUnit.Unknown)]
    public void UnqualifiedDurationUnitsDoNotInferSecondsOrMinutes(AchievementDurationUnit wireUnit,
        AchievementDurationUnit displayUnit)
    {
        var result = AchievementSemanticEngine.Evaluate(Rule() with
        {
            Kind = AchievementSemanticKind.Duration, Duration = new(wireUnit, displayUnit)
        }, Input(600));
        Assert.Equal(AchievementTruth.Unknown, result.RequirementsMetNow);
        Assert.Null(result.ProgressValue);
        Assert.Contains(AchievementEvaluationIssue.MissingDurationContract, result.Issues);
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(99L, 1)]
    [InlineData(100L, 2)]
    [InlineData(299L, 2)]
    [InlineData(300L, 3)]
    [InlineData(999L, 3)]
    public void ExperienceIsMappedThroughTheQualifiedThresholdCurve(long raw, int level)
    {
        var result = AchievementSemanticEngine.Evaluate(Rule() with
        {
            Kind = AchievementSemanticKind.Experience, Target = 2, Experience = Curve()
        }, Input(raw));
        Assert.Equal((decimal)level, result.ProgressValue);
        Assert.Equal(level >= 2 ? AchievementTruth.True : AchievementTruth.False, result.RequirementsMetNow);
        Assert.Equal("Level", result.DisplayUnit);
    }

    [Fact]
    public void MissingMalformedAndPartialExperienceCurvesRemainUnknownOutsideTheirProof()
    {
        var rule = Rule() with { Kind = AchievementSemanticKind.Experience, Target = 2 };
        Assert.Contains(AchievementEvaluationIssue.InvalidExperienceCurve,
            AchievementSemanticEngine.Evaluate(rule, Input(100)).Issues);
        var bad = new AchievementExperienceCurve([new(1, 0), new(3, 100)]);
        Assert.Contains(AchievementEvaluationIssue.InvalidExperienceCurve,
            AchievementSemanticEngine.Evaluate(rule with { Experience = bad }, Input(100)).Issues);
        bad = new([new(1, 0), new(2, 0)]);
        Assert.Contains(AchievementEvaluationIssue.InvalidExperienceCurve,
            AchievementSemanticEngine.Evaluate(rule with { Experience = bad }, Input(100)).Issues);
        var partial = Curve() with { FinalLevelIsQualifiedMaximum = false };
        Assert.Equal(3m, AchievementSemanticEngine.Evaluate(rule with { Experience = partial }, Input(300)).ProgressValue);
        var outside = AchievementSemanticEngine.Evaluate(rule with { Experience = partial }, Input(301));
        Assert.Equal(AchievementTruth.Unknown, outside.RequirementsMetNow);
        Assert.Contains(AchievementEvaluationIssue.ExperienceOutsideQualifiedCurve, outside.Issues);
        var startsLater = new AchievementExperienceCurve([new(10, 1000), new(11, 2000)]);
        Assert.Contains(AchievementEvaluationIssue.ExperienceOutsideQualifiedCurve,
            AchievementSemanticEngine.Evaluate(rule with { Experience = startsLater }, Input(999)).Issues);
    }

    [Fact]
    public void AnUnqualifiedPeriodNeverInfersAResetOrAssignsFactsToCurrentContent()
    {
        var raw = Input(0) with { PeriodQualified = false, PreviouslyEarned = AchievementTruth.True,
            ExplicitEarned = AchievementTruth.True, Claimed = AchievementTruth.True, Availability = AchievementTruth.True };
        var result = AchievementSemanticEngine.Evaluate(Rule() with { CounterCanConfirmCompletion = true }, raw);
        Assert.Same(raw, result.Raw);
        Assert.Equal(AchievementTruth.Unknown, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.Unknown, result.EarnedCompletion);
        Assert.Equal(AchievementTruth.Unknown, result.Claimed);
        Assert.Equal(AchievementTruth.Unknown, result.Available);
        Assert.Contains(AchievementEvaluationIssue.UnqualifiedPeriod, result.Issues);
    }

    [Fact]
    public void AvailabilityAndContradictoryCompletionFactsDoNotErasePositiveEarnedEvidence()
    {
        var result = AchievementSemanticEngine.Evaluate(Rule(), Input(0) with
        {
            Availability = AchievementTruth.False, PreviouslyEarned = AchievementTruth.True,
            ExplicitEarned = AchievementTruth.False, Claimed = AchievementTruth.False
        });
        Assert.Equal(AchievementTruth.False, result.Available);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
        Assert.Equal(AchievementTruth.False, result.Claimed);
        Assert.Contains(AchievementEvaluationIssue.ContradictoryCompletionFact, result.Issues);
    }

    [Fact]
    public void DependenciesConsumeQualifiedThreeValuedResultsWithoutInventingCounters()
    {
        var rule = Rule() with { Kind = AchievementSemanticKind.Dependency, CounterCanConfirmCompletion = true };
        var result = AchievementSemanticEngine.Evaluate(rule, Input(999) with { DependencyRequirements = AchievementTruth.Unknown });
        Assert.Equal(AchievementTruth.Unknown, result.RequirementsMetNow);
        Assert.Null(result.ProgressValue);
        result = AchievementSemanticEngine.Evaluate(rule, Input(null) with { DependencyRequirements = AchievementTruth.True });
        Assert.Equal(AchievementTruth.True, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
    }

    [Fact]
    public void InvalidBindingAndUnknownKindCannotGrantCounterAuthority()
    {
        Assert.Contains(AchievementEvaluationIssue.InvalidContract,
            AchievementSemanticEngine.Evaluate(Rule() with { Version = 0 }, Input(999)).Issues);
        var result = AchievementSemanticEngine.Evaluate(Rule() with { Kind = AchievementSemanticKind.Unknown },
            Input(999) with { ExplicitEarned = AchievementTruth.True });
        Assert.Equal(AchievementTruth.Unknown, result.RequirementsMetNow);
        Assert.Equal(AchievementTruth.True, result.EarnedCompletion);
        Assert.Contains(AchievementEvaluationIssue.UnknownSemantics, result.Issues);
    }

    private static AchievementSemanticRule Rule() => new("synthetic-qualified-family-stage", 1, AchievementSemanticKind.CumulativeCount, 5)
    { IsQualified = true };
    private static AchievementSemanticInput Input(long? counter) => new(counter) { PeriodQualified = true };
    private static AchievementExperienceCurve Curve() => new([new(1, 0), new(2, 100), new(3, 300)])
    { FinalLevelIsQualifiedMaximum = true };
}
