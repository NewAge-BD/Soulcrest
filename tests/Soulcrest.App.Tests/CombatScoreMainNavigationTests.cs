using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatScoreMainNavigationTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MainNavigationShowsOneCombinedCalculatorEntry(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        var start = html.IndexOf("<nav class=\"tabs\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var navigation = html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];

        Assert.Equal(1, navigation.Split(">Combat Score Calculator</button>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Daevanion Board Calculator", navigation);
        Assert.DoesNotContain("data-combat-score-workspace", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LegacyDaevanionLinkRendersItsBoardInsideTheWorkspaceAndRootClickRestoresOverview(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Ui.Navigate("daevanion-calculator");
        var html = await fixture.Render();
        Assert.Contains("data-combat-score-workspace", html);
        Assert.Contains("data-workspace-section=\"daevanion\"", html);
        Assert.Contains("data-daevanion-boards", html);
        Assert.DoesNotContain("data-daevanion-calculator", html);
        Assert.Contains("class=\"active\" aria-current=\"page\"", html);

        html = await fixture.ClickCalculator();

        Assert.Equal("combat-score", fixture.Ui.Tab);
        Assert.Equal("stat-overview", fixture.Ui.CombatScoreSection);
        Assert.Contains("data-workspace-section=\"stat-overview\"", html);
        Assert.Contains("data-stat-overview", html);
        Assert.DoesNotContain("data-daevanion-calculator", html);
        Assert.False(File.Exists(fixture.PlanPath));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-combat-navigation-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly SettingsService _settings;
        private readonly bool _previousTutorialDone;
        private HtmlRootComponent _root;
        private CapturedMain? _view;
        public UiState Ui { get; }
        public string PlanPath => Path.Combine(_directory, "plans.json");

        public Fixture(string language)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<ExplorationService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<INetworkAchievementSource>(sp => sp.GetRequiredService<NetworkLootService>());
            services.AddSingleton(_ => new AchievementStateStore(Path.Combine(AppPaths.DataDirectory, "achievement-render-tests")));
            services.AddSingleton<AchievementStateService>();
            services.AddSingleton(sp => new AchievementFavoriteService(Path.Combine(AppPaths.DataDirectory, "achievement-favorite-render-tests"), sp.GetRequiredService<ExplorationService>()));
            services.AddSingleton<INetworkSkillSource>(provider => provider.GetRequiredService<NetworkLootService>());
            services.AddSingleton<INetworkCharacterCombatSource>(provider => provider.GetRequiredService<NetworkLootService>());
            services.AddSingleton<OwnSkillStateService>();
            services.AddSingleton<CharacterCombatStateService>();
            services.AddSingleton<DaevanionBoardEditService>();
            services.AddSingleton(new DaevanionPlanService(PlanPath, new NoSolve()));
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton<BossRushService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_directory, "alerts.json"), runTimer: false));
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IJSRuntime, NoJs>();
            _services = services.BuildServiceProvider();
            _settings = _services.GetRequiredService<SettingsService>();
            _previousTutorialDone = _settings.Current.TutorialDone;
            _settings.Current.TutorialDone = true;
            UiText.Language = language;
            Ui = _services.GetRequiredService<UiState>();
            Ui.Navigate("navigation-test");
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task<string> Render() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedMain>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedMain.Captured)] = (Action<CapturedMain>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });

#pragma warning disable BL0006 // Invoke the actual callback emitted by the production main navigation.
        public Task<string> ClickCalculator() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(Main).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [tree]);
            var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var contents = Enumerable.Range(index + 1, frame.ElementSubtreeLength - 1).Select(i => frames.Array[i]).ToArray();
                if (!contents.Any(item => item.FrameType == RenderTreeFrameType.Text && item.TextContent == "Combat Score Calculator")) continue;
                var callback = Assert.Single(contents, item => item.FrameType == RenderTreeFrameType.Attribute && item.AttributeName == "onclick").AttributeValue;
                if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
                else if (callback is EventCallback untyped) await untyped.InvokeAsync();
                else if (callback is Action synchronous) { synchronous(); await _view!.RefreshNow(); }
                else if (callback is Func<Task> asynchronous) { await asynchronous(); await _view!.RefreshNow(); }
                else throw new InvalidOperationException("Unexpected production button callback.");
                return WebUtility.HtmlDecode(_root.ToHtmlString());
            }
            throw new InvalidOperationException("The combined calculator navigation button is missing.");
        });
#pragma warning restore BL0006

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            _settings.Current.TutorialDone = _previousTutorialDone;
            await _services.DisposeAsync();
            UiText.Language = _previousLanguage;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    public sealed class CapturedMain : Main
    {
        [Parameter] public Action<CapturedMain>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        public Task RefreshNow() => InvokeAsync(StateHasChanged);
    }

    private sealed class NoSolve : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Navigation must not start optimization.");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
