using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapPetFilterControlsTests
{
    private static readonly string[] Genera = ["cogni", "natura", "varian", "fera"];

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LegendAndProgressionUseTheFourExistingGenusSpritesAndPreserveOtherCategoryIcons(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var html = await fixture.Html();
        foreach (var (genus, label, sprite) in new[]
        {
            ("cogni", "Cogni", "intellect"), ("natura", "Natura", "nature"),
            ("varian", "Varian", "trans"), ("fera", "Fera", "feral")
        })
        {
            var expected = "https://mapdata.soulcrest/icons/gamingtools/ui/resource/sprite/fwindow_vehicle/ut_fwindow_vehicle_creatureslot_" + sprite + "_sprite.png";
            var progressionLabel = Regex.Match(html, "<label\\b[^>]*>\\s*<input\\b[^>]*id=\"progression-genus-" + genus + "\"[^>]*>.*?</label>", RegexOptions.Singleline);
            Assert.True(progressionLabel.Success, "Missing progression genus label: " + genus);
            AssertGenusIcon(progressionLabel.Value, expected, label);
            AssertGenusIcon(LegendCategories(html, label)[0], expected, label);
        }
        Assert.Equal(8, Regex.Matches(html, "<img\\b[^>]*class=\"pet-genus-icon\"[^>]*>").Count);

        var special = Assert.Single(LegendCategories(html, "Special"));
        Assert.Contains("src=\"icons/synthetic-special.png\"", special);
        Assert.DoesNotContain("pet-genus-icon", special);
        var unknown = Assert.Single(LegendCategories(html, "Unknown"));
        Assert.Contains("class=\"dot\" style=\"background:#abcdef\"", unknown);
        Assert.DoesNotContain("<img", unknown);

        await fixture.Invoke("ToggleGroupOpen", "Monsters");
        html = await fixture.Html();
        var monster = LegendCategories(html, "Cogni")[1];
        Assert.Contains("src=\"icons/synthetic-monster.png\"", monster);
        Assert.DoesNotContain("pet-genus-icon", monster);
        var plain = Assert.Single(LegendCategories(html, "Plain monster"));
        Assert.Contains("class=\"dot\" style=\"background:#aabbcc\"", plain);
        Assert.DoesNotContain("<img", plain);
    }

    [Theory]
    [InlineData("de", "Exklusivfilter aktiv:", "Aktiv")]
    [InlineData("en", "Exclusive filter active:", "Active")]
    public async Task ExclusiveNoticeSurvivesCollapsedLegendAndDismissesWithoutChangingTheFilter(string language, string notice, string badge)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Update(s => s.ExclusivePetsOnly = true);
        await fixture.Render();
        var html = await fixture.Html();
        Assert.Contains(notice, html);
        Assert.Contains("pet-exclusive-option is-active", html);
        Assert.Contains("pet-exclusive-active\">" + badge, html);
        Assert.Contains("checked", Input(html, "map-exclusive-only"));
        Assert.True(html.IndexOf("id=\"map-exclusive-notice\"", StringComparison.Ordinal) < html.IndexOf("id=\"map\"", StringComparison.Ordinal));
        await fixture.Export("exclusive-active");
        await fixture.Invoke("ToggleSidebar");
        Assert.Contains("id=\"map-exclusive-notice\"", await fixture.Html());
        await fixture.Export("exclusive-collapsed");

        var saved = File.ReadAllBytes(AppPaths.SettingsFile);
        await fixture.Click("dismiss-map-exclusive-notice");
        Assert.DoesNotContain("id=\"map-exclusive-notice\"", await fixture.Html());
        Assert.True(fixture.Settings.Current.ExclusivePetsOnly);
        Assert.Equal(saved, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Contains("checked", Input(await fixture.Html(), "map-exclusive-only"));
        var invalidations = fixture.Js.Calls.Count(c => c.Name == "soulcrestMap.invalidate");
        await fixture.Invoke("OnAfterRenderAsync", false);
        Assert.True(fixture.Js.Calls.Count(c => c.Name == "soulcrestMap.invalidate") > invalidations);

        await fixture.Invoke("SwitchMap", "verteron", null);
        fixture.Ui.Navigate("options");
        await fixture.SetActive(false);
        fixture.Ui.Navigate("map");
        await fixture.SetActive(true);
        await fixture.Remount();
        Assert.DoesNotContain("id=\"map-exclusive-notice\"", await fixture.Html());
        await fixture.Change("map-exclusive-only", false);
        Assert.DoesNotContain("pet-exclusive-option is-active", await fixture.Html());
        await fixture.Change("map-exclusive-only", true);
        Assert.Contains("id=\"map-exclusive-notice\"", await fixture.Html());
    }

    [Fact]
    public async Task NewSessionShowsTheSavedFilterAgainWithoutPersistingTheDismissal()
    {
        string persisted;
        await using (var first = new Fixture())
        {
            await first.Render();
            Assert.DoesNotContain("id=\"map-exclusive-notice\"", await first.Html());
            await first.Change("map-exclusive-only", true);
            await first.Click("dismiss-map-exclusive-notice");
            persisted = File.ReadAllText(AppPaths.SettingsFile);
        }
        await using var restarted = new Fixture(settingsJson: persisted);
        await restarted.Render();
        Assert.True(restarted.Settings.Current.ExclusivePetsOnly);
        Assert.Contains("id=\"map-exclusive-notice\"", await restarted.Html());
    }

    [Theory]
    [InlineData("de", "Keine Pet-Gattung ausgewählt", "Ziel je Pet")]
    [InlineData("en", "No pet genus selected", "Goal per pet")]
    public async Task GenusControlsDefaultToAllInOrderAndEmptySelectionPersistsWhenPetModeIsHidden(string language, string paused, string goalLabel)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var html = await fixture.Html();
        var previous = -1;
        foreach (var genus in Genera)
        {
            var id = "progression-genus-" + genus;
            Assert.Contains("checked", Input(html, id));
            var current = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
            Assert.True(current > previous);
            previous = current;
        }
        Assert.True(html.IndexOf(goalLabel, StringComparison.Ordinal) > previous);
        Assert.DoesNotContain("progression-genus-special", html);
        await fixture.Export("genera-all");
        foreach (var genus in Genera) await fixture.Change("progression-genus-" + genus, false);
        Assert.Contains(paused, await fixture.Html());
        Assert.Empty(fixture.AllowedPets());
        Assert.Empty(new SettingsService().Current.ProgressionPetGenera);
        await fixture.Export("genera-empty");

        await fixture.Invoke("SetProgressionKind", "pets", false);
        Assert.DoesNotContain("id=\"progression-genus-", await fixture.Html());
        await fixture.Invoke("SetProgressionKind", "pets", true);
        html = await fixture.Html();
        foreach (var genus in Genera) Assert.DoesNotContain("checked", Input(html, "progression-genus-" + genus));
        Assert.Contains(paused, html);
    }

    [Theory]
    [InlineData("cogni")]
    [InlineData("natura")]
    [InlineData("varian")]
    [InlineData("fera")]
    public async Task ChangingAGenusImmediatelyClearsItsCurrentTargetAndRejectsQueuedCallbacks(string genus)
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        await fixture.Callback(genus, 200, 300);
        Assert.Equal(genus, fixture.Targets.Progression?.PetId);
        await fixture.Change("progression-genus-" + genus, false);
        Assert.Null(fixture.Targets.Progression);
        await fixture.Callback(genus, 201, 301);
        Assert.Null(fixture.Targets.Progression);
        await fixture.Callback("legacy-" + genus, 202, 302);
        Assert.Null(fixture.Targets.Progression);
        var allowed = Genera.First(g => g != genus);
        await fixture.Callback(allowed, 600, 700);
        var target = Assert.IsType<MapTarget>(fixture.Targets.Progression);
        Assert.Equal(allowed, target.PetId);
        await fixture.Callback(genus, 203, 303);
        Assert.Same(target, fixture.Targets.Progression);
        Assert.DoesNotContain(genus, new SettingsService().Current.ProgressionPetGenera);
        foreach (var surface in new[] { MapSurface.Minimap, MapSurface.Worldmap })
        {
            var overlay = RouteOverlayForm.PrepareOverlay("altgard", surface, "en", fixture.Settings.Current,
                fixture.Progress, fixture.Targets, (_, _) => null, (_, _) => null);
            var shown = Assert.Single(overlay.Targets).Target;
            Assert.Equal(target.X, shown.X);
            Assert.Equal(target.Y, shown.Y);
        }
    }

    [Fact]
    public async Task AllowedIdsIncludeCatalogAliasesAndRemainIndependentOfGoalAndDensity()
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        Assert.Equal(Genera.SelectMany(g => new[] { g, "legacy-" + g }).Order(StringComparer.Ordinal), fixture.AllowedPets().Order(StringComparer.Ordinal));
        await fixture.Change("progression-genus-cogni", false);
        await fixture.Change("progression-genus-varian", false);
        var expected = new[] { "natura", "legacy-natura", "fera", "legacy-fera" }.Order(StringComparer.Ordinal).ToArray();
        foreach (var goal in new[] { "5", "30", "105", "closest" })
        {
            await fixture.Invoke("SetProgressionGoal", new ChangeEventArgs { Value = goal });
            foreach (var density in new[] { false, true })
            {
                await fixture.Change("progression-highest-density", density);
                Assert.Equal(expected, fixture.AllowedPets().Order(StringComparer.Ordinal));
            }
        }
        await fixture.Callback("special", 1, 2);
        Assert.Null(fixture.Targets.Progression);
        await fixture.Callback("unknown", 1, 2);
        Assert.Null(fixture.Targets.Progression);
        await fixture.Callback("not-in-catalog", 1, 2);
        Assert.Null(fixture.Targets.Progression);
        await fixture.Callback("legacy-fera", 400, 500);
        Assert.NotNull(fixture.Targets.Progression);
    }

    [Fact]
    public async Task EmptyPetSelectionStillAllowsDungeonsAndStrongholds()
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        await fixture.Invoke("SetProgressionKind", "dungeon", true);
        await fixture.Invoke("SetProgressionKind", "stronghold", true);
        foreach (var genus in Genera) await fixture.Change("progression-genus-" + genus, false);
        foreach (var (id, kind) in new[] { ("dungeon-fixture", "Sealed Dungeon"), ("stronghold-fixture", "Stronghold") })
        {
            await fixture.Callback(id, 100, 100);
            var target = Assert.IsType<MapTarget>(fixture.Targets.Progression);
            Assert.Equal(kind, target.Kind);
            Assert.Null(target.PetId);
            await fixture.Callback("fera", 200, 200);
            Assert.Same(target, fixture.Targets.Progression);
        }
        Assert.Empty(fixture.AllowedPets());
        var kinds = JsonSerializer.Deserialize<string[]>((string)fixture.ProgressionCall().Arguments[3]!)!;
        Assert.Equal(new[] { "pets", "dungeon", "stronghold" }, kinds);
    }

    [Fact]
    public async Task GenusSelectionLeavesMapMarkerFiltersManualTargetsAndSoulProgressUnchanged()
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        fixture.Progress.SetSouls("cogni", 17);
        fixture.Targets.Toggle("altgard", 100, 120, "Synthetic manual target", "Pet", null, petId: "cogni");
        await fixture.Html();
        var manual = Assert.Single(fixture.Targets.VisibleTargets);
        var layers = JsonSerializer.Serialize(new { fixture.Settings.Current.MapCategories, fixture.Settings.Current.MapMinimapLayers,
            fixture.Settings.Current.MapWorldmapLayers, fixture.Settings.Current.MapHiddenKinds, fixture.Settings.Current.ExclusivePetsOnly,
            fixture.Settings.Current.HideLevel1, fixture.Settings.Current.HideLevel2, fixture.Settings.Current.HideLevel3 });
        var progress = File.ReadAllBytes(fixture.ProgressFile);
        fixture.Js.Calls.Clear();
        await fixture.Change("progression-genus-cogni", false);
        Assert.Equal(manual, Assert.Single(fixture.Targets.VisibleTargets));
        Assert.Equal(17, fixture.Progress.Souls("cogni"));
        Assert.Equal(progress, File.ReadAllBytes(fixture.ProgressFile));
        Assert.Equal(layers, JsonSerializer.Serialize(new { fixture.Settings.Current.MapCategories, fixture.Settings.Current.MapMinimapLayers,
            fixture.Settings.Current.MapWorldmapLayers, fixture.Settings.Current.MapHiddenKinds, fixture.Settings.Current.ExclusivePetsOnly,
            fixture.Settings.Current.HideLevel1, fixture.Settings.Current.HideLevel2, fixture.Settings.Current.HideLevel3 }));
        Assert.DoesNotContain(fixture.Js.Calls, call => call.Name is "soulcrestMap.setDone" or "soulcrestMap.setCategories" or "soulcrestMap.setHiddenKinds");
        Assert.Equal(0, fixture.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
    }

    private static string Input(string html, string id)
    {
        var match = Regex.Match(html, "<input\\b[^>]*id=\"" + Regex.Escape(id) + "\"[^>]*>");
        Assert.True(match.Success, "Missing input: " + id);
        return match.Value;
    }

    private static string[] LegendCategories(string html, string label) => Regex.Matches(html,
        "<div class=\"cat legend-row\" title=\"" + Regex.Escape(label) + "\">.*?</div>", RegexOptions.Singleline)
        .Select(match => match.Value).ToArray();

    private static void AssertGenusIcon(string markup, string url, string label)
    {
        var icon = Regex.Match(markup, "<img\\b[^>]*>");
        Assert.True(icon.Success, "Missing genus icon: " + label);
        Assert.Contains("class=\"pet-genus-icon\"", icon.Value);
        Assert.Contains("src=\"" + url + "\"", icon.Value);
        // HtmlRenderer may serialize an empty attribute as bare `alt`; both forms mean alt="".
        Assert.Matches("\\salt(?:=\"\"|(?=[\\s/>]))", icon.Value);
        Assert.Contains(">" + label + "<", markup.Replace(" ", "", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-filters-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly string _language;
        private HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private MapView? _view;
        public ServiceProvider Provider { get; }
        public SettingsService Settings => Provider.GetRequiredService<SettingsService>();
        public UiState Ui => Provider.GetRequiredService<UiState>();
        public ProgressService Progress => Provider.GetRequiredService<ProgressService>();
        public MapTargetsService Targets => Provider.GetRequiredService<MapTargetsService>();
        public string ProgressFile => Path.Combine(_folder, "progress.json");
        public RecordingJs Js { get; } = new();

        public Fixture(string language = "en", string? settingsJson = null)
        {
            _language = language;
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile, Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"),
                Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (File.Exists(path)) File.Delete(path);
            }
            if (settingsJson is not null) File.WriteAllText(AppPaths.SettingsFile, settingsJson);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian","metersPerPixel":1},{"id":"verteron","label":"Verteron","group":"Elyos","metersPerPixel":2}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var pets = Genera.Select(genus => new PetDefinition { Id = genus, En = "Synthetic " + genus + " pet", Genus = genus,
                Spawns = new() { ["altgard"] = 2 }, Monsters = new() { ["altgard"] = 12 } }).Concat([
                new PetDefinition { Id = "special", En = "Synthetic special", Genus = "special", Spawns = new() { ["altgard"] = 1 } },
                new PetDefinition { Id = "unknown", En = "Synthetic unknown", Genus = "unknown", Spawns = new() { ["altgard"] = 1 } }
            ]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(new ProgressService(ProgressFile, new PetCatalog(pets, Genera.ToDictionary(g => "legacy-" + g, g => g))));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton(sp => new TrackerService(sp.GetRequiredService<SettingsService>(), network: null));
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), [
                new("dungeon-fixture", "altgard", "dungeon", 8, "Synthetic dungeon", "Beispiel-Dungeon", 100, 100),
                new("stronghold-fixture", "altgard", "stronghold", 9, "Synthetic stronghold", "Beispiel-Garnison", 200, 200)
            ]));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton<IJSRuntime>(Js);
            Provider = services.BuildServiceProvider();
            Settings.Update(s => { s.LootTrackingEnabled = false; s.UiLanguage = s.NameLanguage = language; s.LastMap = "altgard";
                s.FollowPlayer = false; s.ProgressionEnabled = true; s.ProgressionKinds = ["pets"]; s.ShowRoutesInGame = true; });
            _renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(view => _view = view) })));
            await Invoke("OnAfterRenderAsync", true);
        }

        public async Task Remount()
        {
            await _renderer.DisposeAsync();
            _renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
            await Render();
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task SetActive(bool active) => _renderer.Dispatcher.InvokeAsync(() => _view!.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(MapView.Active)] = active })));
        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (typeof(MapView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, args) is Task task) await task;
            await _view!.SetParametersAsync(ParameterView.Empty);
        });
        public Task Callback(string petId, double x, double y) => _renderer.Dispatcher.InvokeAsync(() => _view!.OnProgressionTarget("altgard", petId, x, y, "Synthetic target"));
        public (string Name, object?[] Arguments) ProgressionCall() => Js.Calls.Last(call => call.Name == "soulcrestMap.setProgression");
        public string[] AllowedPets()
        {
            var call = ProgressionCall();
            Assert.Equal(10, call.Arguments.Length);
            return JsonSerializer.Deserialize<string[]>((string)call.Arguments[9]!)!;
        }

        public Task Change(string id, bool value) => Event(id, "onchange", new ChangeEventArgs { Value = value });
        public Task Click(string id) => Event(id, "onclick", new MouseEventArgs());

