using Soulcrest.Core.PetWindow;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetWindowTextTests
{
    [Theory]
    [InlineData("MAX", 3, 0, 0, true)]
    [InlineData("MAX_ 3", 3, 0, 0, true)]
    [InlineData("42/75", 2, 42, 75, false)]
    [InlineData("20 / 75", 2, 20, 75, false)]
    [InlineData("17I75", 2, 17, 75, false)]
    [InlineData("3/25", 1, 3, 25, false)]
    [InlineData("2/5", 0, 2, 5, false)]
    [InlineData("4/S", 0, 4, 5, false)]
    [InlineData("42175", 2, 42, 75, false)]
    [InlineData("17175", 2, 17, 75, false)]
    [InlineData("11175", 2, 11, 75, false)]
    [InlineData("3125", 1, 3, 25, false)]
    [InlineData("415", 0, 4, 5, false)]
    [InlineData("215", 0, 2, 5, false)]
    public void ParsesCardProgress(string text, int level, int inLevel, int needed, bool isMax)
    {
        var progress = PetWindowText.ParseCard(text);
        Assert.Equal(new PetCardProgress(level, inLevel, needed, isMax), progress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Equipped")]
    [InlineData("80/75")]
    [InlineData("12/40")]
    public void RejectsNonProgress(string text) => Assert.Null(PetWindowText.ParseCard(text));

    [Theory]
    [InlineData("Lv. 3 (MAX)", 3)]
    [InlineData("Lv.2", 2)]
    [InlineData("Lv. I", 1)]
    [InlineData("Pet-Kenntnis St. 1", 1)]
    [InlineData("St. 2 (31/75)", 2)]
    [InlineData("St. 3 (MAX)", 3)]
    [InlineData("Pet Insight", null)]
    public void ParsesPanelLevel(string text, int? level) => Assert.Equal(level, PetWindowText.ParsePanelLevel(text));

    [Theory]
    [InlineData(0.754, 4)] // fixture: 4/5 cards measured 0.73–0.75
    [InlineData(0.588, 3)]
    [InlineData(0.412, 2)]
    [InlineData(0.0, 0)]
    public void ReadsLockedSoulsFromBar(double fill, int souls) => Assert.Equal(souls, PetWindowText.SoulsFromBar(fill, 5));

    [Theory]
    [InlineData("6/2Ä", 1, 6, 25)] // live capture 2026-10-03
    [InlineData("5/22", 1, 5, 25)]
    [InlineData("30/7?", 2, 30, 75)]
    public void LenientParseCompletesGarbledDenominator(string text, int level, int inLevel, int needed) =>
        Assert.Equal(new PetCardProgress(level, inLevel, needed, false), PetWindowText.ParseCardLenient(text));

    [Fact]
    public void StrictParseRejectsGarbledDenominator() => Assert.Null(PetWindowText.ParseCard("6/2Ä"));

    [Fact]
    public void BarIsNotUsedForCoarseLevels() => Assert.Null(PetWindowText.SoulsFromBar(0.56, 75));

    [Theory]
    [InlineData(0.33, 8)] // live capture: 8/25 measured 0.32–0.33
    [InlineData(0.25, 6)]
    [InlineData(0.19, 5)]
    [InlineData(0.16, 4)]
    public void ReadsLevelOneSoulsFromBar(double fill, int souls) => Assert.Equal(souls, PetWindowText.SoulsFromBar(fill, 25));

    [Theory]
    [InlineData("Pet Insight@ Lv. 6 | 1 (5/25)", 1, 5, 25, false)] // live capture 2026-10-03 (Tog)
    [InlineData("Pet Insight Lv. 3 (MAX)", 3, 0, 0, true)]
    [InlineData("Pet Insight Lv. 2 (40/75)", 2, 40, 75, false)]
    public void ParsesPanelProgress(string text, int level, int inLevel, int needed, bool isMax) =>
        Assert.Equal(new PetCardProgress(level, inLevel, needed, isMax), PetWindowText.ParsePanelProgress(text));

    [Fact]
    public void PanelWithoutProgressYieldsNull() => Assert.Null(PetWindowText.ParsePanelProgress("Pet Insight Lv. 1"));

    [Fact]
    public void ParsesCollectionStatus() => Assert.Equal((94, 200), PetWindowText.ParseCollection("Collection Status 94/200"));

    [Theory]
    [InlineData("Sammlungsfortschritt 174/200", 174)]
    [InlineData("Sammlungsfortschritt 0/200", 0)]
    public void ParsesConfirmedGermanCollectionCaption(string text, int owned) =>
        Assert.Equal((owned, 200), PetWindowText.ParseCollection(text));

    [Theory]
    [InlineData("15/75")]
    [InlineData("201/200")]
    [InlineData("174/200 175/200")]
    [InlineData("")]
    public void CollectionFooterRejectsCardValuesAndAmbiguousCounts(string text) =>
        Assert.Null(PetWindowText.ParseCollectionFooter(text));
}
