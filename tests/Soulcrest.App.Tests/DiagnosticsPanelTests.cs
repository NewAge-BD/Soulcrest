using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DiagnosticsPanelTests
{
    [Theory]
    [InlineData("de", "Alle auswählen", "Spielverkehr bei Bedarf aufnehmen")]
    [InlineData("en", "Select all", "Record game traffic when needed")]
    public async Task AllProductFeaturesAreSelectableAndTheRecordingRemainsOptIn(string language, string all, string recording)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();

        Assert.Contains(all, html);
        Assert.Contains(recording, html);
        Assert.Contains("data-diagnostics-recording=\"Idle\"", html);
        foreach (var feature in DiagnosticFeatureCatalog.All)
            Assert.Contains("data-diagnostic-feature=\"" + feature.Id + "\"><input type=\"checkbox\" checked", html);
        Assert.DoesNotContain("data-diagnostic-result=", html);
        Assert.Equal(DiagnosticFeatureCatalog.All.Count, fixture.Selection.Count);
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
        fixture.ExportPreview("selection", language, html);
    }

    [Fact]
    public async Task EmptySelectionDisablesChecksAndAGroupStillSelectsItsHiddenSearchMatches()
    {
        await using var fixture = new Fixture("de");
        await fixture.Render();
        await fixture.Invoke("SelectNone");
        var empty = await fixture.Html();
        Assert.Contains("Wähle mindestens eine Funktion aus.", empty);
        Assert.Contains("disabled>Auswahl prüfen</button>", empty);
        Assert.Empty(fixture.Selection);

        var feature = DiagnosticFeatureCatalog.All.First(item => item.Id == "loot-killfeed");
        await fixture.Invoke("SearchChanged", new ChangeEventArgs { Value = "Killfeed" });
        await fixture.Invoke("SelectGroup", feature.Group, true);
        Assert.Equal(DiagnosticFeatureCatalog.All.Count(item => item.Group == feature.Group), fixture.Selection.Count);
        Assert.Contains("data-diagnostic-feature=\"loot-killfeed\"", await fixture.Html());
        Assert.DoesNotContain("data-diagnostic-feature=\"loot-pve\"", await fixture.Html());
        await fixture.Invoke("SearchChanged", new ChangeEventArgs { Value = "" });
        Assert.Contains("data-diagnostic-feature=\"loot-pve\"><input type=\"checkbox\" checked", await fixture.Html());
    }

    [Fact]
    public async Task ATargetedCheckShowsItsDependenciesAndSelectionChangesHideTheOldResults()
    {
        await using var fixture = new Fixture("de");
        await fixture.Render();
        await fixture.Invoke("SelectNone");
        await fixture.Invoke("SelectFeature", "loot-killfeed", true);
        var selected = await fixture.Html();
        Assert.Contains("Zusätzlich benötigte Quellen:", selected);
        Assert.Contains("Abyss, AP und Rangpunkte", selected);
        await fixture.Invoke("CheckNow");

        var checkedHtml = await fixture.Html();
        Assert.Contains("data-diagnostic-result=\"loot-killfeed\"", checkedHtml);
        Assert.Contains("data-diagnostic-result=\"loot-abyss\"", checkedHtml);
        Assert.Contains("Benötigte gemeinsame Quelle", checkedHtml);
        Assert.DoesNotContain("data-diagnostic-result=\"combat-pantheon\"", checkedHtml);
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
        fixture.ExportPreview("results", "de", checkedHtml);

        await fixture.Invoke("SelectFeature", "loot-killfeed", false);
        await fixture.Invoke("SelectFeature", "combat-pantheon", true);
        var changed = await fixture.Html();
        Assert.DoesNotContain("data-diagnostic-result=", changed);
        Assert.Contains("Auswahl geändert.", changed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-diagnostic-panel-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _reportBefore;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private DiagnosticsPanel _panel = default!;
        public GameCaptureService Capture { get; }
        private DiagnosticsService Diagnostics { get; }
        public IReadOnlySet<string> Selection => (HashSet<string>)typeof(DiagnosticsPanel)
            .GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_panel)!;

        public Fixture(string language)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), "{\"maps\":[]}");
            File.WriteAllText(Path.Combine(_folder, "pets.json"), "[]");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            UiText.Language = language;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton(provider => new DiagnosticsService(provider.GetRequiredService<SettingsService>(),
                provider.GetRequiredService<GameCaptureService>(), provider.GetRequiredService<TrackerService>(),
                provider.GetRequiredService<MapTrackingService>(), contributors: [new SyntheticContributor()], runTimer: false));
            _provider = services.BuildServiceProvider();
            Capture = _provider.GetRequiredService<GameCaptureService>();
            Diagnostics = _provider.GetRequiredService<DiagnosticsService>();
            var settings = _provider.GetRequiredService<SettingsService>();
            settings.Current.UiLanguage = settings.Current.NameLanguage = UiText.Language = language;
            _reportBefore = File.Exists(Diagnostics.ReportPath) ? File.ReadAllBytes(Diagnostics.ReportPath) : null;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CapturePanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                    { [nameof(CapturePanel.Capture)] = (Action<DiagnosticsPanel>)(panel => _panel = panel) })));
            return await Html();
        }
        public async Task<string> Html() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task Invoke(string method, params object?[] args) => await _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(DiagnosticsPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_panel, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_panel, null);
        });
        public void ExportPreview(string state, string language, string html)
        {
            var directory = Environment.GetEnvironmentVariable("SOULCREST_DIAGNOSTICS_UI_PREVIEW");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, state + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/diagnostics.css\"></head><body><div class=\"page info\">" + html + "</div></body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            if (_reportBefore is not null) File.WriteAllBytes(Diagnostics.ReportPath, _reportBefore);
            else if (File.Exists(Diagnostics.ReportPath)) File.Delete(Diagnostics.ReportPath);
            UiText.Language = _previousLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class SyntheticContributor : IDiagnosticContributor
    {
        public IReadOnlySet<string> FeatureIds { get; } = DiagnosticFeatureCatalog.All.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        public DiagnosticResult Collect(string featureId) => new(featureId,
            featureId == "loot-killfeed" ? DiagnosticStatus.Waiting : featureId == "loot-abyss" ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy,
            new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero),
            featureId == "loot-killfeed" ? "Die Funktion wartet auf passende Daten."
                : featureId == "loot-abyss" ? "Unvollständige oder veraltete Daten; Details prüfen." : "In Ordnung",
            new Dictionary<string, object?> { ["sourceAvailable"] = featureId != "loot-killfeed", ["observedAtUtc"] = null });
    }

    private sealed class CapturePanel : ComponentBase
    {
        [Parameter] public Action<DiagnosticsPanel> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<DiagnosticsPanel>(0);
            builder.AddComponentReferenceCapture(1, component => Capture((DiagnosticsPanel)component));
            builder.CloseComponent();
        }
    }
}
