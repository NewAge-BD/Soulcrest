using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayViewTests
{
    [Theory]
    [InlineData("de", "Gemeinsame Einstellungen", "Place Mode starten")]
    [InlineData("en", "Shared settings", "Start Place Mode")]
    public async Task CentralTabListsEveryOverlayAndOpeningItLeavesSettingsUntouched(string language, string shared, string place)
    {
        await using var fixture = new Fixture(language);
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var fileBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var html = await fixture.Render();
        foreach (var overlay in OverlayRegistry.All)
            Assert.Contains($"data-overlay-select=\"{overlay.Id}\"", html);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(html, "data-overlay-select=").Count);
        Assert.Contains(shared, html);
        Assert.Contains(place, html);
        Assert.Contains(language == "de" ? "Pet-Fortschritt" : "Pet progress", html);
        Assert.DoesNotContain("Loot &amp; Pet", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("id=\"overlay-language\"", html);
        Assert.Contains("id=\"overlays-in-recordings\"", html);
        Assert.Contains("hotkey-label-loot.toggle", html);
        Assert.Contains("hotkey-label-loot.unlock", html);
        Assert.DoesNotContain("hotkey-label-boss.toggle", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(fileBefore, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
        Assert.False(fixture.Placement.Active);
        Assert.Empty(fixture.Alerts.Items);
        await fixture.Export(html, "loot");
    }

    [Theory]
    [InlineData("map")]
    [InlineData("merchant")]
    [InlineData("daevanion")]
    public async Task BoundOverlaysHavePurePreviewsAndNoFreeLayoutControls(string id)
    {
        await using var fixture = new Fixture("en");
        fixture.Ui.ShowOverlay(id);
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Contains($"data-overlay-detail=\"{id}\"", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Anchored to game content", html);
        Assert.DoesNotContain("id=\"overlay-scale\"", html);
        Assert.DoesNotContain("id=\"overlay-x\"", html);
        Assert.DoesNotContain("id=\"overlay-context-menu\"", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        if (id == "merchant")
            Assert.Contains("The shop overlay always uses the Soulcrest Legacy style and is excluded from themes.", html);
        if (id == "map")
        {
            Assert.Contains("id=\"map-overlay-routes\"", html);
            Assert.Contains("id=\"guard-overlay-radius\"", html);
            Assert.Contains("map-layers-legend-hint", html);
            Assert.Contains("Open map legend", html);
        }
        await fixture.Export(html, id);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EachFreeOverlayOffersItsOwnContextRulesAndAppropriateControls(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        foreach (var overlay in OverlayRegistry.All.Where(item => item.CanPlace))
        {
            var html = await fixture.Invoke("SelectOverlay", overlay.Id);
            foreach (var context in new[] { "worldmap", "menu", "instances", "missing-minimap" })
                Assert.Contains($"id=\"overlay-context-{context}\"", html);
            Assert.Contains("id=\"overlay-scale\"", html);
            Assert.Contains("id=\"overlay-x\"", html);
            Assert.Contains("id=\"overlay-y\"", html);
            Assert.Equal(overlay.Id != "notifications", html.Contains("id=\"overlay-enabled\"", StringComparison.Ordinal));
            Assert.Equal(overlay.Id is "notifications" or "event-timeline", html.Contains("id=\"overlay-width\"", StringComparison.Ordinal));
            if (overlay.Id == "farm-loot")
            {
                Assert.Contains("id=\"loot-background-transparency\"", html);
                Assert.Contains("id=\"loot-overlay-auto-show\"", html);
                foreach (var rarity in new[] { "unique", "epic", "mythic", "special" })
                    Assert.Contains($"data-rarity-alert=\"{rarity}\"", html);
                await fixture.Export(html, "farm-loot");
            }
        }
        var boss = await fixture.Invoke("SelectOverlay", "boss");
        Assert.Contains("id=\"boss-overlay-enabled\"", boss);
        Assert.Contains("id=\"boss-overlay-count\"", boss);
        var timeline = await fixture.Invoke("SelectOverlay", "event-timeline");
        Assert.Contains("id=\"event-timeline-hours\"", timeline);
        Assert.Contains("value=\"48\"", timeline);
        var timers = await fixture.Invoke("SelectOverlay", "alerts");
        Assert.Contains("id=\"timer-overlay-settings\"", timers);
        Assert.Contains("id=\"timer-overlay-gap\"", timers);
        Assert.Contains("id=\"timer-overlay-seconds\"", timers);
        Assert.Contains("data-timer-choice=", timers);
        Assert.DoesNotContain("id=\"timer-overlay-scale\"", timers);
        Assert.DoesNotContain("id=\"timer-overlay-menu\"", timers);
        await fixture.Export(timers, "timers");
        await fixture.Export(timeline, "timeline");
    }

    [Theory]
    [InlineData("de", "Wann anzeigen", "Wenn Aion2 im Vordergrund ist", "Immer")]
    [InlineData("en", "When to Show", "When Aion2 is in the foreground", "Always")]
    public async Task SharedShowModeOffersExactLabelsAndPersistsWithoutChangingOtherPreferences(string language, string label, string foreground, string always)
    {
        await using var fixture = new Fixture(language);
        Assert.Equal("foreground", new AppSettings().OverlayShowMode);
        fixture.Settings.Current.OverlayShowMode = "foreground";
        static string OtherSettings(AppSettings settings)
        {
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayShowMode));
            return node.ToJsonString();
        }
        var before = OtherSettings(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Contains(label, html);
        Assert.Contains(foreground, html);
        Assert.Contains(always, html);
        Assert.Matches("id=\"overlay-show-mode\"[^>]*value=\"foreground\"", html);
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "always" });
        Assert.Equal("always", fixture.Settings.Current.OverlayShowMode);
        Assert.Equal("always", new SettingsService().Current.OverlayShowMode);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "invalid" });
        Assert.Equal("always", fixture.Settings.Current.OverlayShowMode);
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "foreground" });
        Assert.Equal("foreground", new SettingsService().Current.OverlayShowMode);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Placement.Active);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AllSixThemesAreLocalizedPersistImmediatelyAndRefreshThePreviewWithoutChangingOtherSettings(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.OverlayTheme = OverlayThemeDefinition.DefaultId;
        static string OtherSettings(AppSettings settings)
        {
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayTheme));
            return node.ToJsonString();
        }
        static string PreviewImage(string markup) => System.Text.RegularExpressions.Regex.Match(markup, "data:image/png;base64,[^\"]+").Value;
        var before = OtherSettings(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Matches("id=\"overlay-theme\"[^>]*value=\"aion2-stylized\"", html);
        var themeSelect = System.Text.RegularExpressions.Regex.Match(html, "<select id=\"overlay-theme\"[^>]*>(.*?)</select>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        var expectedIds = new[] { "aion2-stylized", "aion2-neutral", "obsidian", "aurora", "runic", "soulcrest-legacy" };
        var expectedLabels = language == "de"
            ? new[] { "Aion2 - stilisierte", "Aion2 - neutral", "Obsidian", "Nordlicht", "Runen", "Soulcrest legacy" }
            : new[] { "Aion2 - stylized", "Aion2 - neutral", "Obsidian", "Aurora", "Runic", "Soulcrest legacy" };
        var options = System.Text.RegularExpressions.Regex.Matches(themeSelect,
            "<option\\b[^>]*\\bvalue=\"([^\"]*)\"[^>]*>(.*?)</option>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(6, options.Count);
        for (var index = 0; index < expectedIds.Length; index++)
        {
            Assert.Equal(expectedIds[index], options[index].Groups[1].Value);
            var label = System.Text.RegularExpressions.Regex.Replace(options[index].Groups[2].Value,
                "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.Equal(expectedLabels[index], WebUtility.HtmlDecode(label).Trim());
        }
        var previews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in expectedIds)
        {
            html = await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = id });
            Assert.Equal(id, fixture.Settings.Current.OverlayTheme);
            Assert.Equal(id, new SettingsService().Current.OverlayTheme);
            Assert.Matches("id=\"overlay-theme\"[^>]*value=\"" + id + "\"", html);
            Assert.Equal(before, OtherSettings(fixture.Settings.Current));
            Assert.NotEmpty(PreviewImage(html));
            Assert.True(previews.Add(PreviewImage(html)), id + " must refresh the production preview");
        }
        await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = "unknown" });
        Assert.Equal("soulcrest-legacy", fixture.Settings.Current.OverlayTheme);
        await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = "aion2" });
        Assert.Equal(OverlayThemeDefinition.DefaultId, new SettingsService().Current.OverlayTheme);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Placement.Active);
    }

    [Fact]
    public async Task LayoutAndPoliciesPersistSeparatelyWithoutEnablingOverlaysOrChangingReminders()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.OverlayEnabled = false;
        fixture.Settings.Current.BossOverlayEnabled = false;
        var alertsBefore = JsonSerializer.Serialize(fixture.Alerts.Items);
        await fixture.Render();
        await fixture.Invoke("SetScale", new ChangeEventArgs { Value = "135" });
        await fixture.Invoke("SetRule", "menu", true);
        Assert.False(fixture.Settings.Current.OverlayEnabled);
        await fixture.Invoke("SelectOverlay", "boss");
        await fixture.Invoke("SetEnabled", new ChangeEventArgs { Value = false });
        Assert.False(fixture.Settings.Current.BossOverlayEnabled);
        await fixture.Invoke("SetRule", "instances", false);
        var saved = new SettingsService().Current;
        Assert.Equal(1.35, saved.OverlayScale);
        Assert.True(OverlayVisibilityPolicy.Get(saved, "loot").Menu);
        Assert.False(OverlayVisibilityPolicy.Get(saved, "boss").Instances);
        Assert.False(saved.OverlayEnabled);
        Assert.False(saved.BossOverlayVisible);
        Assert.Equal(alertsBefore, JsonSerializer.Serialize(fixture.Alerts.Items));
    }

    [Fact]
    public async Task AutomaticMonitorBringUsesGameMonitorAndStoppingKeepsLayout()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render();
        await fixture.Invoke("BringToMonitor");
        Assert.Equal((-1240, 80), (fixture.Settings.Current.OverlayX, fixture.Settings.Current.OverlayY));
        await fixture.Invoke("StartPlaceMode");
        Assert.True(fixture.Placement.Active);
        Assert.Equal("game", fixture.Placement.Monitor!.Id);
        await fixture.Invoke("SetScale", new ChangeEventArgs { Value = "125" });
        var html = await fixture.Invoke("StopPlaceMode");
        Assert.False(fixture.Placement.Active);
        Assert.Equal(1.25, new SettingsService().Current.OverlayScale);
        Assert.Contains("Start Place Mode", html);
    }

    [Fact]
    public void SectionNavigationDoesNotTouchVisibilityOrOtherNavigationTargets()
    {
        var ui = new UiState();
        ui.ShowOverlay("alerts");
        Assert.Equal("overlay", ui.Tab);
        Assert.Equal("alerts", ui.OverlaySection);
        Assert.Null(ui.TakePendingPetFocus());
        ui.ShowOverlay("map");
        Assert.Equal("map", ui.OverlaySection);
        ui.Navigate("pets");
        Assert.Equal("pets", ui.Tab);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-overlay-ui-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private OverlayView? _view;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public UiState Ui => _provider.GetRequiredService<UiState>();
        public OverlayPlacementService Placement => _provider.GetRequiredService<OverlayPlacementService>();
        public AlertsService Alerts => _provider.GetRequiredService<AlertsService>();

        public Fixture(string language)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton(new RareDropAlertService(() => new(), () => throw new InvalidOperationException("Rendering cannot play audio."), TimeProvider.System));
            services.AddSingleton<UiState>();
            services.AddSingleton<OverlayHotkeyService>();
            services.AddSingleton(new OverlayPreviewRenderer(new PetCatalog([])));
            services.AddSingleton(sp => new OverlayPlacementService(sp.GetRequiredService<SettingsService>(),
                () => [new("primary", "Primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040)),
                       new("game", "Game", new(-1280, 0, 1280, 720), new(-1280, 0, 1280, 680))],
                () => new Rectangle(-1280, 0, 1280, 720)));
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false, settings: sp.GetRequiredService<SettingsService>()));
            _provider = services.BuildServiceProvider();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.OverlayVisibility = new(StringComparer.Ordinal);
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedOverlayView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedOverlayView.Captured)] = (Action<OverlayView>)(view => _view = view)
            })));
            return await Markup();
        }
        public async Task<string> Invoke(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(OverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, args);
                if (result is Task task) await task;
                await ((CapturedOverlayView)_view!).RefreshNow();
            });
            return await Markup();
        }
        private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public Task Export(string markup, string id)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_UI_PREVIEW") is not { Length: > 0 } output) return Task.CompletedTask;
            Directory.CreateDirectory(output);
            return File.WriteAllTextAsync(Path.Combine(output, $"overlay-{id}-{_language}.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/overlay.css\"><link rel=\"stylesheet\" href=\"/css/loot-tracker.css\"></head><body>" + markup + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }
}

public sealed class CapturedOverlayView : OverlayView
{
    [Parameter] public Action<OverlayView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
