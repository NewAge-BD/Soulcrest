using Soulcrest.App.Components;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementProgressUnitTests
{
    [Theory]
    [InlineData("de", "0.5", "0,5 Min.")]
    [InlineData("en", "0.5", "0.5 min")]
    [InlineData("de", "1.25", "1,25 Min.")]
    [InlineData("en", "125", "2 h 5 min")]
    public void ConvertedMinuteDisplayPreservesFractionsAndTheExistingWholeMinuteFormat(string language, string value, string expected)
    {
        var display = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, CombatAchievementsPresentation.ProgressDisplay(display, 9999, CombatAchievementProgressUnit.Minutes, language));
    }

    [Fact]
    public void DisplayConversionAndRawBarComparisonRemainIndependent()
    {
        Assert.Equal("Level 1", CombatAchievementsPresentation.ProgressDisplay(1m, 1500, CombatAchievementProgressUnit.Level, "en"));
        Assert.Equal("Stufe 1,5", CombatAchievementsPresentation.ProgressDisplay(1.5m, 1500, CombatAchievementProgressUnit.Level, "de"));
        Assert.Equal("50%", CombatAchievementsPresentation.ProgressPercent(1500, 3000));
        Assert.Equal("2 h 5 min", CombatAchievementsPresentation.ProgressDisplay(null, 125, CombatAchievementProgressUnit.Minutes, "en"));
        Assert.Equal("—", CombatAchievementsPresentation.ProgressDisplay(null, null, CombatAchievementProgressUnit.Level, "en"));
        Assert.Equal("—", CombatAchievementsPresentation.ProgressDisplay(1m, 100, CombatAchievementProgressUnit.Unknown, "en"));
        Assert.Equal("2,5 %", CombatAchievementsPresentation.ProgressDisplay(2.5m, 250, CombatAchievementProgressUnit.Percent, "de"));
        Assert.Equal("3 items", CombatAchievementsPresentation.ProgressDisplay(3m, 300, CombatAchievementProgressUnit.Items, "en"));
    }

    [Theory]
    [InlineData("de", 0, "0 Min.")]
    [InlineData("en", 0, "0 min")]
    [InlineData("de", 59, "59 Min.")]
    [InlineData("en", 60, "1 h")]
    [InlineData("de", 120, "2 Std.")]
    [InlineData("de", 125, "2 Std. 5 Min.")]
    [InlineData("en", 125, "2 h 5 min")]
    public void MinutesFormatAsLocalizedHoursAndMinutesWithoutChangingTheCounter(string language, long value, string expected)
    {
        Assert.Equal(expected, CombatAchievementsPresentation.ProgressValue(value, CombatAchievementProgressUnit.Minutes, language));
    }

    [Theory]
    [InlineData("de", "Stufe 23")]
    [InlineData("en", "Level 23")]
    public void LevelHasAClearLocalizedLabel(string language, string expected) =>
        Assert.Equal(expected, CombatAchievementsPresentation.ProgressValue(23, CombatAchievementProgressUnit.Level, language));

    [Fact]
    public void CountFormattingUnknownValuesAndVisualRatiosRemainIndependentOfTheUnit()
    {
        Assert.Equal("1.250", CombatAchievementsPresentation.ProgressValue(1250, CombatAchievementProgressUnit.Count, "de"));
        Assert.Equal("1,250", CombatAchievementsPresentation.ProgressValue(1250, CombatAchievementProgressUnit.Count, "en"));
        Assert.Equal("—", CombatAchievementsPresentation.ProgressValue(null, CombatAchievementProgressUnit.Minutes, "en"));
        Assert.Equal("50%", CombatAchievementsPresentation.ProgressPercent(125, 250));
        Assert.Equal("100%", CombatAchievementsPresentation.ProgressPercent(500, 250));
        Assert.Null(CombatAchievementsPresentation.ProgressPercent(null, 250));
        Assert.Null(CombatAchievementsPresentation.ProgressPercent(125, null));
        Assert.Equal("—", CombatAchievementsPresentation.ProgressValue(125, CombatAchievementProgressUnit.Unknown, "en"));
    }
}
