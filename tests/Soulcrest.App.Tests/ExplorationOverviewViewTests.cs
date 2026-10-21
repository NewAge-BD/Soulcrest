using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

#pragma warning disable BL0006 // Inspect the actual Razor checkbox callback without starting the app.
[Collection("Settings file")]
public sealed class ExplorationOverviewViewTests
{
    [Theory]
    [InlineData("de", "Gesamtübersicht", "Erkundungs-Checkliste", "Manuell fixiert", "Spielkarte folgen")]
    [InlineData("en", "Overview", "Exploration checklist", "Manually pinned", "Follow game map")]
    public async Task PinnedOverviewShowsPassiveStatusAboveSavedCompletionAndTheLockedChecklist(string language, string overview, string checklist, string source, string follow)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        var html = await fixture.Render();
        Assert.Contains(overview, html);
        Assert.Contains(checklist, html);
        Assert.Contains(source, html);
        Assert.Contains(follow, html);
        Assert.Contains("<progress value=\"1\" max=\"3\"", html);
        Assert.Contains("data-place=\"rift\"", html);
        Assert.Contains(language == "de" ? "Ohne Abschluss: 4" : "Without completion: 4", html);
        Assert.Contains(language == "de" ? "Unbestätigt: 4" : "Unconfirmed: 4", html);
        Assert.Contains("exploration-place locked", html);
        Assert.Contains("Twisted Pit Entrance", html);
        Assert.Contains("class=\"exploration-rows\" tabindex=\"0\"", html);
        AssertNetworkAboveOverview(html);
        Assert.DoesNotContain(language == "de" ? "Scan starten" : "Start scan", html);
        Assert.False(fixture.Tracking.Running);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "pinned");
    }

    [Theory]
    [InlineData("de", "Automatisch erkannt · Spielkarte", "Auf Karte anzeigen")]
    [InlineData("en", "Detected automatically · player map", "Show on map")]
    public async Task FreshDetectionChangesTheOverviewAndShowsTheNearestOpenPlace(string language, string source, string focus)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = null;
        fixture.Detect("verteron");
        var html = await fixture.Render();
        Assert.Contains(source, html);
        Assert.Contains("Verteron open place", html);
        Assert.Contains("data-place=\"verteron-open\"", html);
        Assert.DoesNotContain("data-place=\"rift\"", html);
        Assert.Contains(focus, html);
        Assert.Contains("class=\"btn primary\"", html);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "detected");
    }

    [Theory]
    [InlineData("de", "Ohne Abschluss", "Erledigt", "Unbestätigt")]
    [InlineData("en", "Without completion", "Completed", "Unconfirmed")]
    public async Task CompletionCheckboxesHideDiscoveryStatesWithoutChangingLegacyProfiles(string language, string incomplete, string complete, string unknown)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["twisted"], ExplorationProgressState.DiscoveredIncomplete);
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["rift"], ExplorationProgressState.Undiscovered);
        var html = await fixture.Render();
        Assert.Contains(incomplete, html);
        Assert.Contains(complete, html);
        Assert.Contains(unknown, html);
        Assert.Equal(5, Regex.Matches(html, "type=\"checkbox\"").Count);
        Assert.DoesNotContain("class=\"exploration-state\"", html);
        Assert.DoesNotContain("value=\"DiscoveredIncomplete\"", html);
        Assert.DoesNotContain("value=\"Undiscovered\"", html);
        Assert.DoesNotContain("value=\"undiscovered\"", html);
        Assert.DoesNotContain("value=\"discovered\"", html);
        Assert.Equal(ExplorationProgressState.DiscoveredIncomplete, fixture.Exploration.Snapshot().Progress["twisted"].State);
        Assert.Equal(ExplorationProgressState.Undiscovered, fixture.Exploration.Snapshot().Progress["rift"].State);
        AssertNetworkAboveOverview(html);
        Assert.DoesNotContain("data-scan-state", html);
        Assert.False(fixture.Tracking.Running);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
    }

    [Theory]
    [InlineData("de", "Manuell", "Gespeicherter Fortschritt", "Unbestätigt")]
    [InlineData("en", "Manual", "Saved progress", "Unconfirmed")]
    public async Task CompactWorkspaceKeepsOneEditableRowPerPlaceAndDistinctSavedAndUnknownStates(
        string language, string manual, string saved, string unknown)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["twisted"], ExplorationProgressState.DiscoveredIncomplete);
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["rift"], ExplorationProgressState.Undiscovered);
        fixture.Detect("altgard");
        var html = await fixture.Render();
        Assert.Contains("class=\"exploration-workspace\"", html);
        Assert.Contains("class=\"exploration-sidebar\"", html);
        Assert.Contains("<details class=\"exploration-status-details\">", html); // Details start collapsed.
        AssertNetworkAboveOverview(html);
        foreach (var id in new[] { "lost", "twisted", "rift", "stronghold", "kibelisk" })
            Assert.Single(Regex.Matches(html, "data-place=\"" + id + "\""));
        var incomplete = Regex.Match(html, "<li[^>]*data-place=\"twisted\".*?</li>", RegexOptions.Singleline).Value;
        Assert.Contains("exploration-state-badge undiscovered", incomplete);
        Assert.Contains(manual, incomplete);
        Assert.Contains(saved, incomplete);
        Assert.Contains("type=\"checkbox\"", incomplete);
        Assert.DoesNotContain("checked", Regex.Match(incomplete, "<input[^>]*>").Value);
        var unconfirmed = Regex.Match(html, "<li[^>]*data-place=\"stronghold\".*?</li>", RegexOptions.Singleline).Value;
        Assert.Contains("exploration-state-badge undiscovered", unconfirmed);
        Assert.Contains(unknown, unconfirmed);
        Assert.Contains("type=\"checkbox\"", unconfirmed);
        Assert.DoesNotContain("checked", Regex.Match(unconfirmed, "<input[^>]*>").Value);
        Assert.Contains("class=\"exploration-kibelisk-toggle\"", html);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "workspace");
    }

    [Fact]
    public async Task CompletionCheckboxUsesTheRenderedCharacterAndMapAndKeepsManualProfileCompatibility()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        var original = fixture.Exploration.ActiveId;
        await fixture.WithView(async view =>
        {
            var rift = CheckboxCallback(view, "Rift Fissure Entrance completed");
            await Change(rift, true);
            Assert.Contains("rift", fixture.Exploration.Completed);
            Assert.True(fixture.Exploration.Snapshot().Progress["rift"].ManualOverride);
            await Change(rift, false);
            Assert.DoesNotContain("rift", fixture.Exploration.Completed);
            Assert.Equal(ExplorationProgressState.DiscoveredIncomplete, fixture.Exploration.Snapshot().Progress["rift"].State);
            Assert.True(fixture.Exploration.Snapshot().Progress["rift"].ManualOverride);

            var stronghold = CheckboxCallback(view, "Synthetic stronghold completed");
            fixture.Exploration.Add("Synthetic alternate profile");
            await Change(stronghold, true);
            Assert.DoesNotContain("stronghold", fixture.Exploration.Completed);
            Assert.Null(fixture.Exploration.Snapshot().Progress["stronghold"].State);
            fixture.Exploration.Select(original);
            Assert.DoesNotContain("stronghold", fixture.Exploration.Completed);

            fixture.Settings.Current.ExplorationPinnedMap = "verteron";
            await view.SetParametersAsync(ParameterView.Empty);
            await Change(rift, true);
            Assert.DoesNotContain("rift", fixture.Exploration.Completed);
            Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        });
    }

    private static object CheckboxCallback(ExplorationView view, string label)
    {
        using var builder = new RenderTreeBuilder();
        typeof(ExplorationView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [builder]);
        var frames = builder.GetFrames();
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "input") continue;
            string? type = null, name = null;
            object? callback = null;
            for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                var attribute = frames.Array[j];
                if (attribute.AttributeName == "type") type = attribute.AttributeValue as string;
                if (attribute.AttributeName == "aria-label") name = attribute.AttributeValue as string;
                if (attribute.AttributeName == "onchange") callback = attribute.AttributeValue;
            }
            if (type == "checkbox" && name == label) return Assert.IsAssignableFrom<object>(callback);
        }
        throw new InvalidOperationException("Completion checkbox not rendered: " + label);
    }

    private static async Task Change(object callback, bool value)
    {
        var args = new ChangeEventArgs { Value = value };
        switch (callback)
        {
            case EventCallback<ChangeEventArgs> typed: await typed.InvokeAsync(args); break;
            case EventCallback untyped: await untyped.InvokeAsync(args); break;
            case MulticastDelegate handler:
                if (handler.DynamicInvoke(args) is Task task) await task;
                break;
            default: throw new InvalidOperationException("Unexpected checkbox callback: " + callback.GetType().Name);
        }
    }

    private static void AssertNetworkAboveOverview(string html)
    {
        Assert.Contains("class=\"exploration-network-setup\"", html);
        Assert.Single(Regex.Matches(html, "data-progress-source="));
        var context = html.IndexOf("class=\"card exploration-context\"", StringComparison.Ordinal);
        var network = html.IndexOf("data-progress-source=", StringComparison.Ordinal);
        var overview = html.IndexOf("class=\"exploration-summary\"", StringComparison.Ordinal);
        var checklist = html.IndexOf("class=\"card exploration-checklist\"", StringComparison.Ordinal);
        Assert.True(context < network && network < overview && overview < checklist);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _language;
        private readonly string _profile = Path.Combine(AppPaths.DataDirectory, "exploration-overview-view-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly (string Ui, string Names, string Map, string? Pin) _previous;
        private readonly string _previousLanguage;
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _mapData = Path.Combine(AppPaths.DataDirectory, "overview-mapdata-" + Guid.NewGuid().ToString("N"));
        public SettingsService Settings { get; }
        public ExplorationService Exploration { get; }
        public MapTrackingService Tracking => _provider.GetRequiredService<MapTrackingService>();

        public Fixture(string language)
        {
            _language = language;
            _previousLanguage = UiText.Language;
            Directory.CreateDirectory(_mapData);
            File.WriteAllText(Path.Combine(_mapData, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            Exploration = new ExplorationService(_profile,
            [new("lost", "altgard", "dungeon", 0, "Lost Ruin Entrance", null, 100, 100),
             new("twisted", "altgard", "dungeon", 1, "Twisted Pit Entrance", null, 200, 100),
             new("rift", "altgard", "dungeon", 2, "Rift Fissure Entrance", null, 300, 100),
             new("stronghold", "altgard", "stronghold", 3, "Synthetic stronghold", null, 400, 100),
             new("kibelisk", "altgard", "kibelisk", 4, "Synthetic Kibelisk", null, 500, 100),
             new("verteron-open", "verteron", "stronghold", 0, "Verteron open place", null, 110, 100)]);
            Exploration.SetDone(Exploration.ActiveId, ["lost"]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(Exploration);
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<UiState>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Current.LootTrackingEnabled = false; // Test positions are visual fallback evidence.
            _previous = (Settings.Current.UiLanguage, Settings.Current.NameLanguage, Settings.Current.LastMap, Settings.Current.ExplorationPinnedMap);
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.LastMap = "altgard";
        }
        public void Detect(string map)
        {
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, true);
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Found))!.SetValue(Tracking, true);
            var at = DateTimeOffset.UtcNow;
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Position))!.SetValue(Tracking, new PlayerPosition(map, 100, 100, at));
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.DetectedMap))!.SetValue(Tracking, new DetectedMapContext(map, false, at));
        }
        public async Task<string> Render()
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ExplorationView>()).ToHtmlString()));
        }
        public async Task WithView(Func<ExplorationView, Task> inspect)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ExplorationView? view = null;
                _ = await renderer.RenderComponentAsync<CaptureExplorationView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CaptureExplorationView.Capture)] = (Action<ExplorationView>)(value => view = value) }));
                await inspect(Assert.IsType<ExplorationView>(view));
            });
        }
        public async Task Export(string html, string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_EXPLORATION_UI_PREVIEW")
                ?? Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            foreach (var (width, height) in new[] { (1600, 900), (1280, 720) })
            {
                var name = $"exploration-{state}-{_language}-{width}x{height}.html";
                var document = "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\">"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/exploration.css\">"
                    + "</head><body data-preview-width=\"" + width + "\" data-preview-height=\"" + height + "\">"
                    + "<div class=\"shell\"><header class=\"topbar\"><div class=\"brand\">Soulcrest</div><nav class=\"tabs\">"
                    + "<button>" + UiText.T("Karte") + "</button><button>Pets</button><button class=\"active\">"
                    + UiText.T("Erkundungsfortschritt") + "</button><button>Boss Rush</button><button>Alerts</button></nav></header>"
                    + "<main class=\"content\">" + html + "</main></div></body></html>";
                await File.WriteAllTextAsync(Path.Combine(output, name), document);
            }
        }
        public async ValueTask DisposeAsync()
        {
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, false);
            Settings.Current.UiLanguage = _previous.Ui;
            Settings.Current.NameLanguage = _previous.Names;
            Settings.Current.LastMap = _previous.Map;
            Settings.Current.ExplorationPinnedMap = _previous.Pin;
            UiText.Language = _previousLanguage;
            await _provider.DisposeAsync();
            File.Delete(_profile);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            File.Delete(Path.Combine(_mapData, "manifest.json"));
            Directory.Delete(_mapData);
        }
    }

    private sealed class CaptureExplorationView : ComponentBase
    {
        [Parameter] public Action<ExplorationView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ExplorationView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((ExplorationView)value));
            builder.CloseComponent();
        }
    }
}
#pragma warning restore BL0006
