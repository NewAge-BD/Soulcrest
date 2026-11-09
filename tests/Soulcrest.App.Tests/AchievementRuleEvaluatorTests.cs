using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Achievements;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementRuleEvaluatorTests
{
    [Fact]
    public void ExplicitPermanentClaimHistoryIsIndependentOfARegressedRawPrefix()
    {
        const int familyId = 444100500;
        var stages = CombatAchievementProgressContractCatalog.Default.FindFamily(familyId)!.Families.Single(family => family.Id == familyId).Stages;
        var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(familyId, new(0, 0, 0)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var family = new AchievementRuleEvaluator().Evaluate(raw, AchievementDefinitionCatalog.Default.MetadataBuild, 1, 1,
            knownClaimedStageIds: ImmutableHashSet.Create(stages[0].Id, stages[1].Id)).Find(familyId)!;
        Assert.Equal(stages[2].Id, family.CurrentStageId);
        Assert.True(family.Find(stages[0].Id)!.Claimed); Assert.True(family.Find(stages[1].Id)!.Claimed);
        Assert.True(family.Find(stages[0].Id)!.Completed); Assert.True(family.Find(stages[1].Id)!.Completed);
        Assert.False(family.Find(stages[2].Id)!.Claimed); Assert.Null(family.Find(stages[2].Id)!.Completed);
        Assert.Equal(0, raw.Achievements[familyId].RewardedStep);
    }

    [Fact]
    public void ClaimHistoryCannotQualifyAnUnresolvedCandidatePeriod()
    {
        var (definition, binding) = Example();
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default, [binding]);
        var facts = evaluator.Evaluate(Raw(definition.Id, 0), binding.MetadataBuild, 1, 1,
            knownClaimedStageIds: ImmutableHashSet.Create(binding.StageId)).Find(definition.Id)!.Find(binding.StageId)!;
        Assert.Null(facts.Claimed);
        Assert.NotEqual<bool?>(true, facts.Completed);
    }

    [Fact]
    public void EveryQualifiedNormalPrefixIncludingZeroSelectsItsExactNativeFirstStage()
    {
        var contracts = CombatAchievementProgressContractCatalog.Default;
        var raw = new CharacterAchievementState(contracts.Contracts.SelectMany(type => type.Families)
            .ToImmutableDictionary(family => family.Id, _ => new CharacterAchievement(0, long.MaxValue, 0)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var result = new AchievementRuleEvaluator().Evaluate(raw, contracts.MetadataBuild, 1, 1);
        Assert.Equal(676, result.Families.Count);
        foreach (var type in contracts.Contracts)
        foreach (var family in type.Families)
        {
            var facts = result.Find(family.Id)!;
            Assert.Equal(family.Stages[0].Id, facts.CurrentStageId);
            Assert.All(facts.Stages.Values, stage => Assert.False(stage.Claimed));
            if (type.CounterRule != CombatAchievementCounterRule.Unqualified
                || AchievementProgressThresholdCatalog.Default.FindFamily(family.Id) is not null) continue;
            Assert.All(facts.Stages.Values, stage =>
            {
                Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed);
                Assert.Null(stage.RequirementsMetNow); Assert.Equal(CombatAchievementProgressUnit.Unknown, stage.Unit);
            });
        }
    }

    [Fact]
    public void ClaimOnlyNativeStagesFollowTheirExplicitPrefixAtEveryOrdinalWithoutPromotingTheRawCounter()
    {
        const int familyId = 444100500;
        var contract = CombatAchievementProgressContractCatalog.Default.FindFamily(familyId)!.Families.Single(family => family.Id == familyId);
        for (var prefix = 0; prefix <= contract.Stages.Count; prefix++)
        {
            var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty
                .Add(familyId, new(-17, long.MaxValue, checked((byte)prefix))), ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
            var family = new AchievementRuleEvaluator().Evaluate(raw, AchievementDefinitionCatalog.Default.MetadataBuild, 1, prefix + 1).Find(familyId)!;
            var expected = prefix < contract.Stages.Count ? contract.Stages[prefix] : contract.Stages[^1];
            Assert.Equal(expected.Id, family.CurrentStageId);
            foreach (var stage in contract.Stages)
            {
                var facts = family.Find(stage.Id)!;
                Assert.Equal(stage.Ordinal <= prefix, facts.Claimed);
                Assert.Equal<bool?>(stage.Ordinal <= prefix ? true : null, facts.Completed);
                Assert.Null(facts.Current); Assert.Null(facts.Target);
            }
        }
    }

    [Fact]
    public void AQualifiedClaimSurvivesAnUnknownNegativeCounter()
    {
        const int family = 420101100;
        var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(family, new(-1, -2, 1)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var value = new AchievementRuleEvaluator().Evaluate(raw, AchievementDefinitionCatalog.Default.MetadataBuild, 1, 1).Find(family)!;
        Assert.True(value.Find(family + 1)!.Claimed);
        Assert.True(value.Find(family + 1)!.Completed);
        Assert.Null(value.Find(family + 1)!.Current);
        Assert.Null(value.Find(family + 2)!.Completed);
    }

    [Fact]
    public void ExperienceNeedsTheExactMetadataBindingAndUsesItsCurve()
    {
        var (definition, binding) = Example();
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default, [binding]);
        var stage = evaluator.Evaluate(Raw(definition.Id, 1500), binding.MetadataBuild, 1, 1).Find(definition.Id)!.Find(binding.StageId)!;
        Assert.Equal(1500, stage.Current);
        Assert.Equal(1500, stage.Target);
        Assert.Equal(1m, stage.DisplayCurrent);
        Assert.Equal(1m, stage.DisplayTarget);
        Assert.True(stage.RequirementsMetNow);
        Assert.True(stage.Completed);
        Assert.Null(stage.Claimed);
        var mismatched = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default,
            [binding with { DefinitionSignature = new string('0', 64) }]);
        Assert.Null(mismatched.Evaluate(Raw(definition.Id, 1500), binding.MetadataBuild, 1, 1).Find(definition.Id));
    }

    [Fact]
    public void AResetLabelOrAnObservedCounterDoesNotQualifyAnActivePeriod()
    {
        var (definition, binding) = Example();
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default,
            [binding with { PermanentPeriodQualified = false }]);
        var facts = evaluator.Evaluate(Raw(definition.Id, 5000), binding.MetadataBuild, 1, 1).Find(definition.Id)!.Find(binding.StageId)!;
        Assert.Null(facts.Current);
        Assert.Null(facts.Completed);
        Assert.Null(facts.Claimed);
        Assert.Null(evaluator.QualifiedPeriodId(definition.Id));
    }

    [Fact]
    public void EveryDefinitionHasAnExplicitRecognitionStatusWithoutPromotingCandidates()
    {
        var definitions = AchievementDefinitionCatalog.Default;
        var contracts = CombatAchievementProgressContractCatalog.Default;
        Assert.Equal(6428, definitions.Definitions.Count);
        Assert.Equal(14, definitions.Definitions.Count(definition => contracts.FindFamily(definition.Id)?.CounterRule == CombatAchievementCounterRule.Cumulative));
        Assert.Equal(662, definitions.Definitions.Count(definition => contracts.FindFamily(definition.Id)?.CounterRule == CombatAchievementCounterRule.Unqualified));
        Assert.All(definitions.Definitions.Where(definition => definition.Role != AchievementRole.PlayerFacing),
            definition => Assert.Null(contracts.FindFamily(definition.Id)));
    }

    [Fact]
    public void SamePeriodEarnedHistorySurvivesACumulativeCounterRegressionAndReportsConflict()
    {
        const int family = 420101100;
        var evaluator = new AchievementRuleEvaluator();
        var progress = evaluator.Evaluate(Raw(family, 4), AchievementDefinitionCatalog.Default.MetadataBuild, 1, 2,
            ImmutableHashSet.Create(family + 1)).Find(family)!;
        var stage = progress.Find(family + 1)!;
        Assert.Equal(Soulcrest.Core.Combat.CombatKnowledgeStatus.Conflict, progress.Status);
        Assert.True(stage.Completed);
        Assert.False(stage.RequirementsMetNow);
        Assert.False(stage.Claimed);
        Assert.Equal(4, stage.Current);
        Assert.Equal(family + 1, progress.CurrentStageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarnedHistoryRemainsKnownWithNegativeOrMissingCountersWithoutInventingZero(bool missing)
    {
        const int family = 420101100;
        var raw = missing ? new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty,
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty) : Raw(family, -1);
        var progress = new AchievementRuleEvaluator().Evaluate(raw, AchievementDefinitionCatalog.Default.MetadataBuild, 1, 2,
            ImmutableHashSet.Create(family + 1)).Find(family)!;
        Assert.Equal(Soulcrest.Core.Combat.CombatKnowledgeStatus.Partial, progress.Status);
        Assert.True(progress.Find(family + 1)!.Completed);
        Assert.Null(progress.Find(family + 1)!.Current);
        Assert.Null(progress.Find(family + 1)!.RequirementsMetNow);
        Assert.Equal<bool?>(missing ? null : false, progress.Find(family + 1)!.Claimed);
        Assert.Null(progress.Find(family + 2)!.Completed);
    }

    [Fact]
    public void AdditionalCurrentStateKeepsEarnedCompletionWhileRequirementsCanBecomeFalse()
    {
        var (definition, binding) = Example();
        binding = binding with
        {
            RawTarget = 1, Unit = CombatAchievementProgressUnit.Count,
            Semantics = new("synthetic-reversible-state", 1, AchievementSemanticKind.CurrentState, 1)
            {
                IsQualified = true, CounterCanConfirmCompletion = true, MonotoneCounterQualified = true,
            },
        };
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default, [binding]);
        var progress = evaluator.Evaluate(Raw(definition.Id, 0), binding.MetadataBuild, 1, 2,
            ImmutableHashSet.Create(binding.StageId)).Find(definition.Id)!;
        Assert.True(progress.Find(binding.StageId)!.Completed);
        Assert.False(progress.Find(binding.StageId)!.RequirementsMetNow);
        Assert.Equal(Soulcrest.Core.Combat.CombatKnowledgeStatus.Partial, progress.Status);
        Assert.Null(progress.Find(binding.StageId)!.Claimed);
    }

    [Fact]
    public void EarnedIdsDoNotBypassAnUnqualifiedAdditionalContentPeriod()
    {
        var (definition, binding) = Example();
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default,
            [binding with { PermanentPeriodQualified = false }]);
        var stage = evaluator.Evaluate(Raw(definition.Id, 5000), binding.MetadataBuild, 1, 2,
            ImmutableHashSet.Create(binding.StageId)).Find(definition.Id)!.Find(binding.StageId)!;
        Assert.Null(stage.Completed);
        Assert.Null(stage.RequirementsMetNow);
    }

    [Fact]
    public void UnrelatedStageHistoryDoesNotLatchAnotherStageAndDefaultAdapterRecordsStayCompatible()
    {
        const int family = 420101100;
        var evaluator = new AchievementRuleEvaluator();
        var compatible = evaluator.Evaluate(Raw(family, 4), AchievementDefinitionCatalog.Default.MetadataBuild, 1, 1)
            .Find(family)!.Find(family + 1)!;
        Assert.Equal(new CombatAchievementStageProgress(family + 1, 4, 5, false, false), compatible);
        var unrelated = evaluator.Evaluate(Raw(family, 4), AchievementDefinitionCatalog.Default.MetadataBuild, 1, 2,
            ImmutableHashSet.Create(999999)).Find(family)!.Find(family + 1)!;
        Assert.False(unrelated.Completed);
        Assert.False(unrelated.RequirementsMetNow);
    }

    private static (AchievementDefinitionInfo Definition, AchievementBoundStageRule Binding) Example()
    {
        var definitions = AchievementDefinitionCatalog.Default;
        var definition = definitions.Definitions.First(item => item.Role == AchievementRole.Candidate);
        var semantics = new AchievementSemanticRule("synthetic-xp-curve", 1, AchievementSemanticKind.Experience, 1)
        {
            IsQualified = true, CounterCanConfirmCompletion = true,
            Experience = new([new(0, 0), new(1, 1500), new(2, 5000)]) { FinalLevelIsQualifiedMaximum = true },
        };
        return (definition, new(definitions.MetadataBuild, definition.Id, definition.StageIds[0], definition.Signature,
            1500, semantics, CombatAchievementProgressUnit.Level) { PermanentPeriodQualified = true });
    }

    private static CharacterAchievementState Raw(int id, int value) => new(
        ImmutableDictionary<int, CharacterAchievement>.Empty.Add(id, new(value, long.MaxValue, 0)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
}
