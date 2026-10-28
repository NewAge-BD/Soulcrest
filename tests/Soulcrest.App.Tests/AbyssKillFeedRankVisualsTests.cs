using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssKillFeedRankVisualsTests
{
    [Fact]
    public void ExplicitNativeGradesHaveStrictlyIncreasingStrengthAndMovementTowardsGradeOne()
    {
        var previousStrength = 0d;
        var previousMotion = 0d;
        for (var grade = 19; grade >= 1; grade--)
        {
            var treatment = Assert.IsType<AbyssKillFeedRankVisuals.Treatment>(AbyssKillFeedRankVisuals.ForGrade(grade));
            Assert.InRange(treatment.Strength, .01, 1);
            Assert.InRange(treatment.MotionIntensity, .01, 1);
            Assert.True(treatment.Strength > previousStrength);
            Assert.True(treatment.MotionIntensity > previousMotion);
            previousStrength = treatment.Strength;
            previousMotion = treatment.MotionIntensity;
        }
    }

    [Theory]
    [InlineData(19, LootItemRarity.Unique)] [InlineData(10, LootItemRarity.Unique)]
    [InlineData(9, LootItemRarity.Epic)] [InlineData(5, LootItemRarity.Epic)]
    [InlineData(4, LootItemRarity.Mythic)] [InlineData(2, LootItemRarity.Mythic)]
    [InlineData(1, LootItemRarity.Special)]
    public void RankColorGroupsAreAnOverlayPaletteIndependentOfItemOrPointsClassification(int grade, LootItemRarity palette)
    {
        var treatment = Assert.IsType<AbyssKillFeedRankVisuals.Treatment>(AbyssKillFeedRankVisuals.ForGrade(grade));
        Assert.Equal(LootRarityVisuals.Accent(palette), treatment.Accent);
    }

    [Theory]
    [InlineData(null)] [InlineData(0)] [InlineData(-1)] [InlineData(20)] [InlineData(int.MaxValue)]
    public void UnknownOrUnsupportedGradeNeverProducesRankLighting(int? grade)
    {
        Assert.Null(AbyssKillFeedRankVisuals.ForGrade(grade));
        using var empty = new Bitmap(160, 30);
        using var candidate = new Bitmap(160, 30);
        using (var graphics = Graphics.FromImage(candidate))
            AbyssKillFeedRankVisuals.PaintBackground(graphics, new(0, 0, 160, 30), grade, 2.3, true);
        Assert.Equal(Pixels(empty), Pixels(candidate));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(Snapshot(grade), false));
    }

    [Fact]
    public void FeedAnimationOnlyRunsForAnActuallySelectedVisibleKnownGradeAndHonorsReducedMotion()
    {
        var snapshot = Snapshot(1);
        Assert.True(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot, false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot, true));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot, false, presentationVisible: false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { KillFeedEnabled = false }, false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { CharacterBound = false }, false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { ShowKills = false }, false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { KillFeed = [] }, false));
        var rows = Enumerable.Range(0, 11).Select(index => Entry(index == 0 ? 1 : null) with
        { Sequence = index + 1, At = DateTimeOffset.UnixEpoch.AddSeconds(index) }).ToArray();
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { KillFeed = rows }, false));
        var death = Entry(1) with { Kind = AbyssKillFeedKind.Death };
        Assert.True(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { KillFeed = [death], ShowDeaths = true }, false));
        Assert.False(AbyssKillFeedRankVisuals.NeedsAnimation(snapshot with { KillFeed = [death], ShowDeaths = false }, false));
    }

    [Theory]
    [InlineData(AbyssKillFeedKind.Kill)] [InlineData(AbyssKillFeedKind.Death)]
    public void RankLightingMovesOnlyInsideFeedRowsAndStaticModeIsIndependentOfTime(AbyssKillFeedKind kind)
    {
        var snapshot = Snapshot(1, kind);
        using var first = Render(snapshot, 0, true);
        using var later = Render(snapshot, 2.3, true);
        var changed = Positions(first).Where(point => first.GetPixel(point.X, point.Y) != later.GetPixel(point.X, point.Y)).ToArray();
        Assert.NotEmpty(changed);
        Assert.All(changed, point => Assert.True(point.Y >= AbyssRankOverlayRenderer.KillFeedTop));
        Assert.Equal(Pixels(first, new(0, 0, 480, AbyssRankOverlayRenderer.Height)),
            Pixels(later, new(0, 0, 480, AbyssRankOverlayRenderer.Height)));
        using var still = Render(snapshot, 0, false);
        using var stillLater = Render(snapshot, 9, false);
        Assert.Equal(Pixels(still), Pixels(stillLater));
        using var unknown = Render(Snapshot(null, kind), 0, true);
        using var unknownLater = Render(Snapshot(null, kind), 9, true);
        Assert.Equal(Pixels(unknown), Pixels(unknownLater));
    }

    [Theory]
    [InlineData(AbyssKillFeedKind.Kill)] [InlineData(AbyssKillFeedKind.Death)]
    public void EveryRankKeepsItsGreenOrRedResultStripeDotAndLabelAboveTheEffect(AbyssKillFeedKind kind)
    {
        var expected = kind == AbyssKillFeedKind.Kill ? AbyssRankOverlayRenderer.GainColor : AbyssRankOverlayRenderer.LossColor;
        foreach (var grade in Enumerable.Range(1, 19))
        {
            using var image = Render(Snapshot(grade, kind), 2.3, true);
            var y = AbyssRankOverlayRenderer.KillFeedTop;
            Assert.Equal(expected.ToArgb(), image.GetPixel(14, y + 7).ToArgb());
            Assert.Equal(expected.ToArgb(), image.GetPixel(20, y + 13).ToArgb());
            Assert.Contains(Pixels(image, new(31, y + 2, 60, 23)), value => value == expected.ToArgb());
        }
    }

    [Fact]
    public void TheOpponentsGradeAloneChoosesTheFeedEffectAndNeverTheOwnPointsTier()
    {
        var snapshot = Snapshot(13);
        using var lowOwn = Render(snapshot, 2.3, true);
        using var highOwn = Render(snapshot with { RankPoints = 20_000_000 }, 2.3, true);
        var rows = new Rectangle(0, AbyssRankOverlayRenderer.KillFeedTop, 480, AbyssRankOverlayRenderer.KillFeedRowHeight);
        Assert.Equal(Pixels(lowOwn, rows), Pixels(highOwn, rows));
        using var differentGrade = Render(snapshot with { KillFeed = [Entry(1)] }, 2.3, true);
        Assert.NotEqual(Pixels(lowOwn, rows), Pixels(differentGrade, rows));
    }

    [Fact]
    public void SharedLightingRespectsAndRestoresTheCallersClip()
    {
        using var bitmap = new Bitmap(160, 60);
        using var graphics = Graphics.FromImage(bitmap);
        var clip = new RectangleF(20, 10, 80, 20);
        graphics.SetClip(clip);
        AbyssKillFeedRankVisuals.PaintBackground(graphics, new(0, 0, 160, 60), 1, 2.3, true);
        Assert.Equal(clip, graphics.ClipBounds);
        Assert.All(Positions(bitmap).Where(point => !clip.Contains(point)), point => Assert.Equal(0, bitmap.GetPixel(point.X, point.Y).A));
        Assert.Contains(Positions(bitmap).Where(point => clip.Contains(point)), point => bitmap.GetPixel(point.X, point.Y).A > 0);
    }

    private static AbyssKillFeedEntry Entry(int? grade) => new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill, "Example")
    { Generation = 1, Sequence = 1, OpponentClassId = "Ranger", OpponentAbyssRankGrade = grade };
    private static AbyssRankOverlaySnapshot Snapshot(int? grade, AbyssKillFeedKind kind = AbyssKillFeedKind.Kill) => new(0, true, true)
    { KillFeed = [Entry(grade) with { Kind = kind }], ShowDeaths = true };
    private static Bitmap Render(AbyssRankOverlaySnapshot snapshot, double seconds, bool animated) =>
        AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", language: "en",
            killFeedSeconds: seconds, animateKillFeed: animated);
    private static IEnumerable<Point> Positions(Bitmap image)
    {
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++) yield return new(x, y);
    }
    private static int[] Pixels(Bitmap image, Rectangle? bounds = null)
    {
        var area = bounds ?? new Rectangle(Point.Empty, image.Size);
        return Positions(image).Where(area.Contains).Select(point => image.GetPixel(point.X, point.Y).ToArgb()).ToArray();
    }
}
