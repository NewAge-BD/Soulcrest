using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatTitleLinkViewTests
{
    private const int Build = 26425393;
    private static readonly AchievementProfileBinding Binding = new("11111111111111111111111111111111", "22222222222222222222222222222222");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelectingTitleReplacesSlotsWhileFavoriteClickKeepsSelectionAndUsesIndependentTitleIds()
    {
        await using var fixture = new Fixture(); CapturedTitles? view = null; var toggled = 0;
        await fixture.Render<CapturedTitles>(TitleParameters(capture => view = capture, title => toggled = title));
        Assert.Contains("data-title-slot=", await fixture.Html());
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-select-id", "42");
        var html = await fixture.Html();
        Assert.Contains("data-title-detail-id=\"42\"", html); Assert.DoesNotContain("data-title-slot=", html);
        Assert.Contains("data-owned-title-count=\"2\"", html);
        Assert.Contains("data-title-required-achievement=\"7\"", html);
        Assert.Contains("data-title-achievement-source=\"Live\"", html);
        Assert.Contains("data-title-achievement-current=\"13\"", html);
        Assert.True(HasStopPropagation(view!, typeof(CombatTitlesView), "data-title-favorite-id", "43"));
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-favorite-id", "43");
        Assert.Equal(43, toggled); Assert.Contains("data-title-detail-id=\"42\"", await fixture.Html());
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-close-detail", "close");
        Assert.Contains("data-title-slot=", await fixture.Html());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("revision")]
    [InlineData("metadata")]
    public async Task ForeignOrOldFavoriteBindingsCannotWrite(string mismatch)
    {
        await using var fixture = new Fixture(); CapturedTitles? view = null; var calls = 0;
        var parameters = TitleParameters(capture => view = capture, _ => calls++);
        if (mismatch == "revision") parameters[nameof(CombatTitlesView.Favorites)] = new AchievementFavoriteSnapshot(Binding with { Revision = "33333333333333333333333333333333" }, []) { TitleIds = [42] };
        else parameters[nameof(CombatTitlesView.AchievementState)] = State() with { ProfileId = mismatch == "profile" ? "33333333333333333333333333333333" : Binding.ProfileId, MetadataBuild = mismatch == "metadata" ? Build + 1 : Build };
        await fixture.Render<CapturedTitles>(parameters);
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-favorite-id", "42");
        Assert.Equal(0, calls);
        Assert.Contains("data-title-favorite-id=\"42\" aria-pressed=\"false\" disabled", await fixture.Html());
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("unknown")]
    public async Task ExactBindingCanPinWhileLiveProgressIsUnavailable(string unavailable)
    {
        await using var fixture = new Fixture(); CapturedTitles? view = null; var calls = 0;
        var parameters = TitleParameters(capture => view = capture, _ => calls++);
        parameters[nameof(CombatTitlesView.SelectedTitleId)] = 42;
        parameters[nameof(CombatTitlesView.AchievementState)] = State() with
        {
            Generation = unavailable == "generation" ? 6 : 7,
            Source = unavailable == "unknown" ? AchievementViewSource.Unknown : AchievementViewSource.Live
        };
        await fixture.Render<CapturedTitles>(parameters);
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-favorite-id", "42");
        Assert.Equal(1, calls);
        var html = await fixture.Html();
        Assert.Contains("data-title-achievement-source=\"Unknown\"", html);
        Assert.DoesNotContain("data-title-achievement-current=", html);
        Assert.DoesNotContain("data-title-achievement-reported=", html);
    }

    [Fact]
    public async Task FavoriteStoreFailureIsGenericAndDoesNotSurviveProfileSwitch()
    {
        await using var fixture = new Fixture(); CapturedTitles? view = null;
        var parameters = TitleParameters(capture => view = capture, _ => { });
        parameters[nameof(CombatTitlesView.ToggleTitleFavorite)] = EventCallback.Factory.Create<int>(this,
            (Func<int, Task>)(_ => Task.FromException(new IOException("PRIVATE-STORE-PATH"))));
        await fixture.Render<CapturedTitles>(parameters);
        await fixture.Click(view!, typeof(CombatTitlesView), "data-title-favorite-id", "42");
        var html = await fixture.Html(); Assert.Contains("data-title-favorite-error", html); Assert.DoesNotContain("PRIVATE-STORE-PATH", html);
        await fixture.Update(view!, Snapshot() with { ProfileId = "33333333333333333333333333333333" });
        Assert.DoesNotContain("data-title-favorite-error", await fixture.Html());
    }

    [Fact]
    public async Task ExternalSelectionShowsMetadataWithoutInventingOwnershipAndProfileChangeClearsIt()
    {
        await using var fixture = new Fixture(); CapturedTitles? view = null;
        var parameters = TitleParameters(capture => view = capture, _ => { });
        parameters[nameof(CombatTitlesView.Snapshot)] = Snapshot() with { TitleOwnership = new(ImmutableHashSet<int>.Empty, NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list") };
        parameters[nameof(CombatTitlesView.SelectedTitleId)] = 42;
        await fixture.Render<CapturedTitles>(parameters);
        var html = await fixture.Html(); Assert.Contains("data-title-detail-id=\"42\"", html); Assert.DoesNotContain("data-owned-title=", html);
        await fixture.Update(view!, Snapshot() with { ProfileId = "33333333333333333333333333333333" });
        Assert.DoesNotContain("data-title-detail-id=", await fixture.Html());
    }

    [Fact]
    public async Task UnlockCardShowsExactRequiredStageAndNavigationRetainsItsStageId()
    {
        await using var fixture = new Fixture(); CapturedUnlocks? view = null; CombatAchievementNavigationRequest? navigation = null;
        var parameters = UnlockParameters(State());
        parameters[nameof(CapturedUnlocks.Captured)] = (Action<CapturedUnlocks>)(capture => view = capture);
        parameters[nameof(CombatTitleUnlockCards.ShowAchievement)] = EventCallback.Factory.Create<CombatAchievementNavigationRequest>(this, request => navigation = request);
        await fixture.Render<CapturedUnlocks>(parameters);
        var html = await fixture.Html();
        Assert.Contains("data-title-required-stage=\"72\"", html); Assert.Contains("Example II", html); Assert.DoesNotContain("Example III", html);
        Assert.Contains("data-title-achievement-current=\"13\"", html); Assert.Contains("data-title-achievement-target=\"20\"", html);
        Assert.Contains("data-title-achievement-completed=\"false\"", html);
        await fixture.Click(view!, typeof(CombatTitleUnlockCards), "data-title-show-achievement", "7");
        Assert.Equal(new CombatAchievementNavigationRequest(7, 72), navigation);
    }

    [Fact]
    public async Task SavedProgressIsLabeledAndForeignProfilesOrVersionsCannotExposeIt()
    {
        await using var saved = new Fixture(); await saved.Render<CombatTitleUnlockCards>(UnlockParameters(State() with { Source = AchievementViewSource.Saved }));
        Assert.Contains("data-title-achievement-saved", await saved.Html());
        Assert.Contains("data-title-achievement-source=\"Saved\"", await saved.Html());
        await using var foreign = new Fixture(); await foreign.Render<CombatTitleUnlockCards>(UnlockParameters(State() with { ProfileId = "foreign" }));
        Assert.DoesNotContain("data-title-achievement-current=", await foreign.Html());
        Assert.Contains("data-title-achievement-source=\"Unknown\"", await foreign.Html());
        await using var differentBuild = new Fixture(); await differentBuild.Render<CombatTitleUnlockCards>(UnlockParameters(State() with { MetadataBuild = Build + 1 }));
        Assert.DoesNotContain("data-title-achievement-current=", await differentBuild.Html());
    }

    [Fact]
    public async Task UnknownUnitKeepsRawValueAndDoesNotInventFractionOrGoalFromMetadataText()
    {
        var state = State(CombatAchievementProgressUnit.Unknown);
        await using var fixture = new Fixture(); await fixture.Render<CombatTitleUnlockCards>(UnlockParameters(state));
        var html = await fixture.Html();
        Assert.Contains("data-title-achievement-reported=\"13\"", html);
        Assert.DoesNotContain("data-title-achievement-target=", html); Assert.DoesNotContain("role=\"progressbar\"", html);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionTitleLinks(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_TITLE_LINK_RENDER_DIRECTORY"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var catalog = CombatTitleDisplayCatalog.Default;
        var unlocks = CombatTitleUnlockCatalog.Default;
        var achievements = CombatAchievementDisplayCatalog.Default;
        var thresholds = AchievementProgressThresholdCatalog.Default;
        var example = unlocks.Titles.Values.Where(title => title.Id is not (12010061 or 22010061)
                && catalog.Find(title.Id) is { Grade: "Unique", OwnedEffects.Count: > 0 })
            .OrderBy(title => title.Id).SelectMany(title => title.Routes
                .Where(route => route.Kind == "AchievementRewardItem" && thresholds.FindFamily(route.SourceId)?.Stages.Any(stage => stage.Id == route.StageId) == true)
                .Select(route => (Title: title, Route: route))).First();
        var chain = thresholds.FindFamily(example.Route.SourceId)!;
        var target = chain.Stages.Single(stage => stage.Id == example.Route.StageId).Target;
        var counter = Math.Max(0, target * 3 / 5);
        var facts = new CombatAchievementFamilyProgress(chain.Id, example.Route.StageId,
            chain.Stages.ToImmutableDictionary(stage => stage.Id, stage => new CombatAchievementStageProgress(stage.Id, counter,
                stage.Target, counter >= stage.Target, false) { Unit = CombatAchievementProgressUnit.Count }));
        var state = new AchievementViewState
        {
            ProfileId = Binding.ProfileId, Binding = Binding, MetadataBuild = catalog.MetadataBuild,
            Source = AchievementViewSource.Live, Generation = 7, Sequence = 1, ObservedAt = At,
            ObservedFamilyIds = [chain.Id], ObservedValues = ImmutableDictionary<int, int>.Empty.Add(chain.Id, checked((int)counter)),
            Progress = new(7, 1, ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(chain.Id, facts))
        };
        var owned = catalog.Titles.Values.Where(title => title.Id is not (12010061 or 22010061)
                && title.Grade is "Unique" or "Legend" or "Rare" && title.OwnedEffects is { Count: > 0 })
            .OrderBy(title => GradeRank(title.Grade)).ThenBy(title => title.Id).Take(17).Select(title => title.Id)
            .Append(example.Title.Id).ToImmutableHashSet();
        var snapshot = Snapshot() with
        {
            TitleOwnership = new(owned, NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list"),
            Titles = new(new(owned, ImmutableDictionary<byte, int>.Empty), NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list")
        };
        await using var fixture = new Fixture(); UiText.Language = language;
        await fixture.Render<CombatTitlesView>(new()
        {
            [nameof(CombatTitlesView.Snapshot)] = snapshot, [nameof(CombatTitlesView.SelectedTitleId)] = example.Title.Id,
            [nameof(CombatTitlesView.AchievementState)] = state,
            [nameof(CombatTitlesView.Favorites)] = new AchievementFavoriteSnapshot(Binding, []) { TitleIds = [example.Title.Id] },
            [nameof(CombatTitlesView.ToggleTitleFavorite)] = EventCallback.Factory.Create<int>(this, _ => { }),
            [nameof(CombatTitlesView.ShowAchievement)] = EventCallback.Factory.Create<CombatAchievementNavigationRequest>(this, _ => { })
        });
        var html = await fixture.Html();
        Assert.Contains("data-title-detail-id=\"" + example.Title.Id + "\"", html);
        Assert.Contains("data-title-required-stage=\"" + example.Route.StageId + "\"", html);
        Assert.Contains("data-title-achievement-source=\"Live\"", html);
        Assert.Contains("data-title-achievement-current=\"" + counter + "\"", html);
        Assert.DoesNotContain(Binding.ProfileId, html); Assert.DoesNotContain(Binding.Revision, html);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }.Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "titles-link-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri) + "\"><style>" + css
            + "\nhtml,body{height:auto}body{padding:24px}</style></head><body>" + html + "</body></html>");
    }

    private static int GradeRank(string? grade) => CombatTitlesPresentation.GradeSortRank(grade);

    private Dictionary<string, object?> TitleParameters(Action<CapturedTitles> capture, Action<int> toggle) => new()
    {
        [nameof(CapturedTitles.Captured)] = capture, [nameof(CombatTitlesView.Snapshot)] = Snapshot(),
        [nameof(CombatTitlesView.Catalog)] = Titles(), [nameof(CombatTitlesView.UnlockCatalog)] = Unlocks(),
        [nameof(CombatTitlesView.AchievementCatalog)] = Achievements(), [nameof(CombatTitlesView.AchievementState)] = State(),
        [nameof(CombatTitlesView.Favorites)] = new AchievementFavoriteSnapshot(Binding, [43]) { TitleIds = [42] },
        [nameof(CombatTitlesView.ToggleTitleFavorite)] = EventCallback.Factory.Create(this, toggle)
    };
    private static Dictionary<string, object?> UnlockParameters(AchievementViewState state) => new()
    {
        [nameof(CombatTitleUnlockCards.TitleId)] = 42, [nameof(CombatTitleUnlockCards.MetadataBuild)] = Build,
        [nameof(CombatTitleUnlockCards.Catalog)] = Unlocks(), [nameof(CombatTitleUnlockCards.AchievementCatalog)] = Achievements(),
        [nameof(CombatTitleUnlockCards.ProfileId)] = Binding.ProfileId, [nameof(CombatTitleUnlockCards.AchievementState)] = state
    };
    private static CharacterCombatSnapshot Snapshot() => new()
    {
        ProfileId = Binding.ProfileId, IsFresh = true, Generation = 7,
        TitleOwnership = new(ImmutableHashSet.Create(42, 43), NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list"),
        Titles = new(new(ImmutableHashSet.Create(42, 43), ImmutableDictionary<byte, int>.Empty.Add(1, 43)), NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list")
    };
    private static AchievementViewState State(CombatAchievementProgressUnit unit = CombatAchievementProgressUnit.Count) => new()
    {
        ProfileId = Binding.ProfileId, Binding = Binding, MetadataBuild = Build, Source = AchievementViewSource.Live,
        Generation = 7, Sequence = 1, ObservedAt = At, ObservedFamilyIds = [7], ObservedValues = ImmutableDictionary<int, int>.Empty.Add(7, 13),
        Progress = new(7, 1, ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(7,
            new(7, 73, ImmutableDictionary<int, CombatAchievementStageProgress>.Empty.Add(72, new(72, 13, 20, false, false) { Unit = unit }))))
    };
    private static CombatTitleDisplayCatalog Titles() => CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 2, metadataBuild = Build, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
        titles = new[] { new CombatTitleDisplayDefinition(42, "Beispieltitel", "Example title", "Attack") { Grade = "Unique", EquippedEffects = [], OwnedEffects = [] },
            new CombatTitleDisplayDefinition(43, "Weiterer Titel", "Another title", "Defense") { Grade = "Common", EquippedEffects = [], OwnedEffects = [] } }
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private static CombatTitleUnlockCatalog Unlocks() => CombatTitleUnlockCatalog.FromJson("""
        {"schema":1,"metadataBuild":26425393,"referenceOnly":true,"doesNotEstablishAvailabilityOwnershipCompletionOrClaim":true,
        "titles":[{"id":42,"directGrantItemIds":[123],"routes":[{"kind":"AchievementRewardItem","sourceId":7,"stageId":72,"itemId":123,"nameDe":"Beispiel II","nameEn":"Example II","conditionDe":"Schließe 20 Aufgaben ab","conditionEn":"Complete 20 tasks"}]}]}
        """);
    private static CombatAchievementDisplayCatalog Achievements() => CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = Build, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
        categories = Array.Empty<object>(), groups = Array.Empty<object>(),
        achievements = new[] { new CombatAchievementDisplayDefinition(7, null, null, "Beispiel I", "Example I", "Schließe 10 Aufgaben ab", "Complete 10 tasks", null, null, [])
        { Stages = new[] { new CombatAchievementDisplayStage(71, "Beispiel I", "Example I", "Schließe 10 Aufgaben ab", "Complete 10 tasks", null, null, []),
            new CombatAchievementDisplayStage(72, "Beispiel II", "Example II", "Schließe 20 Aufgaben ab", "Complete 20 tasks", null, null, []),
            new CombatAchievementDisplayStage(73, "Beispiel III", "Example III", "Schließe 30 Aufgaben ab", "Complete 30 tasks", null, null, []) } } }
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    public sealed class CapturedTitles : CombatTitlesView
    {
        [Parameter] public Action<CapturedTitles>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        public void Update(CharacterCombatSnapshot snapshot) { Snapshot = snapshot; OnParametersSet(); StateHasChanged(); }
        public void Refresh() => StateHasChanged();
    }
    public sealed class CapturedUnlocks : CombatTitleUnlockCards
    {
        [Parameter] public Action<CapturedUnlocks>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        public void Refresh() => StateHasChanged();
    }
#pragma warning disable BL0006 // Inspect only attributes of the selected production element.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private readonly string _language = UiText.Language;
        public Fixture() { UiText.Language = "en"; var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider(); _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>()); }
        public async Task Render<T>(Dictionary<string, object?> parameters) where T : IComponent => _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task Update(CapturedTitles view, CharacterCombatSnapshot snapshot) => _renderer.Dispatcher.InvokeAsync(() => view.Update(snapshot));
        public Task Click(ComponentBase view, Type componentType, string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var callback = ElementAttributes(view, componentType, attribute, value).Single(frame => frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action action) action();
            else if (callback is Func<Task> asyncAction) await asyncAction();
            else throw new InvalidOperationException("Unexpected title callback.");
            if (view is CapturedTitles titles) titles.Refresh(); else if (view is CapturedUnlocks unlocks) unlocks.Refresh();
        });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _language; }
    }
#pragma warning disable BL0006 // Exercise the exact production callbacks and bubbling contract without game input.
    private static bool HasStopPropagation(ComponentBase view, Type type, string attribute, string value) =>
        ElementAttributes(view, type, attribute, value).Any(frame => frame.AttributeName == "__internal_stopPropagation_onclick" && Equals(frame.AttributeValue, true));
    private static RenderTreeFrame[] ElementAttributes(ComponentBase view, Type type, string attribute, string value)
    {
        using var tree = new RenderTreeBuilder(); type.GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [tree]);
        var frames = tree.GetFrames(); var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == attribute && Equals(frames.Array[i].AttributeValue, value));
        while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
        return frames.Array.Skip(index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray();
    }
#pragma warning restore BL0006
}
