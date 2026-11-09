using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatAchievementViewTests
{
    [Theory]
    [InlineData("de", "Erster Auftrag", "Schließe drei Aufträge ab", "Münzen")]
    [InlineData("en", "First duty", "Complete three duties", "Coins")]
    public async Task NamedCatalogueConditionsAndRewardsAreLocalizedWithoutInventingLiveProgress(string language,
        string name, string condition, string reward)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(Snapshot(), Catalog());
            Assert.Contains(name, html); Assert.Contains(condition, html);
            AssertCompactRewardTooltip(Card(html, 1), reward);
            Assert.Contains("data-achievement-target=\"3\"", html);
            Assert.Contains("data-achievement-reward-quantity=\"7000\"", html);
            Assert.Contains("data-achievement-progress-status=\"Unknown\"", html);
            Assert.Contains("data-achievement-card-progress=\"Unknown\"", html);
            Assert.Contains("data-achievement-current>—", html);
            Assert.DoesNotContain("data-achievement-group-level", html); Assert.DoesNotContain("data-achievement-group-score", html);
            Assert.DoesNotContain("aria-valuenow", html); Assert.DoesNotContain("data-achievement-completed", html);
            Assert.DoesNotContain("data-achievement-claimed", html); Assert.DoesNotContain("Collect Rewards", html);
            Assert.DoesNotContain("123456789", html); Assert.DoesNotContain("2531456178", html);
            Assert.DoesNotContain("PRIVATE-ACH-PROFILE", html); Assert.DoesNotContain("PRIVATE-ACH-SOURCE", html);
            Assert.DoesNotContain("data-source-achievement", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task CategoryAndGroupButtonsIntersectAndChangingCategoryResetsOnlyTheGroup()
    {
        await using var fixture = new InteractiveFixture();
        var html = await fixture.Render(CharacterCombatSnapshot.Empty, Catalog());
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(html).Order());
        html = await fixture.Click("data-achievement-category-filter", "Growth");
        Assert.Equal(new[] { 1, 2 }, Ids(html).Order());
        Assert.Contains("data-achievement-group-filter=\"501\"", html);
        Assert.DoesNotContain("data-achievement-group-filter=\"601\"", html);
        html = await fixture.Click("data-achievement-group-filter", "501");
        Assert.Equal(new[] { 1 }, Ids(html));
        html = await fixture.Click("data-achievement-category-filter", "Challenge");
        Assert.Equal(new[] { 3 }, Ids(html));
        Assert.DoesNotContain("data-achievement-group=\"501\"", html);
        html = await fixture.Click("data-achievement-category-filter", "All");
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(html).Order());
        html = await fixture.Click("data-achievement-group-filter", "601");
        Assert.Equal(new[] { 3 }, Ids(html));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("conflict")]
    [InlineData("snapshot")]
    public async Task SourceInvalidationKeepsCatalogueVisibleAndNeverReusesObservedCounters(string variant)
    {
        await using var fixture = new InteractiveFixture();
        var snapshot = Snapshot();
        snapshot = variant switch
        {
            "unknown" => snapshot with { Achievements = NetworkCombatDomain<CharacterAchievementState>.Unknown },
            "stale" => snapshot with { Achievements = snapshot.Achievements with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } },
            "conflict" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            "snapshot" => snapshot with { IsFresh = false },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var html = await fixture.Render(snapshot, Catalog());
        Assert.Contains("data-source-awaiting-current=\"true\"", html);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(html).Order());
        Assert.Equal(4, Regex.Matches(html, "data-achievement-card-progress=\"Unknown\"").Count);
        Assert.DoesNotContain("123456789", html); Assert.DoesNotContain("aria-valuenow", html);
    }

    [Fact]
    public async Task FreshSourceUpdatesDoNotTurnRawCompletionOrRewardsIntoQualifiedUiFacts()
    {
        await using var fixture = new InteractiveFixture();
        await fixture.Render(Snapshot(), Catalog());
        await fixture.Click("data-achievement-category-filter", "Growth");
        await fixture.Click("data-achievement-group-filter", "501");
        await fixture.Update(Snapshot() with { IsFresh = false });
        var html = await fixture.Html();
        Assert.Equal(new[] { 1 }, Ids(html));
        Assert.Contains("data-achievement-group=\"501\"", html);
        await fixture.Update(Snapshot() with { Sequence = 99, ProfileId = "ANOTHER-PRIVATE-PROFILE" });
        html = await fixture.Html();
        Assert.Equal(new[] { 1 }, Ids(html));
        Assert.Contains("data-achievement-card-progress=\"Unknown\"", html);
        Assert.DoesNotContain("123456789", html); Assert.DoesNotContain("2531456178", html);
        Assert.DoesNotContain("data-achievement-claimed", html);
    }

    [Fact]
    public async Task PaginationLimitsDomRowsAndCategorySelectionReturnsToFirstPage()
    {
        await using var fixture = new InteractiveFixture();
        var definitions = Enumerable.Range(1, 28).Select(id => Definition(id, "Growth", 501,
            "Erfolg " + id.ToString("D2"), "Achievement " + id.ToString("D2"))).ToArray();
        var html = await fixture.Render(CharacterCombatSnapshot.Empty, Catalog(definitions));
        Assert.Equal(24, Ids(html).Length); Assert.Contains("data-achievement-match-count=\"28\"", html);
        html = await fixture.Click("data-achievement-page", "next");
        Assert.Equal(4, Ids(html).Length);
        var secondPage = Ids(html);
        html = await fixture.Click("data-achievement-category-filter", "Growth");
        Assert.Equal(24, Ids(html).Length); Assert.Empty(Ids(html).Intersect(secondPage));
        html = await fixture.Click("data-achievement-page", "next");
        Assert.Equal(secondPage, Ids(html));
        html = await fixture.Click("data-achievement-page", "previous");
        Assert.Equal(24, Ids(html).Length);
    }

    [Fact]
    public async Task UnknownMetadataTargetsAndRewardsStayUnknownInsteadOfZero()
    {
        await using var fixture = new InteractiveFixture();
        var definition = Definition(1, null, null, "Ohne Zuordnung", "Unmapped") with
        {
            NativeTarget = null, Rewards = [new("Unbekannte Menge", "Unknown quantity", null, null)]
        };
        var html = await fixture.Render(CharacterCombatSnapshot.Empty, Catalog([definition]));
        Assert.DoesNotContain("data-achievement-target=", html); Assert.DoesNotContain("data-achievement-reward-quantity=", html);
        Assert.Contains("data-achievement-current>—", html);
        Assert.DoesNotContain("data-achievement-current>0", html);
        Assert.Equal(new[] { 1 }, Ids(html));
        html = await fixture.Click("data-achievement-category-filter", "Growth");
        Assert.Empty(Ids(html));
    }

    [Fact]
    public async Task NamedFamiliesSortBeforeUnknownFamiliesAndTheFirstNamedCatalogueStageIsNotALiveStage()
    {
        await using var fixture = new InteractiveFixture();
        var unknown = Definition(1, "Growth", 501, "Alpha unbekannt", "Alpha unknown") with { HasNativeName = false };
        var staged = Definition(2, "Growth", 501, "Zeta Familie", "Zeta family") with
        {
            Stages = [new(207, "Erste Katalogstufe", "First catalogue stage", "Erste Bedingung", "First condition", null, null, []),
                new(219, "Andere Katalogstufe", "Other catalogue stage", "Andere Bedingung", "Other condition", null, null, [])]
        };
        var common = Definition(3, "Growth", 501, "Beta gemeinsam", "Beta common");
        var html = await fixture.Render(Snapshot(), Catalog([unknown, staged, common]));
        Assert.Equal(new[] { 3, 2, 1 }, Ids(html));
        var card = Card(html, 2);
        Assert.Contains("data-achievement-stage=\"207\"", card);
        Assert.Contains("data-achievement-stage-scope=\"Catalogue\"", card);
        Assert.Contains(UiText.T("Katalogstufe"), card);
        Assert.Contains(CombatAchievementsPresentation.Label(staged.Stages[0].ConditionDe, staged.Stages[0].ConditionEn, UiText.Language), card);
        Assert.DoesNotContain("data-achievement-target=", card); // Unknown stage target never inherits the family's target.
        Assert.Contains("data-achievement-card-progress=\"Unknown\"", card);
        Assert.DoesNotContain("123456789", html); Assert.DoesNotContain("2531456178", html);
        Assert.DoesNotContain("data-achievement-stage=", Card(html, 3));
    }

    [Fact]
    public async Task CatalogueStageSelectionChangesOnlyReferenceConditionTargetAndRewardsAcrossLiveUpdates()
    {
        await using var fixture = new InteractiveFixture();
        var definition = Definition(1, "Growth", 501, "Familie", "Family") with
        {
            Stages = [new(117, "Stufe A", "Stage A", "Bedingung A", "Condition A", null, null,
                    [new("Belohnung A", "Reward A", null, 10)]),
                new(149, "Stufe B", "Stage B", "Bedingung B", "Condition B", null, 15,
                    [new("Belohnung B", "Reward B", null, 25)])]
        };
        var html = await fixture.Render(Snapshot(), Catalog([definition]));
        Assert.Contains("data-achievement-stage=\"117\"", html);
        html = await fixture.ChangeStage(1, 149);
        Assert.Contains("data-achievement-stage=\"149\"", html);
        Assert.Contains(CombatAchievementsPresentation.Label(definition.Stages[1].ConditionDe, definition.Stages[1].ConditionEn, UiText.Language), html);
        Assert.DoesNotContain(CombatAchievementsPresentation.Label(definition.Stages[0].ConditionDe, definition.Stages[0].ConditionEn, UiText.Language), html);
        AssertCompactRewardTooltip(Card(html, 1), CombatAchievementsPresentation.Label(
            definition.Stages[1].Rewards[0].NameDe, definition.Stages[1].Rewards[0].NameEn, UiText.Language));
        Assert.DoesNotContain(CombatAchievementsPresentation.Label(definition.Stages[0].Rewards[0].NameDe, definition.Stages[0].Rewards[0].NameEn, UiText.Language), html);
        Assert.Contains("data-achievement-target=\"15\"", html);
        Assert.Contains("data-achievement-reward-quantity=\"25\"", html);
        Assert.Contains("data-achievement-current>—", html); Assert.DoesNotContain("aria-valuenow", html);
        Assert.DoesNotContain("data-achievement-completed", html); Assert.DoesNotContain("data-achievement-claimed", html);
        await fixture.Update(CharacterCombatSnapshot.Empty);
        html = await fixture.Html();
        Assert.Contains("data-achievement-stage=\"149\"", html);
        await fixture.Update(Snapshot() with { Sequence = 999 });
        html = await fixture.Html();
        Assert.Contains("data-achievement-stage=\"149\"", html);
        Assert.Contains("data-achievement-card-progress=\"Unknown\"", html);
        html = await fixture.ChangeStage(1, 999999);
        Assert.Contains("data-achievement-stage=\"149\"", html);
        Assert.DoesNotContain("123456789", html);
    }

    [Theory]
    [InlineData("de", 502, 501)]
    [InlineData("en", 501, 502)]
    public async Task GroupNavigationShowsOccupiedNamedGroupsFirstAndDisclosesUnknownGroupsWithoutInventingNames(
        string language, int firstNamed, int secondNamed)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var groups = new[]
            {
                new CombatAchievementGroupDefinition(501, "Growth", "Zebra", "Alpha"),
                new CombatAchievementGroupDefinition(502, "Growth", "Alpha", "Zulu"),
                new CombatAchievementGroupDefinition(503, "Growth", "Gruppenname noch nicht zugeordnet", "Group name not mapped") { HasNativeName = false, ScopeDe = "Elyos", ScopeEn = "Elyos" },
                new CombatAchievementGroupDefinition(504, "Growth", "Leere Gruppe", "Empty group"),
                new CombatAchievementGroupDefinition(505, null, "Gruppenname noch nicht zugeordnet", "Group name not mapped") { HasNativeName = false, ScopeDe = "Asmodier", ScopeEn = "Asmodians" }
            };
            var definitions = new[]
            {
                Definition(1, "Growth", 501, "Erster Erfolg", "First achievement"),
                Definition(2, "Growth", 502, "Zweiter Erfolg", "Second achievement"),
                Definition(3, "Growth", 503, "Dritter Erfolg", "Third achievement"),
                Definition(4, null, 505, "Vierter Erfolg", "Fourth achievement")
            };
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(CharacterCombatSnapshot.Empty, Catalog(definitions, groups));
            Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(html).Order());
            var groupIds = Regex.Matches(html, "data-achievement-group-filter=\"(\\d+)\"")
                .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            Assert.Equal(new[] { firstNamed, secondNamed, 505, 503 }, groupIds);
            Assert.DoesNotContain("data-achievement-group-filter=\"504\"", html);
            Assert.Contains("data-achievement-unmapped-groups=\"2\"", html);
            var disclosure = Regex.Match(html, "<details[^>]*data-achievement-unmapped-groups=\"2\"[^>]*>.*?</details>", RegexOptions.Singleline).Value;
            Assert.NotEmpty(disclosure); Assert.DoesNotContain(" open", disclosure);
            Assert.Contains(UiText.F("Weitere Gruppen ({0})", 2), disclosure);
            Assert.DoesNotContain(">503<", disclosure); Assert.DoesNotContain(">505<", disclosure);
            html = await fixture.Click("data-achievement-group-filter", "503");
            Assert.Equal(new[] { 3 }, Ids(html));
            html = await fixture.Click("data-achievement-category-filter", "Growth");
            Assert.Equal(new[] { 1, 2, 3 }, Ids(html).Order());
            Assert.Contains("data-achievement-unmapped-groups=\"1\"", html);
            Assert.DoesNotContain("data-achievement-group-filter=\"505\"", html);
            html = await fixture.Click("data-achievement-category-filter", "All");
            Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(html).Order());
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task QualifiedMovementRowsFollowTheObservedStageAndKeepCompletionAndClaimSeparate(string language)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(MovementSnapshot((420101000, 5, 0), (420101100, 5, 1)), MovementCatalog());
            Assert.Contains("data-achievement-known-family-count=\"2\"", html);
            var soaring = Card(html, 420101000); var riding = Card(html, 420101100);
            Assert.Contains("data-achievement-stage=\"420101002\"", soaring);
            Assert.Contains("data-current-stage-scope=\"Live\"", soaring);
            Assert.Contains("data-achievement-current-value=\"5\"", soaring);
            Assert.Contains("data-achievement-target=\"20\"", soaring);
            Assert.Contains("data-achievement-completed=\"false\"", soaring);
            Assert.Contains("data-achievement-claimed=\"false\"", soaring);
            Assert.Contains(UiText.T("In Arbeit"), soaring); Assert.DoesNotContain(UiText.T("Belohnung offen"), soaring);
            Assert.Contains("data-achievement-stage=\"420101102\"", riding);
            Assert.Contains("data-achievement-target=\"20\"", riding);
            Assert.Contains("data-achievement-current-value=\"5\"", riding);
            Assert.Contains("data-achievement-completed=\"false\"", riding);
            Assert.Contains(UiText.T("In Arbeit"), riding); Assert.DoesNotContain(UiText.T("Belohnung offen"), riding);
            var absent = Card(html, 320101000);
            Assert.Contains("data-achievement-card-progress=\"Unknown\"", absent);
            Assert.Contains("data-current-stage-scope=\"Catalogue\"", absent);
            Assert.DoesNotContain("data-achievement-current-value=", absent); Assert.DoesNotContain("data-achievement-claimed=", absent);
            Assert.DoesNotContain("data-achievement-group-level", html); Assert.DoesNotContain("data-achievement-group-score", html);
            Assert.DoesNotContain("9223372036854775807", html);
            soaring = Card(await fixture.ChangeStage(420101000, 420101001), 420101000);
            Assert.Contains("data-achievement-completed=\"true\"", soaring);
            Assert.Contains("data-achievement-claimed=\"false\"", soaring);
            Assert.Contains(UiText.T("Belohnung offen"), soaring);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task ManualCatalogueChoicePersistsAcrossUpdatesAndCanReturnToTheQualifiedCurrentStage()
    {
        await using var fixture = new InteractiveFixture();
        await fixture.Render(MovementSnapshot((420101100, 5, 1)), MovementCatalog());
        var html = await fixture.ChangeStage(420101100, 420101101);
        var card = Card(html, 420101100);
        Assert.Contains("data-current-stage-scope=\"Catalogue\"", card);
        Assert.Contains("data-achievement-completed=\"true\"", card);
        Assert.Contains("data-achievement-claimed=\"true\"", card);
        Assert.Contains(UiText.T("Belohnung abgeholt"), card);
        await fixture.Update(MovementSnapshot((420101100, 20, 1)));
        card = Card(await fixture.Html(), 420101100);
        Assert.Contains("data-achievement-stage=\"420101101\"", card);
        Assert.Contains("data-achievement-current-value=\"20\"", card);
        Assert.Contains("data-achievement-target=\"5\"", card);
        Assert.Contains("width:100%", card); // The visual bar clamps; the observed count remains 20.
        html = await fixture.Click("data-achievement-follow-current", "420101100");
        card = Card(html, 420101100);
        Assert.Contains("data-achievement-stage=\"420101103\"", card);
        Assert.Contains("data-current-stage-scope=\"Live\"", card);
        Assert.Contains("data-achievement-target=\"50\"", card);
        Assert.DoesNotContain(UiText.T("Belohnung offen"), card);
        Assert.DoesNotContain("data-achievement-follow-current", card);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("profile")]
    [InlineData("gap")]
    [InlineData("conflict")]
    public async Task InvalidatedMovementFactsDisappearWithoutReplacingUnknownProgressWithZero(string variant)
    {
        await using var fixture = new InteractiveFixture();
        var snapshot = MovementSnapshot((420101000, 4, 0));
        await fixture.Render(snapshot, MovementCatalog());
        var card = Card(await fixture.Html(), 420101000);
        Assert.Contains("data-achievement-current-value=\"4\"", card);
        Assert.Contains("data-achievement-completed=\"false\"", card); // A positive CompleteTime does not finish the stage.
        snapshot = variant switch
        {
            "snapshot" => snapshot with { IsFresh = false },
            "profile" => snapshot with { ProfileId = null },
            "gap" => snapshot with { Achievements = snapshot.Achievements with { IsFresh = false } },
            "conflict" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        await fixture.Update(snapshot);
        card = Card(await fixture.Html(), 420101000);
        Assert.Contains("data-current-stage-scope=\"Catalogue\"", card);
        Assert.Contains("data-achievement-card-progress=\"Unknown\"", card);
        Assert.DoesNotContain("data-achievement-current-value=", card); Assert.DoesNotContain("data-achievement-completed=", card);
        Assert.DoesNotContain("aria-valuenow", card); Assert.Contains("data-achievement-current>—", card);
    }

    [Fact]
    public async Task DetectedProgressFilterUsesOnlyQualifiedFamiliesAndIntersectsCategoryAndGroupFilters()
    {
        await using var fixture = new InteractiveFixture();
        var extras = Enumerable.Range(1, 25).Select(id => Definition(id, null, null,
            "Beispiel " + id.ToString("D2"), "Example " + id.ToString("D2"))).ToArray();
        var html = await fixture.Render(MovementSnapshot((420101000, 5, 0), (420101100, 5, 1)), MovementCatalog(extras));
        var initial = Ids(html);
        await fixture.Click("data-achievement-page", "next");
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Equal(new[] { 420101000, 420101100 }, Ids(html).Order());
        Assert.Contains("data-achievement-observed-family-count>2", html);
        html = await fixture.Click("data-achievement-progress-filter", "All");
        Assert.Equal(initial, Ids(html)); // Switching scope returns to page one.
        await fixture.Click("data-achievement-progress-filter", "Observed");
        html = await fixture.Click("data-achievement-category-filter", "Adventure");
        Assert.Equal(new[] { 420101000, 420101100 }, Ids(html).Order());
        html = await fixture.Click("data-achievement-group-filter", "300000301");
        Assert.Empty(Ids(html)); // The Elyos definitions have no observed rows in this synthetic own state.
        html = await fixture.Click("data-achievement-group-filter", "400000301");
        Assert.Equal(new[] { 420101000, 420101100 }, Ids(html).Order());
        await fixture.Update(CharacterCombatSnapshot.Empty);
        html = await fixture.Html();
        Assert.Empty(Ids(html)); Assert.Contains("data-achievement-known-family-count=\"0\"", html);
    }

    [Fact]
    public void UnknownCurrentStageAndClaimNeverPromoteTheFirstCatalogueStageToLive()
    {
        var achievement = CombatAchievementDisplayCatalog.Default.Find(420101000)!;
        var family = new CombatAchievementFamilyProgress(achievement.Id, null,
            new Dictionary<int, CombatAchievementStageProgress> { [420101001] = new(420101001, 5, 5, true, null) });
        var selected = CombatAchievementsPresentation.StageSelection(achievement, family, null);
        Assert.Equal(420101001, selected.StageId); Assert.False(selected.IsLive);
        Assert.Equal("Belohnungsstatus unbekannt", CombatAchievementsPresentation.ClaimStatus(family.Find(420101001)));
        Assert.Null(CombatAchievementsPresentation.ClaimStatus(new(420101001, 4, 5, false, false)));
        Assert.Equal("100%", CombatAchievementsPresentation.ProgressPercent(long.MaxValue, 5));
        Assert.Equal("0%", CombatAchievementsPresentation.ProgressPercent(-1, 5));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ClaimOnlyCardsShowConfirmedCompletionAndRewardWithoutCountersBarsOrCurrentStage(string language)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var definition = Definition(1, "Growth", 501, "Familie", "Family") with
            {
                Stages = [new(117, "Stufe A", "Stage A", "Bedingung A", "Condition A", null, 3, []),
                    new(149, "Stufe B", "Stage B", "Bedingung B", "Condition B", null, 7, [])]
            };
            var family = new CombatAchievementFamilyProgress(1, null, new Dictionary<int, CombatAchievementStageProgress>
            {
                [117] = new(117, null, null, true, true) { Unit = CombatAchievementProgressUnit.Unknown },
                [149] = new(149, null, null, null, null) { Unit = CombatAchievementProgressUnit.Unknown }
            }) { Status = CombatKnowledgeStatus.Partial };
            var projection = new CombatAchievementProgressSnapshot(7, 11, new Dictionary<int, CombatAchievementFamilyProgress> { [1] = family });
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(MovementSnapshot(), Catalog([definition]), projection);
            var card = Card(html, 1);
            Assert.Contains("data-achievement-completed=\"true\"", card); Assert.Contains("data-achievement-claimed=\"true\"", card);
            Assert.Contains(UiText.T("Abgeschlossen"), card); Assert.Contains(UiText.T("Belohnung abgeholt"), card);
            Assert.Contains("data-current-stage-scope=\"Catalogue\"", card);
            Assert.DoesNotContain("data-achievement-current", card); Assert.DoesNotContain("data-achievement-target", card);
            Assert.DoesNotContain("role=\"progressbar\"", card); Assert.DoesNotContain("aria-valuenow", card);
            html = await fixture.ChangeStage(1, 149); card = Card(html, 1);
            Assert.DoesNotContain("data-achievement-completed=", card); Assert.DoesNotContain("data-achievement-claimed=", card);
            Assert.DoesNotContain("data-achievement-current", card); Assert.DoesNotContain("data-achievement-target", card);
            Assert.DoesNotContain(UiText.T("In Arbeit"), card); Assert.DoesNotContain(UiText.T("Belohnung offen"), card);
            Assert.Contains(UiText.T("Abschlussstatus unbekannt"), card); Assert.DoesNotContain("role=\"progressbar\"", card);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task CounterClaimConflictShowsObservedCounterAndClaimWithWarningUntilFreshFactsAgree()
    {
        await using var fixture = new InteractiveFixture();
        var html = await fixture.Render(MovementSnapshot((420101100, 4, 1)), MovementCatalog());
        var card = Card(html, 420101100);
        Assert.Contains("data-achievement-family-status=\"Conflict\"", card);
        Assert.Contains("data-achievement-stage=\"420101102\"", card);
        Assert.Contains("data-achievement-current-value=\"4\"", card); Assert.Contains("data-achievement-target=\"20\"", card);
        Assert.Contains("data-achievement-claimed=\"false\"", card);
        Assert.Contains("data-current-stage-scope=\"Live\"", card);
        card = Card(await fixture.ChangeStage(420101100, 420101101), 420101100);
        Assert.Contains("data-achievement-target=\"5\"", card);
        Assert.DoesNotContain("data-achievement-completed=", card); Assert.Contains("data-achievement-claimed=\"true\"", card);
        Assert.Contains(UiText.T("Belohnung abgeholt"), card); Assert.Contains("data-achievement-conflict", card);
        Assert.Contains("data-current-stage-scope=\"Catalogue\"", card);
        await fixture.Update(MovementSnapshot((420101100, 5, 1)));
        card = Card(await fixture.Html(), 420101100);
        Assert.DoesNotContain("data-achievement-conflict", card);
        Assert.Contains("data-achievement-claimed=\"true\"", card);
        card = Card(await fixture.Click("data-achievement-follow-current", "420101100"), 420101100);
        Assert.Contains("data-current-stage-scope=\"Live\"", card);
        Assert.Contains("data-achievement-stage=\"420101102\"", card);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FlightMinutesAdvanceBeforeClaimsAndPreserveHistoricalCompletionFacts(string language)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new InteractiveFixture();
            var definition = CombatAchievementDisplayCatalog.Default.Find(420101200)!;
            var html = await fixture.Render(MovementSnapshot((420101200, 8, 0)), MovementCatalog(definition));
            var card = Card(html, 420101200);
            Assert.Contains("data-achievement-progress-unit=\"Minutes\"", card);
            Assert.Contains("data-achievement-current-value=\"8\"", card);
            Assert.Contains("data-achievement-target=\"10\"", card);
            Assert.Contains(language == "de" ? "8 Min." : "8 min", card);
            Assert.Contains("data-achievement-completed=\"false\"", card);
            Assert.DoesNotContain(UiText.T("Belohnung offen"), card);
            await fixture.Update(MovementSnapshot((420101200, 9, 0)));
            card = Card(await fixture.Html(), 420101200);
            Assert.Contains("data-achievement-current-value=\"9\"", card);
            Assert.Contains("data-achievement-completed=\"false\"", card);
            await fixture.Update(MovementSnapshot((420101200, 10, 0)));
            card = Card(await fixture.Html(), 420101200);
            Assert.Contains("data-achievement-stage=\"420101202\"", card);
            Assert.Contains("data-achievement-completed=\"false\"", card);
            Assert.Contains("data-achievement-current-value=\"10\"", card);
            Assert.Contains("data-achievement-target=\"30\"", card);
            card = Card(await fixture.ChangeStage(420101200, 420101201), 420101200);
            Assert.Contains("data-achievement-completed=\"true\"", card);
            Assert.Contains("data-achievement-claimed=\"false\"", card);
            Assert.Contains(UiText.T("Belohnung offen"), card);
            await fixture.Click("data-achievement-follow-current", "420101200");
            await fixture.Update(MovementSnapshot((420101200, 10, 1)));
            card = Card(await fixture.Html(), 420101200);
            Assert.Contains("data-achievement-stage=\"420101202\"", card);
            Assert.Contains("data-current-stage-scope=\"Live\"", card);
            Assert.Contains("data-achievement-current-value=\"10\"", card);
            Assert.Contains("data-achievement-target=\"30\"", card);
            Assert.Contains(language == "de" ? "10 Min." : "10 min", card);
            Assert.Contains("data-achievement-completed=\"false\"", card);
            Assert.DoesNotContain(UiText.T("Belohnung offen"), card);
            html = await fixture.ChangeStage(420101200, 420101201); card = Card(html, 420101200);
            Assert.Contains("data-current-stage-scope=\"Catalogue\"", card);
            Assert.Contains("data-achievement-target=\"10\"", card);
            Assert.Contains("data-achievement-completed=\"true\"", card);
            Assert.Contains("data-achievement-claimed=\"true\"", card);
            Assert.Contains(UiText.T("Belohnung abgeholt"), card);
            html = await fixture.Click("data-achievement-follow-current", "420101200"); card = Card(html, 420101200);
            Assert.Contains("data-achievement-stage=\"420101202\"", card);
            Assert.Contains("data-current-stage-scope=\"Live\"", card);
            Assert.DoesNotContain("data-achievement-follow-current", card);
        }
        finally { UiText.Language = previous; }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionFlightPanel(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new InteractiveFixture();
            await fixture.Render(MovementSnapshot((320101200, 10, 0), (420101200, 10, 1)), CombatAchievementDisplayCatalog.Default);
            var html = await fixture.Click("data-achievement-progress-filter", "Observed");
            Assert.Equal(new[] { 320101200, 420101200 }, Ids(html).Order());
            Assert.Contains(UiText.T("Belohnung offen"), Card(html, 320101200));
            Assert.Contains("data-achievement-target=\"30\"", Card(html, 420101200));
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            Directory.CreateDirectory(folder);
            var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
            File.WriteAllText(Path.Combine(folder, "achievements-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
                + "\nhtml,body{height:auto}body{padding:30px}</style></head><body>" + html + "</body></html>");
        }
        finally { UiText.Language = previous; }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionAchievementPanel(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new InteractiveFixture();
            var catalog = CombatAchievementDisplayCatalog.Default;
            await fixture.Render(MovementSnapshot((420101000, 5, 0), (420101100, 5, 1),
                (401100100, 22, 3), (401100300, 125, 0), (400100200, 50, 7)), catalog);
            await fixture.Click("data-achievement-progress-filter", "Observed");
            var html = await fixture.ChangeStage(400100200, 400100207);
            Assert.Equal(new[] { 400100200, 401100100, 401100300, 420101000, 420101100 }, Ids(html).Order());
            Assert.Contains("data-achievement-progress-unit=\"Level\"", Card(html, 401100100));
            Assert.Contains("data-achievement-progress-unit=\"Minutes\"", Card(html, 401100300));
            Assert.DoesNotContain("data-achievement-current", Card(html, 400100200));
            Assert.Contains("data-achievement-claimed=\"true\"", Card(html, 400100200));
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            Directory.CreateDirectory(folder);
            var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
            File.WriteAllText(Path.Combine(folder, "achievements-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
                + "\nhtml,body{height:auto}body{padding:30px}</style></head><body>" + html + "</body></html>");
        }
        finally { UiText.Language = previous; }
    }

    private static void AssertCompactRewardTooltip(string card, string rewardName)
    {
        var reward = Regex.Match(card, "<div\\b[^>]*data-achievement-reward\\b[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(reward);
        Assert.Contains("tabindex=\"0\"", reward);
        Assert.Contains("aria-describedby=\"", reward);
        Assert.Contains("title=\"" + rewardName + " ", reward);
        Assert.Contains("role=\"tooltip\">" + rewardName + " ", reward);
        var withoutTooltip = Regex.Replace(reward, "<span\\b[^>]*role=\"tooltip\"[^>]*>.*?</span>", "", RegexOptions.Singleline);
        var visibleText = WebUtility.HtmlDecode(Regex.Replace(withoutTooltip, "<[^>]*>", ""));
        Assert.DoesNotContain(rewardName, visibleText);
    }

    private static string Card(string html, int id) => Regex.Match(html,
        "<article\\b[^>]*data-achievement-card=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;

    private static int[] Ids(string html) => Regex.Matches(html, "data-achievement-card=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();

    private static CombatAchievementDisplayDefinition Definition(int id, string? category, int? group,
        string german, string english) => new(id, category, group, german, english,
            "Schließe drei Aufträge ab", "Complete three duties", null, 3,
            [new("Münzen", "Coins", null, 7000)]);

    private static CombatAchievementDisplayCatalog Catalog(CombatAchievementDisplayDefinition[]? definitions = null, CombatAchievementGroupDefinition[]? groups = null) =>
        CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 26425393, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = new[] { new CombatAchievementCategoryDefinition("Growth", "Wachstum", "Progress"),
                new CombatAchievementCategoryDefinition("Challenge", "Herausforderung", "Challenge") },
            groups = groups ?? [new CombatAchievementGroupDefinition(501, "Growth", "Durchführung", "Quest"),
                new CombatAchievementGroupDefinition(502, "Growth", "Abschluss", "Complete"),
                new CombatAchievementGroupDefinition(601, "Challenge", "Herausforderung", "Challenge")],
            achievements = definitions ?? [Definition(1, "Growth", 501, "Erster Auftrag", "First duty"),
                Definition(2, "Growth", 502, "Zweiter Auftrag", "Second duty"),
                Definition(3, "Challenge", 601, "Herausforderung", "Challenge example"),
                Definition(4, null, null, "Andere Quelle", "Other source")]
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static CombatAchievementDisplayCatalog MovementCatalog(params CombatAchievementDisplayDefinition[] extras)
    {
        var native = CombatAchievementDisplayCatalog.Default;
        var ids = new[] { 320101000, 320101100, 420101000, 420101100 };
        var rows = ids.Select(id => native.Find(id)!).Concat(extras).ToArray();
        var groups = rows.Select(row => row.GroupId).OfType<int>().ToHashSet();
        return CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = native.MetadataBuild, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = native.Categories, groups = native.Groups.Where(group => groups.Contains(group.Id)).ToArray(), achievements = rows
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static CharacterCombatSnapshot MovementSnapshot(params (int Id, int Count, byte Claimed)[] rows) => new()
    {
        ProfileId = "SYNTHETIC-MOVEMENT-PROFILE", IsFresh = true, Generation = 7, Sequence = 11,
        Achievements = new(new CharacterAchievementState(rows.ToImmutableDictionary(row => row.Id,
            row => new CharacterAchievement(row.Count, long.MaxValue, row.Claimed)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty),
            NetworkCombatSourceStatus.Complete, true, new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), 11, "own-achievement-list")
    };

    private static CharacterCombatSnapshot Snapshot() => new()
    {
        ProfileId = "PRIVATE-ACH-PROFILE", IsFresh = true, Generation = 7, Sequence = 1,
        Achievements = new(new CharacterAchievementState(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(1, new(123456789, 2531456178, 251)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(501, [true, true, false])),
            NetworkCombatSourceStatus.Complete, true, new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), 1, "PRIVATE-ACH-SOURCE")
    };

    private sealed class InteractiveFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedAchievements? _view;
        public InteractiveFixture()
        {
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render(CharacterCombatSnapshot snapshot, CombatAchievementDisplayCatalog catalog, CombatAchievementProgressSnapshot? projected = null)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedAchievements>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CapturedAchievements.Snapshot)] = snapshot, [nameof(CapturedAchievements.Catalog)] = catalog,
                    [nameof(CapturedAchievements.TestProjection)] = projected,
                    [nameof(CapturedAchievements.Captured)] = (Action<CapturedAchievements>)(view => _view = view)
                })));
            return await Html();
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task<string> ChangeStage(int familyId, int stageId) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.ChangeStage(familyId, stageId); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task Update(CharacterCombatSnapshot snapshot) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(snapshot));
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    public sealed class CapturedAchievements : CombatAchievementsView
    {
        [Parameter] public Action<CapturedAchievements>? Captured { get; set; }
        [Parameter] public CombatAchievementProgressSnapshot? TestProjection { get; set; }
        protected override CombatAchievementProgressSnapshot ProjectProgress() => TestProjection ?? base.ProjectProgress();
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(CharacterCombatSnapshot snapshot) { Snapshot = snapshot; OnParametersSet(); StateHasChanged(); }
#pragma warning disable BL0006 // Exercise the production callbacks used by native pointer and keyboard activation.
        public async Task ChangeStage(int familyId, int stageId)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatAchievementsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var key = familyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-achievement-stage-select" && Equals(frames.Array[i].AttributeValue, key));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeValue;
            var args = new ChangeEventArgs { Value = stageId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (callback is EventCallback<ChangeEventArgs> typed) await typed.InvokeAsync(args);
            else if (callback is EventCallback untyped) await untyped.InvokeAsync(args);
            else throw new InvalidOperationException("Unexpected catalogue-stage callback.");
            StateHasChanged();
        }

        public async Task Click(string attribute, string value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatAchievementsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute && Equals(frames.Array[i].AttributeValue, value));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected achievement-filter callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
