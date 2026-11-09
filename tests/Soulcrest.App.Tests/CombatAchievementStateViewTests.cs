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
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatAchievementStateViewTests
{
    private const string Profile = "SYNTHETIC-ACHIEVEMENT-VIEW";
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task NormalCollectionAndObservedFilterUseAllObservedIdsWithoutPromotingReferenceRows()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(State());
        Assert.Equal(new[] { 100, 200 }, Cards(html));
        Assert.Contains("data-achievement-catalog-count=\"2\"", html);
        Assert.Contains("data-achievement-known-family-count=\"1\"", html);
        Assert.Contains("data-achievement-observed-family-count>2", html);
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Equal(new[] { 100, 200 }, Cards(html)); // 200 has no interpreted progress contract.
        Assert.DoesNotContain("data-achievement-current-value=\"99\"", html);
        Assert.DoesNotContain(Profile, html);
    }

    [Fact]
    public async Task FavoriteUsesTheFamilyAndKeepsItsStateWhenTheUserBrowsesAnotherStage()
    {
        await using var fixture = new Fixture();
        var binding = new AchievementProfileBinding(Profile, "SYNTHETIC-REVISION");
        var toggled = new List<int>();
        await fixture.Render(State() with { Binding = binding }, Catalog(true), Definitions(true),
            new(binding, [100]), toggled.Add);
        var html = await fixture.ChangeStage(100, 111);
        var card = Card(html, 100);
        Assert.Contains("data-achievement-stage=\"111\"", card);
        Assert.Contains("data-achievement-favorite-family=\"100\" aria-pressed=\"true\"", card);
        await fixture.Click("data-achievement-favorite-family", "100");
        Assert.Equal(new[] { 100 }, toggled);
    }

    [Fact]
    public async Task SearchInputFiltersImmediatelyAndClearingItRestoresTheSelection()
    {
        await using var fixture = new Fixture();
        await fixture.Render(State());
        var html = await fixture.Search("Example 200");
        Assert.Equal(new[] { 200 }, Cards(html));
        Assert.Contains("maxlength=\"200\"", html);
        html = await fixture.Search("unmatched reward name");
        Assert.Empty(Cards(html)); Assert.Contains("data-achievement-search-empty", html);
        html = await fixture.Search("");
        Assert.Equal(new[] { 100, 200 }, Cards(html));
        Assert.DoesNotContain("ca-pillar", html); Assert.DoesNotContain("ca-group-summary", html);
    }

    [Fact]
    public async Task ExactTitleRewardSearchOpensItsRequiredCatalogueStage()
    {
        await using var fixture = new Fixture();
        var display = CombatAchievementDisplayCatalog.Default;
        const int titleId = 13020043;
        var route = CombatTitleUnlockCatalog.Default.Find(titleId)!.Routes.Single(item => item.Kind == "AchievementRewardItem");
        await fixture.Render(State() with { MetadataBuild = display.MetadataBuild }, display, AchievementDefinitionCatalog.Default);
        var html = await fixture.Search(CombatTitleDisplayCatalog.Default.Find(titleId)!.NameEn);
        var card = Card(html, route.SourceId);
        Assert.NotEmpty(card);
        Assert.Contains("data-achievement-stage=\"" + route.StageId + "\"", card);
        Assert.Contains("data-achievement-title-link=\"" + titleId + "\"", card);
        Assert.DoesNotContain("data-achievement-observed-value", card);
    }

    [Fact]
    public async Task FactionChangeUpdatesCountsAndCardsWithoutLosingCommonOrUnknownDefinitions()
    {
        await using var fixture = new Fixture();
        var factions = CombatAchievementFactionCatalog.FromJson("""
            {"schema":1,"metadataBuild":42,"referenceOnly":true,"families":[
            {"id":100,"faction":"Elyos","stageIds":[110]},
            {"id":200,"faction":"Asmodian","stageIds":[210]}]}
            """);
        var html = await fixture.Render(State(), faction: Factions.Elyos, factions: factions);
        Assert.Equal(new[] { 100 }, Cards(html)); Assert.Contains("data-achievement-catalog-count=\"1\"", html);
        html = await fixture.ChangeFaction(Factions.Asmodian);
        Assert.Equal(new[] { 200 }, Cards(html));
        html = await fixture.ChangeFaction(null);
        Assert.Equal(new[] { 100, 200 }, Cards(html));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(139)]
    [InlineData(int.MinValue)]
    public async Task BaselineValuesStayInStateWithoutAppearingAsUiCounters(int value)
    {
        await using var fixture = new Fixture();
        var state = State() with { ObservedValues = ImmutableDictionary<int, int>.Empty.Add(200, value) };
        var html = await fixture.Render(state);
        var card = Card(html, 200);
        AssertNoRawValueVisuals(card);
        Assert.Contains("data-achievement-view-source=\"Live\"", html);
        Assert.Equal(value, state.ObservedValues[200]);
        Assert.DoesNotContain("data-achievement-current-value=", card);
        Assert.DoesNotContain("data-achievement-activity=", card);
        Assert.DoesNotContain("data-achievement-target=", card);
        Assert.DoesNotContain("aria-valuenow", card);
        Assert.DoesNotContain("data-achievement-completed=", card);
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Equal(new[] { 100, 200 }, Cards(html));
    }

    [Fact]
    public async Task SavedRawValuesStayHiddenWhileQualifiedSavedCountersKeepTheirSource()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "en";
        try
        {
            var state = State() with { Source = AchievementViewSource.Saved,
                ObservedValues = ImmutableDictionary<int, int>.Empty.Add(100, 1).Add(200, 139) };
            var html = await fixture.Render(state);
            AssertNoRawValueVisuals(html);
            Assert.Contains("data-achievement-view-source=\"Saved\"", html);
            Assert.Contains("data-achievement-stage-scope=\"Saved\"", Card(html, 100));
            Assert.Contains("data-achievement-current-value=\"1\"", Card(html, 100));
            Assert.Contains("aria-valuenow=\"1\"", Card(html, 100));
            Assert.DoesNotContain("data-achievement-current-value=", Card(html, 200));
            Assert.DoesNotContain("data-achievement-activity=", html);
            Assert.Equal(139, state.ObservedValues[200]);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawFamilyValuesStayHiddenWithoutReplacingQualifiedStageCountersOrBars(bool saved)
    {
        await using var fixture = new Fixture();
        var state = State() with { Source = saved ? AchievementViewSource.Saved : AchievementViewSource.Live,
            ObservedValues = ImmutableDictionary<int, int>.Empty.Add(100, 139) };
        var card = Card(await fixture.Render(state), 100);
        AssertNoRawValueVisuals(card);
        Assert.Contains("data-achievement-stage-scope=\"" + (saved ? "Saved" : "Live") + "\"", card);
        Assert.Contains("data-achievement-current-value=\"1\"", card);
        Assert.Contains("data-achievement-target=\"5\"", card);
        Assert.Contains("aria-valuenow=\"1\"", card);
        Assert.Contains("width:20%", card);
        Assert.DoesNotContain("data-achievement-current-value=\"139\"", card);
        Assert.Equal(139, state.ObservedValues[100]);
    }

    [Fact]
    public async Task AQualifiedStageNeverInventsAnUnobservedRawValue()
    {
        await using var fixture = new Fixture();
        var card = Card(await fixture.Render(State()), 100);
        Assert.Contains("data-achievement-current-value=\"1\"", card);
        Assert.DoesNotContain("data-achievement-observed-value=", card);
        Assert.DoesNotContain("data-achievement-reported-value=", card);
    }

    [Fact]
    public async Task UnknownForeignAndWrongBuildValuesCannotEnterAnActiveCard()
    {
        await using var fixture = new Fixture();
        var state = State() with { ObservedValues = ImmutableDictionary<int, int>.Empty.Add(200, 139).Add(999, 987) };
        foreach (var unbound in new[] { state with { Source = AchievementViewSource.Unknown },
            state with { ProfileId = "SYNTHETIC-OTHER-PROFILE" }, state with { MetadataBuild = 43 } })
        {
            var html = await fixture.Render(unbound);
            Assert.DoesNotContain("data-achievement-observed-value=", html);
        }
        Assert.Null(CombatAchievementsPresentation.ObservedValue(state with { ObservedFamilyIds = [100] }, 200));
        Assert.Null(CombatAchievementsPresentation.ObservedValue(state, 999));
    }

    [Fact]
    public async Task ACurrentFamilyValueDoesNotBecomeTheCounterOfAManuallyViewedHistoricalStage()
    {
        await using var fixture = new Fixture();
        await fixture.Render(State() with { ObservedValues = ImmutableDictionary<int, int>.Empty.Add(100, 139) },
            Catalog(true), Definitions(true));
        var card = Card(await fixture.ChangeStage(100, 111), 100);
        AssertNoRawValueVisuals(card);
        Assert.DoesNotContain("aria-valuenow", card);
        Assert.DoesNotContain("data-achievement-current-value=\"139\"", card);
        Assert.Contains("data-achievement-stage-scope=\"Catalogue\"", card);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 8)]
    [InlineData(true, 0)]
    public async Task LiveProgressCannotOutliveItsOwnGenerationWhileBoundFavoritesRemainEditable(bool fresh, long generation)
    {
        await using var fixture = new Fixture();
        var binding = new AchievementProfileBinding(Profile, "SYNTHETIC-REVISION");
        var toggled = new List<int>();
        var html = await fixture.Render(State() with { Binding = binding,
            ObservedValues = ImmutableDictionary<int, int>.Empty.Add(100, 1).Add(200, 139),
            Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(200, new(200, 1, 139, At)) },
            favorites: new(binding, [100]), toggle: toggled.Add,
            snapshot: new CharacterCombatSnapshot { ProfileId = Profile, IsFresh = fresh, Generation = generation });
        Assert.Contains("data-achievement-view-source=\"Unknown\"", html);
        Assert.DoesNotContain("data-achievement-current-value=", html);
        Assert.DoesNotContain("data-achievement-observed-value=", html);
        Assert.DoesNotContain("data-achievement-activity=", html);
        await fixture.Click("data-achievement-favorite-family", "100");
        Assert.Equal(new[] { 100 }, toggled);
    }

    [Fact]
    public async Task ANativeFamilyUsesItsProgressStageWhileEarlierRewardsRemainOpen()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "en";
        try
        {
            const int familyId = 400100300;
            var native = CombatAchievementDisplayCatalog.Default;
            var next = native.Find(familyId)!.Stages[3];
            var raw = EmptyRaw() with { Achievements = EmptyRaw().Achievements.Add(familyId, new(47, 0, 2)) };
            var state = new AchievementViewState
            {
                ProfileId = Profile, Source = AchievementViewSource.Live, MetadataBuild = native.MetadataBuild,
                Generation = 7, Sequence = 12, ObservedFamilyIds = [familyId],
                ObservedValues = ImmutableDictionary<int, int>.Empty.Add(familyId, 47),
                Progress = new AchievementRuleEvaluator().Evaluate(raw, native.MetadataBuild, 7, 12)
            };
            await fixture.Render(state, native, AchievementDefinitionCatalog.Default);
            var card = Card(await fixture.Click("data-achievement-progress-filter", "Observed"), familyId);
            Assert.Contains("data-achievement-stage=\"" + next.Id + "\"", card);
            Assert.Contains("data-achievement-stage-scope=\"Live\"", card);
            Assert.Contains(WebUtility.HtmlEncode(next.NameEn), card);
            Assert.Contains(WebUtility.HtmlEncode(next.ConditionEn), card);
            Assert.Contains("data-achievement-current-value=\"47\"", card);
            AssertNoRawValueVisuals(card);
            Assert.Contains("data-achievement-target=\"50\"", card);
            Assert.Contains("data-achievement-claimed=\"false\"", card);
            Assert.Contains("data-achievement-completed=\"false\"", card);
            Assert.Contains("aria-valuenow=\"47\"", card);
            foreach (var reward in next.Rewards) Assert.Contains(WebUtility.HtmlEncode(reward.NameEn), card);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(9, 0, 0, 100)]
    [InlineData(100, 0, 1, 300)]
    [InlineData(301, 0, 2, 1000)]
    [InlineData(74600, 0, 7, 100000)]
    [InlineData(74600, 1, 7, 100000)]
    public async Task MonsterChainUsesTheAdvancedProgressGoalWithoutRequiringOldClaims(int value, byte prefix, int index, long target)
    {
        await using var fixture = new Fixture();
        const int id = 411100100;
        var native = CombatAchievementDisplayCatalog.Default;
        var raw = EmptyRaw() with { Achievements = EmptyRaw().Achievements.Add(id, new(value, 0, prefix)) };
        var state = new AchievementViewState
        {
            ProfileId = Profile, Source = AchievementViewSource.Live, MetadataBuild = native.MetadataBuild,
            Generation = 7, Sequence = 12, ObservedFamilyIds = [id], ObservedValues = ImmutableDictionary<int, int>.Empty.Add(id, value),
            Progress = new AchievementRuleEvaluator().Evaluate(raw, native.MetadataBuild, 7, 12)
        };
        await fixture.Render(state, native, AchievementDefinitionCatalog.Default);
        var card = Card(await fixture.Click("data-achievement-progress-filter", "Observed"), id);
        Assert.Contains("data-achievement-stage=\"" + native.Find(id)!.Stages[index].Id + "\"", card);
        Assert.Contains("data-achievement-current-value=\"" + value + "\"", card);
        Assert.Contains("data-achievement-target=\"" + target + "\"", card);
        Assert.Contains("data-achievement-claimed=\"false\"", card);
        Assert.Equal(prefix, raw.Achievements[id].RewardedStep);
    }

    [Fact]
    public async Task MissingOrWrongFavoriteRevisionCannotInvokeTheToggleCallback()
    {
        await using var fixture = new Fixture();
        var binding = new AchievementProfileBinding(Profile, "SYNTHETIC-REVISION");
        var toggled = new List<int>();
        foreach (var state in new[] { State(), State() with { Binding = binding } })
        {
            var html = await fixture.Render(state, favorites: new(binding with { Revision = "SYNTHETIC-OTHER-REVISION" }, [100]), toggle: toggled.Add);
            Assert.Contains("disabled", Card(html, 100));
            await fixture.Click("data-achievement-favorite-family", "100");
        }
        Assert.Empty(toggled);
    }

    [Fact]
    public async Task AsyncFavoriteStoreFailureShowsASafeErrorAndDoesNotConfirmAPin()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "de";
        try
        {
            var binding = new AchievementProfileBinding(Profile, "SYNTHETIC-REVISION");
            await fixture.Render(State() with { Binding = binding }, favorites: new(binding, []), toggleAsync: async _ =>
            { await Task.Yield(); throw new IOException("SYNTHETIC-PRIVATE-PATH-MUST-NOT-RENDER"); });
            var html = await fixture.Click("data-achievement-favorite-family", "100");
            Assert.Contains("data-achievement-favorite-error", html);
            Assert.Contains("role=\"alert\"", html); Assert.Contains("Favorit konnte nicht gespeichert werden.", html);
            Assert.DoesNotContain("SYNTHETIC-PRIVATE-PATH-MUST-NOT-RENDER", html);
            Assert.DoesNotContain("data-achievement-favorite-saving", html);
            Assert.Contains("data-achievement-favorite-family=\"100\" aria-pressed=\"false\"", Card(html, 100));
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task PendingFavoriteShowsBusyAndItsFailureCannotLeakIntoAnotherAssignment()
    {
        await using var fixture = new Fixture();
        var binding = new AchievementProfileBinding(Profile, "SYNTHETIC-REVISION");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Render(State() with { Binding = binding }, favorites: new(binding, []), toggleAsync: async _ =>
        { entered.SetResult(); await release.Task; throw new IOException("Synthetic asynchronous favorite failure."); });
        var click = fixture.Click("data-achievement-favorite-family", "100");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var html = await fixture.Html();
        Assert.Contains("data-achievement-favorite-saving", html); Assert.Contains("disabled", Card(html, 100));
        var replacement = binding with { Revision = "SYNTHETIC-OTHER-REVISION" };
        await fixture.Update(State() with { Binding = replacement }, new(replacement, []));
        release.SetResult(); await click;
        html = await fixture.Html();
        Assert.DoesNotContain("data-achievement-favorite-error", html);
        Assert.DoesNotContain("data-achievement-favorite-saving", html);
    }

    [Fact]
    public async Task RetiredEventRowsDisappearFromActiveCountsAndActivityWhileTheirHistoryRemainsReadable()
    {
        await using var fixture = new Fixture();
        var native = CombatAchievementDisplayCatalog.Default;
        var retired = 239241400;
        var state = new AchievementViewState
        {
            ProfileId = Profile, Source = AchievementViewSource.Live, MetadataBuild = native.MetadataBuild,
            Generation = 7, Sequence = 12, ObservedFamilyIds = [retired, 510340100],
            Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(retired, new(retired, 0, 1, At)),
            Milestones = [new("synthetic-retired-history", AchievementMilestoneKind.Completed, retired, 239241401,
                At, null, "synthetic", native.MetadataBuild, "v1", null, EmptyRaw())]
        };
        var html = await fixture.Render(state, native, AchievementDefinitionCatalog.Default);
        Assert.DoesNotContain("data-achievement-area-filter", html);
        Assert.DoesNotContain("data-achievement-card=\"239241400\"", html);
        Assert.Contains("data-achievement-catalog-count=\"676\"", html);
        Assert.Contains("data-achievement-observed-family-count>0", html);
        Assert.Contains("data-achievement-recent-family-count>0", html);
        Assert.Contains("data-achievement-milestone-kind=\"Completed\"", html);
        Assert.Contains("[Event] Abyss", html);
        Assert.DoesNotContain("data-achievement-card=\"510340100\"", html);
        Assert.DoesNotContain("data-achievement-card=\"520340100\"", html);
        Assert.True(state.Activities.ContainsKey(retired));
    }

    [Fact]
    public async Task GenericOwnActivityKeepsItsTimestampWithoutShowingRawValuesOrInventingAGoal()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "de";
        try
        {
            var state = State() with { Activities = ImmutableDictionary<int, AchievementActivity>.Empty
                .Add(200, new(200, 1499, 1500, At)) };
            var html = await fixture.Render(state);
            var card = Card(html, 200);
            Assert.Contains("Fortschritt aktualisiert", card);
            Assert.Contains("data-achievement-activity=\"200\"", card);
            AssertNoRawValueVisuals(card);
            Assert.DoesNotContain("1.500", card); Assert.DoesNotContain("1500", card);
            Assert.Equal(1500, state.Activities[200].CurrentValue);
            Assert.Contains("datetime=\"" + At.ToString("O") + "\"", card);
            Assert.Contains("Abschlussstatus unbekannt", card);
            Assert.DoesNotContain("data-achievement-completed=", card); Assert.DoesNotContain("data-achievement-claimed=", card);
            Assert.DoesNotContain("data-achievement-target=", card); Assert.DoesNotContain("aria-valuenow", card);
            Assert.DoesNotContain("data-achievement-current-value=", card);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task RecentFilterShowsOnlyNamedActivityCardsInNewestFirstOrder()
    {
        await using var fixture = new Fixture();
        var activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(100, new(100, 1, 2, At))
            .Add(200, new(200, null, 9, At.AddSeconds(1)))
            .Add(900001, new(900001, 0, 1, At.AddSeconds(2)));
        await fixture.Render(State() with { Activities = activities });
        var html = await fixture.Click("data-achievement-progress-filter", "Recent");
        Assert.Equal(new[] { 200, 100 }, RenderedCards(html));
        Assert.Contains("data-achievement-recent-family-count>2", html);
        AssertNoRawValueVisuals(html);
        Assert.Contains("data-achievement-activity=\"200\"", Card(html, 200));
        Assert.Contains("data-achievement-activity=\"100\"", Card(html, 100));
        Assert.DoesNotContain("data-achievement-card=\"900001\"", html);
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Equal(new[] { 100, 200 }, Cards(html));
    }

    [Fact]
    public async Task BaselinesWithoutActivitiesKeepObservedRowsButLeaveRecentFilterEmpty()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(State());
        Assert.DoesNotContain("data-achievement-activity=", html);
        Assert.Contains("data-achievement-recent-family-count>0", html);
        html = await fixture.Click("data-achievement-progress-filter", "Recent");
        Assert.Empty(Cards(html));
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Equal(new[] { 100, 200 }, Cards(html));
    }

    [Fact]
    public async Task SavedUnknownWrongProfileAndUnchangedCountsNeverShowLiveActivity()
    {
        await using var fixture = new Fixture();
        var state = State() with { Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(200, new(200, 0, 1, At)) };
        foreach (var invalid in new[] { state with { Source = AchievementViewSource.Saved },
            state with { Source = AchievementViewSource.Unknown }, state with { ProfileId = "SYNTHETIC-OTHER-OWNER" },
            state with { MetadataBuild = 43 }, state with { Activities = state.Activities.SetItem(200, new(200, 1, 1, At)) } })
        {
            var html = await fixture.Render(invalid);
            Assert.DoesNotContain("data-achievement-activity=", html);
            html = await fixture.Click("data-achievement-progress-filter", "Recent");
            Assert.Empty(Cards(html));
        }
    }

    [Fact]
    public async Task ActivityCannotReplaceIndependentQualifiedCompletionOrClaimFacts()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(State() with
        {
            Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(100, new(100, 2, 1, At)),
            Progress = Progress(new(110, 1, 5, false, false))
        });
        var card = Card(html, 100);
        Assert.Contains("data-achievement-activity=\"100\"", card);
        Assert.Contains("data-achievement-completed=\"false\"", card);
        Assert.Contains("data-achievement-claimed=\"false\"", card);
        Assert.Contains("data-achievement-current-value=\"1\"", card);
        Assert.Contains("aria-valuenow=\"1\"", card);
    }

    [Theory]
    [InlineData("Season")]
    [InlineData("Event")]
    [InlineData("BattlePass")]
    [InlineData("Attendance")]
    [InlineData("Onboarding")]
    public async Task HiddenModuleAreasStayOutsideCardsCountsSearchAndDirectNavigation(string area)
    {
        await using var fixture = new Fixture();
        var state = State() with { Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(300, new(300, 0, 1, At)) };
        var definitions = Definitions(candidateArea: area);
        var html = await fixture.Render(state, definitions: definitions, navigation: new(300, 310));
        Assert.Equal(new[] { 100, 200 }, Cards(html));
        Assert.DoesNotContain("data-achievement-area-filter", html);
        Assert.DoesNotContain("ca-areas", html);
        Assert.DoesNotContain("data-achievement-area-unqualified", html);
        Assert.DoesNotContain("data-achievement-linked-family", html);
        Assert.Contains("data-achievement-area=\"Normal\"", html);
        Assert.Contains("data-achievement-catalog-count=\"2\"", html);
        Assert.Contains("data-achievement-observed-family-count>2", html);
        Assert.Contains("data-achievement-recent-family-count>0", html);
        Assert.DoesNotContain("data-achievement-card=\"300\"", html);
        Assert.DoesNotContain("data-achievement-card=\"400\"", html);
        Assert.DoesNotContain("data-achievement-card=\"500\"", html);
        Assert.Contains("data-achievement-category-filter=\"Growth\"", html);
        html = await fixture.Search("Example 300");
        Assert.Empty(Cards(html));
        Assert.Contains("data-achievement-search-empty", html);
    }

    [Fact]
    public async Task LegacyViewAlsoHidesKnownSpecialAreasWithoutACharacterState()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(null, navigation: new(300, 310));
        Assert.Equal(new[] { 100, 200 }, Cards(html));
        Assert.Contains("data-achievement-catalog-count=\"2\"", html);
        Assert.DoesNotContain("data-achievement-area-filter", html);
        Assert.DoesNotContain("data-achievement-linked-family", html);
        Assert.Contains("data-achievement-category-filter=\"Growth\"", html);
    }

    [Fact]
    public async Task SavedProgressHasSavedStageMarkersAndFirstObservedHistoryWithoutOriginalCompletionTime()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "de";
        try
        {
            var milestone = new AchievementMilestone("SYNTHETIC-MILESTONE-SECRET", AchievementMilestoneKind.Completed,
                100, 110, At, new(2001, 1, 2, 3, 4, 5, TimeSpan.Zero), "INTERNAL-ORIGIN-SECRET", 42, "v1", null, EmptyRaw());
            var state = State() with { Source = AchievementViewSource.Saved, Milestones = [milestone], HistoryGap = true };
            var html = await fixture.Render(state);
            Assert.Contains("data-achievement-view-source=\"Saved\"", html);
            Assert.Contains("data-achievement-saved-source", html); Assert.Contains("data-achievement-saved-at", html);
            Assert.Contains("data-achievement-stage-scope=\"Saved\"", html);
            Assert.DoesNotContain("data-achievement-stage-scope=\"Live\"", html);
            Assert.Contains("Abschluss erstmals erkannt", html); Assert.Contains("data-achievement-history-gap", html);
            Assert.Contains(At.ToString("O"), html); Assert.DoesNotContain("2001-01-02", html);
            Assert.DoesNotContain("SYNTHETIC-MILESTONE-SECRET", html); Assert.DoesNotContain("INTERNAL-ORIGIN-SECRET", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task AViewStateForAnotherKnownProfileCannotSupplyProgressObservedIdsOrHistory()
    {
        await using var fixture = new Fixture();
        var other = State() with { ProfileId = "SYNTHETIC-OTHER-PROFILE", Milestones = [new("unshown", AchievementMilestoneKind.RewardClaimed,
            null, null, At, null, "private", 42, "v1", null, EmptyRaw(), "Fremder Verlauf", "Foreign history")] };
        var html = await fixture.Render(other);
        Assert.Contains("data-achievement-view-source=\"Unknown\"", html);
        Assert.DoesNotContain("data-achievement-current-value", html);
        Assert.DoesNotContain("Foreign history", html); Assert.DoesNotContain("Fremder Verlauf", html);
        html = await fixture.Click("data-achievement-progress-filter", "Observed");
        Assert.Empty(Cards(html));
    }

    [Fact]
    public async Task HistoricalFactsRemainSeparateWhileBarsUseTheValidatedDisplayPairAndRetainRawCounters()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "en";
        try
        {
            var stage = new CombatAchievementStageProgress(110, 1500, 3000, true, true)
            { Unit = CombatAchievementProgressUnit.Level, DisplayCurrent = 1, DisplayTarget = 2, RequirementsMetNow = false };
            var html = await fixture.Render(State() with { Progress = Progress(stage) });
            Assert.Contains("Level 1", html); Assert.Contains("Level 2", html);
            Assert.Contains("aria-valuenow=\"1\"", html); Assert.Contains("aria-valuemax=\"2\"", html);
            Assert.Contains("data-achievement-current-value=\"1500\"", html);
            Assert.Contains("data-achievement-target=\"3000\"", html);
            Assert.Contains("width:50%", html);
            Assert.Contains("data-achievement-completed=\"true\"", html);
            Assert.Contains("data-achievement-claimed=\"true\"", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task InvalidVersionAndUnknownSourceDoNotFallBackToTheLegacySnapshot()
    {
        await using var fixture = new Fixture();
        foreach (var state in new[] { State() with { MetadataBuild = 43 }, State() with { Source = AchievementViewSource.Unknown } })
        {
            var html = await fixture.Render(state);
            Assert.DoesNotContain("data-achievement-current-value", html);
            Assert.Contains("data-achievement-known-family-count=\"0\"", html);
        }
    }

    [Fact]
    public async Task ExplicitPeriodMilestoneUsesAGenericLocalizedLabelWithoutItsInternalPeriodKey()
    {
        await using var fixture = new Fixture();
        var previous = UiText.Language; UiText.Language = "en";
        try
        {
            var period = new AchievementMilestone("SYNTHETIC-PERIOD-ROW", AchievementMilestoneKind.PeriodChanged,
                null, null, At, null, "INTERNAL-PERIOD-ORIGIN", 42, "v1", "PRIVATE-PERIOD-KEY", EmptyRaw());
            var html = await fixture.Render(State() with { Milestones = [period] });
            Assert.Contains("New achievement period", html);
            Assert.Contains("data-achievement-milestone-kind=\"PeriodChanged\"", html);
            Assert.DoesNotContain("PRIVATE-PERIOD-KEY", html); Assert.DoesNotContain("INTERNAL-PERIOD-ORIGIN", html);
            Assert.DoesNotContain("SYNTHETIC-PERIOD-ROW", html);
        }
        finally { UiText.Language = previous; }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de", "live", "Normal")]
    [InlineData("en", "live", "Normal")]
    [InlineData("de", "saved", "Normal")]
    [InlineData("en", "saved", "Normal")]
    [InlineData("de", "activity", "Normal")]
    [InlineData("en", "activity", "Normal")]
    [InlineData("de", "elyos", "Normal")]
    [InlineData("en", "elyos", "Normal")]
    [InlineData("de", "asmodian", "Normal")]
    [InlineData("en", "asmodian", "Normal")]
    [InlineData("de", "title-search", "Normal")]
    [InlineData("en", "title-search", "Normal")]
    public async Task ExportAnonymousProductionAchievementStatePanel(string language, string source, string area)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var native = CombatAchievementDisplayCatalog.Default;
            var binding = new AchievementProfileBinding("11111111111111111111111111111111", "22222222222222222222222222222222");
            var ids = new[] { 420101200, 401100100, 401100300, 441100900, 444100500, 400100300, 411100100, 210000000 };
            var rows = new[] { (420101200, 10, (byte)1), (401100100, 22, (byte)3),
                (401100300, 251, (byte)1), (441100900, 20, (byte)2), (444100500, source == "activity" ? 1 : 0, (byte)0),
                (400100300, 27, (byte)2), (411100100, 74600, (byte)1), (210000000, 1500, (byte)0) };
            var raw = new CharacterAchievementState(rows.ToImmutableDictionary(row => row.Item1,
                row => new CharacterAchievement(row.Item2, 0, row.Item3)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
            var snapshot = new CharacterCombatSnapshot
            {
                ProfileId = binding.ProfileId, IsFresh = true, Generation = 7, Sequence = 12,
                Achievements = new(raw, Soulcrest.App.Network.NetworkCombatSourceStatus.Complete, true, At, 12, "own-achievement-list")
            };
            var state = new AchievementViewState
            {
                ProfileId = binding.ProfileId, Binding = binding,
                Source = source == "saved" ? AchievementViewSource.Saved : AchievementViewSource.Live,
                MetadataBuild = native.MetadataBuild, Generation = 7, Sequence = 12, ObservedAt = At,
                ObservedFamilyIds = ids.ToImmutableHashSet(), ObservedValues = raw.Achievements.ToImmutableDictionary(row => row.Key, row => row.Value.Count),
                Progress = CombatAchievementProgressProjection.Create(snapshot, native.MetadataBuild),
                Milestones = [new("synthetic-completed", AchievementMilestoneKind.Completed, 420101200, 420101201,
                    At.AddMinutes(-4), null, "synthetic", native.MetadataBuild, "v1", null, EmptyRaw()),
                    new("synthetic-claimed", AchievementMilestoneKind.RewardClaimed, 420101200, 420101201,
                        At.AddMinutes(-3), null, "synthetic", native.MetadataBuild, "v1", null, EmptyRaw()),
                    new("synthetic-period", AchievementMilestoneKind.PeriodChanged, null, null,
                        At.AddMinutes(-2), null, "synthetic", native.MetadataBuild, "v1", "synthetic-private-period", EmptyRaw())]
            };
            if (source == "activity") state = state with { Activities = ImmutableDictionary<int, AchievementActivity>.Empty
                .Add(444100500, new(444100500, 0, 1, At)).Add(420101200, new(420101200, 9, 10, At.AddSeconds(-10))) };
            await using var fixture = new Fixture();
            var faction = source is "elyos" or "title-search" ? Factions.Elyos : source == "asmodian" ? Factions.Asmodian : null;
            await fixture.Render(state, native, AchievementDefinitionCatalog.Default,
                favorites: new(binding, [420101200, 444100500]), toggle: _ => { }, snapshotProfile: binding.ProfileId, faction: faction);
            var html = await fixture.Click("data-achievement-progress-filter", faction is not null ? "All" : source == "activity" ? "Recent" : "Observed");
            if (source == "title-search") html = await fixture.Search(CombatTitleDisplayCatalog.Default.Find(13020043)!.NameEn);
            Assert.DoesNotContain(Profile, html); Assert.DoesNotContain("synthetic-private-period", html);
            Assert.DoesNotContain(binding.ProfileId, html); Assert.DoesNotContain(binding.Revision, html);
            Assert.Contains("data-achievement-history", html);
            if (area == "Normal" && faction is null)
            {
                Assert.Contains("data-achievement-favorite-family=\"420101200\" aria-pressed=\"true\"", Card(html, 420101200));
                Assert.Contains("data-achievement-favorite-family=\"444100500\" aria-pressed=\"true\"", Card(html, 444100500));
            }
            if (source == "activity")
            {
                Assert.Equal(new[] { 444100500, 420101200 }, RenderedCards(html));
                Assert.Contains("data-achievement-current-value=\"1\"", Card(html, 444100500));
                Assert.Contains("data-achievement-target=\"10\"", Card(html, 444100500));
                Assert.Contains("data-achievement-completed=\"false\"", Card(html, 444100500));
            }
            Assert.Contains("data-achievement-view-source=\"" + (source == "saved" ? "Saved" : "Live") + "\"", html);
            Assert.DoesNotContain("data-achievement-observed-value", html);
            Assert.DoesNotContain("ca-pillar", html);
            Assert.DoesNotContain("data-achievement-area-filter", html);
            Assert.DoesNotContain("ca-areas", html);
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            Directory.CreateDirectory(folder);
            var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
            File.WriteAllText(Path.Combine(folder, "achievement-state-" + source + "-" + area.ToLowerInvariant() + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
                + "\nhtml,body{height:auto}body{padding:30px}</style></head><body>" + html + "</body></html>");
        }
        finally { UiText.Language = previous; }
    }

    private static void AssertNoRawValueVisuals(string html)
    {
        foreach (var marker in new[] { "data-achievement-observed-value", "data-achievement-reported-value",
            "data-achievement-observed-source", "data-achievement-value-scope", "data-achievement-value-family-label",
            "Gemeldeter Wert:", "Gespeicherter Wert:", "Reported value:", "Saved value:",
            "Stand der gesamten Erfolgsreihe", "Value for the entire achievement chain" })
            Assert.DoesNotContain(marker, html);
    }

    [Theory]
    [InlineData(AchievementViewSource.Live, false)]
    [InlineData(AchievementViewSource.Live, true)]
    [InlineData(AchievementViewSource.Saved, false)]
    [InlineData(AchievementViewSource.Saved, true)]
    public async Task ConfirmedCompletedCardsAreMutedAndLastRegardlessOfRewardClaim(AchievementViewSource source, bool claimed)
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(State() with
        {
            Source = source, Progress = Progress(new(110, 5, 5, true, claimed))
        });
        Assert.Equal(new[] { 200, 100 }, RenderedCards(html));
        Assert.Contains("ca-card-completed", Card(html, 100));
        Assert.DoesNotContain("ca-card-completed", Card(html, 200));
        Assert.Contains("data-achievement-completed=\"true\"", Card(html, 100));
        Assert.Contains("data-achievement-claimed=\"" + claimed.ToString().ToLowerInvariant() + "\"", Card(html, 100));
        if (!claimed) Assert.Contains(UiText.T("Belohnung offen"), Card(html, 100));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task NumericGoalsAndCurrentRequirementsDoNotInventACompletedCard(bool? completed)
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(State() with
        {
            Progress = Progress(new(110, 5, 5, completed, null) { RequirementsMetNow = true })
        });
        Assert.Equal(new[] { 100, 200 }, RenderedCards(html));
        Assert.DoesNotContain("ca-card-completed", html);
    }

    [Fact]
    public async Task ConflictingFamiliesAreNotStyledOrSortedAsConfirmedCompletedCards()
    {
        await using var fixture = new Fixture();
        var progress = Progress(new(110, 5, 5, true, true));
        var family = progress.Find(100)! with { Status = Soulcrest.Core.Combat.CombatKnowledgeStatus.Conflict };
        var html = await fixture.Render(State() with
        {
            Progress = progress with { Families = ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(100, family) }
        });
        Assert.Equal(new[] { 100, 200 }, RenderedCards(html));
        Assert.Contains("ca-card-conflict", Card(html, 100));
        Assert.DoesNotContain("ca-card-completed", Card(html, 100));
    }

    [Fact]
    public async Task CompletionSortingAndStylingFollowTheStageActuallyShownOnTheCard()
    {
        await using var fixture = new Fixture();
        var stages = ImmutableDictionary<int, CombatAchievementStageProgress>.Empty
            .Add(110, new(110, 5, 5, true, false)).Add(111, new(111, 5, 10, false, false));
        var progress = new CombatAchievementProgressSnapshot(7, 12,
            ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(100, new(100, 111, stages)));
        var html = await fixture.Render(State() with { Progress = progress }, Catalog(true), Definitions(true));
        Assert.Equal(new[] { 100, 200 }, RenderedCards(html));
        Assert.DoesNotContain("ca-card-completed", Card(html, 100));
        html = await fixture.ChangeStage(100, 110);
        Assert.Equal(new[] { 200, 100 }, RenderedCards(html));
        Assert.Contains("ca-card-completed", Card(html, 100));
        html = await fixture.Click("data-achievement-follow-current", "100");
        Assert.Equal(new[] { 100, 200 }, RenderedCards(html));
        Assert.DoesNotContain("ca-card-completed", Card(html, 100));
    }

    [Fact]
    public async Task CompletedCardsMoveAfterAllUncompletedCardsBeforePagination()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(SortingState(26, 1001), SortingCatalog(26), SortingDefinitions(26));
        Assert.Equal(Enumerable.Range(1002, 24), RenderedCards(html));
        Assert.DoesNotContain("ca-card-completed", html);
        html = await fixture.Click("data-achievement-page", "next");
        Assert.Equal(new[] { 1026, 1001 }, RenderedCards(html));
        Assert.Contains("ca-card-completed", Card(html, 1001));
    }

    [Fact]
    public async Task RecentFilterKeepsNewestFirstOrderWithinTheOpenAndCompletedPartitions()
    {
        await using var fixture = new Fixture();
        var activities = new[] { 1003, 1005, 1001, 1004, 1002 }.Select((id, index) =>
            new AchievementActivity(id, 0, 1, At.AddSeconds(index))).ToImmutableDictionary(activity => activity.FamilyId);
        await fixture.Render(SortingState(5, 1002, 1005) with { Activities = activities }, SortingCatalog(5), SortingDefinitions(5));
        var html = await fixture.Click("data-achievement-progress-filter", "Recent");
        Assert.Equal(new[] { 1004, 1001, 1003, 1002, 1005 }, RenderedCards(html));
        Assert.Contains("ca-card-completed", Card(html, 1002));
        Assert.Contains("ca-card-completed", Card(html, 1005));
    }

    private static AchievementViewState SortingState(int count, params int[] completed)
    {
        var ids = Enumerable.Range(1001, count).ToImmutableHashSet();
        return State() with
        {
            ObservedFamilyIds = ids,
            Progress = new(7, 12, ids.ToImmutableDictionary(id => id, id =>
                new CombatAchievementFamilyProgress(id, id + 10000,
                    ImmutableDictionary<int, CombatAchievementStageProgress>.Empty.Add(id + 10000,
                        new(id + 10000, completed.Contains(id) ? 1 : 0, 1, completed.Contains(id), false)))))
        };
    }

    private static CombatAchievementDisplayCatalog SortingCatalog(int count) => CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
        categories = new[] { new CombatAchievementCategoryDefinition("Growth", "Wachstum", "Progress") },
        groups = Array.Empty<CombatAchievementGroupDefinition>(),
        achievements = Enumerable.Range(1001, count).Select(id => new CombatAchievementDisplayDefinition(id,
            "Growth", null, "Beispiel " + id, "Example " + id, "Bedingung", "Condition", null, null, [])
            { Stages = [new(id + 10000, "Beispiel " + id, "Example " + id, "Bedingung", "Condition", null, null, [])] }).ToArray()
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static AchievementDefinitionCatalog SortingDefinitions(int count) => AchievementDefinitionCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42,
        definitions = Enumerable.Range(1001, count).Select(id => new
        {
            id, area = "Normal", role = "PlayerFacing", semanticGroup = "example", resetPolicy = "None",
            signature = new string('a', 64), stageIds = new[] { id + 10000 }, reason = "example"
        }).ToArray()
    }));

    private static AchievementViewState State() => new()
    {
        ProfileId = Profile, Source = AchievementViewSource.Live, ObservedAt = At, MetadataBuild = 42,
        Generation = 7, Sequence = 12, ObservedFamilyIds = [100, 200, 300, 400, 500],
        Progress = Progress(new(110, 1, 5, false, false))
    };
    private static CombatAchievementProgressSnapshot Progress(CombatAchievementStageProgress stage) => new(7, 12,
        ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(100, new(100, 110,
            ImmutableDictionary<int, CombatAchievementStageProgress>.Empty.Add(110, stage))));
    private static CharacterAchievementState EmptyRaw() => new(ImmutableDictionary<int, CharacterAchievement>.Empty,
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
    private static int[] Cards(string html) => RenderedCards(html).Order().ToArray();
    private static int[] RenderedCards(string html) => Regex.Matches(html, "data-achievement-card=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
    private static string Card(string html, int id) => Regex.Match(html,
        "<article\\b[^>]*data-achievement-card=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
    private static CombatAchievementDisplayCatalog Catalog(bool extraStage = false) => CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
        categories = new[] { new CombatAchievementCategoryDefinition("Growth", "Wachstum", "Progress") },
        groups = Array.Empty<CombatAchievementGroupDefinition>(),
        achievements = new[] { 100, 200, 300, 400, 500 }.Select(id => new CombatAchievementDisplayDefinition(id,
            id <= 200 ? "Growth" : null, null, "Beispiel " + id, "Example " + id, "Bedingung", "Condition", null, null, [])
            { Stages = id == 100 && extraStage
                ? [new(id + 10, "Beispielstufe", "Example stage", "Bedingung", "Condition", null, null, []),
                    new(id + 11, "Weitere Beispielstufe", "Another example stage", "Bedingung", "Condition", null, null, [])]
                : [new(id + 10, "Beispielstufe", "Example stage", "Bedingung", "Condition", null, null, [])] }).ToArray()
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private static AchievementDefinitionCatalog Definitions(bool extraStage = false, string candidateArea = "Season") => AchievementDefinitionCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42,
        definitions = new[] { 100, 200, 300, 400, 500 }.Select(id => new
        {
            id, area = id <= 200 ? "Normal" : id == 500 ? "Reference" : candidateArea,
            role = id <= 200 ? "PlayerFacing" : id == 300 ? "Candidate" : "Reference",
            semanticGroup = "example", resetPolicy = "None", signature = new string('a', 64),
            stageIds = id == 100 && extraStage ? new[] { id + 10, id + 11 } : new[] { id + 10 }, reason = "example"
        }).ToArray()
    }));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedStateView? _view;
        internal Fixture()
        {
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal async Task<string> Render(AchievementViewState? state, CombatAchievementDisplayCatalog? catalog = null,
            AchievementDefinitionCatalog? definitions = null, AchievementFavoriteSnapshot? favorites = null, Action<int>? toggle = null,
            Func<int, Task>? toggleAsync = null, string? snapshotProfile = null, CharacterCombatSnapshot? snapshot = null,
            string? faction = null, CombatAchievementFactionCatalog? factions = null, CombatAchievementNavigationRequest? navigation = null)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedStateView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedStateView.Snapshot)] = snapshot ?? new CharacterCombatSnapshot { ProfileId = snapshotProfile ?? Profile, IsFresh = true, Generation = 7 },
                [nameof(CapturedStateView.Catalog)] = catalog ?? Catalog(), [nameof(CapturedStateView.Definitions)] = definitions ?? Definitions(),
                [nameof(CapturedStateView.AchievementState)] = state,
                [nameof(CapturedStateView.Faction)] = faction,
                [nameof(CapturedStateView.FactionCatalog)] = factions ?? CombatAchievementFactionCatalog.Default,
                [nameof(CapturedStateView.Navigation)] = navigation,
                [nameof(CapturedStateView.Favorites)] = favorites,
                [nameof(CapturedStateView.ToggleFavorite)] = toggleAsync is not null ? EventCallback.Factory.Create<int>(this, toggleAsync)
                    : toggle is null ? default(EventCallback<int>) : EventCallback.Factory.Create<int>(this, toggle),
                [nameof(CapturedStateView.Captured)] = (Action<CapturedStateView>)(view => _view = view)
            })));
            return await _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        }
        internal Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        internal Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        internal Task<string> Search(string query) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Search(query); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        internal Task<string> ChangeFaction(string? faction) => _renderer.Dispatcher.InvokeAsync(() =>
        { _view!.ChangeFaction(faction); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        internal Task Update(AchievementViewState state, AchievementFavoriteSnapshot favorites) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(state, favorites));
        internal Task<string> ChangeStage(int familyId, int stageId) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.ChangeStage(familyId, stageId); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    public sealed class CapturedStateView : CombatAchievementsView
    {
        [Parameter] public Action<CapturedStateView>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        public void ChangeFaction(string? faction) { Faction = faction; OnParametersSet(); StateHasChanged(); }
        internal void Update(AchievementViewState state, AchievementFavoriteSnapshot favorites)
        { AchievementState = state; Favorites = favorites; OnParametersSet(); StateHasChanged(); }
#pragma warning disable BL0006
        public async Task Search(string query)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatAchievementsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-achievement-search");
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "oninput").AttributeValue;
            var args = new ChangeEventArgs { Value = query };
            if (callback is EventCallback<ChangeEventArgs> typed) await typed.InvokeAsync(args);
            else if (callback is EventCallback untyped) await untyped.InvokeAsync(args);
            else if (callback is Action<ChangeEventArgs> action) action(args);
            else if (callback is Func<ChangeEventArgs, Task> asyncAction) await asyncAction(args);
            else throw new InvalidOperationException("Unexpected search callback.");
            StateHasChanged();
        }
        public async Task ChangeStage(int familyId, int stageId)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatAchievementsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-achievement-stage-select"
                && Equals(frames.Array[i].AttributeValue, familyId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeValue;
            var args = new ChangeEventArgs { Value = stageId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (callback is EventCallback<ChangeEventArgs> typed) await typed.InvokeAsync(args);
            else if (callback is EventCallback untyped) await untyped.InvokeAsync(args);
            else throw new InvalidOperationException("Unexpected stage callback.");
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
            else if (callback is Action action) action();
            else throw new InvalidOperationException("Unexpected achievement callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
