using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionBoardViewTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FreshOwnBoardsRenderSixAutomaticTabsWithoutSolverOrManualClassInputs(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        Assert.Equal(6, Regex.Matches(html, "data-board-tab=").Count);
        foreach (var board in fixture.Boards) Assert.Contains(board.Name.For(language), html);
        Assert.Contains("role=\"group\"", html);
        Assert.Contains("data-fresh=\"true\"", html);
        Assert.Contains("data-board-actual-count=\"1\"", html); // Free start is not a purchased node.
        Assert.Contains("data-board-desired-count=\"1\"", html);
        Assert.Contains("<image href=", html);
        Assert.DoesNotContain("<select", html);
        foreach (var marker in new[] { "data-priority", "data-budget", "data-calculate-route", "data-combat-score-model", "data-calculator-class" })
            Assert.DoesNotContain(marker, html);
        Assert.DoesNotContain(UiText.T("CP hängt von Ausgangswerten und der gesamten Route ab."), html);
        Assert.False(fixture.Edits.Snapshot.IsActive);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BoardSvgReceivesTheUiLanguageValueInsteadOfTheLiteralParameterName(string language)
    {
        // Regression: Language="Language" passed the text "Language" to the string parameter, so German showed English.
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        RazorStringParameterBindingTests.AssertDaevanionSvgLanguage(html, fixture.Board, language);
    }

    [Fact]
    public async Task ClickingUnownedNodeAddsItLocallyAndSecondClickRollsBackWithoutChangingActualFacts()
    {
        await using var fixture = new Fixture("de"); await fixture.Render();
        var node = fixture.Unowned;
        await fixture.Toggle(node.Id);
        var html = await fixture.Html();
        AssertNodeFacts(html, node.Id, actual: false, desired: true, added: true, removed: false);
        Assert.Contains("aria-pressed=\"true\"", NodeButton(html, node.Id));
        Assert.Contains("Lokal hinzugefügt", NodeButton(html, node.Id));
        Assert.Contains("data-board-actual-count=\"1\"", html);
        Assert.Contains("data-board-desired-count=\"2\"", html);
        Assert.DoesNotContain(node.Id, fixture.Source.CombatSnapshot.DaevanionBoards.Value!.Boards[fixture.Board.Id]);
        await fixture.Toggle(node.Id);
        html = await fixture.Html();
        AssertNodeFacts(html, node.Id, actual: false, desired: false, added: false, removed: false);
        Assert.Contains("aria-pressed=\"false\"", NodeButton(html, node.Id));
        Assert.True(fixture.Edits.Snapshot.IsActive);
    }

    [Fact]
    public async Task RemovingAnActualNodeRetainsItsActualFactAndNativeFrameUntilUndo()
    {
        await using var fixture = new Fixture("de"); await fixture.Render();
        var node = fixture.Actual;
        await fixture.Toggle(node.Id);
        var html = await fixture.Html();
        AssertNodeFacts(html, node.Id, actual: true, desired: false, added: false, removed: true);
        Assert.Contains("aria-pressed=\"false\"", NodeButton(html, node.Id));
        Assert.Contains("Lokal entfernt · aktuell erkannt", NodeButton(html, node.Id));
        Assert.Contains(DaevanionCatalog.Default.GetAsset(node.FrameAssetId)!.Path, html);
        Assert.Contains("data-board-desired-count=\"0\"", html);
        await fixture.Toggle(node.Id);
        AssertNodeFacts(await fixture.Html(), node.Id, actual: true, desired: true, added: false, removed: false);
    }

    [Fact]
    public async Task ResetKeepsAnEmptyDraftAndEndExplicitlyReleasesIt()
    {
        await using var fixture = new Fixture("en"); await fixture.Render();
        await fixture.Toggle(fixture.Unowned.Id);
        await fixture.Click("data-board-reset");
        var html = await fixture.Html();
        Assert.True(fixture.Edits.Snapshot.IsActive);
        AssertNodeFacts(html, fixture.Unowned.Id, actual: false, desired: false, added: false, removed: false);
        Assert.Contains("data-board-added-count=\"0\"", html);
        await fixture.Click("data-board-end");
        Assert.False(fixture.Edits.Snapshot.IsActive);
        Assert.Contains("data-edit-active=\"false\"", await fixture.Html());
    }

    [Fact]
    public async Task NewOwnObservationUpdatesActualNodesAndReconcilesVisibleDeltas()
    {
        await using var fixture = new Fixture("en"); await fixture.Render();
        await fixture.Toggle(fixture.Unowned.Id);
        await fixture.PublishOnRenderer(() => fixture.Publish(extra: fixture.Unowned.Id));
        var html = await fixture.Html();
        AssertNodeFacts(html, fixture.Unowned.Id, actual: true, desired: true, added: false, removed: false);
        Assert.Contains("data-board-actual-count=\"2\"", html);
        Assert.Contains("data-board-added-count=\"0\"", html);
    }

    [Fact]
    public async Task MissingPartialBoardKeysRemainUnknownAndCannotBeSelectedAsEmptyBoards()
    {
        await using var fixture = new Fixture("de"); fixture.Publish(onlyFirst: true);
        var html = await fixture.Render();
        Assert.Equal(6, Regex.Matches(html, "data-board-tab=").Count);
        Assert.Equal(5, Regex.Matches(html, "<button[^>]*data-board-tab=\"[^\"]+\"[^>]*disabled").Count);
        var other = fixture.Boards[1];
        await fixture.Click("data-board-tab", other.Id.ToString());
        Assert.Contains($"data-current-board=\"{fixture.Board.Id}\"", await fixture.Html());
        Assert.False(fixture.Edits.Snapshot.Boards.Single(board => board.Board.Id == other.Id).IsKnown);
    }

    [Fact]
    public async Task LockedAndStaleBoardsDisableEditingAndDoNotInventZeroCounts()
    {
        await using var fixture = new Fixture("de"); fixture.Publish(level: 1);
        var html = await fixture.Render();
        Assert.Equal(6, Regex.Matches(html, "<button[^>]*data-board-tab=\"[^\"]+\"[^>]*disabled").Count);
        Assert.Contains("data-board-count-unknown", html);
        Assert.DoesNotContain("data-board-actual-count=", html);
        Assert.Contains("disabled", NodeButton(html, fixture.Actual.Id));
        var old = await fixture.NodeCallback();
        await fixture.PublishOnRenderer(() => fixture.Publish(fresh: false));
        html = await fixture.Html();
        Assert.Contains("data-board-source-missing", html);
        Assert.Contains("data-board-unknown", html);
        Assert.DoesNotContain("data-node-button=", html);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => old.InvokeAsync(fixture.Unowned.Id));
        Assert.False(fixture.Edits.Snapshot.IsActive);
    }

    [Fact]
    public async Task QueuedNodeClickCannotEditAfterChangingBoardOrOwnGeneration()
    {
        await using var fixture = new Fixture("en"); await fixture.Render();
        var old = await fixture.NodeCallback();
        await fixture.Click("data-board-tab", fixture.Boards[1].Id.ToString());
        await fixture.Renderer.Dispatcher.InvokeAsync(() => old.InvokeAsync(fixture.Unowned.Id));
        Assert.False(fixture.Edits.Snapshot.IsActive);
        await fixture.PublishOnRenderer(() => fixture.Publish(generation: 8));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => old.InvokeAsync(fixture.Unowned.Id));
        Assert.False(fixture.Edits.Snapshot.IsActive);
        Assert.All(fixture.Edits.Snapshot.Boards, board => Assert.Empty(board.AddedNodeIds));
    }

    [Theory]
    [InlineData("data-board-reset", false)]
    [InlineData("data-board-end", false)]
    [InlineData("data-board-reset", true)]
    [InlineData("data-board-end", true)]
    public async Task QueuedActionCannotResetOrEndTheActiveDraftAfterGenerationOrProfileChanges(string attribute, bool changeProfile)
    {
        await using var fixture = new Fixture("en"); await fixture.Render();
        await fixture.Toggle(fixture.Unowned.Id);
        var old = await fixture.CaptureAction(attribute);
        await fixture.PublishOnRenderer(() => fixture.Publish(generation: changeProfile ? 7 : 8,
            profileId: changeProfile ? "other-synthetic-profile" : null));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Edits.ResetToCurrent());
        await fixture.Toggle(fixture.Unowned.Id);
        Assert.True(fixture.Edits.Snapshot.IsActive);
        Assert.Contains(fixture.Unowned.Id, fixture.Edits.Snapshot.Boards.Single(board => board.Board.Id == fixture.Board.Id).AddedNodeIds);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Invoke(old));
        var current = fixture.Edits.Snapshot;
        Assert.True(current.IsActive);
        Assert.Contains(fixture.Unowned.Id, current.Boards.Single(board => board.Board.Id == fixture.Board.Id).AddedNodeIds);
        AssertNodeFacts(await fixture.Html(), fixture.Unowned.Id, actual: false, desired: true, added: true, removed: false);
    }

    [Fact]
    public async Task NativeNodeButtonsExposeDesiredStateAndRejectStartOrDisabledCallbacks()
    {
        await using var fixture = new Fixture("de");
        CapturedBoardSvg? view = null;
        var selected = new List<int>();
        var start = fixture.Board.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start);
        var parameters = new Dictionary<string, object?>
        {
            [nameof(DaevanionBoardSvg.Board)] = fixture.Board, [nameof(DaevanionBoardSvg.EditMode)] = true,
            [nameof(DaevanionBoardSvg.CanEdit)] = true, [nameof(DaevanionBoardSvg.ActualNodeIds)] = new HashSet<int> { start.Id, fixture.Actual.Id },
            [nameof(DaevanionBoardSvg.DesiredNodeIds)] = new HashSet<int> { start.Id },
            [nameof(DaevanionBoardSvg.RemovedNodeIds)] = new HashSet<int> { fixture.Actual.Id },
            [nameof(DaevanionBoardSvg.NodeSelected)] = EventCallback.Factory.Create<int>(selected, (int id) => selected.Add(id)),
            [nameof(CapturedBoardSvg.Captured)] = (Action<CapturedBoardSvg>)(value => view = value)
        };
        var root = await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.RenderComponentAsync<CapturedBoardSvg>(ParameterView.FromDictionary(parameters)));
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("aria-pressed=\"false\"", NodeButton(html, fixture.Actual.Id));
        Assert.Contains("aria-disabled=\"false\"", NodeButton(html, fixture.Actual.Id));
        Assert.Contains("aria-disabled=\"true\"", NodeButton(html, start.Id));
        Assert.Contains("tabindex=\"-1\"", NodeButton(html, start.Id));
        Assert.Contains("disabled", NodeButton(html, start.Id));
        Assert.Contains("<foreignObject", html);
        // Native button activation handles Enter/Space without blocking Tab or scrolling on Space.
        Assert.DoesNotContain("preventDefault", html);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(ElementCallback(view!, typeof(DaevanionBoardSvg), "data-node-button", start.Id.ToString()));
            await Invoke(ElementCallback(view!, typeof(DaevanionBoardSvg), "data-node-button", fixture.Actual.Id.ToString()));
        });
        Assert.Equal(new[] { fixture.Actual.Id }, selected);
        parameters[nameof(DaevanionBoardSvg.CanEdit)] = false;
        var disabled = await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.RenderComponentAsync<CapturedBoardSvg>(ParameterView.FromDictionary(parameters)));
        Assert.Contains("aria-disabled=\"true\"", NodeButton(await fixture.Renderer.Dispatcher.InvokeAsync(disabled.ToHtmlString), fixture.Actual.Id));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Invoke(ElementCallback(view!, typeof(DaevanionBoardSvg), "data-node-button", fixture.Actual.Id.ToString())));
        Assert.Single(selected);
    }

    [Fact]
    public async Task LegacySvgRetainsPlanPresentationAndSelectionCallback()
    {
        await using var fixture = new Fixture("de");
        CapturedBoardSvg? view = null; int? selected = null;
        var root = await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.RenderComponentAsync<CapturedBoardSvg>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(DaevanionBoardSvg.Board)] = fixture.Board,
            [nameof(DaevanionBoardSvg.Plan)] = new DaevanionBoardPlan { NodeIds = [fixture.Actual.Id] },
            [nameof(DaevanionBoardSvg.NodeSelected)] = EventCallback.Factory.Create<int>(fixture, (int id) => selected = id),
            [nameof(CapturedBoardSvg.Captured)] = (Action<CapturedBoardSvg>)(value => view = value)
        })));
        var html = WebUtility.HtmlDecode(await fixture.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("role=\"img\"", html);
        Assert.Contains(UiText.T("CP hängt von Ausgangswerten und der gesamten Route ab."), html);
        Assert.DoesNotContain("data-node-button", html);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Invoke(ElementCallback(view!, typeof(DaevanionBoardSvg), "data-node-id", fixture.Actual.Id.ToString())));
        Assert.Equal(fixture.Actual.Id, selected);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousBoardView(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_DAEVANION_BOARD_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        await using var fixture = new Fixture(language);
        var secondActual = fixture.Board.Nodes.First(node => node.Kind != DaevanionNodeKind.Start && node.Id != fixture.Actual.Id && node.Id != fixture.Unowned.Id);
        fixture.Publish(extra: secondActual.Id);
        await fixture.Render();
        await fixture.Toggle(fixture.Actual.Id);
        await fixture.Toggle(fixture.Unowned.Id);
        var html = await fixture.Markup();
        Assert.Equal(6, Regex.Matches(html, "data-board-tab=").Count);
        AssertNodeFacts(html, fixture.Actual.Id, actual: true, desired: false, added: false, removed: true);
        AssertNodeFacts(html, fixture.Unowned.Id, actual: false, desired: true, added: true, removed: false);
        Assert.DoesNotContain("synthetic-profile", html); Assert.DoesNotContain("SyntheticIdentity", html);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "daevanion-calculator.css", "daevanion-board.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "boards-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
            + "\nhtml,body{height:auto}</style></head><body>" + html + "</body></html>");
    }

    private static string NodeButton(string html, int id) => Regex.Match(html, $"<button[^>]*data-node-button=\"{id}\"[^>]*>").Value is { Length: > 0 } found ? found : throw new InvalidOperationException("Node button missing.");
    private static void AssertNodeFacts(string html, int id, bool actual, bool desired, bool added, bool removed)
    {
        var node = Regex.Match(html, $"<g[^>]*data-node-id=\"{id}\"[^>]*>").Value;
        Assert.NotEmpty(node);
        foreach (var (name, value) in new[] { ("actual", actual), ("desired", desired), ("added", added), ("removed", removed) })
            Assert.Contains($"data-{name}=\"{value.ToString().ToLowerInvariant()}\"", node);
    }