#pragma warning disable BL0006
        private Task Event(string id, string eventName, EventArgs args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [builder]);
            var frames = builder.GetFrames();
            var attribute = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "id" && Equals(frames.Array[i].AttributeValue, id));
            var element = attribute;
            while (frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
            var callback = Enumerable.Range(element + 1, frames.Array[element].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .First(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == eventName).AttributeValue;
            switch (callback)
            {
                case EventCallback<ChangeEventArgs> change: await change.InvokeAsync((ChangeEventArgs)args); break;
                case EventCallback<MouseEventArgs> click: await click.InvokeAsync((MouseEventArgs)args); break;
                case EventCallback untyped: await untyped.InvokeAsync(args); break;
                case MulticastDelegate handler:
                    if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task;
                    break;
                default: throw new InvalidOperationException("Unexpected callback for " + id);
            }
            await _view!.SetParametersAsync(ParameterView.Empty);
        });
#pragma warning restore BL0006

        public async Task Export(string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_MAP_FILTER_UI_OUTPUT") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"map-pet-filters-{state}-{_language}.html"), "<!doctype html><html lang=\"" + _language
                + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\">"
                + "<link rel=\"stylesheet\" href=\"/css/monster-search.css\"><style>html,body{height:100%;margin:0}.mapview{height:100vh}"
                + "#map{background:linear-gradient(135deg,#1b302c,#253c32)}</style></head><body>" + await Html() + "</body></html>");
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
                if (bytes is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }

    private sealed class CaptureMapView : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddAttribute(1, nameof(MapView.Active), true);
            builder.AddComponentReferenceCapture(2, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return typeof(TValue) == typeof(string)
                ? ValueTask.FromResult((TValue)(object)(identifier == "soulcrestMap.show"
                    ? """
                        {"groups":[{"name":"Pets","categories":[
                          {"index":0,"name":"Cogni","count":2,"hidden":false,"color":"#38bdf8"},
                          {"index":1,"name":"Natura","count":2,"hidden":false,"color":"#4ade80"},
                          {"index":2,"name":"Varian","count":2,"hidden":false,"color":"#facc15"},
                          {"index":3,"name":"Fera","count":2,"hidden":false,"color":"#f87171"},
                          {"index":4,"name":"Special","count":1,"hidden":false,"color":"#38bdf8","icon":"icons/synthetic-special.png"},
                          {"index":5,"name":"Unknown","count":1,"hidden":false,"color":"#abcdef"}
                        ]},{"name":"Monsters","categories":[
                          {"index":6,"name":"Cogni","count":1,"hidden":false,"color":"#fff","icon":"icons/synthetic-monster.png"},
                          {"index":7,"name":"Plain monster","count":1,"hidden":false,"color":"#aabbcc"}
                        ]}],"regions":0,"markers":12}
                        """ : "[]"))
                : ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
