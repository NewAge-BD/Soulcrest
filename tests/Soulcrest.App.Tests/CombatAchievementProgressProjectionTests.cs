using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementProgressProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private const int Soaring = 420101000, Riding = 420101100;

    [Fact]
    public void IntermediateProgressDoesNotBecomeCompletionBecauseAClockValueIsPositive()
    {
        var state = Snapshot((Soaring, new(4, long.MaxValue, 0)), (Riding, new(3, 17, 0)));
        var projection = CombatAchievementProgressProjection.Create(state, 26425393);
        Assert.Equal(7, projection.Generation); Assert.Equal(11, projection.SourceSequence);
        var soaring = projection.Find(Soaring)!;
        Assert.Equal(Soaring + 1, soaring.CurrentStageId);
        Assert.Equal(new(Soaring + 1, 4, 5, false, false), soaring.Find(Soaring + 1));
        Assert.Equal(new(Riding + 1, 3, 5, false, false), projection.Find(Riding)!.Find(Riding + 1));
        Assert.Equal(long.MaxValue, state.Achievements.Value!.Achievements[Soaring].CompleteTime);
    }

    [Fact]
    public void CompletionAndRewardClaimRemainIndependentAndNextStageKeepsCumulativeUses()
    {
        var before = CombatAchievementProgressProjection.Create(Snapshot((Riding, new(5, 1234, 0))), 26425393);
        Assert.Equal(new(Riding + 1, 5, 5, true, false), before.Find(Riding)!.Find(Riding + 1));
        var state = Snapshot((Riding, new(5, 1234, 1)), (Soaring, new(5, 5678, 0)));
        var after = CombatAchievementProgressProjection.Create(state, 26425393);
        Assert.Equal(Riding + 2, after.Find(Riding)!.CurrentStageId);
        Assert.Equal(new(Riding + 1, 5, 5, true, true), after.Find(Riding)!.Find(Riding + 1));
        Assert.Equal(new(Riding + 2, 5, 20, false, false), after.Find(Riding)!.Find(Riding + 2));
        Assert.Equal(Soaring + 2, after.Find(Soaring)!.CurrentStageId);
        Assert.Equal(new(Soaring + 1, 5, 5, true, false), after.Find(Soaring)!.Find(Soaring + 1));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, CombatAchievementFamilyProgress>)after.Families).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, CombatAchievementStageProgress>)after.Find(Riding)!.Stages).Clear());
    }

    [Fact]
    public void MissingRowsAreUnknownWhileAnExplicitZeroAndZeroRewardStepAreUsable()
    {
        var empty = CombatAchievementProgressProjection.Create(Snapshot(), 26425393);
        Assert.Empty(empty.Families);
        Assert.Null(empty.Find(Soaring));
        var zero = CombatAchievementProgressProjection.Create(Snapshot((Soaring, new(0, 0, 0))), 26425393);
        Assert.Equal(new(Soaring + 1, 0, 5, false, false), zero.Find(Soaring)!.Find(Soaring + 1));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("domain")]
    [InlineData("stale")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    [InlineData("source")]
    [InlineData("timestamp")]
    [InlineData("sequence")]
    [InlineData("metadata")]
    public void SourceAndVersionLossCannotReviveProgressOrClaimFacts(string variant)
    {
        var state = Snapshot((Riding, new(5, 1234, 1)));
        state = variant switch
        {
            "snapshot" => state with { IsFresh = false },
            "profile" => state with { ProfileId = "" },
            "generation" => state with { Generation = 0 },
            "domain" => state with { Achievements = state.Achievements with { IsFresh = false } },
            "stale" => state with { Achievements = state.Achievements with { Status = NetworkCombatSourceStatus.Stale } },
            "conflict" => state with { Achievements = state.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            "unknown" => state with { Achievements = NetworkCombatDomain<CharacterAchievementState>.Unknown },
            "source" => state with { Achievements = state.Achievements with { Source = "unqualified" } },
            "timestamp" => state with { Achievements = state.Achievements with { ObservedAt = null } },
            "sequence" => state with { Achievements = state.Achievements with { Sequence = 0 } },
            "metadata" => state,
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Empty(CombatAchievementProgressProjection.Create(state, variant == "metadata" ? 1 : 26425393).Families);
    }

    [Theory]
    [InlineData(-1, 0)]
    public void NegativeMovementCounterKeepsOnlyItsIndependentlyQualifiedPrefixStage(int count, byte rewarded)
    {
        var family = CombatAchievementProgressProjection.Create(Snapshot((Riding, new(count, 99, rewarded))), 26425393).Find(Riding)!;
        Assert.Equal(Riding + 1, family.CurrentStageId);
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
        });
    }

    [Fact]
    public void CounterAndKnownClaimContradictionRemainsVisibleAsConflictWithoutInventingCompletion()
    {
        var result = CombatAchievementProgressProjection.Create(Snapshot((Riding, new(4, 99, 1))), 26425393);
        var family = Assert.IsType<CombatAchievementFamilyProgress>(result.Find(Riding));
        Assert.Equal(CombatKnowledgeStatus.Conflict, family.Status); Assert.Equal(Riding + 2, family.CurrentStageId);
        var first = family.Find(Riding + 1)!;
        Assert.Equal(4, first.Current); Assert.Equal(5, first.Target); Assert.True(first.Claimed);
        Assert.All(family.Stages.Values, stage => Assert.Null(stage.Completed));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(255)]
    public void UnqualifiedLaterClaimBytesDoNotBlockTheIndependentCountStage(byte rewarded)
    {
        var result = CombatAchievementProgressProjection.Create(Snapshot((Riding, new(20, 99, rewarded))), 26425393);
        var family = Assert.IsType<CombatAchievementFamilyProgress>(result.Find(Riding));
        Assert.Equal(Riding + 3, family.CurrentStageId);
        var first = family.Find(Riding + 1)!;
        Assert.Equal(20, first.Current); Assert.Equal(5, first.Target); Assert.True(first.Completed);
        Assert.All(family.Stages.Values, stage => Assert.Null(stage.Claimed));
    }

    [Fact]
    public void DefaultContiguousMovementContractRecognizesTheSecondClaimAndMovesToStageThree()
    {
        var family = CombatAchievementProgressProjection.Create(Snapshot((Riding, new(20, 99, 2))), 26425393).Find(Riding)!;
        Assert.Equal(Riding + 3, family.CurrentStageId);
        Assert.True(family.Find(Riding + 1)!.Claimed); Assert.True(family.Find(Riding + 2)!.Claimed);
        Assert.Equal(new(Riding + 3, 20, 50, false, false), family.Find(Riding + 3));
    }

    [Fact]
    public void AMetadataCountFamilyUsesItsOwnExplicitGoalsWithoutChangingItsClaims()
    {
        var family = CombatAchievementProgressProjection.Create(Snapshot((400100200, new(50, long.MaxValue, 7))), 26425393).Find(400100200)!;
        Assert.NotNull(family);
        var contract = CombatAchievementProgressContractCatalog.Default.FindFamily(400100200)!.Families.Single(item => item.Id == 400100200);
        var expected = contract.Stages.FirstOrDefault(stage => stage.Ordinal > 7) ?? contract.Stages[^1];
        Assert.Equal(expected.Id, family.CurrentStageId);
        var goals = AchievementProgressThresholdCatalog.Default.FindFamily(400100200)!;
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Equal(50, stage.Current); Assert.Equal(goals.Stages.Single(goal => goal.Id == stage.StageId).Target, stage.Target);
        });
        Assert.Contains(family.Stages.Values, stage => stage.Completed is true && stage.Claimed is true);
    }

    [Fact]
    public void UnsupportedObjectivesAndOtherCharacterRowsDoNotReceiveMovementSemantics()
    {
        var state = Snapshot((1, new(1500, 1234, 1)), (2, new(50, 5678, 7)));
        Assert.Empty(CombatAchievementProgressProjection.Create(state, 26425393).Families);
    }

    [Fact]
    public void EverySupportedStageExistsInTheReferenceCatalogueAndBothScopesStayIndependent()
    {
        var ids = new[] { 320101000, 320101100, Soaring, Riding };
        var state = Snapshot(ids.Select(id => (id, new CharacterAchievement(300, 0, 0))).ToArray());
        var result = CombatAchievementProgressProjection.Create(state, CombatAchievementDisplayCatalog.Default.MetadataBuild);
        Assert.Equal(4, result.Families.Count);
        foreach (var id in ids)
        {
            var family = result.Find(id)!;
            Assert.Equal(id + 7, family.CurrentStageId);
            Assert.Equal(new long[] { 5, 20, 50, 100, 150, 200, 300 }, family.Stages.Values.OrderBy(stage => stage.StageId).Select(stage => stage.Target!.Value));
            Assert.All(family.Stages.Values, stage => Assert.Contains(CombatAchievementDisplayCatalog.Default.Find(id)!.Stages, definition => definition.Id == stage.StageId));
            Assert.All(family.Stages.Values, stage => { Assert.True(stage.Completed); Assert.False(stage.Claimed); });
        }
    }

    private static CharacterCombatSnapshot Snapshot(params (int Id, CharacterAchievement Value)[] rows) => new()
    {
        ProfileId = "ACHIEVEMENT-SYNTHETIC-PROFILE", Generation = 7, Sequence = 23, IsFresh = true,
        Achievements = new(new(rows.ToImmutableDictionary(row => row.Id, row => row.Value),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty), NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };
}
