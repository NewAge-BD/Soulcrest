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

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class EventTimersDashboardTests
{
    [Theory]
    [InlineData("de", "Einstellungen")]
    [InlineData("en", "Settings")]
    public async Task WorkspaceUsesFourAccessibleCompactRowsWithLocalArtworkAndUniqueControlIds(string language, string settings)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        Assert.Equal(4, Regex.Matches(html, "data-event-options=").Count);
        Assert.Empty(Expanded(html));
        Assert.DoesNotContain("workspace-event-cards", html);
        Assert.DoesNotContain("workspace-event-select", html);
        Assert.Contains(settings, html);
        foreach (var id in new[] { "shugo", "rift", "invasion", "abyss" })
        {
            Assert.Contains("data-event-options=\"" + id + "\"", html);
            Assert.Contains("aria-controls=\"alerts-event-details-" + id + "\"", html);
            Assert.Contains("id=\"alerts-event-details-" + id + "\"", html);
            Assert.Contains(TimerOverlayIcons.BrowserSource(id), html);
        }
        var ids = Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("event-timeline-portal", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"workspace-reset-select\"").Count);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Equal(0, fixture.SoundPlaybacks);
        await fixture.Export(html, "collapsed");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task NativeSummaryCallbacksKeepSeveralRowsOpenAcrossSecondTicksAndTimelineSelection(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        await fixture.ClickSummary("rift");
        await fixture.ClickSummary("invasion");
        Assert.Equal(new[] { "invasion", "rift" }, Expanded(await fixture.Html()));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        await fixture.Update();
        Assert.Equal(new[] { "invasion", "rift" }, Expanded(await fixture.Html()));
        Assert.Contains("00:54:59", await fixture.Html());
        await fixture.SelectTimeline("abyss");
        Assert.Equal(new[] { "abyss", "invasion", "rift" }, Expanded(await fixture.Html()));
        Assert.Equal("abyss", Assert.Single(fixture.Selections));
        await fixture.ClickSummary("rift");
        Assert.Equal(new[] { "abyss", "invasion" }, Expanded(await fixture.Html()));
        Assert.Empty(fixture.Alerts.Items);
        Assert.Equal(0, fixture.SoundPlaybacks);
        await fixture.Export(await fixture.Html(), "multiple-expanded");
    }

    [Theory]
    [InlineData("de", "Spacetime Rift offen", "Portal schließt in", "Startet in")]
    [InlineData("en", "Spacetime Rift Open", "Portal closes in", "Starts in")]
    public async Task RiftSummaryUsesTenMinuteDisplayPhaseWhileTimelineRetainsTheSixtyMinuteCalendar(string language, string openTitle, string closes, string starts)
    {
        await using var fixture = new Fixture(language);
        fixture.Clock.UtcNow = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);
        var html = await fixture.Render();
        var row = Summary(html, "rift");
        Assert.Contains(openTitle, row);
        Assert.Contains(closes, row);
        Assert.Contains("00:05:00", row);
        await fixture.ClickSummary("rift");
        await fixture.Export(await fixture.Html(), "rift-open");
        fixture.Clock.UtcNow = new(2026, 10, 12, 18, 10, 0, TimeSpan.Zero);
        await fixture.Update();
        html = await fixture.Html();
        row = Summary(html, "rift");
        Assert.DoesNotContain(openTitle, row);
        Assert.Contains(starts, row);
        Assert.Contains("02:50:00", row);
        Assert.Contains("rift", Expanded(html));
        var source = fixture.Alerts.EventTimers.Single(timer => timer.Definition.Id == "rift");
        Assert.True(source.Window.IsActive);
        Assert.Equal(TimeSpan.FromMinutes(60), source.Window.EndsAt - source.Window.StartsAt);
        Assert.Contains("20:00 – 21:00", html);
        Assert.Contains("event-timeline-portal", html);
        var detailsAt = html.IndexOf("data-event-details=\"rift\"", StringComparison.Ordinal);
        var details = html[detailsAt..html.IndexOf("</section>", detailsAt, StringComparison.Ordinal)];
        Assert.Matches("<li><span>[^<]*23:00</span><span>23:10</span></li>", details);
        Assert.Empty(fixture.Alerts.Items);
        await fixture.Export(html, "rift-after10");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task InlineControlsUseTheOriginalReminderCallbacksAndTheExplicitTestDoesNotFireOnOpening(string language)
    {
        await using var fixture = new Fixture(language);
        var reminder = fixture.Alerts.GetEventReminder("shugo") with { Enabled = true, ShowOverlay = true };
        Assert.True(fixture.Alerts.Save(reminder));
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items);
        await fixture.RenderDetails("shugo");
        Assert.Empty(fixture.Previews);
        Assert.Empty(fixture.Edits);
        await fixture.ClickDetails("event-quick-reminders", 2);
        Assert.Equal(new KeyValuePair<string, int>("shugo", 5), Assert.Single(fixture.ReminderChanges));
        await fixture.ClickDetails("event-detail-test");
        Assert.Equal("shugo", Assert.Single(fixture.Previews));
        await fixture.ClickDetails("event-detail-edit");
        Assert.Equal("shugo", Assert.Single(fixture.Edits));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.Alerts.Items));
        Assert.Equal(0, fixture.SoundPlaybacks);
    }

    private static string[] Expanded(string html) => Regex.Matches(html, "<details[^>]*data-event-options=\"([^\"]+)\"[^>]*\\bopen(?:[ =>])")
        .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
    private static string Summary(string html, string id)
    {
        var at = html.IndexOf("data-event-options=\"" + id + "\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var start = html.IndexOf("<summary", at, StringComparison.Ordinal);
        return html[start..(html.IndexOf("</summary>", start, StringComparison.Ordinal) + "</summary>".Length)];
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-event-ui-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedEventDashboard? _dashboard;
        private CapturedEventDetails? _details;
        public ManualTime Clock { get; } = new();
        public AlertsService Alerts { get; }
        public List<string> Selections { get; } = [];
        public List<string> Previews { get; } = [];
        public List<string> Edits { get; } = [];
        public List<KeyValuePair<string, int>> ReminderChanges { get; } = [];
        public int SoundPlaybacks { get; private set; }

        public Fixture(string language)
        {
            _language = language;
            UiText.Language = language;
            Directory.CreateDirectory(_directory);
            Alerts = new(Path.Combine(_directory, "alerts.json"), Clock, runTimer: false);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<UiState>();
            services.AddSingleton(Alerts);
            services.AddSingleton(new AlertSoundPlayer(() => throw new InvalidOperationException("No playback expected"), _ => SoundPlaybacks++));
            _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedEventDashboard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedEventDashboard.Captured)] = (Action<CapturedEventDashboard>)(component => _dashboard = component),
                [nameof(EventTimersDashboard.WorkspaceMode)] = true,
                [nameof(EventTimersDashboard.Timers)] = Alerts.EventTimers,
                [nameof(EventTimersDashboard.Now)] = Clock.GetUtcNow(),
                [nameof(EventTimersDashboard.SelectedEventChanged)] = EventCallback.Factory.Create<string>(this, Selections.Add)
            })));
            return await Html();
        }

        public Task Update() => _renderer.Dispatcher.InvokeAsync(() => _dashboard!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(EventTimersDashboard.Timers)] = Alerts.EventTimers,
            [nameof(EventTimersDashboard.Now)] = Clock.GetUtcNow()
        })));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        // Test-only render-tree inspection invokes the callbacks emitted by the real Razor component.
