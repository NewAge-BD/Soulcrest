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
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Production component callbacks and typed metadata only; no host, network or user profile.</summary>
[Collection("Settings file")]
public sealed class CombatTitleAchievementNavigationTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ATitleRouteNavigatesToItsExactFamilyAndRequiredStageAfterAnotherFilter(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.RenderView();
        Assert.Equal(new[] { 11, 22, 33 }, AchievementIds(html).Order());
        html = await fixture.Click("data-achievement-category-filter", "Challenge");
        Assert.Equal(new[] { 33 }, AchievementIds(html));
        html = await fixture.Update(new(22, 222));
        Assert.Equal(new[] { 22 }, AchievementIds(html));
        Assert.Contains("data-achievement-linked-family=\"22\"", html);
        Assert.Contains("data-achievement-stage=\"222\"", Card(html, 22));
        Assert.Contains("data-current-stage-scope=\"Catalogue\"", Card(html, 22));
        Assert.Contains("data-achievement-title-link=\"1001\"", Card(html, 22));
        Assert.DoesNotContain("data-achievement-title-link=\"1002\"", Card(html, 22));
        Assert.DoesNotContain("data-achievement-title-link=\"1003\"", Card(html, 22));
        Assert.DoesNotContain("disabled", Regex.Match(Card(html, 22), "<button[^>]*data-achievement-title-link=\"1001\"[^>]*>").Value);
    }

    [Fact]
    public async Task NormalParameterUpdatesKeepTheManualStageWhileANewNavigationRequestCanChooseAnother()
    {
        await using var fixture = new Fixture("en");
        var request = new CombatAchievementNavigationRequest(22, 222);
        await fixture.RenderView(request);
        var html = await fixture.ChangeStage(22, 223);
        Assert.Contains("data-achievement-stage=\"223\"", Card(html, 22));
        html = await fixture.Update(request);
        Assert.Equal(new[] { 22 }, AchievementIds(html));
        Assert.Contains("data-achievement-stage=\"223\"", Card(html, 22));
        html = await fixture.Update(new(22, 221));
        Assert.Contains("data-achievement-stage=\"221\"", Card(html, 22));
    }

    [Fact]
    public async Task ANavigationWithoutAStageReturnsToAutomaticSelectionInsteadOfKeepingAnOlderRequiredTier()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.RenderView(new(22, 222));
        Assert.Contains("data-achievement-stage=\"222\"", Card(html, 22));
        html = await fixture.Update(new(22));
        Assert.Equal(new[] { 22 }, AchievementIds(html));
        Assert.Contains("data-achievement-stage=\"221\"", Card(html, 22));
        Assert.DoesNotContain("data-achievement-follow-current", Card(html, 22));
    }

    [Fact]
    public async Task ShowAllAndNormalFilterButtonsLeaveTheLinkedFamilySelection()
    {
        await using var fixture = new Fixture("en");
        await fixture.RenderView(new(22, 222));
        var html = await fixture.ClickText(UiText.T("Alle Erfolge anzeigen"));
        Assert.Equal(new[] { 11, 22, 33 }, AchievementIds(html).Order());
        Assert.DoesNotContain("data-achievement-linked-family=", html);
        await fixture.Update(new(22, 222));
        html = await fixture.Click("data-achievement-progress-filter", "All");
        Assert.Equal(new[] { 11, 22, 33 }, AchievementIds(html).Order());
        Assert.DoesNotContain("data-achievement-linked-family=", html);
        await fixture.Update(new(22, 222));
        html = await fixture.Click("data-achievement-category-filter", "Growth");
        Assert.Equal(new[] { 11, 22 }, AchievementIds(html).Order());
        Assert.DoesNotContain("data-achievement-linked-family=", html);
    }

    [Fact]
    public async Task InvalidNavigationIdsNeverCreateAnUnmappedCardOrStage()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.RenderView(new(99999, 9999901));
        Assert.Equal(new[] { 11, 22, 33 }, AchievementIds(html).Order());
        Assert.DoesNotContain("data-achievement-linked-family=", html);
        html = await fixture.Update(new(22, 222));
        Assert.Contains("data-achievement-stage=\"222\"", Card(html, 22));
        html = await fixture.Update(new(22, 9999901));
        Assert.Equal(new[] { 22 }, AchievementIds(html));
        Assert.Contains("data-achievement-stage=\"221\"", Card(html, 22));
        Assert.DoesNotContain("data-achievement-stage=\"9999901\"", html);
    }

    [Fact]
    public async Task InverseTitleRewardsUseExactFamilyStageAndBuildAndInvokeTheTitleId()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.RenderRewards(22, 222, 42);
        Assert.Equal(new[] { 1001 }, TitleIds(html));
        await fixture.ClickReward(1001);
        Assert.Equal(new[] { 1001 }, fixture.OpenedTitles);
        html = await fixture.RenderRewards(33, 222, 42);
        Assert.Equal(new[] { 1002 }, TitleIds(html));
        html = await fixture.RenderRewards(22, 221, 42);
        Assert.Equal(new[] { 1003 }, TitleIds(html));
        html = await fixture.RenderRewards(22, 222, 43);
        Assert.Empty(TitleIds(html));
        html = await fixture.RenderRewards(22, 222, 42, titleBuild: 43);
        Assert.Empty(TitleIds(html));
        html = await fixture.RenderRewards(22, 222, 42, unlockBuild: 43);
        Assert.Empty(TitleIds(html));
    }

    [Fact]
    public async Task MatchingNamesAndQuestRewardsNeverProduceAdditionalAchievementTitleLinks()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.RenderRewards(22, 222, 42);
        Assert.Equal(new[] { 1001 }, TitleIds(html));
        Assert.Contains(">Shared title name</span>", html);
        Assert.DoesNotContain("data-achievement-title-link=\"1004\"", html);
    }

    private static int[] AchievementIds(string html) => Regex.Matches(html, "data-achievement-card=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
    private static int[] TitleIds(string html) => Regex.Matches(html, "data-achievement-title-link=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
    private static string Card(string html, int id) => Regex.Match(html,
        "<article\\b[^>]*data-achievement-card=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;

    private static CombatAchievementDisplayCatalog Achievements() => CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
        categories = new[] { new CombatAchievementCategoryDefinition("Growth", "Wachstum", "Progress"),
            new CombatAchievementCategoryDefinition("Challenge", "Herausforderung", "Challenge") },
        groups = Array.Empty<CombatAchievementGroupDefinition>(),
        achievements = new[] { 11, 22, 33 }.Select(id => new CombatAchievementDisplayDefinition(id,
            id == 33 ? "Challenge" : "Growth", null, "Gleicher Familienname", "Shared family name",
            "Bedingung", "Condition", null, 10, [])
        {
            Stages = Enumerable.Range(1, 3).Select(ordinal => new CombatAchievementDisplayStage(id * 10 + ordinal,
                "Stufe " + ordinal, "Stage " + ordinal, "Bedingung " + ordinal, "Condition " + ordinal,
                null, ordinal * 10, [])).ToArray(),
        }).ToArray(),
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static CombatTitleDisplayCatalog Titles(int build = 42) => CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 2, metadataBuild = build, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
        titles = new[] { 1001, 1002, 1003, 1004 }.Select(id => new CombatTitleDisplayDefinition(id,
            "Gleicher Titelname", "Shared title name", "Attack") { Grade = "Common", Origin = "Growth" }).ToArray(),
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private static CombatTitleUnlockCatalog Unlocks(int build = 42) => CombatTitleUnlockCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = build, referenceOnly = true,
        doesNotEstablishAvailabilityOwnershipCompletionOrClaim = true,
        titles = new[]
        {
            new CombatTitleUnlockDefinition(1001, [7001], [Route("AchievementRewardItem", 22, 222, 7001)]),
            new CombatTitleUnlockDefinition(1002, [7002], [Route("AchievementRewardItem", 33, 222, 7002)]),
            new CombatTitleUnlockDefinition(1003, [7003], [Route("AchievementRewardItem", 22, 221, 7003)]),
            new CombatTitleUnlockDefinition(1004, [7004], [Route("QuestRewardItem", 22, null, 7004)]),
        },
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private static CombatTitleUnlockRoute Route(string kind, int source, int? stage, int item) =>
        new(kind, source, stage, item, "Gleiche Quelle", "Shared source", "Gleiche Bedingung", "Shared condition");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedView? _view;
        private CapturedRewards? _rewards;
        public List<int> OpenedTitles { get; } = [];
        public Fixture(string language)
        {
            UiText.Language = language;
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> RenderView(CombatAchievementNavigationRequest? navigation = null)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CapturedView.Snapshot)] = CharacterCombatSnapshot.Empty with { ProfileId = "synthetic-navigation-profile" },
                    [nameof(CapturedView.Catalog)] = Achievements(), [nameof(CapturedView.TitleCatalog)] = Titles(),
                    [nameof(CapturedView.TitleUnlockCatalog)] = Unlocks(), [nameof(CapturedView.Navigation)] = navigation,
                    [nameof(CapturedView.ShowTitle)] = EventCallback.Factory.Create<int>(OpenedTitles, (int id) => OpenedTitles.Add(id)),
                    [nameof(CapturedView.Captured)] = (Action<CapturedView>)(view => _view = view),
                })));
            return await Html();
        }
        public async Task<string> RenderRewards(int family, int stage, int build, int titleBuild = 42, int unlockBuild = 42)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedRewards>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CapturedRewards.FamilyId)] = family, [nameof(CapturedRewards.StageId)] = stage,
                    [nameof(CapturedRewards.MetadataBuild)] = build, [nameof(CapturedRewards.Catalog)] = Titles(titleBuild),
                    [nameof(CapturedRewards.UnlockCatalog)] = Unlocks(unlockBuild),
                    [nameof(CapturedRewards.ShowTitle)] = EventCallback.Factory.Create<int>(OpenedTitles, (int id) => OpenedTitles.Add(id)),
                    [nameof(CapturedRewards.Captured)] = (Action<CapturedRewards>)(view => _rewards = view),
                })));
            return await Html();
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Update(CombatAchievementNavigationRequest? navigation) => _renderer.Dispatcher.InvokeAsync(() =>
        { _view!.Update(navigation); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task<string> ChangeStage(int family, int stage) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.ChangeStage(family, stage); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task<string> ClickText(string text) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.ClickText(text); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task ClickReward(int id) => _renderer.Dispatcher.InvokeAsync(() => _rewards!.Click(id));
        public async ValueTask DisposeAsync()
        { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _language; }
    }

    public sealed class CapturedView : CombatAchievementsView
    {
        [Parameter] public Action<CapturedView>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(CombatAchievementNavigationRequest? navigation)
        { Navigation = navigation; Snapshot = Snapshot with { Sequence = Snapshot.Sequence + 1 }; OnParametersSet(); StateHasChanged(); }
        internal async Task Click(string attribute, string value)
        { await InvokeElement(this, typeof(CombatAchievementsView), attribute, value, "onclick"); StateHasChanged(); }
        internal async Task ClickText(string text)
        { await InvokeElement(this, typeof(CombatAchievementsView), null, text, "onclick"); StateHasChanged(); }
        internal async Task ChangeStage(int family, int stage)
        {
            await InvokeElement(this, typeof(CombatAchievementsView), "data-achievement-stage-select",
                family.ToString(System.Globalization.CultureInfo.InvariantCulture), "onchange",
                new ChangeEventArgs { Value = stage.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            StateHasChanged();
        }
    }
    public sealed class CapturedRewards : CombatAchievementTitleRewards
    {
        [Parameter] public Action<CapturedRewards>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal Task Click(int title) => InvokeElement(this, typeof(CombatAchievementTitleRewards), "data-achievement-title-link", title, "onclick");
    }

#pragma warning disable BL0006 // Exercise actual production pointer/keyboard callbacks without a browser.
    private static async Task InvokeElement(object component, Type productionType, string? attribute, object value,
        string eventName, ChangeEventArgs? change = null)
    {
        using var tree = new RenderTreeBuilder();
        productionType.GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [tree]);
        var frames = tree.GetFrames();
        var elements = Enumerable.Range(0, frames.Count).Where(index => frames.Array[index].FrameType == RenderTreeFrameType.Element);
        var element = elements.Single(index => attribute is null
            ? frames.Array[index].ElementName == "button" && Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1)
                .Select(child => frames.Array[child]).Any(frame => frame.FrameType == RenderTreeFrameType.Text && Equals(frame.TextContent, value))
            : Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(child => frames.Array[child])
                .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).Any(frame => frame.AttributeName == attribute
                    && string.Equals(Convert.ToString(frame.AttributeValue, System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        var callback = Enumerable.Range(element + 1, frames.Array[element].ElementSubtreeLength - 1).Select(index => frames.Array[index])
            .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == eventName).AttributeValue;
        if (callback is EventCallback<ChangeEventArgs> changed) await changed.InvokeAsync(change!);
        else if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> clicked) await clicked.InvokeAsync(new());
        else if (callback is EventCallback untyped) await untyped.InvokeAsync(change);
        else if (callback is Action action) action();
        else if (callback is Func<Task> asynchronous) await asynchronous();
        else throw new InvalidOperationException("Unexpected production navigation callback.");
    }
#pragma warning restore BL0006
}
