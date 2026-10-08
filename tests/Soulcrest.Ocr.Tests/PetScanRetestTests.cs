using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>OCR strings recorded during the 2026-10-08 live retest, not invented image fixtures.</summary>
public sealed class PetScanRetestTests
{
    [Theory]
    [InlineData(.407)]
    [InlineData(.415)]
    [InlineData(.423)]
    [InlineData(.438)]
    public void MomoMissingLeadingDigitCannotOutvoteTheVisibleBar(double bar)
    {
        Assert.Null(CardReadConsensus.Resolve([("1/75", 0), ("1/75", 1), ("1/75", 2)], 2, bar));
        Assert.Equal(31, CardReadConsensus.Resolve([("31/75", 0), ("31/75", 1)], 2, bar)?.SoulsInLevel);
        Assert.True(PetWindowScanner.ContradictsVisibleBar(new(2, 1, 75, false), bar));
    }

    [Fact]
    public void AVisibleBarRejectsRadicallyWrongHighReadingsButHiddenBarsStayUnknown()
    {
        Assert.Null(CardReadConsensus.Resolve([("61/75", 0), ("61/75", 1), ("61/75", 2)], 2, .423));
        Assert.Equal(2, CardReadConsensus.Resolve([("2/75", 0), ("2/75", 1)], 2, 0)?.SoulsInLevel);
    }

    [Theory]
    [InlineData(2, 33, .446, "33175|33/75|33 75|331/75")]
    [InlineData(2, 8, 0, "8/75|8/75")]
    [InlineData(2, 7, 0, "7/75|7/715|7/715|7/715|7175|7/715")]
    [InlineData(2, 6, 0, "6/7|6/7|87|87")]
    [InlineData(2, 4, 0, "4/75|4/75|4/7|4/15_|$7.5")]
    [InlineData(2, 3, .137, "3/75|3/75|3/7Sq|3/75N|3/55|3/55")]
    public void KnownCircleRecoversAgreedNumerators(int level, int souls, double bar, string raw)
    {
        // Independent preprocessing views. Separate tests below model two scales of only one mask.
        var attempts = raw.Split('|').Select((text, i) => (text, i));
        var result = CardReadConsensus.Resolve(attempts, level, bar);
        Assert.NotNull(result);
        Assert.Equal(souls, result.SoulsInLevel);
        Assert.Equal(level, result.Level);
    }

    [Fact]
    public void SelectedOdyleNeedsPreciseBarWhenOnlyOneMaskAgrees()
    {
        (string, int)[] views = [("5/25", 1), ("5/25", 1)];
        Assert.Equal(5, CardReadConsensus.Resolve(views, 1, .187)?.SoulsInLevel);
        Assert.Null(CardReadConsensus.Resolve(views, 1, .24));
        Assert.Null(CardReadConsensus.Resolve(views, 1, 0));
        Assert.Null(CardReadConsensus.Resolve(views, null, .187));
    }

    [Fact]
    public void WrongOdyleAndMissingLeadingDigitsStayRejected()
    {
        Assert.Null(CardReadConsensus.Resolve([("515", 0), ("3+25", 1), ("3/25", 2), ("i! 575", 3)], 1, .204));
        Assert.Null(CardReadConsensus.Resolve([("1/25", 0), ("1/25", 1), ("1/25", 2)], 1, .45));
        Assert.Null(CardReadConsensus.Resolve([("7/75", 0), ("2/75", 1)], 2, 0));
        Assert.Null(CardReadConsensus.Resolve([("MAX", 0), ("MAX", 1)], 2, 0));
    }

    [Theory]
    [InlineData("331/75")]
    [InlineData("75/75")]
    [InlineData("3/25")]
    [InlineData("3/55")]
    [InlineData("7/7512")]
    public void DenominatorRepairDoesNotInventANumeratorOrChangeAnExplicitOtherLevel(string text) =>
        Assert.Null(CardReadConsensus.ParseAtLevel(text, 2));

    [Fact]
    public void TabHeadingCannotBecomeThePanelName()
    {
        Assert.False(PetWindowScanner.IsNameLine("All Owned Effe...", 15, 25, 180, 18, 1440));
        Assert.False(PetWindowScanner.IsNameLine("All Owned Effe...", 110, 25, 180, 18, 1440));
        Assert.True(PetWindowScanner.IsNameLine("Fossa", 100, 22, 180, 18, 1440));
        Assert.False(PetWindowScanner.IsNameLine("Pet Insight", 180, 18, 180, 18, 1440));
    }
}
