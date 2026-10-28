using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssKillFeedRenderTests
{
    [Theory]
    [InlineData("Gladiator")] [InlineData("Templar")] [InlineData("Ranger")] [InlineData("Assassin")]
    [InlineData("Elementalist")] [InlineData("Sorcerer")] [InlineData("Cleric")] [InlineData("Chanter")]
    public void KnownClassKeysUseExistingBundledOriginalIcons(string classId)
    {
        var asset = AbyssOpponentIconAssets.ClassAsset(classId);
        Assert.Equal($"class-icons/{classId}.png", asset);
        using var cache = new LootOverlayIconCache();
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        Assert.True(cache.Draw(graphics, asset, new(0, 0, 32, 32)));
    }

    [Theory]
    [InlineData(1, "Supreme Commander")] [InlineData(2, "Commander")] [InlineData(3, "Marshal")]
    [InlineData(4, "General")] [InlineData(5, "5-Star Officer")] [InlineData(6, "4-Star Officer")]
    [InlineData(7, "3-Star Officer")] [InlineData(8, "2-Star Officer")] [InlineData(9, "1-Star Officer")]
    [InlineData(10, "First-Class Soldier")] [InlineData(11, "Second-Class Soldier")]
    [InlineData(12, "Third-Class Soldier")] [InlineData(13, "Fourth-Class Soldier")]
    [InlineData(14, "Fifth-Class Soldier")] [InlineData(15, "Sixth-Class Soldier")]
    [InlineData(16, "Seventh-Class Soldier")] [InlineData(17, "Eighth-Class Soldier")]
    [InlineData(18, "Ninth-Class Soldier")] [InlineData(19, "Trainee")]
    public void ActualNativeGradeUsesTheOriginalOrdinalRatherThanThePointsStageIndex(int grade, string label)
    {
        var asset = AbyssOpponentIconAssets.RankAsset(grade);
        Assert.Equal($"abyss-rank-icons/rank-{grade:00}.png", asset);
        Assert.Equal(label, Assert.Single(AbyssRankCatalog.Default.Stages, stage => stage.IconAsset == asset).NameEn);
        using var cache = new LootOverlayIconCache();
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        Assert.True(cache.Draw(graphics, asset, new(0, 0, 32, 32)));
    }

    [Theory]
    [InlineData(null, null)] [InlineData("", 0)] [InlineData("Brawler", 20)]
    [InlineData("cleric", -1)] [InlineData("../Cleric", int.MaxValue)]
    public void UnknownMetadataNeverInventsAClassOrRankIcon(string? classId, int? grade)
    {
        Assert.Null(AbyssOpponentIconAssets.ClassAsset(classId));
        Assert.Null(AbyssOpponentIconAssets.RankAsset(grade));
        var snapshot = Snapshot([Entry("Example")]);
        using var unknown = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized");
        using var invalid = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        {
            KillFeed = [Entry("Example") with { OpponentClassId = classId, OpponentAbyssRankGrade = grade }]
        }, "aion2-stylized");
        Assert.Equal(Pixels(unknown), Pixels(invalid));
    }

    [Theory]
    [InlineData(AbyssKillFeedKind.Kill)] [InlineData(AbyssKillFeedKind.Death)]
    public void ClassThenActualRankPrecedeTheNameWithoutChangingTheRankPointsPanel(AbyssKillFeedKind kind)
    {
        var entry = Entry("Example") with { Kind = kind, OpponentClassId = "Cleric", OpponentAbyssRankGrade = 1 };
        var snapshot = Snapshot([entry]); // own points deliberately identify the opposite, lowest points tier
        using var commander = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized");
        using var trainee = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { OpponentAbyssRankGrade = 19 }] }, "aion2-stylized");
        using var noClass = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { OpponentClassId = null }] }, "aion2-stylized");
        var y = AbyssRankOverlayRenderer.KillFeedTop + 3;
        // Only the fully backed interior is grade-independent; anti-aliased outer
        // edges correctly blend with the opponent's changing rank background.
        var classSlot = new Rectangle(98, y + 2, 18, 17);
        var rankSlot = new Rectangle(120, y, 22, 21);
        Assert.Equal(Pixels(commander, classSlot), Pixels(trainee, classSlot));
        Assert.NotEqual(Pixels(commander, rankSlot), Pixels(trainee, rankSlot));
        Assert.NotEqual(Pixels(commander, classSlot), Pixels(noClass, classSlot));
        Assert.Equal(Pixels(commander, AbyssRankOverlayRenderer.ProgressBounds),
            Pixels(trainee, AbyssRankOverlayRenderer.ProgressBounds));
    }

    [Theory]
    [InlineData("de")] [InlineData("en")]
    public void TenRowsAndLongOpponentNamesFitEveryThemeAndScaleWithNoInventedRp(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var entries = Enumerable.Range(0, 13).Select(index => Entry(
                language == "de" ? $"Beispielgegner mit sehr langem Namen {index}" : $"Example opponent with a very long name {index}") with
            {
                Sequence = index + 1,
                At = DateTimeOffset.UnixEpoch.AddSeconds(index),
                Kind = index % 3 == 0 ? AbyssKillFeedKind.Death : AbyssKillFeedKind.Kill,
                OpponentClassId = index % 3 == 0 ? null : Classes[index % Classes.Length],
                OpponentAbyssRankGrade = index % 3 == 0 ? null : index + 1,
                AbyssPointGain = index % 3 == 1 ? 200 : null,
                RankPointDelta = index % 3 == 0 ? -585 : index % 3 == 1 ? 200 : null
            }).ToArray();
            var snapshot = Snapshot(entries);
            Assert.Equal(Enumerable.Range(4, 10).Reverse().Select(value => (long)value),
                AbyssRankOverlayRenderer.KillFeedEntries(snapshot).Select(row => row.Sequence));
            foreach (var theme in OverlayThemeDefinition.All)
            foreach (var scale in new[] { .6, 1, 2 })
            {
                using var bitmap = AbyssRankOverlayRenderer.RenderBitmap(snapshot, theme.Id, scale, language: language);
                Assert.Equal((int)Math.Ceiling(480 * scale), bitmap.Width);
                Assert.Equal((int)Math.Ceiling(487 * scale), bitmap.Height);
                var lastRow = Rectangle.Ceiling(new RectangleF(17 * (float)scale,
                    (AbyssRankOverlayRenderer.KillFeedTop + 9 * AbyssRankOverlayRenderer.KillFeedRowHeight) * (float)scale,
                    70 * (float)scale, AbyssRankOverlayRenderer.KillFeedRowHeight * (float)scale));
                Assert.Contains(Points(lastRow), point => bitmap.GetPixel(point.X, point.Y).G > 180
                    && bitmap.GetPixel(point.X, point.Y).R < 150 || bitmap.GetPixel(point.X, point.Y).R > 220
                    && bitmap.GetPixel(point.X, point.Y).G < 160);
                var output = Environment.GetEnvironmentVariable("SOULCREST_ABYSS_KILLFEED_PNG_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(output) && theme.Id == "aion2-stylized" && scale == 1)
                {
                    Directory.CreateDirectory(output);
                    bitmap.Save(Path.Combine(output, $"abyss-killfeed-{language}.png"), ImageFormat.Png);
                }
            }
            using var noRp = AbyssRankOverlayRenderer.RenderBitmap(Snapshot([Entry("Example")]), "aion2-stylized");
            var rpColumn = new Rectangle(355, AbyssRankOverlayRenderer.KillFeedTop, 108, 23);
            Assert.DoesNotContain(Points(rpColumn), point => noRp.GetPixel(point.X, point.Y).G > 180
                && noRp.GetPixel(point.X, point.Y).R < 150);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de")] [InlineData("en")]
    public void UnknownAmountsStayIndependentAndNeverBecomeZeros(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        Assert.Equal("— AP", AbyssRankOverlayRenderer.KillFeedAmountLabel(null, "AP", culture));
        Assert.Equal("— RP", AbyssRankOverlayRenderer.KillFeedAmountLabel(null, "RP", culture));
        Assert.Equal("0 AP", AbyssRankOverlayRenderer.KillFeedAmountLabel(0, "AP", culture));
        Assert.Equal("0 RP", AbyssRankOverlayRenderer.KillFeedAmountLabel(0, "RP", culture));
        Assert.Equal("+200 AP", AbyssRankOverlayRenderer.KillFeedAmountLabel(200, "AP", culture));
        Assert.Equal("-585 RP", AbyssRankOverlayRenderer.KillFeedAmountLabel(-585, "RP", culture));
        var entry = Entry("Example") with { OpponentClassId = "Ranger", OpponentAbyssRankGrade = 19 };
        var snapshot = Snapshot([entry]);
        using var missing = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", language: language);
        using var ap = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { AbyssPointGain = 200 }] }, "aion2-stylized", language: language);
        using var rp = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { RankPointDelta = 200 }] }, "aion2-stylized", language: language);
        using var zeros = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { AbyssPointGain = 0, RankPointDelta = 0 }] }, "aion2-stylized", language: language);
        var top = new Rectangle(355, AbyssRankOverlayRenderer.KillFeedTop, 108, 13);
        var bottom = new Rectangle(355, AbyssRankOverlayRenderer.KillFeedTop + 14, 108, 13);
        Assert.NotEqual(Pixels(missing, top), Pixels(ap, top));
        Assert.Equal(Pixels(missing, bottom), Pixels(ap, bottom));
        Assert.Equal(Pixels(missing, top), Pixels(rp, top));
        Assert.NotEqual(Pixels(missing, bottom), Pixels(rp, bottom));
        Assert.NotEqual(Pixels(missing, top), Pixels(zeros, top));
        Assert.NotEqual(Pixels(missing, bottom), Pixels(zeros, bottom));
    }

    [Theory]
    [InlineData(AbyssKillFeedKind.Kill)] [InlineData(AbyssKillFeedKind.Assist)]
    [InlineData(AbyssKillFeedKind.Death)]
    public void VeryLargeAmountsStayInsideTheirColumnAndDoNotMoveIconsOrTheName(AbyssKillFeedKind kind)
    {
        var entry = Entry(new string('W', 96)) with
        { Kind = kind, OpponentClassId = "Cleric", OpponentAbyssRankGrade = 1 };
        var snapshot = Snapshot([entry]);
        using var missing = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized");
        using var huge = AbyssRankOverlayRenderer.RenderBitmap(snapshot with
        { KillFeed = [entry with { AbyssPointGain = long.MaxValue, RankPointDelta = long.MinValue }] }, "aion2-stylized");
        var column = new Rectangle(355, AbyssRankOverlayRenderer.KillFeedTop, 108, 27);
        var changed = Points(new(Point.Empty, huge.Size)).Where(point => huge.GetPixel(point.X, point.Y)
            != missing.GetPixel(point.X, point.Y)).ToArray();
        Assert.NotEmpty(changed);
        Assert.All(changed, point => Assert.True(column.Contains(point)));
        Assert.Contains(Points(column), point => huge.GetPixel(point.X, point.Y).R > 220
            && huge.GetPixel(point.X, point.Y).G < 160);
    }

    [Fact]
    public void DeathShowsOnlyItsRankLossAndNeverInventsAnApLossOrGain()
    {
        var entry = Entry("Example") with
        { Kind = AbyssKillFeedKind.Death, OpponentClassId = "Templar", OpponentAbyssRankGrade = 13, RankPointDelta = -585 };
        using var death = AbyssRankOverlayRenderer.RenderBitmap(Snapshot([entry]), "aion2-stylized");
        using var extraneousAp = AbyssRankOverlayRenderer.RenderBitmap(Snapshot([entry with
        { AbyssPointGain = long.MaxValue }]), "aion2-stylized");
        Assert.Equal(Pixels(death), Pixels(extraneousAp));
    }

    private static readonly string[] Classes = ["Gladiator", "Templar", "Ranger", "Assassin", "Elementalist", "Sorcerer", "Cleric", "Chanter"];

    private static AbyssKillFeedEntry Entry(string name) => new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill, name)
    { Generation = 1, Sequence = 1 };

    private static AbyssRankOverlaySnapshot Snapshot(IReadOnlyList<AbyssKillFeedEntry> entries) => new(0, true, true)
    { KillFeed = entries, ShowDeaths = true, SessionActive = false };

    private static IEnumerable<Point> Points(Rectangle bounds)
    {
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++) yield return new(x, y);
    }

    private static int[] Pixels(Bitmap bitmap, Rectangle? bounds = null) =>
        Points(bounds ?? new(Point.Empty, bitmap.Size)).Select(point => bitmap.GetPixel(point.X, point.Y).ToArgb()).ToArray();
}
