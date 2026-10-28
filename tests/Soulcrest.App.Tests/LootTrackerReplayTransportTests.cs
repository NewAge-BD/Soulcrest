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
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

/// <summary>Dispatches the production Razor callbacks with an inert local replay model; no timer or capture.</summary>
[Collection("Settings file")]
public sealed class LootTrackerReplayTransportTests
{
    [Fact]
    public async Task RenderedButtonsDriveStartSkipDirectionPauseAndEndCallbacksInTransportOrder()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render();
        string[] ids = ["loot-replay-start", "loot-replay-reverse", "loot-replay-back", "loot-replay-play",
            "loot-replay-forward", "loot-replay-direction-forward", "loot-replay-end"];
        Assert.Equal(ids, ids.OrderBy(id => html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal)));
        await fixture.Click("loot-replay-start");
        Assert.Equal(1, fixture.StartCount); Assert.Equal(TimeSpan.Zero, fixture.Playback.Elapsed);
        await fixture.Click("loot-replay-forward");
        Assert.Equal(TimeSpan.FromSeconds(10), fixture.Playback.Elapsed); Assert.False(fixture.Playback.IsPlaying);
        html = await fixture.Click("loot-replay-reverse");
        Assert.True(fixture.Playback.IsPlaying); Assert.Equal(-1, fixture.Playback.Direction);
        Assert.Contains("aria-pressed=\"true\"", Tag(html, "loot-replay-reverse"));
        Assert.Contains("aria-pressed=\"false\"", Tag(html, "loot-replay-direction-forward"));
        Assert.Contains(UiText.T("Pause"), WebUtility.HtmlDecode(html));
        html = await fixture.Click("loot-replay-play");
        Assert.False(fixture.Playback.IsPlaying);
        Assert.Contains("aria-label=\"" + UiText.T("Abspielen") + "\"", WebUtility.HtmlDecode(Tag(html, "loot-replay-play")));
        await fixture.Click("loot-replay-play");
        Assert.True(fixture.Playback.IsPlaying); Assert.Equal(-1, fixture.Playback.Direction);
        await fixture.Click("loot-replay-back");
        Assert.Equal(TimeSpan.Zero, fixture.Playback.Elapsed); Assert.False(fixture.Playback.IsPlaying);
        html = await fixture.Click("loot-replay-direction-forward");
        Assert.True(fixture.Playback.IsPlaying); Assert.Equal(1, fixture.Playback.Direction);
        Assert.Contains("aria-pressed=\"true\"", Tag(html, "loot-replay-direction-forward"));
        await fixture.Click("loot-replay-end");
        Assert.Equal(1, fixture.EndCount); Assert.True(fixture.Playback.FollowingEnd);
        Assert.Equal(fixture.Playback.Duration, fixture.Playback.Elapsed); Assert.False(fixture.Playback.IsPlaying);
        Assert.Equal(new[] { -1, 1 }, fixture.Directions);
        Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(-10) }, fixture.Skips);
        Assert.Equal(2, fixture.ToggleCount);
    }

    [Theory]
    [InlineData("0.25", .25)]
    [InlineData("0.5", .5)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData("8", 8)]
    public async Task EveryRenderedSpeedOptionDispatchesItsInvariantValue(string value, double speed)
    {
        await using var fixture = new Fixture("de");
        var html = await fixture.Render();
        Assert.Contains("value=\"" + value + "\"", html);
        html = await fixture.ChangeSpeed(value);
        Assert.Equal(speed, fixture.Playback.Speed); Assert.Equal(speed, Assert.Single(fixture.Speeds));
        Assert.Contains("value=\"" + value + "\"", Tag(html, "loot-replay-speed"));
    }

    [Fact]
    public async Task InvalidOrLocalizedSpeedValuesCannotDispatchAnUnsupportedSpeed()
    {
        await using var fixture = new Fixture(); await fixture.Render();
        foreach (var invalid in new[] { "0,5", "0.75", "NaN", "Infinity", "-1", "9", "" })
            await fixture.ChangeSpeed(invalid);
        Assert.Empty(fixture.Speeds); Assert.Equal(1, fixture.Playback.Speed);
    }

    [Theory]
    [InlineData("de", "0,25×")]
    [InlineData("en", "0.25×")]
    public async Task SpeedLabelsFollowTheUiLanguageAndLiveStatusDoesNotAnnounceElapsedTicks(string language, string speedLabel)
    {
        await using var fixture = new Fixture(language);
        fixture.Playback.Seek(TimeSpan.FromSeconds(10)); fixture.Playback.Play(-1); fixture.Playback.SetSpeed(.25);
        var html = await fixture.Render();
        Assert.Contains(speedLabel, WebUtility.HtmlDecode(html));
        var status = Status(html);
        Assert.Contains(UiText.T("Rückwärts"), status); Assert.Contains(speedLabel, status);
        Assert.Contains("aria-live=\"polite\"", html);
        fixture.Playback.Advance(TimeSpan.FromSeconds(1));
        html = await fixture.Refresh();
        Assert.Equal(status, Status(html));
    }

    [Fact]
    public async Task ZeroDurationDisablesPlaybackAndSpeedButKeepsTheEndControlAvailable()
    {
        await using var fixture = new Fixture(duration: TimeSpan.Zero);
        var html = await fixture.Render();
        foreach (var id in new[] { "loot-replay-start", "loot-replay-reverse", "loot-replay-back", "loot-replay-play",
            "loot-replay-forward", "loot-replay-direction-forward", "loot-replay-speed" })
            Assert.Contains(" disabled", Tag(html, id));
        Assert.DoesNotContain(" disabled", Tag(html, "loot-replay-end"));
        await fixture.ChangeSpeed("4"); Assert.Empty(fixture.Speeds);
        await fixture.Click("loot-replay-end"); Assert.Equal(1, fixture.EndCount);
    }

    private static string Tag(string html, string id) => Regex.Match(html,
        "<(?:button|select)\\b[^>]*\\bid=\"" + Regex.Escape(id) + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
    private static string Status(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "<span class=\"lt-replay-transport-status\"[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.CultureInvariant).Groups[1].Value);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _oldLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CaptureLootTrackerReplayTransport? _component;
        public LootTrackerReplayPlayback Playback { get; } = new();
        public List<int> Directions { get; } = [];
        public List<TimeSpan> Skips { get; } = [];
        public List<double> Speeds { get; } = [];
        public int StartCount { get; private set; }
        public int EndCount { get; private set; }
        public int ToggleCount { get; private set; }
        public Fixture(string language = "en", TimeSpan? duration = null)
        {
            UiText.Language = language;
            Playback.UpdateSession("synthetic-transport", duration ?? TimeSpan.FromSeconds(30), true);
            _provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureLootTrackerReplayTransport>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerReplayTransport.Playback)] = Playback,
                    [nameof(LootTrackerReplayTransport.TogglePlay)] = EventCallback.Factory.Create(this, () => { ToggleCount++; Playback.TogglePlayPause(); }),
                    [nameof(LootTrackerReplayTransport.PlayDirection)] = EventCallback.Factory.Create<int>(this, direction => { Directions.Add(direction); Playback.Play(direction); }),
                    [nameof(LootTrackerReplayTransport.Skip)] = EventCallback.Factory.Create<TimeSpan>(this, delta => { Skips.Add(delta); Playback.Step(delta); }),
                    [nameof(LootTrackerReplayTransport.ChangeSpeed)] = EventCallback.Factory.Create<double>(this, speed => { Speeds.Add(speed); Playback.SetSpeed(speed); }),
                    [nameof(LootTrackerReplayTransport.Start)] = EventCallback.Factory.Create(this, () => { StartCount++; Playback.Seek(TimeSpan.Zero); }),
                    [nameof(LootTrackerReplayTransport.End)] = EventCallback.Factory.Create(this, () => { EndCount++; Playback.JumpToEnd(); }),
                    [nameof(CaptureLootTrackerReplayTransport.Captured)] = (Action<CaptureLootTrackerReplayTransport>)(component => _component = component)
                })));
            return await Markup();
        }
        public Task<string> Click(string id) => Dispatch(id, "onclick", new MouseEventArgs());
        public Task<string> ChangeSpeed(string value) => Dispatch("loot-replay-speed", "onchange", new ChangeEventArgs { Value = value });
        private Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public async Task<string> Refresh()
        {
            await _renderer.Dispatcher.InvokeAsync(() => _component!.RefreshNow());
            return await Markup();
        }
