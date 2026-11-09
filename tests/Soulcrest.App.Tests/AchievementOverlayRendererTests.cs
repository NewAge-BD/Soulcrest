using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Components;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementOverlayRendererTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void FavoriteMarkerChangesOnlyItsSmallHeaderAndEdgeRegionsAcrossAllThemes(string language)
    {
        foreach (var theme in OverlayThemeDefinition.All)
        {
            var row = Example();
            using var favorite = AchievementOverlayRenderer.Render([row], theme, language: language);
            using var recent = AchievementOverlayRenderer.Render([row with { IsFavorite = false }], theme, language: language);
            var header = new Rectangle(325, AchievementOverlayRenderer.HeaderHeight + 12, 20, 22);
            var edge = new Rectangle(9, AchievementOverlayRenderer.HeaderHeight + 14, 5, 80);
            var headerChanges = 0; var edgeChanges = 0;
            for (var y = 0; y < favorite.Height; y++)
            for (var x = 0; x < favorite.Width; x++)
            {
                if (favorite.GetPixel(x, y) == recent.GetPixel(x, y)) continue;
                if (header.Contains(x, y)) headerChanges++;
                else if (edge.Contains(x, y)) edgeChanges++;
                else Assert.Fail("Favorite paint changed text, progress, rewards or the surrounding panel.");
            }
            Assert.InRange(headerChanges, 15, 180);
            Assert.InRange(edgeChanges, 40, 240);
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void LongFavoriteNameCannotPaintOverTheHeaderStar(string language)
    {
        var row = Example() with { NameDe = new string('W', 160), NameEn = new string('W', 160) };
        using var longName = AchievementOverlayRenderer.Render([row], language: language);
        using var emptyName = AchievementOverlayRenderer.Render([row with { NameDe = "", NameEn = "" }], language: language);
        for (var y = AchievementOverlayRenderer.HeaderHeight + 12; y < AchievementOverlayRenderer.HeaderHeight + 34; y++)
        for (var x = 325; x < 345; x++) Assert.Equal(emptyName.GetPixel(x, y), longName.GetPixel(x, y));
    }

    [Theory]
    [InlineData("de", "Titelziel: Mein Titel")]
    [InlineData("en", "Title goal: My title")]
    public void TrackedTitleGoalIsVisibleAlongsideTheCurrentAchievementCondition(string language, string expected)
    {
        var row = Example() with { TrackedTitleId = 11, TitleNameDe = "Mein Titel", TitleNameEn = "My title" };
        var condition = AchievementOverlayRenderer.Condition(row, language);
        Assert.StartsWith(expected + "\n", condition);
        Assert.EndsWith(language == "de" ? row.ConditionDe : row.ConditionEn, condition);
        Assert.DoesNotContain(expected, AchievementOverlayRenderer.Condition(row with { IsTitlePlaceholder = true }, language));
        using var image = AchievementOverlayRenderer.Render([row], language: language);
        Assert.Equal(AchievementOverlayRenderer.HeightFor(1), image.Height);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void ProductionRendererSupportsEverySharedThemeAndRetainsTheFullRowList(string language)
    {
        var rows = Enumerable.Range(0, 9).Select(index => Example(index + 1)).ToArray();
        Assert.Equal(9 * AchievementOverlayRenderer.RowHeight + AchievementOverlayRenderer.HeaderHeight
            + AchievementOverlayRenderer.FooterHeight, AchievementOverlayRenderer.HeightFor(rows.Length));
        foreach (var theme in OverlayThemeDefinition.All)
        {
            using var image = AchievementOverlayRenderer.Render(rows, theme, maximumHeight: 640, language: language);
            Assert.Equal(360, image.Width);
            Assert.Equal(640, image.Height);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.True(image.GetPixel(180, 50).A > 0);
            using var bottom = AchievementOverlayRenderer.Render(rows, theme, maximumHeight: 640, scrollOffset: 10000, language: language);
            Assert.NotEqual(ImageHash(image), ImageHash(bottom));
        }
    }

    [Fact]
    public void UnknownAndNegativeRawCountersNeverAcquireAnInventedTargetOrPercentage()
    {
        var row = Example() with { State = AchievementTrackerRowState.Unknown, Current = null, Target = null, RawValue = -17,
            Unit = CombatAchievementProgressUnit.Unknown };
        Assert.Null(row.ProgressFraction);
        Assert.Equal("Reported value: -17", AchievementOverlayRenderer.Counter(row, "en"));
        Assert.Equal("Gemeldeter Wert: -17", AchievementOverlayRenderer.Counter(row, "de"));
        Assert.DoesNotContain("/", AchievementOverlayRenderer.Counter(row, "en"));
        using var image = AchievementOverlayRenderer.Render([row]);
        Assert.Equal(AchievementOverlayRenderer.HeightFor(1), image.Height);
    }

    [Fact]
    public void AReportedValueRemainsKnownWhenTheGoalIsUnqualifiedAndAbsenceRemainsUnknown()
    {
        var row = Example() with { State = AchievementTrackerRowState.Unknown, Current = null, Target = null,
            RawValue = 3, Unit = CombatAchievementProgressUnit.Unknown };
        Assert.Equal("Reported value: 3", AchievementOverlayRenderer.Counter(row, "en"));
        Assert.Equal("Gemeldeter Wert: 3", AchievementOverlayRenderer.Counter(row, "de"));
        Assert.Null(row.ProgressFraction);
        Assert.Equal("Progress unknown", AchievementOverlayRenderer.Counter(row with { RawValue = null }, "en"));
        Assert.Equal("Fortschritt unbekannt", AchievementOverlayRenderer.Counter(row with { RawValue = null }, "de"));
    }

    [Fact]
    public void QualifiedMinutesAndRewardAvailabilityUseDistinctReadableLabels()
    {
        var time = Example() with { Current = 10, Target = 30, Unit = CombatAchievementProgressUnit.Minutes };
        Assert.Equal("10 min / 30 min", AchievementOverlayRenderer.Counter(time, "en"));
        Assert.Equal("Reward available", AchievementOverlayRenderer.Counter(time with { State = AchievementTrackerRowState.AwaitingClaim }, "en"));
        Assert.Equal("Abgeschlossen!", AchievementOverlayRenderer.Counter(time with { State = AchievementTrackerRowState.Celebrating }, "de"));
    }

    [Fact]
    public void APreviousStagesCelebrationShowsTheCurrentFractionWithoutCompletingTheNewStage()
    {
        var row = Example() with { StageId = 102, Current = 10, Target = 20,
            State = AchievementTrackerRowState.Celebrating, CelebratedStageId = 101, HasUnclaimedRewards = true };
        Assert.Equal("✓ 10 / 20", AchievementOverlayRenderer.Counter(row, "en"));
        Assert.Equal("✓ 10 / 20", AchievementOverlayRenderer.Counter(row, "de"));
        Assert.False(row.IsCompleted); Assert.Equal(.5, row.ProgressFraction);
        Assert.Equal("Completed!", AchievementOverlayRenderer.Counter(row with { CelebratedStageId = null }, "en"));
    }

    [Fact]
    public void CompletionPulseAndSweepArePaintedOnTheProductionRow()
    {
        var row = Example() with { State = AchievementTrackerRowState.Celebrating, Current = 5, Target = 5, CompletionProgress = .12 };
        using var first = AchievementOverlayRenderer.Render([row]);
        using var next = AchievementOverlayRenderer.Render([row with { CompletionProgress = .14 }]);
        using var settled = AchievementOverlayRenderer.Render([row with { State = AchievementTrackerRowState.AwaitingClaim }]);
        Assert.NotEqual(ImageHash(first), ImageHash(next));
        Assert.NotEqual(ImageHash(next), ImageHash(settled));
    }

    [Fact]
    public void LongCountersLeaveAllThreeRewardIconsUnobscured()
    {
        var row = Example() with { Current = long.MaxValue, Target = long.MaxValue,
            Rewards = [new("A", "A", null, 1), new("B", "B", null, 2), new("C", "C", null, 3)] };
        using var longCounter = AchievementOverlayRenderer.Render([row]);
        using var shortCounter = AchievementOverlayRenderer.Render([row with { Current = 1, Target = 1 }]);
        for (var y = AchievementOverlayRenderer.HeaderHeight + 77; y < AchievementOverlayRenderer.HeaderHeight + 95; y++)
        for (var x = 195; x < 213; x++) Assert.Equal(shortCounter.GetPixel(x, y), longCounter.GetPixel(x, y));
    }

    [Theory]
    [InlineData("de", "Gespeichert: 11 / 20")]
    [InlineData("en", "Saved: 11 / 20")]
    public void HistoricalCountersNameTheirSourceWithoutInventingLiveClaimsOrCompletions(string language, string expected)
    {
        var row = Example() with { IsHistorical = true, Current = 11, Target = 20 };
        Assert.Equal(expected, AchievementOverlayRenderer.Counter(row, language));
        Assert.Equal(expected, AchievementOverlayRenderer.Counter(row with { State = AchievementTrackerRowState.AwaitingClaim }, language));
        Assert.Equal(expected, AchievementOverlayRenderer.Counter(row with { State = AchievementTrackerRowState.Celebrating }, language));
        var unknown = row with { Current = null, Target = null, RawValue = 11, Unit = CombatAchievementProgressUnit.Unknown };
        Assert.Equal(language == "de" ? "Gespeichert: Gemeldeter Wert: 11" : "Saved: Reported value: 11",
            AchievementOverlayRenderer.Counter(unknown, language));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void HistoricalRowsKeepThreeRewardsUnobscuredAndNeverAnimate(string language)
    {
        var row = Example() with { IsHistorical = true, Current = 11, Target = 20,
            State = AchievementTrackerRowState.Celebrating, CompletionProgress = .1,
            Rewards = [new("A", "A", null, 1), new("B", "B", null, 2), new("C", "C", null, 3)] };
        foreach (var theme in OverlayThemeDefinition.All)
        {
            using var saved = AchievementOverlayRenderer.Render([row], theme, language: language);
            using var later = AchievementOverlayRenderer.Render([row with { CompletionProgress = .8 }], theme, language: language);
            Assert.Equal(ImageHash(saved), ImageHash(later));
            using var longCounter = AchievementOverlayRenderer.Render([row with { Current = long.MaxValue, Target = long.MaxValue }], theme, language: language);
            using var live = AchievementOverlayRenderer.Render([row with { IsHistorical = false, State = AchievementTrackerRowState.InProgress }], theme, language: language);
            for (var y = AchievementOverlayRenderer.HeaderHeight + 77; y < AchievementOverlayRenderer.HeaderHeight + 96; y++)
            for (var x = 195; x < 340; x++)
            {
                Assert.Equal(saved.GetPixel(x, y), longCounter.GetPixel(x, y));
                Assert.Equal(saved.GetPixel(x, y), live.GetPixel(x, y));
            }
            Assert.NotEqual(ImageHash(saved), ImageHash(live));
        }
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(.1, .6)]
    [InlineData(9, 2.5)]
    public void ScaleIsFiniteAndBoundedAndNativeHeightUsesOnlyEightyPercentOfTheWorkingArea(double requested, double expected)
    {
        Assert.Equal(expected, AchievementOverlayRenderer.NormalizeScale(requested));
        Assert.InRange(AchievementOverlayRenderer.ViewportHeight(100, requested, 1000), 1, 800);
    }

    internal static AchievementTrackerRow Example(int id = 1) => new()
    {
        FamilyId = id, StageId = id + 1, NameDe = "Mit dem Wind I", NameEn = "Riding the Winds I",
        ConditionDe = "Nutze Windpfade 5-mal.", ConditionEn = "Use Windstreams 5 times.",
        Current = 4, Target = 5, Unit = CombatAchievementProgressUnit.Count, IsFavorite = true,
        State = AchievementTrackerRowState.InProgress,
        Rewards = [new("Kinah", "Kina", MonsterLootIconCatalog.Default.ItemAsset(930100031), 7000)],
    };

    private static long ImageHash(Bitmap image)
    {
        var hash = 17L;
        for (var y = 0; y < image.Height; y += 3)
        for (var x = 0; x < image.Width; x += 3) hash = unchecked(hash * 31 + image.GetPixel(x, y).ToArgb());
        return hash;
    }
}
