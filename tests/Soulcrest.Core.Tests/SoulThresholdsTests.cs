using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Pet levels: 5, then 25 new, then 75 new souls (each level counted from zero).</summary>
public sealed class SoulThresholdsTests
{
    private static readonly SoulThresholds T = SoulThresholds.Default;

    [Theory]
    [InlineData(0, 0, 0, 5)]
    [InlineData(4, 0, 4, 5)]
    [InlineData(5, 1, 0, 25)]
    [InlineData(29, 1, 24, 25)]
    [InlineData(30, 2, 0, 75)]
    [InlineData(104, 2, 74, 75)]
    [InlineData(105, 3, 0, 0)]
    [InlineData(500, 3, 0, 0)]
    public void SplitsTotalIntoGameLevels(int total, int level, int inLevel, int needed)
    {
        Assert.Equal(level, T.Level(total));
        Assert.Equal(inLevel, T.SoulsInLevel(total));
        Assert.Equal(needed, T.NeededForNextLevel(total));
    }

    [Theory]
    [InlineData(0, 3, 3)]
    [InlineData(1, 0, 5)]
    [InlineData(1, 12, 17)]
    [InlineData(2, 40, 70)]
    [InlineData(3, 0, 105)]
    [InlineData(2, 999, 104)]
    public void GameEntryRoundTrips(int level, int inLevel, int total)
    {
        Assert.Equal(total, T.Total(level, inLevel));
        Assert.Equal(Math.Min(level, 3), T.Level(total));
    }

    /// <summary>Confirmed in game: excess souls carry over into the next level.</summary>
    [Theory]
    [InlineData(4, 3, 1, 2)]     // 4/5 + 3 -> level 1, 2/25
    [InlineData(28, 5, 2, 3)]    // level 1 at 23/25 + 5 -> level 2, 3/75
    [InlineData(100, 10, 3, 0)]  // capped at max
    public void ExcessSoulsCarryOver(int total, int added, int level, int inLevel)
    {
        var after = Math.Min(total + added, T.Max);
        Assert.Equal(level, T.Level(after));
        Assert.Equal(inLevel, T.SoulsInLevel(after));
    }

    [Fact]
    public void MaxIsSumOfSteps()
    {
        Assert.Equal(105, T.Max);
        Assert.Equal(5, T.Unlock);
        Assert.Equal(3, T.MaxLevel);
    }
}
