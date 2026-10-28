using System.Drawing;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssRankPresentationTests
{
    [Fact]
    public void BundledPointMilestonesHaveOriginalBilingualNamesAndDistinctTransparentIcons()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        Assert.Equal(19, stages.Count);
        Assert.Equal(new long[] { 0, 3000, 12000, 35000, 75000, 180000, 350000, 550000, 750000,
            1000000, 1500000, 2100000, 2800000, 3500000, 4200000, 6000000, 9000000, 13000000, 20000000 },
            stages.Select(stage => stage.Threshold));
        Assert.Equal("Rekrut", stages[0].Name("de"));
        Assert.Equal("Trainee", stages[0].Name());
        Assert.Equal("Oberbefehlshaber", stages[^1].Name("de"));
        Assert.Equal("Supreme Commander", stages[^1].Name("en"));
        var imageHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", stage.IconAsset.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Missing bundled original rank icon: " + stage.Id);
            imageHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            using var bitmap = new Bitmap(path);
            Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
            Assert.Contains(Enumerable.Range(0, bitmap.Height), y =>
                Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).A < 255));
        }
        Assert.Equal(19, imageHashes.Count);
    }

    [Fact]
    public void EveryExactThresholdBeginsItsNewIntervalAndKeepsItsOriginalIcon()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var projection = AbyssRankPresentation.Project(stage.Threshold);
            Assert.Same(stage, projection.Current);
            Assert.Equal(0, projection.PointsInStage);
            Assert.Equal(index == stages.Count - 1 ? 1d : 0d, projection.Progress);
            Assert.Equal(index == stages.Count - 1, projection.IsMaximum);
            if (index == stages.Count - 1) Assert.Null(projection.Next);
            else
            {
                Assert.Same(stages[index + 1], projection.Next);
                Assert.Equal(stages[index + 1].Threshold - stage.Threshold, projection.PointsToNext);
                var before = AbyssRankPresentation.Project(stages[index + 1].Threshold - 1);
                Assert.Same(stage, before.Current);
                Assert.Equal(1, before.PointsToNext);
                Assert.InRange(before.Progress, 0, 1);
            }
        }
    }

    [Fact]
    public void ProgressUsesTheCurrentIntervalAndCanMoveBackwardWithoutState()
    {
        var halfway = AbyssRankPresentation.Project(7500);
        Assert.Equal("rank-18", halfway.Current?.Id);
        Assert.Equal("rank-17", halfway.Next?.Id);
        Assert.Equal(4500, halfway.PointsInStage);
        Assert.Equal(4500, halfway.PointsToNext);
        Assert.Equal(0.5, halfway.Progress);
        Assert.Equal("rank-19", AbyssRankPresentation.Project(2999).Current?.Id);
        var maximum = AbyssRankPresentation.Project(long.MaxValue);
        Assert.True(maximum.IsMaximum);
        Assert.Null(maximum.Next); Assert.Null(maximum.PointsToNext);
        Assert.Equal(1, maximum.Progress);
    }

    [Theory]
    [InlineData(null)] [InlineData(-1L)]
    public void MissingOrInvalidPointsDoNotInventARecruitOrZero(long? points)
    {
        var projection = AbyssRankPresentation.Project(points);
        Assert.False(projection.HasPoints);
        Assert.Null(projection.TotalPoints); Assert.Null(projection.Current); Assert.Null(projection.Next);
        Assert.Null(projection.PointsToNext); Assert.False(projection.IsMaximum);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"stages\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"stages\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"stages\":[{\"id\":\"rank-01\",\"nameDe\":\"A\",\"nameEn\":\"A\",\"threshold\":1,\"iconAsset\":\"abyss-rank-icons/rank-01.png\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"stages\":[{\"id\":\"rank-01\",\"nameDe\":\"A\",\"nameEn\":\"A\",\"threshold\":0,\"iconAsset\":\"../secret.png\"}]}")]
    public void InvalidOrNonBundledCatalogsAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => AbyssRankCatalog.FromJson(json));
}