#pragma warning disable BL0006
        public Task ClickSummary(string id) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(_dashboard!);
            var frames = tree.GetFrames();
            var parent = Enumerable.Range(0, frames.Count).Single(index => frames.Array[index].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[index].AttributeName == "data-event-options" && Equals(frames.Array[index].AttributeValue, id));
            while (frames.Array[parent].FrameType != RenderTreeFrameType.Element) parent--;
            var end = parent + frames.Array[parent].ElementSubtreeLength;
            var callback = Enumerable.Range(parent, end - parent).Where(index => frames.Array[index].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[index].AttributeName == "onclick").Select(index => frames.Array[index].AttributeValue).First();
            await Click(callback);
            await _dashboard!.RefreshNow();
        });
        public Task SelectTimeline(string id) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(_dashboard!);
            var frames = tree.GetFrames();
            var parent = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-event-timeline" && Equals(frames.Array[i].AttributeValue, id));
            while (frames.Array[parent].FrameType != RenderTreeFrameType.Element) parent--;
            var end = parent + frames.Array[parent].ElementSubtreeLength;
            var callback = Enumerable.Range(parent, end - parent).Where(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "onclick").Select(i => frames.Array[i].AttributeValue).First();
            await Click(callback);
            await _dashboard!.RefreshNow();
        });