#pragma warning disable BL0006
        private async Task<string> Dispatch<TEventArgs>(string id, string eventName, TEventArgs args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using var builder = new RenderTreeBuilder();
                typeof(LootTrackerReplayTransport).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_component, [builder]);
                var frames = builder.GetFrames();
                var index = Enumerable.Range(0, frames.Count).Single(current => frames.Array[current].FrameType == RenderTreeFrameType.Attribute
                    && frames.Array[current].AttributeName == "id" && Equals(frames.Array[current].AttributeValue, id));
                var element = index;
                while (frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                object? callback = null;
                for (var current = element + 1; current < frames.Count && frames.Array[current].FrameType == RenderTreeFrameType.Attribute; current++)
                    if (frames.Array[current].AttributeName == eventName) callback = frames.Array[current].AttributeValue;
                switch (callback)
                {
                    case EventCallback<TEventArgs> typed: await typed.InvokeAsync(args); break;
                    case EventCallback untyped: await untyped.InvokeAsync(args); break;
                    case MulticastDelegate handler:
                        if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task;
                        break;
                    default: throw new InvalidOperationException("Rendered transport control has no matching callback.");
                }
                await _component!.RefreshNow();
            });
            return await Markup();
        }
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _oldLanguage;
        }
    }
}

public sealed class CaptureLootTrackerReplayTransport : LootTrackerReplayTransport
{
    [Parameter] public Action<CaptureLootTrackerReplayTransport>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