#pragma warning disable BL0006 // Invoke only callbacks emitted by the production components.
    private static RenderTreeBuilder Tree(ComponentBase view, Type type)
    {
        var tree = new RenderTreeBuilder();
        type.GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [tree]);
        return tree;
    }
    private static object ElementCallback(ComponentBase view, Type type, string attribute, string? value = null)
    {
        using var tree = Tree(view, type); var frames = tree.GetFrames();
        var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
            && frames.Array[i].AttributeName == attribute && (value is null || Equals(frames.Array[i].AttributeValue?.ToString(), value)));
        while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
        for (var i = index + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
            if (frames.Array[i].AttributeName == "onclick") return frames.Array[i].AttributeValue;
        throw new InvalidOperationException("Element callback missing.");
    }
    private static Task Invoke(object callback) => callback switch
    {
        EventCallback<MouseEventArgs> typed => typed.InvokeAsync(new()), EventCallback untyped => untyped.InvokeAsync(),
        Action action => Run(action), Func<Task> function => function(), _ => throw new InvalidOperationException("Unexpected callback.")
    };
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
#pragma warning restore BL0006

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly OwnSkillStateService _skills;
        private readonly CharacterCombatStateService _character;
        private HtmlRootComponent _root;
        private CapturedBoardView? _view;
        private string _profileId = "synthetic-profile";
        internal Source Source { get; } = new();
        internal DaevanionBoardEditService Edits { get; }
        internal HtmlRenderer Renderer { get; }
        internal DaevanionBoard[] Boards { get; } = DaevanionRuleCatalog.Default.ForClass("Cleric").Select(rule => rule.Board).ToArray();
        internal DaevanionBoard Board => Boards[0];
        internal DaevanionNode Actual => Board.Nodes.First(node => node.Kind != DaevanionNodeKind.Start);
        internal DaevanionNode Unowned => Board.Nodes.First(node => node.Kind != DaevanionNodeKind.Start && node.Id != Actual.Id);
        internal Fixture(string language)
        {
            UiText.Language = language;
            LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext _) => new(_profileId, "Unused");
            _skills = new(Source, Resolve); _character = new(Source, _skills, Resolve); Edits = new(_character, Source);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Edits);
            _provider = services.BuildServiceProvider(); Renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
            Publish();
        }
        internal void Publish(bool onlyFirst = false, int level = 45, bool fresh = true, long generation = 7, int? extra = null, string? profileId = null)
        {
            if (profileId is not null) _profileId = profileId;
            var at = DateTimeOffset.UnixEpoch.AddDays(1);
            var own = new OwnCharacterContext(new(101, 501, 300, "SyntheticIdentity") { ClassId = "Cleric" }, generation);
            var boards = (onlyFirst ? Boards.Take(1) : Boards).ToImmutableDictionary(board => board.Id,
                board => board.Id == Board.Id ? (extra is { } id ? ImmutableHashSet.Create(Actual.Id, id) : ImmutableHashSet.Create(Actual.Id)) : ImmutableHashSet<int>.Empty);
            Source.CombatSnapshot = new()
            {
                Character = own, ClassId = "Cleric", CharacterLevel = level, Generation = generation,
                Sequence = Source.CombatSnapshot.Sequence + 1, ObservedAt = at,
                DaevanionBoards = new(new(boards), fresh ? NetworkCombatSourceStatus.Partial : NetworkCombatSourceStatus.Stale,
                    fresh, at, Source.CombatSnapshot.Sequence + 1, "own-daevanion-board-lists")
            };
            Source.Raise();
        }
        internal Task<string> Render() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await Renderer.RenderComponentAsync<CapturedBoardView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CapturedBoardView.Captured)] = (Action<CapturedBoardView>)(view => _view = view) }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        internal Task<string> Html() => Renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        internal Task<string> Markup() => Renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        internal Task PublishOnRenderer(Action publish) => Renderer.Dispatcher.InvokeAsync(publish);
        internal Task Toggle(int id) => Renderer.Dispatcher.InvokeAsync(async () => await (await NodeCallback()).InvokeAsync(id));
        internal Task Click(string attribute, string? value = null) => Renderer.Dispatcher.InvokeAsync(() => Invoke(ElementCallback(_view!, typeof(DaevanionBoardView), attribute, value)));
        internal Task<object> CaptureAction(string attribute) => Renderer.Dispatcher.InvokeAsync(() => ElementCallback(_view!, typeof(DaevanionBoardView), attribute));
#pragma warning disable BL0006
        internal Task<EventCallback<int>> NodeCallback() => Renderer.Dispatcher.InvokeAsync(() =>
        {
            using var tree = Tree(_view!, typeof(DaevanionBoardView)); var frames = tree.GetFrames();
            return (EventCallback<int>)Enumerable.Range(0, frames.Count).Select(index => frames.Array[index])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == nameof(DaevanionBoardSvg.NodeSelected)).AttributeValue;
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync(); Edits.Dispose(); _character.Dispose(); _skills.Dispose();
            await _provider.DisposeAsync(); UiText.Language = _language;
        }
    }
    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running => true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot => NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }
    public sealed class CapturedBoardView : DaevanionBoardView
    {
        [Parameter] public Action<CapturedBoardView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CapturedBoardSvg : DaevanionBoardSvg
    {
        [Parameter] public Action<CapturedBoardSvg>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
}
