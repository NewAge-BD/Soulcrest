using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionCalculatorViewTests
{
    [Theory]
    [InlineData("de", "Werte", "Passive", "Route berechnen")]
    [InlineData("en", "Stats", "Passives", "Calculate route")]
    public async Task CalculatorRendersAutomaticClassAndThreeReadonlyPointPoolsWithSeparatePriorityTables(
        string language, string stats, string passives, string calculate)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        Assert.Equal(3, Regex.Matches(html, "data-priority-table=").Count);
        Assert.Equal(3, Regex.Matches(html, "ignore if possible <span>").Count);
        Assert.Equal(3, Regex.Matches(html, "must have <span>").Count);
        Assert.Equal(3, Regex.Matches(html, "data-priority-zone=\"required\"").Count);
        Assert.Equal(3, Regex.Matches(html, "data-priority-zone=\"normal\"").Count);
        Assert.Equal(3, Regex.Matches(html, "data-priority-zone=\"ignored\"").Count);
        Assert.Equal(3, Regex.Matches(html, "data-budget=").Count);
        Assert.DoesNotMatch("<select[^>]*data-calculator-class", html);
        Assert.DoesNotMatch("<input[^>]*data-budget", html);
        Assert.Contains("data-automatic-point-pools", html);
        Assert.Contains("aria-label=\"" + stats + "\"", html);
        Assert.Contains("aria-label=\"" + passives + "\"", html);
        Assert.Contains(calculate, html);
        Assert.Contains("draggable=\"true\"", html);
        Assert.Contains("most combat score possible", html);
        Assert.Contains("data-combat-score-measurements", html);
        foreach (var family in new[] { "Nezekan", "Zikel", "Vaizel", "Triniel", "Ariel", "Azphel" }) Assert.Contains(family, html);
        Assert.All(fixture.Plans.Snapshot.Draft.Priorities, priority => Assert.False(priority.Ignored));
        Assert.Equal(new DaevanionBudgets(), fixture.Plans.Snapshot.Draft.Budgets);
        Assert.Equal(0, fixture.Optimizer.Calls);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BoardSvgReceivesTheUiLanguageValueInsteadOfTheLiteralParameterName(string language)
    {
        // Regression: Language="Language" passed the text "Language" to the string parameter, so German showed English.
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        RazorStringParameterBindingTests.AssertDaevanionSvgLanguage(html, await fixture.CurrentBoard(), language);
    }

    [Theory(Skip = PendingOwnWeights.Reason)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CombatScoreModeEnablesAnExplicitCommunityEstimateWithUnknownBaselines(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Mode = DaevanionOptimizationMode.CombatScore });
        var html = await fixture.Render();
        Assert.Contains("data-combat-score-model", html);
        Assert.Contains("data-combat-score-model", html);
        Assert.DoesNotContain("data-combat-score-unavailable", html);
        Assert.DoesNotMatch("<button[^>]*disabled[^>]*data-calculate-route", html);
        Assert.DoesNotContain("data-combat-baseline", html);
        Assert.Contains("data-automatic-character-source", html);
        Assert.Equal(22, Regex.Matches(html, "data-skill-current=").Count);
        Assert.Equal(22, Regex.Matches(html, "data-skill-board-bonus=").Count);
        Assert.Equal(22, Regex.Matches(html, "data-skill-target=").Count);
        Assert.DoesNotContain("data-baseline-stat-value=", html);
        Assert.DoesNotMatch("<input[^>]*data-skill-current", html);
        Assert.DoesNotMatch("<input[^>]*data-skill-board-bonus", html);
        Assert.Contains(UiText.T("CP hängt von Ausgangswerten und der gesamten Route ab."), html);
        Assert.Equal(0, fixture.Optimizer.Calls);
    }

    [Fact]
    public async Task TargetsAreEditableInPriorityModeAndLateClassOrPoolCallbacksCannotChangeAnotherDraft()
    {
        await using var fixture = new Fixture("en");
        fixture.Plans.SetClass("Cleric"); await fixture.Render();
        var skill = DaevanionCatalog.Default.Skills.First(skill => skill.ClassId == "Cleric" && skill.Kind == DaevanionSkillKind.Active);
        var key = skill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var callback = await fixture.CaptureSkillEdit(DaevanionPriorityKind.Skill);
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.CurrentLevel, 18));
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.CurrentBoardBonus, 4));
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.TargetLevel, 16));
        Assert.Equal(new DaevanionSkillState(skill.Id, null, null, 16), fixture.Plans.Snapshot.Draft.CombatContext!.Skills.Single(item => item.SkillId == skill.Id));
        Assert.Equal(DaevanionOptimizationMode.Priority, fixture.Plans.Snapshot.Draft.Mode);
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.TargetLevel, null));
        Assert.Null(fixture.Plans.Snapshot.Draft.CombatContext!.Skills.Single(item => item.SkillId == skill.Id).TargetLevel);
        await fixture.ClickButton("Ariel");
        var before = File.ReadAllBytes(fixture.StorePath);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => callback.InvokeAsync(new(key, DaevanionSkillEditField.TargetLevel, 25)));
        Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Plans.SetClass("Sorcerer"));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => callback.InvokeAsync(new(key, DaevanionSkillEditField.CurrentLevel, 25)));
        Assert.Equal("Sorcerer", fixture.Plans.Snapshot.Draft.ClassId);
        Assert.DoesNotContain(fixture.Plans.Snapshot.Draft.CombatContext?.Skills ?? [], state => state.SkillId == skill.Id);
    }

    [Fact]
    public async Task LegacyManualBaselinesNeverBecomeCurrentReadonlyCharacterValues()
    {
        await using var fixture = new Fixture("en");
        fixture.Plans.SetClass("Cleric");
        var context = DaevanionCombatScoreModel.CreateContext("Cleric");
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { CombatContext = context with
        {
            BaselineCombatPower = 765432,
            GeneralStats = context.GeneralStats.Select(row => row with { Value = 98765, IncreasePercent = 7 }).ToArray(),
            Skills = context.Skills.Select(row => row with { CurrentLevel = 222, CurrentBoardBonus = 111, TargetLevel = 12 }).ToArray()
        } });
        var html = await fixture.Render();
        Assert.DoesNotContain("data-combat-baseline", html);
        Assert.DoesNotContain("data-baseline-stat-value", html);
        Assert.DoesNotContain("value=\"222\"", html);
        Assert.DoesNotContain("value=\"111\"", html);
        Assert.DoesNotContain("765432", html);
        Assert.DoesNotContain("data-apply-detected-skills", html);
        Assert.DoesNotContain("data-clear-skill-overrides", html);
        Assert.Contains("data-skill-target=", html);
    }

    [Fact]
    public async Task CurrentLevelCallbacksAreIgnoredAndDesiredLevelsSurviveDetectedUpdates()
    {
        await using var fixture = new Fixture("en");
        fixture.Plans.SetClass("Cleric");
        var skill = DaevanionCatalog.Default.Skills.First(skill => skill.ClassId == "Cleric" && skill.Kind == DaevanionSkillKind.Active);
        var key = skill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await fixture.Render();
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.CurrentLevel, 19));
        Assert.Null(fixture.Plans.Snapshot.Draft.CombatContext);
        await fixture.EditSkill(DaevanionPriorityKind.Skill, new(key, DaevanionSkillEditField.TargetLevel, 16));
        fixture.Plans.ObserveDetectedSkills(new("synthetic-local-profile", "Cleric",
            ImmutableDictionary<int, OwnSkillLevel>.Empty.Add(skill.Id, new(skill.Id, 18, 16, new(2, 0, 0, 0, 0))),
            true, true, DateTimeOffset.UnixEpoch, 1, 1, "synthetic", null, null));
        Assert.Equal(16, fixture.Plans.Snapshot.Draft.CombatContext!.Skills.Single(item => item.SkillId == skill.Id).TargetLevel);
        Assert.Equal(0, fixture.Plans.Snapshot.ManualSkillOverrideCount);
        var html = await fixture.Html();
        Assert.DoesNotContain("data-apply-detected-skills", html);
        Assert.DoesNotContain("data-manual-skill-overrides", html);
    }

    [Theory(Skip = PendingOwnWeights.Reason)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EstimatedRouteCpAndTargetExcessRemainGlobalWhenBoardFiltersChange(string language)
    {
        await using var fixture = new Fixture(language, new FullBoardOptimizer());
        fixture.Plans.SetClass("Cleric");
        var context = DaevanionCombatScoreModel.CreateContext("Cleric");
        var first = DaevanionCatalog.Default.Skills.First(skill => skill.ClassId == "Cleric" && skill.Kind == DaevanionSkillKind.Active);
        context = context with { BaselineCombatPower = 100000, GeneralStats = context.GeneralStats.Select(row => row with { Value = 1000, IncreasePercent = 0 }).ToArray(),
            Skills = context.Skills.Select(row => row with { CurrentLevel = 10, CurrentBoardBonus = 0, TargetLevel = row.SkillId == first.Id ? 12 : null }).ToArray() };
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Mode = DaevanionOptimizationMode.CombatScore, Budgets = new(570, 232, 232), CombatContext = context });
        await fixture.Plans.CalculateAsync();
        var result = fixture.Plans.Snapshot.Result!; Assert.True(result.Optimal);
        var html = await fixture.Render();
        Assert.Contains(UiText.T("Geschätzter CP-Zuwachs"), html);
        Assert.Contains(UiText.T("Nutzen bis zu den Wunschleveln"), html);
        Assert.Contains(UiText.F("{0} über Wunsch", 2), html);
        var delta = Regex.Match(html, "data-estimated-combat-score>(.*?)</div>").Groups[1].Value;
        var total = Regex.Match(html, "data-estimated-total-combat-score[^>]*>(.*?)</strong>").Groups[1].Value;
        Assert.NotEmpty(delta); Assert.NotEmpty(total); Assert.NotEqual("—", delta); Assert.NotEqual("—", total);
        await fixture.ChangeInput(UiText.T("Tabellenfilter"), "current");
        await fixture.ClickButton("Ariel");
        html = await fixture.Html();
        Assert.Equal(delta, Regex.Match(html, "data-estimated-combat-score>(.*?)</div>").Groups[1].Value);
        Assert.Equal(total, Regex.Match(html, "data-estimated-total-combat-score[^>]*>(.*?)</strong>").Groups[1].Value);
        Assert.Same(result, fixture.Plans.Snapshot.Result);
        Assert.Contains(UiText.T("CP wird einmal über die gesamte Route gerundet. Pool- und Boardfilter ändern diesen Gesamtwert nicht."), html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TableColorsFollowActualNodeCostsAndRetainMixedStatVariants(string language)
    {
        await using var fixture = new Fixture(language);
        Assert.True(fixture.Plans.SetClass("Cleric"));
        var html = await fixture.Render();
        static string Row(string source, string key) => Regex.Match(source,
            "<tr[^>]*data-priority-key=\"" + Regex.Escape(key) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
        var critical = Row(html, "Critical");
        Assert.Contains("data-node-cost=\"1\"", critical);
        Assert.DoesNotContain("data-node-cost=\"3\"", critical);
        Assert.Contains("+5", critical);
        Assert.DoesNotContain("+10", critical);
        Assert.Contains("class=\"dc-cost-4\"", Row(html, "CombatSpeed"));
        foreach (var (kind, cost, count) in new[] { ("Skill", 3, 12), ("Passive", 2, 10) })
        {
            var table = Regex.Match(html, "<section[^>]*data-priority-kind=\"" + kind + "\".*?</section>", RegexOptions.Singleline).Value;
            var rows = Regex.Matches(table, "<tr class=\"([^\"]*)\"[^>]*data-priority-key=", RegexOptions.Singleline);
            Assert.Equal(count, rows.Count);
            Assert.All(rows.Cast<Match>(), row => Assert.Equal("dc-cost-" + cost, row.Groups[1].Value));
        }
        Assert.DoesNotContain("data-priority-key=\"PvEAddDamage\"", html);
        await fixture.ClickButton("Ariel");
        var ariel = await fixture.Html();
        Assert.Single(Regex.Matches(ariel, "data-priority-table=").Cast<Match>());
        Assert.Contains("data-node-cost=\"3\"", Row(ariel, "Critical"));
        Assert.DoesNotContain("data-node-cost=\"1\"", Row(ariel, "Critical"));
        Assert.Contains("data-node-cost=\"1\"", Row(ariel, "PvEAddDamage"));
        Assert.Contains("data-node-cost=\"2\"", Row(ariel, "PvEAddDamage"));
        await fixture.ClickButton("Nezekan");
        await fixture.ChangeInput(UiText.T("Tabellenfilter"), "current");
        var filtered = await fixture.Html();
        Assert.Contains("data-node-cost=\"1\"", Row(filtered, "Critical"));
        Assert.DoesNotContain("data-node-cost=\"3\"", Row(filtered, "Critical"));
        Assert.Equal(0, fixture.Optimizer.Calls);
    }

    [Fact]
    public async Task ExistingViewRefreshesTranslatedLabelsAfterLanguageAndManualClassChange()
    {
        await using var fixture = new Fixture("en");
        Assert.Contains("Available points", await fixture.Render());
        UiText.Language = "de";
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Plans.SetClass("Sorcerer"));
        var html = await fixture.Html();
        Assert.Contains("Verfügbare Punkte", html);
        Assert.Contains("Prioritäten: Daevanion-Punkte", html);
        Assert.DoesNotContain("Available points", html);
        Assert.Equal("Sorcerer", fixture.Plans.Snapshot.Draft.ClassId);
    }

    [Fact]
    public async Task BoardUsesOriginalLocalArtworkNamedStatsSkillIncrementAndExplicitRouteEdges()
    {
        await using var fixture = new Fixture("en");
        var rules = DaevanionRuleCatalog.Default.ForClass("Cleric")[0];
        var start = rules.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start);
        var neighbor = rules.Nodes.First(node => start.NeighborIds.Contains(node.Node.Id));
        var plan = new DaevanionBoardPlan { BoardId = rules.Board.Id, FamilyId = rules.Board.FamilyId,
            NodeIds = [start.Node.Id, neighbor.Node.Id], Edges = [new(start.Node.Id, neighbor.Node.Id)],
            ActivationOrder = [start.Node.Id, neighbor.Node.Id], Cost = neighbor.Cost };
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode(
            (await fixture.Renderer.RenderComponentAsync<DaevanionBoardSvg>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(DaevanionBoardSvg.Board)] = rules.Board, [nameof(DaevanionBoardSvg.Plan)] = plan,
                [nameof(DaevanionBoardSvg.ShowOrder)] = true, [nameof(DaevanionBoardSvg.Language)] = "en" }))).ToHtmlString()));
        Assert.Contains("class=\"dc-route-edges\"", html);
        Assert.Contains("dc-node-chosen", html);
        Assert.Contains("dc-node-order", html);
        Assert.Contains("dc-node-stat-label", html);
        Assert.Contains("ATK", html);
        Assert.Contains("dc-skill-increment\">+1", html);
        Assert.Contains("href=\"daevanion/", html);
        Assert.DoesNotContain("href=\"http", html);
        Assert.Contains(UiText.T("CP hängt von Ausgangswerten und der gesamten Route ab."), html);
    }

    [Fact]
    public async Task PriorityNoOpCallbackPreservesTheCompletedResultAndStoredDraft()
    {
        await using var fixture = new Fixture("en", new FullBoardOptimizer());
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Budgets = new(570, 232, 232), Weights = new(34, 12, 10) });
        await fixture.Plans.CalculateAsync();
        await fixture.Render();
        var result = fixture.Plans.Snapshot.Result;
        Assert.True(result?.Optimal);
        var before = File.ReadAllBytes(fixture.StorePath);
        var rows = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Where(row => row.Kind == DaevanionPriorityKind.Stat).ToArray();
        await fixture.MovePriority(new(DaevanionPriorityKind.Stat, rows[0].Key, null, false, rows[0].Key));
        await fixture.MovePriority(new(DaevanionPriorityKind.Stat, rows[1].Key, null, false, rows[0].Key));
        Assert.Same(result, fixture.Plans.Snapshot.Result);
        Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
    }

    [Fact]
    public async Task ResultBonusesShowActualQuantitiesAndFollowTheSameBoardFilterAsPriorityTables()
    {
        await using var fixture = new Fixture("en", new FullBoardOptimizer());
        fixture.Plans.SetClass("Cleric");
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Budgets = new(570, 232, 232), Weights = new(34, 12, 10) });
        await fixture.Plans.CalculateAsync();
        var html = await fixture.Render();
        Assert.Matches("data-planned-stat=\"HPMax\"[^>]*><dt>.*?</dt><dd>\\+4500</dd>", html);
        Assert.Matches("data-planned-stat=\"CombatSpeed\"[^>]*><dt>.*?</dt><dd>\\+3%</dd>", html);
        Assert.Equal(22, Regex.Matches(html, "data-planned-skill=").Count);
        Assert.Equal(22, Regex.Matches(html, "<dd>\\+4 levels").Count);
        await fixture.ChangeInput("Table filter", "current");
        html = await fixture.Html();
        Assert.Matches("data-planned-stat=\"HPMax\"[^>]*><dt>.*?</dt><dd>\\+1100</dd>", html);
        Assert.DoesNotContain("data-planned-stat=\"PvEAmplifyDamage\"", html);
        Assert.DoesNotContain("<dd>+4 levels", html);
        Assert.Contains("Skill levels", html); Assert.Contains("Passive levels", html);
        var result = fixture.Plans.Snapshot.Result;
        await fixture.ClickButton("Ariel");
        html = await fixture.Html();
        Assert.Matches("data-planned-stat=\"HPMax\"[^>]*><dt>.*?</dt><dd>\\+400</dd>", html);
        Assert.Matches("data-planned-stat=\"PvEAmplifyDamage\"[^>]*><dt>.*?</dt><dd>\\+6%</dd>", html);
        Assert.DoesNotContain("data-planned-skill=", html);
        Assert.DoesNotContain("data-priority-table=\"Skill\"", html);
        Assert.DoesNotContain("data-priority-table=\"Passive\"", html);
        Assert.DoesNotContain("data-planned-stat=\"PvPAmplifyDamage\"", html);
        await fixture.ClickButton("Azphel");
        html = await fixture.Html();
        Assert.Matches("data-planned-stat=\"PvPAmplifyDamage\"[^>]*><dt>.*?</dt><dd>\\+6%</dd>", html);
        Assert.DoesNotContain("data-planned-stat=\"PvEAmplifyDamage\"", html);
        Assert.Same(result, fixture.Plans.Snapshot.Result);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ArielAndAzphelKeepIndependentRankingsIgnoreEntriesAndCategoryWeights(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Plans.SetClass("Cleric");
        await fixture.Render();
        var original = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal);
        await fixture.ClickButton("Ariel");
        var arielHtml = await fixture.Html();
        Assert.Contains(UiText.F("Prioritäten: {0}", "Ariel"), arielHtml);
        Assert.Contains("data-priority-pool=\"Ariel\"", arielHtml);
        Assert.Single(Regex.Matches(arielHtml, "data-priority-table=").Cast<Match>());
        Assert.DoesNotContain("data-priority-key=\"PvPAddDamage\"", arielHtml);
        await fixture.MovePriority(new(DaevanionPriorityKind.Stat, "HPMax", null, true));
        await fixture.ChangeCategoryWeight(DaevanionPriorityKind.Stat, 73);
        await fixture.ClickButton("Azphel");
        var azphel = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Azphel);
        var first = azphel.Priorities.First(row => row.Kind == DaevanionPriorityKind.Stat && row.Key != "HPMax");
        await fixture.MovePriority(new(DaevanionPriorityKind.Stat, "HPMax", first.Key, false));
        await fixture.ChangeCategoryWeight(DaevanionPriorityKind.Stat, 31);
        var request = fixture.Plans.Snapshot.Draft;
        var savedAriel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        var savedAzphel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Azphel);
        var normal = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal);
        Assert.True(savedAriel.Priorities.Single(row => row.Key == "HPMax").Ignored);
        Assert.False(savedAzphel.Priorities.Single(row => row.Key == "HPMax").Ignored);
        Assert.Equal("HPMax", savedAzphel.Priorities[0].Key);
        Assert.Equal(73, savedAriel.Weights.Stats); Assert.Equal(31, savedAzphel.Weights.Stats);
        Assert.Equal(original.Weights, normal.Weights); Assert.Equal(original.Priorities, normal.Priorities);
        await fixture.ClickButton("Ariel");
        arielHtml = await fixture.Html();
        Assert.Matches("data-priority-key=\"HPMax\"[^>]*data-ignored=\"true\"", arielHtml);
        Assert.Contains("value=\"73\"", arielHtml);
        await fixture.ClickButton("Nezekan");
        var normalHtml = await fixture.Html();
        Assert.Equal(3, Regex.Matches(normalHtml, "data-priority-table=").Count);
        Assert.DoesNotContain("data-priority-key=\"PvEAddDamage\"", normalHtml);
        Assert.DoesNotContain("data-priority-key=\"PvPAddDamage\"", normalHtml);
        Assert.Equal(0, fixture.Optimizer.Calls);
    }

    [Fact]
    public async Task DelayedPriorityCallbackFromThePreviousPoolCannotChangeASharedStatInTheNewPool()
    {
        await using var fixture = new Fixture("en");
        fixture.Plans.SetClass("Cleric"); await fixture.Render();
        var previousCallback = await fixture.CapturePriorityMove(DaevanionPriorityKind.Stat);
        await fixture.ClickButton("Ariel");
        var before = File.ReadAllBytes(fixture.StorePath);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => previousCallback.InvokeAsync(new(DaevanionPriorityKind.Stat, "HPMax", null, true)));
        Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
            Assert.False(DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, pool).Priorities.Single(row => row.Key == "HPMax").Ignored);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RequiredButtonsArrowsAndBoardFilterRetainHardAllNodePoolScope(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Plans.SetClass("Cleric"); await fixture.Render();
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "HPMax", 2); // require
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "MPMax", 2);
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "HPMax", 1); // down within required
        var required = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Where(row => row.Required).Select(row => row.Key).ToArray();
        Assert.Equal(new[] { "MPMax", "HPMax" }, required);
        await fixture.ChangeInput(UiText.T("Tabellenfilter"), "current");
        var html = await fixture.Html();
        var count = DaevanionRuleCatalog.Default.ForClass("Cleric").Where(board => board.Pool == DaevanionBudgetPool.Normal)
            .SelectMany(board => board.Nodes).Count(node => node.Node.Effects.Any(effect => effect.StatId == "HPMax"));
        var requiredSection = Regex.Match(html, "<tbody[^>]*data-priority-zone=\"required\"[^>]*>.*?</tbody>", RegexOptions.Singleline).Value;
        Assert.Contains("data-priority-key=\"HPMax\"", requiredSection);
        Assert.Contains(UiText.F("Pflicht: {0} Nodes im Punktepool", count), requiredSection);
        Assert.Contains("data-required=\"true\"", requiredSection);
        Assert.Contains(UiText.T("must have erzwingt alle passenden Nodes im Punktepool samt Verbindungen. Ein Boardfilter schränkt diese Pflicht nicht ein."), html);
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "HPMax", 3); // ignore clears required
        var row = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Single(row => row.Key == "HPMax");
        Assert.True(row.Ignored); Assert.False(row.Required);
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "HPMax", 2); // require clears ignore
        row = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Single(row => row.Key == "HPMax");
        Assert.True(row.Required); Assert.False(row.Ignored);
        await fixture.ClickPriorityAction(DaevanionPriorityKind.Stat, "HPMax", 2); // return to normal
        row = DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Single(row => row.Key == "HPMax");
        Assert.False(row.Required); Assert.False(row.Ignored);
        await fixture.ClickButton("Ariel");
        Assert.All(DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Ariel).Priorities, row => Assert.False(row.Required));
        Assert.True(DaevanionPoolSettings.ForPool(fixture.Plans.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities.Single(row => row.Key == "MPMax").Required);
    }

    [Fact]
    public async Task InfeasibleRequiredSelectionReportsTheBudgetConflictWithoutClaimingAnIntermediateRoute()
    {
        await using var fixture = new Fixture("en", new InfeasibleOptimizer());
        await fixture.Render();
        await fixture.MovePriority(new(DaevanionPriorityKind.Stat, "HPMax", null, false, Required: true));
        await fixture.ClickButton("Calculate route");
        var html = await fixture.Html();
        Assert.Contains("data-required-budget-conflict", html);
        Assert.Contains("Required nodes and their connections do not fit", html);
        Assert.DoesNotContain("Intermediate result", html);
        Assert.DoesNotContain("data-planned-stat=", html);
        Assert.False(fixture.Plans.SaveRoute("Impossible route"));
    }

    [Fact]
    public async Task SavingDuplicateNamesAtTheSameTimestampSelectsTheNewRouteByItsId()
    {
        await using var fixture = new Fixture("en", new FullBoardOptimizer(), new FixedTime());
        fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Budgets = new(570, 232, 232), Weights = new(34, 12, 10) });
        await fixture.Plans.CalculateAsync();
        Assert.True(fixture.Plans.SaveRoute("Shared name"));
        var oldId = Assert.Single(fixture.Plans.Snapshot.Routes).Id;
        await fixture.Render();
        await fixture.ChangeInput("Route name", "Shared name");
        await fixture.ClickButton("Save new");
        var created = Assert.Single(fixture.Plans.Snapshot.Routes, route => route.Id != oldId);
        var html = await fixture.Html();
        Assert.Contains("checked", Regex.Match(html, "data-saved-route=\"" + created.Id + "\"[^>]*>(.*?)</label>", RegexOptions.Singleline).Value);
        Assert.DoesNotContain("checked", Regex.Match(html, "data-saved-route=\"" + oldId + "\"[^>]*>(.*?)</label>", RegexOptions.Singleline).Value);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportRealCalculatorWithVerifiedOfflineRoute(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_DAEVANION_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        var outputRoot = Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar;
        Assert.True(folder.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase), "Preview output must stay beneath output/social-media.");
        await using var fixture = new Fixture(language, new DaevanionOptimizer());
        Assert.True(fixture.Plans.SetClass("Cleric"));
        Assert.True(fixture.Plans.UpdateDraft(fixture.Plans.Snapshot.Draft with { Budgets = new(20, 0, 0) }));
        using var diagnostics = new StringWriter();
        using var listener = new System.Diagnostics.TextWriterTraceListener(diagnostics);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try { await fixture.Plans.CalculateAsync(); }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
        Assert.True(fixture.Plans.Snapshot.Result?.Optimal, diagnostics.ToString());
        Assert.True(fixture.Plans.SaveRoute(language == "de" ? "PvE · Beispiel" : "PvE · Example"));
        Assert.True(fixture.Plans.ActivateRoute(Assert.Single(fixture.Plans.Snapshot.Routes).Id));
        var html = await fixture.Render();
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = File.ReadAllText(Path.Combine(wwwroot, "css", "app.css")) + "\n" +
            File.ReadAllText(Path.Combine(wwwroot, "daevanion-calculator.css"));
        var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "calculator-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><base href=\"" +
            WebUtility.HtmlEncode(baseUri) + "\"><style>" + css + "\nhtml,body{height:auto}.dc-calculator{height:auto;overflow:visible}</style></head><body>" + html + "</body></html>");
        var active = fixture.Plans.GetActiveRoute("Cleric")!;
        var boardPlan = active.Result.Boards.OrderByDescending(board => board.Cost).First();
        using var painter = new Soulcrest.App.Overlay.DaevanionOverlayRenderer(wwwroot);
        using var picture = painter.Preview("Cleric", boardPlan.FamilyId, language, route: boardPlan);
        picture.Save(Path.Combine(folder, "overlay-route-" + language + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-daevanion-ui-" + Guid.NewGuid().ToString("N"));
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private HtmlRootComponent _root;
        private CapturedDaevanionCalculator? _view;
        public string StorePath => Path.Combine(_directory, "plans.json");
        public DaevanionPlanService Plans { get; }
        public NoSolve Optimizer { get; } = new();
        public HtmlRenderer Renderer { get; }
        public Fixture(string language, IDaevanionOptimizer? optimizer = null, TimeProvider? time = null)
        {
            UiText.Language = language;
            Plans = new(StorePath, optimizer ?? Optimizer, time);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Plans);
            _provider = services.BuildServiceProvider();
            Renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await Renderer.RenderComponentAsync<CapturedDaevanionCalculator>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CapturedDaevanionCalculator.Captured)] = (Action<CapturedDaevanionCalculator>)(view => _view = view) }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public Task<string> Html() => Renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<DaevanionBoard> CurrentBoard() => Renderer.Dispatcher.InvokeAsync(() => (DaevanionBoard)typeof(DaevanionCalculatorView)
            .GetProperty("CurrentBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!);
#pragma warning disable BL0006 // Extract only the callbacks emitted by the production Razor view.
        public Task MovePriority(DaevanionPriorityMove move) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(DaevanionPriorityTable)) continue;
                var attributes = Enumerable.Range(index + 1, frame.ComponentSubtreeLength - 1).Select(i => frames.Array[i])
                    .Where(item => item.FrameType == RenderTreeFrameType.Attribute).ToDictionary(item => item.AttributeName, item => item.AttributeValue);
                if (!Equals(attributes[nameof(DaevanionPriorityTable.Kind)], move.Kind)) continue;
                await ((EventCallback<DaevanionPriorityMove>)attributes[nameof(DaevanionPriorityTable.Moved)]).InvokeAsync(move);
                await _view!.RefreshNow(); return;
            }
            throw new InvalidOperationException("Priority callback not found.");
        });
        public Task EditSkill(DaevanionPriorityKind kind, DaevanionSkillEdit edit) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var callback = await CaptureSkillEdit(kind); await callback.InvokeAsync(edit); await _view!.RefreshNow();
        });
        public Task<EventCallback<DaevanionSkillEdit>> CaptureSkillEdit(DaevanionPriorityKind kind) => Renderer.Dispatcher.InvokeAsync(() =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(DaevanionPriorityTable)) continue;
                var attributes = Enumerable.Range(index + 1, frame.ComponentSubtreeLength - 1).Select(i => frames.Array[i])
                    .Where(item => item.FrameType == RenderTreeFrameType.Attribute).ToDictionary(item => item.AttributeName, item => item.AttributeValue);
                if (Equals(attributes[nameof(DaevanionPriorityTable.Kind)], kind))
                    return (EventCallback<DaevanionSkillEdit>)attributes[nameof(DaevanionPriorityTable.SkillChanged)];
            }
            throw new InvalidOperationException("Skill edit callback not found.");
        });
        public Task ChangeCategoryWeight(DaevanionPriorityKind kind, int value) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(DaevanionPriorityTable)) continue;
                var attributes = Enumerable.Range(index + 1, frame.ComponentSubtreeLength - 1).Select(i => frames.Array[i])
                    .Where(item => item.FrameType == RenderTreeFrameType.Attribute).ToDictionary(item => item.AttributeName, item => item.AttributeValue);
                if (!Equals(attributes[nameof(DaevanionPriorityTable.Kind)], kind)) continue;
                await ((EventCallback<int>)attributes[nameof(DaevanionPriorityTable.WeightChanged)]).InvokeAsync(value);
                await _view!.RefreshNow(); return;
            }
            throw new InvalidOperationException("Weight callback not found.");
        });
        public Task<EventCallback<DaevanionPriorityMove>> CapturePriorityMove(DaevanionPriorityKind kind) => Renderer.Dispatcher.InvokeAsync(() =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(DaevanionPriorityTable)) continue;
                var attributes = Enumerable.Range(index + 1, frame.ComponentSubtreeLength - 1).Select(i => frames.Array[i])
                    .Where(item => item.FrameType == RenderTreeFrameType.Attribute).ToDictionary(item => item.AttributeName, item => item.AttributeValue);
                if (Equals(attributes[nameof(DaevanionPriorityTable.Kind)], kind))
                    return (EventCallback<DaevanionPriorityMove>)attributes[nameof(DaevanionPriorityTable.Moved)];
            }
            throw new InvalidOperationException("Priority callback not found.");
        });
        public Task ClickPriorityAction(DaevanionPriorityKind kind, string key, int actionIndex) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var parentTree = Tree(); var parentFrames = parentTree.GetFrames();
            Dictionary<string, object?>? parameters = null;
            for (var index = 0; index < parentFrames.Count; index++)
            {
                var frame = parentFrames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(DaevanionPriorityTable)) continue;
                var values = Enumerable.Range(index + 1, frame.ComponentSubtreeLength - 1).Select(i => parentFrames.Array[i])
                    .Where(item => item.FrameType == RenderTreeFrameType.Attribute).ToDictionary(item => item.AttributeName, item => (object?)item.AttributeValue);
                if (Equals(values[nameof(DaevanionPriorityTable.Kind)], kind)) { parameters = values; break; }
            }
            Assert.NotNull(parameters);
            CapturedDaevanionPriorityTable? table = null;
            parameters[nameof(CapturedDaevanionPriorityTable.Captured)] = (Action<CapturedDaevanionPriorityTable>)(item => table = item);
            await Renderer.RenderComponentAsync<CapturedDaevanionPriorityTable>(ParameterView.FromDictionary(parameters));
            using var tree = new RenderTreeBuilder();
            typeof(DaevanionPriorityTable).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, [tree]);
            var frames = tree.GetFrames();
            var row = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == "data-priority-key" && Equals(frames.Array[i].AttributeValue, key));
            while (frames.Array[row].FrameType != RenderTreeFrameType.Element) row--;
            var callback = Enumerable.Range(row, frames.Array[row].ElementSubtreeLength).Select(i => frames.Array[i])
                .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").ElementAt(actionIndex).AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else throw new InvalidOperationException("Unexpected row callback.");
            await _view!.RefreshNow();
        });
        public Task ChangeInput(string label, string value) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == "aria-label" && Equals(frames.Array[i].AttributeValue, label));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index, frames.Array[index].ElementSubtreeLength).Select(i => frames.Array[i]).Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeValue;
            var args = new ChangeEventArgs { Value = value };
            if (callback is EventCallback<ChangeEventArgs> typed) await typed.InvokeAsync(args);
            else if (callback is EventCallback untyped) await untyped.InvokeAsync(args);
            else throw new InvalidOperationException("Unexpected change callback.");
            await _view!.RefreshNow();
        });
        public Task ClickButton(string label) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(); var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var children = Enumerable.Range(index + 1, frame.ElementSubtreeLength - 1).Select(i => frames.Array[i]).ToArray();
                if (!children.Any(child => child.FrameType == RenderTreeFrameType.Text && child.TextContent == label)) continue;
                var callback = children.Single(child => child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "onclick").AttributeValue;
                if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
                else if (callback is EventCallback untyped) await untyped.InvokeAsync();
                else if (callback is Func<Task> asynchronous) await asynchronous();
                else if (callback is Action synchronous) synchronous();
                else throw new InvalidOperationException("Unexpected click callback: " + callback?.GetType().FullName);
                await _view!.RefreshNow(); return;
            }
            throw new InvalidOperationException("Button not found.");
        });
        private RenderTreeBuilder Tree()
        {
            var tree = new RenderTreeBuilder(); typeof(DaevanionCalculatorView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [tree]); return tree;
        }
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _language;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    public sealed class NoSolve : IDaevanionOptimizer
    {
        public int Calls { get; private set; }
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("A render must never start optimization."); }
    }

    private sealed class FixedTime : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero); }

    private sealed class FullBoardOptimizer : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var rawPlans = DaevanionRuleCatalog.Default.ForClass(request.ClassId).Select(board =>
            {
                var start = board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
                var order = new List<int> { start }; var seen = new HashSet<int> { start }; var edges = new List<DaevanionPlanEdge>();
                for (var index = 0; index < order.Count; index++)
                    foreach (var neighbor in board.Nodes.First(node => node.Node.Id == order[index]).NeighborIds)
                        if (seen.Add(neighbor)) { edges.Add(new(order[index], neighbor)); order.Add(neighbor); }
                return new DaevanionBoardPlan { BoardId = board.Board.Id, FamilyId = board.Board.FamilyId, Pool = board.Pool,
                    NodeIds = order, Edges = edges, ActivationOrder = order, Cost = board.MaximumCost, Score = DaevanionScoring.ScoreForNodes(request, order) };
            }).ToArray();
            var plans = DaevanionScoring.ScoreBoards(request, rawPlans);
            var estimate = request.Mode == DaevanionOptimizationMode.CombatScore
                ? DaevanionCombatScoreModel.Estimate(request, plans.SelectMany(board => board.NodeIds)) : null;
            return Task.FromResult(new DaevanionOptimizationResult { ClassId = request.ClassId, Status = DaevanionOptimizationStatus.Optimal,
                ScoreModelVersion = estimate is null ? null : DaevanionCombatScoreModel.Version,
                EstimatedCombatScore = estimate?.ObjectiveDelta, ActualEstimatedCombatScore = estimate?.ActualDelta,
                EstimatedTotalCombatScore = estimate?.Total,
                CatalogVersion = DaevanionCatalog.Default.DataVersion, CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint,
                RulesVersion = DaevanionRuleCatalog.Default.Version, Boards = plans });
        }
    }

    private sealed class InfeasibleOptimizer : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new DaevanionOptimizationResult
            { ClassId = request.ClassId, Status = DaevanionOptimizationStatus.Infeasible, CatalogVersion = DaevanionCatalog.Default.DataVersion,
                CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint, RulesVersion = DaevanionRuleCatalog.Default.Version });
    }
}

public sealed class CapturedDaevanionCalculator : DaevanionCalculatorView
{
    [Parameter] public Action<CapturedDaevanionCalculator>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}

public sealed class CapturedDaevanionPriorityTable : DaevanionPriorityTable
{
    [Parameter] public Action<CapturedDaevanionPriorityTable>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
}
