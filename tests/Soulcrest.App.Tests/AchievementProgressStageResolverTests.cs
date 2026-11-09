using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementProgressStageResolverTests
{
    private static readonly ImmutableArray<AchievementProgressThresholdStage> Stages =
        [new(1101, 10), new(1327, 100), new(1599, 1000)];

    [Theory]
    [InlineData(0, 1101)]
    [InlineData(9, 1101)]
    [InlineData(10, 1327)]
    [InlineData(11, 1327)]
    [InlineData(99, 1327)]
    [InlineData(100, 1599)]
    [InlineData(999, 1599)]
    [InlineData(1000, 1599)]
    [InlineData(1001, 1599)]
    public void ReachingTheExactGoalAdvancesToTheNextExplicitStage(int count, int expected) =>
        Assert.Equal(expected, AchievementProgressStageResolver.Select(Stages, count));

    [Fact]
    public void OutstandingRewardsNeverFreezeAnAdvancedCountStage()
    {
        Assert.Equal(1599, AchievementProgressStageResolver.Select(Stages, 750));
        Assert.Equal(1599, AchievementProgressStageResolver.Select(Stages, 750, ImmutableHashSet.Create(1101)));
        Assert.Equal(1599, AchievementProgressStageResolver.Select(Stages, 750, ImmutableHashSet.Create(1101, 1327)));
        Assert.Equal(1599, AchievementProgressStageResolver.Select(Stages, 0, ImmutableHashSet.Create(1101, 1327)));
    }

    [Fact]
    public void AClaimedHistoryFloorPreventsRevisitingTheCollectedTierWithoutRewritingCount()
    {
        Assert.Equal(1327, AchievementProgressStageResolver.Select(Stages, 0, ImmutableHashSet.Create(1101)));
        Assert.Equal(1599, AchievementProgressStageResolver.Select(Stages, 0, ImmutableHashSet.Create(1599)));
        Assert.Equal(1101, AchievementProgressStageResolver.Select(Stages, 0, ImmutableHashSet.Create(7777)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void MissingAndNegativeCountsNeverChooseAThresholdStage(long? value) =>
        Assert.Null(AchievementProgressStageResolver.Select(Stages, value));

    [Fact]
    public void AnIncompleteChainCannotPretendItsLastKnownThresholdIsTerminal()
    {
        Assert.Null(AchievementProgressStageResolver.Select(Stages, 1000, chainComplete: false));
        Assert.Null(AchievementProgressStageResolver.Select(Stages, 1, ImmutableHashSet.Create(1599), chainComplete: false));
        Assert.Equal(1327, AchievementProgressStageResolver.Select(Stages, 10, chainComplete: false));
    }

    [Fact]
    public void InvalidTargetOrderMembershipOrZeroThresholdsAreNotSortedOrGuessed()
    {
        Assert.Null(AchievementProgressStageResolver.Select([], 0));
        Assert.Null(AchievementProgressStageResolver.Select([new(1101, 0)], 0));
        Assert.Null(AchievementProgressStageResolver.Select([new(1101, 10), new(1101, 100)], 0));
        Assert.Null(AchievementProgressStageResolver.Select([new(1101, 100), new(1327, 10)], 0));
        Assert.Null(AchievementProgressStageResolver.Select([new(1101, 10), new(1327, 10)], 0));
    }
}
