using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Actual module timer and renderer dispatcher, without capture, app startup or user data.</summary>
[Collection("Settings file")]
public sealed class LootTrackerSessionReplayViewIntegrationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealClockAdvancesOnlyReplayWhileStatisticsStayAtWholeSessionTotalsAndSeekPauses()
    {
        await using var fixture = new Fixture();
        var initial = await fixture.Render(Session("first", "sample-map", 20));
        var statistics = Statistics(initial);
        var statisticsRenders = fixture.Host.Statistics!.RenderCount;
        await fixture.Dispatch(() => { fixture.Host.Replay!.Seek(TimeSpan.Zero); fixture.Host.Replay.Play(1); });
        var moving = await fixture.WaitForElapsed(seconds => seconds > 0 && seconds < 20);
        Assert.Equal(statistics, Statistics(moving));
        Assert.Equal(statisticsRenders, fixture.Host.Statistics.RenderCount);
        Assert.Contains("data-testid=\"session-replay-map\"", moving);
        Assert.Contains("data-testid=\"replay-transport\"", moving);
        await fixture.Dispatch(() => fixture.Host.Replay!.Seek(TimeSpan.FromSeconds(5)));
        Assert.Equal(5d, Elapsed(await fixture.Markup()));
        var pausedRenders = fixture.Host.Replay!.RenderCount;
        await Task.Delay(250);
        Assert.Equal(5d, Elapsed(await fixture.Markup()));
        Assert.Equal(pausedRenders, fixture.Host.Replay.RenderCount);
        Assert.Equal(statisticsRenders, fixture.Host.Statistics.RenderCount);
    }

    [Fact]
    public async Task ReverseClockRunsAndChangingTheSessionCancelsTheOldClockAndResetsToItsEnd()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session("first", "sample-map", 20));
        await fixture.Dispatch(() => { fixture.Host.Replay!.Seek(TimeSpan.FromSeconds(10)); fixture.Host.Replay.Play(-1); });
        var reversing = await fixture.WaitForElapsed(seconds => seconds < 10 && seconds > 0);
        Assert.Matches("id=\"loot-replay-reverse\"[^>]*aria-pressed=\"true\"", reversing);
        await fixture.Dispatch(() => fixture.Host.ChangeSession(Session("second", "other-map", 7)));
        var changed = await fixture.Markup();
        Assert.Equal(7d, Elapsed(changed));
        Assert.Contains("data-map-id=\"other-map\"", changed);
        var stoppedRenders = fixture.Host.Replay!.RenderCount;
        await Task.Delay(250);
        Assert.Equal(7d, Elapsed(await fixture.Markup()));
        Assert.Equal(stoppedRenders, fixture.Host.Replay.RenderCount);
    }

    [Fact]
    public async Task DisposingWhilePlayingCancelsTheTimerWithoutFurtherRenders()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session("first", "sample-map", 20));
        await fixture.Dispatch(() => { fixture.Host.Replay!.Seek(TimeSpan.Zero); fixture.Host.Replay.Play(1); });
        await fixture.WaitForElapsed(seconds => seconds > 0 && seconds < 20);
        var replay = fixture.Host.Replay!;
        await fixture.DisposeRenderer();
        var renders = replay.RenderCount;
        await Task.Delay(250);
        Assert.Equal(renders, replay.RenderCount);
    }

    private static LootTrackerSession Session(string id, string mapId, int duration) => new()
    {
        Id = id, Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Completed,
        StartedAt = At, EndedAt = At.AddSeconds(duration), LastLootAt = At.AddSeconds(duration),
        Elapsed = TimeSpan.FromSeconds(duration), TimelineVersion = 1, KinaUnbound = 12,
        RouteSegments = [new(mapId, "Example map", [new(1000, 1000, At, TimeSpan.Zero),
            new(1100, 1100, At.AddSeconds(2), TimeSpan.FromSeconds(2)),
            new(1200, 1200, At.AddSeconds(duration), TimeSpan.FromSeconds(duration))])],
        TimelineEvents = [new("known-baseline", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero)
            { Metric = LootTrackerTimelineMetric.KinaUnbound, Available = true },
            new("gain", LootTrackerTimelineEventKind.KinaUnbound, At.AddSeconds(2), TimeSpan.FromSeconds(2)) { Amount = 12 }],
        Loot = [new("example-loot", "Example loot", LootTrackerLootKind.Item, 8)]
    };

    private static double Elapsed(string markup)
    {
        var match = Regex.Match(markup, "id=\"loot-timeline-playhead\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The production timeline slider must expose its current replay position.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
    private static string Statistics(string markup)
    {
        var index = markup.IndexOf("class=\"lt-panel lt-session-statistics\"", StringComparison.Ordinal);
        Assert.True(index >= 0); return markup[index..];
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private bool _rendererDisposed;
        public CaptureReplayIntegrationHost Host { get; private set; } = null!;
        public Fixture()
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = "en", NameLanguage = "en" });
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            UiText.Language = "en";
        }
        public async Task<string> Render(LootTrackerSession session)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureReplayIntegrationHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CaptureReplayIntegrationHost.Session)] = session,
                    [nameof(CaptureReplayIntegrationHost.Captured)] = (Action<CaptureReplayIntegrationHost>)(host => Host = host)
                })));
            Assert.NotNull(Host.Replay); Assert.NotNull(Host.Statistics);
            return await Markup();
        }
        public Task Dispatch(Action action) => _renderer.Dispatcher.InvokeAsync(action);
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public async Task<string> WaitForElapsed(Func<double, bool> condition)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                var markup = await Markup();
                if (condition(Elapsed(markup))) return markup;
                await Task.Delay(25);
            }
            throw new TimeoutException("The production replay timer did not advance within five seconds.");
        }
        public async Task DisposeRenderer()
        {
            if (_rendererDisposed) return;
            await _renderer.DisposeAsync(); _rendererDisposed = true;
        }
        public async ValueTask DisposeAsync()
        {
            await DisposeRenderer(); await _provider.DisposeAsync();
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
        }
    }
}