#pragma warning restore BL0006
        public async Task RenderDetails(string id)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedEventDetails>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedEventDetails.Captured)] = (Action<CapturedEventDetails>)(component => _details = component),
                [nameof(EventTimerDetails.Timer)] = Alerts.EventTimers.Single(timer => timer.Definition.Id == id),
                [nameof(EventTimerDetails.Embedded)] = true,
                [nameof(EventTimerDetails.Id)] = "synthetic-details-" + id,
                [nameof(EventTimerDetails.ReminderChanged)] = EventCallback.Factory.Create<KeyValuePair<string, int>>(this, ReminderChanges.Add),
                [nameof(EventTimerDetails.PreviewReminder)] = EventCallback.Factory.Create<string>(this, Previews.Add),
                [nameof(EventTimerDetails.EditReminder)] = EventCallback.Factory.Create<string>(this, Edits.Add)
            })));
        }
        // Keep the internal render-tree API limited to extracting actual rendered event handlers.
#pragma warning disable BL0006
        public Task ClickDetails(string className, int index = 0) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = Tree(_details!);
            var frames = tree.GetFrames();
            var attribute = Enumerable.Range(0, frames.Count).First(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "class" && (frames.Array[i].AttributeValue?.ToString()?.Split(' ').Contains(className) ?? false));
            var parent = attribute;
            while (frames.Array[parent].FrameType != RenderTreeFrameType.Element) parent--;
            var end = parent + frames.Array[parent].ElementSubtreeLength;
            var callback = Enumerable.Range(parent, end - parent).Where(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "onclick").Select(i => frames.Array[i].AttributeValue).ElementAt(index);
            await Click(callback);
        });
        private static RenderTreeBuilder Tree(ComponentBase component)
        {
            var tree = new RenderTreeBuilder();
            var type = component is EventTimersDashboard ? typeof(EventTimersDashboard) : typeof(EventTimerDetails);
            type.GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [tree]);
            return tree;
        }
#pragma warning restore BL0006
        private static async Task Click(object? callback)
        {
            switch (callback)
            {
                case EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed: await typed.InvokeAsync(new()); break;
                case EventCallback untyped: await untyped.InvokeAsync(); break;
                case MulticastDelegate handler: if (handler.DynamicInvoke() is Task task) await task; break;
                default: throw new InvalidOperationException("Unexpected click callback: " + callback?.GetType().Name);
            }
        }
        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_ALERTS_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"alerts-events-{state}-{_language}.html"), "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/boss-timers.css\"><link rel=\"stylesheet\" href=\"/css/alerts-workspace.css\"></head><body><div class=\"page alerts-page alerts-workspace\">" + html + "</div></body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            Alerts.Dispose();
            UiText.Language = _previousLanguage;
            Directory.Delete(_directory, true);
        }
    }
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

public sealed class CapturedEventDashboard : EventTimersDashboard
{
    [Parameter] public Action<CapturedEventDashboard>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
public sealed class CapturedEventDetails : EventTimerDetails
{
    [Parameter] public Action<CapturedEventDetails>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
}
