using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementFlightProgressTests
{
    private const int MetadataBuild = 26425393;
    private static readonly long[] MinuteTargets = [10, 30, 60, 120, 180, 300];
    private static readonly DateTimeOffset At = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(320101200)]
    [InlineData(420101200)]
    public void QualifiedFlightFamiliesUseTheExactNativeMinuteChains(int familyId)
    {
        var type = Assert.IsType<CombatAchievementTypeContract>(CombatAchievementProgressContractCatalog.Default.FindFamily(familyId));
        Assert.Equal(CombatAchievementCounterRule.Cumulative, type.CounterRule);
        Assert.Equal(CombatAchievementProgressUnit.Minutes, type.Unit);
        Assert.Equal(CombatAchievementCompletionRule.AtLeastTarget, type.CompletionRule);
        Assert.Equal(CombatAchievementClaimRule.ContiguousRewardOrdinal, type.ClaimRule);
        var contract = Assert.Single(type.Families, family => family.Id == familyId);
        var stageIds = NativeStageIds(familyId);
        Assert.Equal(stageIds, contract.Stages.Select(stage => stage.Id));
        Assert.Equal(MinuteTargets, contract.Stages.Select(stage => stage.Target!.Value));
        var definition = Assert.IsType<CombatAchievementDisplayDefinition>(CombatAchievementDisplayCatalog.Default.Find(familyId));
        Assert.Empty(definition.UnknownStageIds);
        Assert.Equal(stageIds, definition.Stages.Select(stage => stage.Id));

        var family = Project((familyId, 300, 0)).Find(familyId)!;
        Assert.Equal(MinuteTargets, stageIds.Select(id => family.Find(id)!.Target!.Value));
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Equal(CombatAchievementProgressUnit.Minutes, stage.Unit);
            Assert.Equal(300, stage.Current);
            Assert.True(stage.Completed);
            Assert.False(stage.Claimed);
        });
    }

    [Theory]
    [InlineData(320101200)]
    [InlineData(420101200)]
    public void MinuteProgressCrossesTheCompletionThresholdWithoutClaimingTheReward(int familyId)
    {
        var firstStageId = NativeStageIds(familyId)[0];
        foreach (var count in new[] { 8, 9, 10 })
        {
            var family = Project((familyId, count, 0)).Find(familyId)!;
            Assert.Equal(CombatKnowledgeStatus.Complete, family.Status);
            Assert.Equal(count < 10 ? firstStageId : NativeStageIds(familyId)[1], family.CurrentStageId);
            var stage = family.Find(firstStageId)!;
            Assert.Equal(count, stage.Current);
            Assert.Equal(10, stage.Target);
            Assert.Equal(CombatAchievementProgressUnit.Minutes, stage.Unit);
            Assert.Equal(count == 10, stage.Completed);
            Assert.False(stage.Claimed);
        }
    }

    [Theory]
    [InlineData(320101200)]
    [InlineData(420101200)]
    public void ClaimingTheFirstFlightTierKeepsItsMinutesInTheSecondTier(int familyId)
    {
        var stageIds = NativeStageIds(familyId);
        var before = Project((familyId, 10, 0)).Find(familyId)!;
        Assert.Equal(stageIds[1], before.CurrentStageId);
        Assert.True(before.Find(stageIds[0])!.Completed);
        Assert.False(before.Find(stageIds[0])!.Claimed);

        var after = Project((familyId, 10, 1)).Find(familyId)!;
        Assert.Equal(stageIds[1], after.CurrentStageId);
        var first = after.Find(stageIds[0])!;
        Assert.Equal(10, first.Current);
        Assert.Equal(10, first.Target);
        Assert.True(first.Completed);
        Assert.True(first.Claimed);
        var second = after.Find(stageIds[1])!;
        Assert.Equal(10, second.Current);
        Assert.Equal(30, second.Target);
        Assert.Equal(CombatAchievementProgressUnit.Minutes, second.Unit);
        Assert.False(second.Completed);
        Assert.False(second.Claimed);
        Assert.False(before.Find(stageIds[0])!.Claimed);
    }

    [Theory]
    [InlineData(320101300)]
    [InlineData(420101300)]
    [InlineData(320101400)]
    [InlineData(420101400)]
    public void SwimmingAndGlidingDoNotInheritTheQualifiedFlightCounter(int otherFamilyId)
    {
        var withoutClaim = Project((420101200, 10, 0), (otherFamilyId, 10, 0));
        Assert.NotNull(withoutClaim.Find(420101200));
        var initial = Assert.IsType<CombatAchievementFamilyProgress>(withoutClaim.Find(otherFamilyId));
        var contract = CombatAchievementProgressContractCatalog.Default.FindFamily(otherFamilyId)!
            .Families.Single(item => item.Id == otherFamilyId);
        if (otherFamilyId is 420101300 or 420101400)
        {
            // Screenshot-qualified scalar goals are separate from the measured flight-minute contract.
            var type = CombatAchievementProgressContractCatalog.Default.FindFamily(otherFamilyId)!;
            Assert.Equal(CombatAchievementCounterRule.Unqualified, type.CounterRule);
            Assert.Equal(CombatAchievementProgressUnit.Unknown, type.Unit);
            var thresholds = AchievementProgressThresholdCatalog.Default.FindFamily(otherFamilyId)!;
            Assert.True(thresholds.ModelCurrentState);
            Assert.Equal(contract.Stages[1].Id, initial.CurrentStageId);
            foreach (var stage in thresholds.Stages)
            {
                var facts = initial.Find(stage.Id)!;
                Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
                Assert.Equal(10, facts.Current); Assert.Equal(stage.Target, facts.Target);
                Assert.False(facts.Claimed);
            }
            var claimed = Project((420101200, 10, 0), (otherFamilyId, 10, 1)).Find(otherFamilyId)!;
            Assert.Equal(contract.Stages[1].Id, claimed.CurrentStageId);
            Assert.True(claimed.Find(contract.Stages[0].Id)!.Claimed);
            Assert.True(claimed.Find(contract.Stages[0].Id)!.Completed);
            Assert.Equal(CombatAchievementProgressUnit.Count, claimed.Find(contract.Stages[1].Id)!.Unit);
            return;
        }
        Assert.Equal(contract.Stages[0].Id, initial.CurrentStageId);
        Assert.All(initial.Stages.Values, stage =>
        {
            Assert.Equal(CombatAchievementProgressUnit.Unknown, stage.Unit);
            Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
        });

        var withClaim = Project((420101200, 10, 0), (otherFamilyId, 10, 1));
        var family = Assert.IsType<CombatAchievementFamilyProgress>(withClaim.Find(otherFamilyId));
        Assert.Equal(CombatKnowledgeStatus.Partial, family.Status);
        Assert.Equal(contract.Stages[1].Id, family.CurrentStageId);
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Equal(CombatAchievementProgressUnit.Unknown, stage.Unit);
            Assert.Null(stage.Current);
            Assert.Null(stage.Target);
        });
        var native = CombatAchievementDisplayCatalog.Default.Find(otherFamilyId)!;
        Assert.True(family.Find(native.Stages[0].Id)!.Completed);
        Assert.True(family.Find(native.Stages[0].Id)!.Claimed);
        Assert.All(native.Stages.Skip(1), stage =>
        {
            Assert.Null(family.Find(stage.Id)!.Completed);
            Assert.False(family.Find(stage.Id)!.Claimed);
        });
    }

    [Theory]
    [InlineData(320101200)]
    [InlineData(420101200)]
    public void AnOutOfRangeSeventhFlightClaimKeepsTheIndependentMinuteStage(int familyId)
    {
        var family = Project((familyId, 300, 7)).Find(familyId)!;
        Assert.Equal(CombatKnowledgeStatus.Partial, family.Status);
        Assert.Equal(NativeStageIds(familyId)[^1], family.CurrentStageId);
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Equal(300, stage.Current);
            Assert.Equal(CombatAchievementProgressUnit.Minutes, stage.Unit);
            Assert.True(stage.Completed);
            Assert.Null(stage.Claimed);
        });
    }

    [Theory]
    [InlineData(320101200)]
    [InlineData(420101200)]
    public void AFlightClaimBelowItsMinuteThresholdRemainsAVisibleConflict(int familyId)
    {
        var family = Project((familyId, 9, 1)).Find(familyId)!;
        Assert.Equal(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(NativeStageIds(familyId)[1], family.CurrentStageId);
        var first = family.Find(NativeStageIds(familyId)[0])!;
        Assert.Equal(9, first.Current);
        Assert.Equal(10, first.Target);
        Assert.Equal(CombatAchievementProgressUnit.Minutes, first.Unit);
        Assert.True(first.Claimed);
        Assert.All(family.Stages.Values, stage => Assert.Null(stage.Completed));
    }

    private static int[] NativeStageIds(int familyId) => familyId switch
    {
        320101200 => [320101201, 320101202, 320101203, 320101204, 320101205, 320101206],
        420101200 => [420101201, 420101202, 420101203, 420101204, 420101205, 420101206],
        _ => throw new ArgumentOutOfRangeException(nameof(familyId))
    };

    private static CombatAchievementProgressSnapshot Project(params (int Id, int Count, byte RewardedStep)[] rows) =>
        CombatAchievementProgressProjection.Create(new CharacterCombatSnapshot
        {
            ProfileId = "FLIGHT-SYNTHETIC-PROFILE", Generation = 7, Sequence = 42, IsFresh = true,
            Achievements = new(new(rows.ToImmutableDictionary(row => row.Id,
                    row => new CharacterAchievement(row.Count, long.MaxValue, row.RewardedStep)),
                ImmutableDictionary<int, ImmutableArray<bool>>.Empty),
                NetworkCombatSourceStatus.Complete, true, At, 41, "own-achievement-list")
        }, MetadataBuild);
}