public sealed class CaptureReplayIntegrationHost : ComponentBase
{
    [Parameter] public LootTrackerSession Session { get; set; } = new();
    [Parameter] public Action<CaptureReplayIntegrationHost>? Captured { get; set; }
    public CountedReplayIntegrationView? Replay { get; private set; }
    public CountedReplayIntegrationStatistics? Statistics { get; private set; }
    protected override void OnInitialized() => Captured?.Invoke(this);
    public void ChangeSession(LootTrackerSession session) { Session = session; StateHasChanged(); }
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CountedReplayIntegrationView>(0);
        builder.AddAttribute(1, nameof(LootTrackerSessionReplayView.Session), Session);
        builder.AddAttribute(2, nameof(LootTrackerSessionReplayView.IsExample), true);
        builder.AddComponentReferenceCapture(3, component => Replay = (CountedReplayIntegrationView)component);
        builder.CloseComponent();
        builder.OpenComponent<CountedReplayIntegrationStatistics>(4);
        builder.AddAttribute(5, nameof(LootTrackerSessionStatistics.Session), Session);
        builder.AddAttribute(6, nameof(LootTrackerSessionStatistics.IsExample), true);
        builder.AddComponentReferenceCapture(7, component => Statistics = (CountedReplayIntegrationStatistics)component);
        builder.CloseComponent();
    }
}

public sealed class CountedReplayIntegrationView : LootTrackerSessionReplayView
{
    public int RenderCount { get; private set; }
    protected override void BuildRenderTree(RenderTreeBuilder builder) { RenderCount++; base.BuildRenderTree(builder); }
}

public sealed class CountedReplayIntegrationStatistics : LootTrackerSessionStatistics
{
    public int RenderCount { get; private set; }
    protected override void BuildRenderTree(RenderTreeBuilder builder) { RenderCount++; base.BuildRenderTree(builder); }
}
