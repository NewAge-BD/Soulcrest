using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerReplayLifecycleViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LiveOnlyRecordsAndDoesNotCreateReplayFilesEvenAfterPlaybackCommands()
    {
        await using var fixture = new Fixture();
        var session = Session with { Status = LootTrackerSessionStatus.Live, EndedAt = null };
        var markup = await fixture.Render(session);
        Assert.Contains("data-testid=\"replay-recording\"", markup);
        Assert.DoesNotContain("data-testid=\"session-replay-map\"", markup);
        Assert.DoesNotContain("data-testid=\"session-timeline\"", markup);
        Assert.DoesNotContain("data-testid=\"replay-transport\"", markup);
        await fixture.Dispatch(() => { fixture.Component.Play(1); fixture.Component.Seek(TimeSpan.Zero); });
        await fixture.DispatchAsync(() => fixture.Component.RenderReplay());
        Assert.Equal(LootTrackerReplayStatus.Recording, fixture.Replays.GetState(session).Status);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task CompletedSessionRequiresTheRenderActionThenLoadsThePersistedReplayAfterReopening()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-replay-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fixture = new Fixture(directory))
            {
                var before = await fixture.Render(Session);
                Assert.Contains("id=\"loot-replay-render\"", before);
                Assert.DoesNotContain("data-testid=\"session-replay-map\"", before);
                await fixture.Click("loot-replay-render");
                var after = await fixture.Markup();
                Assert.Contains("data-testid=\"session-replay-map\"", after);
                Assert.Contains("data-testid=\"playhead-time\">00:10", after);
                Assert.Equal(LootTrackerReplayStatus.Ready, fixture.Replays.GetState(Session).Status);
            }
            await using (var fixture = new Fixture(directory))
            {
                var reopened = await fixture.Render(Session);
                Assert.Contains("data-testid=\"session-replay-map\"", reopened);
                Assert.Contains("id=\"loot-replay-rebuild\"", reopened);
                Assert.DoesNotContain("id=\"loot-replay-render\"", reopened);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InterruptedSessionsAreRenderableAndKeepTheirExistingStatus()
    {
        await using var fixture = new Fixture();
        var session = Session with { Status = LootTrackerSessionStatus.Interrupted };
        Assert.Contains("data-testid=\"replay-interrupted\"", await fixture.Render(session));
        await fixture.Click("loot-replay-render");
        Assert.Contains("data-testid=\"session-timeline\"", await fixture.Markup());
        Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
    }

    [Fact]
    public async Task SwitchingViewKeepsPlayheadAndRouteButFiltersCombatMarkersWithoutRerenderingTheArtifact()
    {
        await using var fixture = new Fixture();
        var session = Session with
        {
            Kind = LootTrackerSessionKind.Abyss, UnifiedVersion = 1,
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddSeconds(2), TimeSpan.FromSeconds(2),
                new("sample-map", "Example map", 1010, 1010, At.AddSeconds(2))) { Origin = LootTrackerActivityOrigin.Abyss }]
        };
        await fixture.Render(session);
        await fixture.Click("loot-replay-render");
        var prepared = fixture.Replays.GetState(session).Replay;
        await fixture.Dispatch(() => fixture.Component.Seek(TimeSpan.FromSeconds(2)));
        Assert.Contains("data-marker-kind=\"kill\"", await fixture.Markup());
        await fixture.Parameters(session, LootTrackerSessionProjection.Project(session, LootTrackerViewMode.Lifeskill));
        var filtered = await fixture.Markup();
        Assert.Contains("data-testid=\"playhead-time\">00:02", filtered);
        Assert.Contains("data-map-id=\"sample-map\"", filtered);
        Assert.DoesNotContain("data-marker-kind=\"kill\"", filtered);
        Assert.Same(prepared, fixture.Replays.GetState(session).Replay);
        await fixture.Parameters(session, LootTrackerSessionProjection.Project(session, LootTrackerViewMode.Mixed));
        Assert.Contains("data-marker-kind=\"kill\"", await fixture.Markup());
        Assert.Contains("data-testid=\"playhead-time\">00:02", await fixture.Markup());
    }

    private static LootTrackerSession Session { get; } = new()
    {
        Id = "eeeecafe111122223333444455556666", Kind = LootTrackerSessionKind.Grind,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddSeconds(10),
        LastLootAt = At.AddSeconds(10), Elapsed = TimeSpan.FromSeconds(10), TimelineVersion = 1,
        RouteSegments = [new("sample-map", "Example map", [new(1000, 1000, At, TimeSpan.Zero),
            new(1010, 1010, At.AddSeconds(2), TimeSpan.FromSeconds(2)), new(1050, 1050, At.AddSeconds(10), TimeSpan.FromSeconds(10))])],
        TimelineEvents = [new("gain", LootTrackerTimelineEventKind.KinaUnbound, At.AddSeconds(2), TimeSpan.FromSeconds(2)) { Amount = 5 }]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly bool _ownsDirectory;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private HtmlRootComponent _root;
        public string CacheRoot { get; }
        public LootTrackerReplayService Replays { get; }
        public CaptureReplayLifecycleView Component { get; private set; } = null!;
        public Fixture(string? directory = null)
        {
            _ownsDirectory = directory is null;
            CacheRoot = directory ?? Path.Combine(Path.GetTempPath(), "soulcrest-replay-ui-" + Guid.NewGuid().ToString("N"));
            Replays = new(CacheRoot);
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton(Replays).BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render(LootTrackerSession session)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureReplayLifecycleView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerSessionReplayView.Session)] = session,
                    [nameof(CaptureReplayLifecycleView.Captured)] = (Action<CaptureReplayLifecycleView>)(view => Component = view)
                })));
            return await Markup();
        }
        public Task Parameters(LootTrackerSession session, LootTrackerSession view) => _renderer.Dispatcher.InvokeAsync(() =>
            Component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerSessionReplayView.Session)] = session, [nameof(LootTrackerSessionReplayView.ViewSession)] = view })));
        public Task Dispatch(Action action) => _renderer.Dispatcher.InvokeAsync(action);
        public Task DispatchAsync(Func<Task> action) => _renderer.Dispatcher.InvokeAsync(action);
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
#pragma warning disable BL0006
        public Task Click(string id) => DispatchAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            typeof(LootTrackerSessionReplayView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, [builder]);
            var frames = builder.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "id" && Equals(frames.Array[i].AttributeValue, id));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            for (var i = index + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                if (frames.Array[i].AttributeName == "onclick")
                {
                    if (frames.Array[i].AttributeValue is EventCallback<MouseEventArgs> typed) await typed.InvokeAsync(new());
                    else if (frames.Array[i].AttributeValue is EventCallback callback) await callback.InvokeAsync();
                    else if (frames.Array[i].AttributeValue is MulticastDelegate handler)
                    {
                        var result = handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [new MouseEventArgs()]);
                        if (result is Task task) await task;
                    }
                    else throw new InvalidOperationException("Expected a production event callback.");
                    return;
                }
            throw new InvalidOperationException("Render action missing.");
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            if (_ownsDirectory && Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, true);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        }
    }
}

public sealed class CaptureReplayLifecycleView : LootTrackerSessionReplayView
{
    [Parameter] public Action<CaptureReplayLifecycleView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
}
