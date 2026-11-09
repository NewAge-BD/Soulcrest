using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatScoreNavigationTests
{
    [Fact]
    public void CalculatorSectionsAreEphemeralAndDoNotChangeTheInitialRootTab()
    {
        var first = new UiState();
        Assert.Equal("map", first.Tab);
        Assert.Equal("stat-overview", first.CombatScoreSection);
        first.ShowCombatScoreSection("titles");

        var next = new UiState();
        Assert.Equal("map", next.Tab);
        Assert.Equal("stat-overview", next.CombatScoreSection);
    }

    [Fact]
    public void LegacyDaevanionLinkOpensItsSectionAndPublishesOneCompleteNavigationState()
    {
        var ui = new UiState();
        List<(string Tab, string Section)> observed = [];
        ui.Changed += () => observed.Add((ui.Tab, ui.CombatScoreSection));

        ui.Navigate("daevanion-calculator");

        Assert.Equal(("combat-score", "daevanion"), Assert.Single(observed));
        Assert.Equal("combat-score", ui.Tab);
        Assert.Equal("daevanion", ui.CombatScoreSection);
    }

    [Theory]
    [InlineData("stat-overview")]
    [InlineData("daevanion")]
    [InlineData("wings")]
    [InlineData("gear")]
    [InlineData("pets")]
    [InlineData("genus-insight")]
    [InlineData("pantheon")]
    [InlineData("achievements")]
    [InlineData("titles")]
    public void EveryWorkspaceSectionOpensInsideTheSameRootTab(string section)
    {
        var ui = new UiState();
        var changed = 0;
        ui.Changed += () => changed++;

        ui.ShowCombatScoreSection(section);

        Assert.Equal("combat-score", ui.Tab);
        Assert.Equal(section, ui.CombatScoreSection);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("daevanion")]
    [InlineData("pets")]
    [InlineData("titles")]
    public void DirectRootNavigationAlwaysReturnsToStatOverview(string section)
    {
        var ui = new UiState();
        ui.ShowCombatScoreSection(section);
        ui.Navigate("map");
        Assert.Equal(section, ui.CombatScoreSection);
        var changed = 0;
        ui.Changed += () => changed++;

        ui.Navigate("combat-score");

        Assert.Equal("combat-score", ui.Tab);
        Assert.Equal("stat-overview", ui.CombatScoreSection);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("overview")]
    [InlineData("STAT-OVERVIEW")]
    public void UnknownSectionsUseTheDefaultWithoutCreatingAnotherRootTab(string section)
    {
        var ui = new UiState();
        ui.ShowCombatScoreSection("daevanion");

        ui.ShowCombatScoreSection(section);

        Assert.Equal("combat-score", ui.Tab);
        Assert.Equal("stat-overview", ui.CombatScoreSection);
    }

    [Fact]
    public void ExistingRootAndOverlayAliasesKeepTheirIndependentState()
    {
        var ui = new UiState();
        ui.ShowCombatScoreSection("gear");
        ui.Navigate("petscan");
        Assert.Equal("pets", ui.Tab);
        Assert.Equal("gear", ui.CombatScoreSection);

        ui.ShowOverlay("farm-loot");
        Assert.Equal("overlay-editor", ui.Tab);
        Assert.Equal("templates", ui.OverlayEditorSection);
        Assert.Equal("gear", ui.CombatScoreSection);

        ui.ShowOverlay("daevanion");
        Assert.Equal("overlay-editor", ui.Tab);
        Assert.Equal("static", ui.OverlayEditorSection);
        Assert.Equal("daevanion", ui.OverlaySection);
        Assert.Equal("gear", ui.CombatScoreSection);
    }
}
