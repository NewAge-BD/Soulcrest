using System.Net;
using System.Reflection;
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
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

// Read the Razor-generated callbacks, rather than invoking the private mutation methods directly.
#pragma warning disable BL0006
[Collection("Settings file")]
public sealed class MapSurfaceLegendTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EveryGroupCategoryAndResourceKindHasThreeLocalizedSurfaceControls(string language)
    {
        await using var fixture = new Fixture(language);
        var view = await fixture.Render();
        var controls = await fixture.Controls(view);
        Assert.Equal(30, controls.Count); // three groups, five categories and two kinds, on three surfaces
        var names = new[] { "Pets", "Cogni", "Fera", "Resources", language == "de" ? "Edelstein" : "Gem",
            language == "de" ? "Kraut" : "Herb", language == "de" ? "Rubin" : "Ruby",
            language == "de" ? "Diamant" : "Diamond", "Locations", "Kibelisk" };
        foreach (var name in names)
        {
            var labels = Surfaces.Select(surface => Label(language, name, surface)).ToArray();
            foreach (var label in labels)
                Assert.NotNull(Assert.Single(controls, control => control.Label == label).Callback);
            Assert.Equal(labels, controls.Where(control => labels.Contains(control.Label, StringComparer.Ordinal)).Select(control => control.Label));
        }
        Assert.True(Assert.Single(controls, control => control.Label == Label(language, "Fera", MapSurface.Minimap)).Checked);
        Assert.False(Assert.Single(controls, control => control.Label == Label(language, "Fera", MapSurface.Interactive)).Checked);
        var html = await fixture.Html();
        Assert.Contains(language == "de" ? "Anzeigen auf" : "Show on", html);
        Assert.Contains("title=\"" + SurfaceLabel(language, MapSurface.Interactive) + "\"", html);
        Assert.Contains("title=\"" + SurfaceLabel(language, MapSurface.Worldmap) + "\"", html);
        await fixture.Export(html);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("category")]
    [InlineData("kind")]
    public async Task FirstInteractiveEditFreezesBothLegacyNativeViewsAndMinimapKindEditSurvivesReload(string firstEdit)
    {
        await using var fixture = new Fixture("en");
        var view = await fixture.Render();
        var settings = fixture.Settings;
        Assert.Empty(settings.Current.MapMinimapLayers);
        Assert.Empty(settings.Current.MapWorldmapLayers);
        var initialNative = NativeSignature(settings.Current, MapSurface.Minimap);
        Assert.Equal(initialNative, NativeSignature(settings.Current, MapSurface.Worldmap));

        var name = firstEdit switch { "group" => "Resources", "category" => "Gem", _ => "Diamond" };
        await fixture.Change(view, Label("en", name, MapSurface.Interactive), false);

        Assert.NotNull(MapLayerVisibility.Selection(settings.Current, "altgard", MapSurface.Minimap));
        Assert.NotNull(MapLayerVisibility.Selection(settings.Current, "altgard", MapSurface.Worldmap));
        Assert.Equal(initialNative, NativeSignature(settings.Current, MapSurface.Minimap));
        Assert.Equal(initialNative, NativeSignature(settings.Current, MapSurface.Worldmap));
        Assert.False(Kind(settings.Current, MapSurface.Interactive, "Diamond"));
        var interactive = InteractiveSignature(settings.Current);

        // Ruby was hidden by the old shared preference. Only the minimap is changed by this callback.
        await fixture.Change(view, Label("en", "Ruby", MapSurface.Minimap), true);
        Assert.True(Kind(settings.Current, MapSurface.Minimap, "Ruby"));
        Assert.False(Kind(settings.Current, MapSurface.Worldmap, "Ruby"));
        Assert.False(Kind(settings.Current, MapSurface.Interactive, "Ruby"));
        Assert.Equal(initialNative, NativeSignature(settings.Current, MapSurface.Worldmap));
        Assert.Equal(interactive, InteractiveSignature(settings.Current));
        var minimap = NativeSignature(settings.Current, MapSurface.Minimap);
        var controls = await fixture.Controls(view);
        Assert.True(Assert.Single(controls, control => control.Label == Label("en", "Ruby", MapSurface.Minimap)).Checked);
        Assert.False(Assert.Single(controls, control => control.Label == Label("en", "Ruby", MapSurface.Worldmap)).Checked);
        Assert.False(Assert.Single(controls, control => control.Label == Label("en", "Ruby", MapSurface.Interactive)).Checked);

        var reloaded = new SettingsService().Current;
        Assert.Equal(minimap, NativeSignature(reloaded, MapSurface.Minimap));
        Assert.Equal(initialNative, NativeSignature(reloaded, MapSurface.Worldmap));
        Assert.Equal(interactive, InteractiveSignature(reloaded));
        Assert.Null(MapLayerVisibility.Selection(reloaded, "another-map", MapSurface.Minimap));
        Assert.Null(MapLayerVisibility.Selection(reloaded, "another-map", MapSurface.Worldmap));
    }

    [Theory]
    [InlineData(MapSurface.Minimap)]
    [InlineData(MapSurface.Worldmap)]
    public async Task NativeGroupAndCategoryCallbacksAffectOnlyTheirChosenSurface(MapSurface surface)
    {
        await using var fixture = new Fixture("en");
        var view = await fixture.Render();
        var other = surface == MapSurface.Minimap ? MapSurface.Worldmap : MapSurface.Minimap;
        var initialOther = NativeSignature(fixture.Settings.Current, other);
        var initialInteractive = InteractiveSignature(fixture.Settings.Current);

        await fixture.Change(view, Label("en", "Resources", surface), false);
        Assert.False(Category(fixture.Settings.Current, surface, "Resources", "Gem"));
        Assert.False(Category(fixture.Settings.Current, surface, "Resources", "Herb"));
        await fixture.Change(view, Label("en", "Herb", surface), true);
        Assert.True(Category(fixture.Settings.Current, surface, "Resources", "Herb"));
        Assert.False(Category(fixture.Settings.Current, surface, "Resources", "Gem"));
        Assert.Equal(initialOther, NativeSignature(fixture.Settings.Current, other));
        Assert.Equal(initialInteractive, InteractiveSignature(fixture.Settings.Current));
        var reloaded = new SettingsService().Current;
        Assert.True(Category(reloaded, surface, "Resources", "Herb"));
        Assert.False(Category(reloaded, surface, "Resources", "Gem"));
        Assert.Equal(initialOther, NativeSignature(reloaded, other));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task VisiblePetLevelChoicesInvertSavedHideFlagsWithoutChangingOtherFilters(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Update(settings =>
        {
            settings.HideLevel1 = true;
            settings.HideLevel2 = false;
            settings.HideLevel3 = true;
            settings.ExclusivePetsOnly = true;
        });
        var view = await fixture.Render();
        var settings = fixture.Settings;
        var minimap = NativeSignature(settings.Current, MapSurface.Minimap);
        var worldmap = NativeSignature(settings.Current, MapSurface.Worldmap);
        var interactive = InteractiveSignature(settings.Current);
        var controls = await fixture.Controls(view, "pet-level-option");
        Assert.Equal(3, controls.Count);
        Assert.False(Assert.Single(controls, control => control.Label == PetLevelLabel(language, 1)).Checked);
        Assert.True(Assert.Single(controls, control => control.Label == PetLevelLabel(language, 2)).Checked);
        Assert.False(Assert.Single(controls, control => control.Label == PetLevelLabel(language, 3)).Checked);
        Assert.Equal(30, (await fixture.Controls(view)).Count); // the three map-surface columns stay independent
        Assert.False(settings.Current.HidesLevel(0));
        await fixture.Export(await fixture.Html(), "levels-mixed");

        for (var level = 1; level <= 3; level++)
        {
            var otherLevels = Enumerable.Range(1, 3).Where(other => other != level)
                .ToDictionary(other => other, other => settings.Current.HidesLevel(other));
            foreach (var visible in new[] { true, false })
            {
                await fixture.Change(view, PetLevelLabel(language, level), visible, "pet-level-option");
                var reloaded = new SettingsService().Current;
                Assert.Equal(!visible, settings.Current.HidesLevel(level));
                Assert.Equal(!visible, reloaded.HidesLevel(level));
                Assert.Equal(visible, Assert.Single(await fixture.Controls(view, "pet-level-option"),
                    control => control.Label == PetLevelLabel(language, level)).Checked);
                foreach (var (other, hidden) in otherLevels)
                {
                    Assert.Equal(hidden, settings.Current.HidesLevel(other));
                    Assert.Equal(hidden, reloaded.HidesLevel(other));
                }
                Assert.False(settings.Current.HidesLevel(0));
                Assert.False(reloaded.HidesLevel(0));
                Assert.True(settings.Current.ExclusivePetsOnly);
                Assert.True(reloaded.ExclusivePetsOnly);
                Assert.Equal(minimap, NativeSignature(settings.Current, MapSurface.Minimap));
                Assert.Equal(worldmap, NativeSignature(settings.Current, MapSurface.Worldmap));
                Assert.Equal(interactive, InteractiveSignature(settings.Current));
                Assert.Equal(minimap, NativeSignature(reloaded, MapSurface.Minimap));
                Assert.Equal(worldmap, NativeSignature(reloaded, MapSurface.Worldmap));
                Assert.Equal(interactive, InteractiveSignature(reloaded));
            }
        }
        Assert.All(await fixture.Controls(view, "pet-level-option"), control => Assert.False(control.Checked));
        await fixture.Export(await fixture.Html(), "levels-none");
        for (var level = 1; level <= 3; level++)
            await fixture.Change(view, PetLevelLabel(language, level), true, "pet-level-option");
        Assert.All(await fixture.Controls(view, "pet-level-option"), control => Assert.True(control.Checked));
        var allShownReloaded = new SettingsService().Current;
        Assert.All(Enumerable.Range(0, 4), level => Assert.False(allShownReloaded.HidesLevel(level)));
        Assert.True(allShownReloaded.ExclusivePetsOnly);
        await fixture.Export(await fixture.Html(), "levels-all");
    }

    private static readonly MapSurface[] Surfaces = [MapSurface.Minimap, MapSurface.Interactive, MapSurface.Worldmap];
    private static string SurfaceLabel(string language, MapSurface surface) => surface switch
    {
        MapSurface.Minimap => "Minimap",
        MapSurface.Worldmap => language == "de" ? "Worldmap" : "World map",
        _ => language == "de" ? "Interaktive Karte" : "Interactive map",
    };
    private static string Label(string language, string name, MapSurface surface) => language == "de"
        ? $"{name} auf {SurfaceLabel(language, surface)} anzeigen" : $"Show {name} on {SurfaceLabel(language, surface)}";
    private static string PetLevelLabel(string language, int level) => language == "de"
        ? level == 3 ? "Stufe 3 (MAX) anzeigen" : $"Stufe {level} anzeigen"
        : level == 3 ? "Show level 3 (MAX) pets" : $"Show level {level} pets";
    private static bool Category(AppSettings settings, MapSurface surface, string group, string category) =>
        MapLayerVisibility.CategoryVisible(settings, "altgard", surface, group, category);
    private static bool Kind(AppSettings settings, MapSurface surface, string kind) =>
        MapLayerVisibility.KindVisible(settings, "altgard", surface, "Resources", "Gem", kind);
    private static string NativeSignature(AppSettings settings, MapSurface surface) => string.Join("|",
        new[] { Category(settings, surface, "Pets", "Cogni"), Category(settings, surface, "Pets", "Fera"),
            Category(settings, surface, "Resources", "Gem"), Category(settings, surface, "Resources", "Herb"),
            Category(settings, surface, "Locations", "Kibelisk"), Kind(settings, surface, "Ruby"), Kind(settings, surface, "Diamond") });
    private static string InteractiveSignature(AppSettings settings) => string.Join("|", settings.MapCategories["altgard"].Order(StringComparer.Ordinal))
        + ";" + string.Join("|", settings.MapHiddenKinds["altgard"].Order(StringComparer.Ordinal));

    private sealed record Checkbox(string Label, bool Checked, object Callback);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-map-legend-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly string _language;
        private Func<string>? _html;
        public SettingsService Settings { get; }

        public Fixture(string language)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"), Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                File.Delete(path);
            }
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton<BossRushService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IJSRuntime, LegendJs>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Update(settings =>
            {
                settings.UiLanguage = settings.NameLanguage = language;
                settings.LastMap = "altgard";
                settings.ShowPetsInGame = settings.ShowResourcesInGame = true;
                settings.MapCategories = new() { ["altgard"] = ["Pets/Cogni", "Resources/Gem"] };
                settings.MapHiddenKinds = new() { ["altgard"] = ["Resources/Gem/Ruby"] };
                settings.MapMinimapLayers = [];
                settings.MapWorldmapLayers = [];
            });
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<MapView> Render()
        {
            MapView? view = null;
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await _renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(value => view = value) }));
                _html = () => root.ToHtmlString();
                await (Task)typeof(MapView).GetMethod("ShowMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [null])!;
                var open = (HashSet<string>)typeof(MapView).GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                open.Add("Resources");
                await view!.SetParametersAsync(ParameterView.Empty);
            });
            return Assert.IsType<MapView>(view);
        }

        public Task<List<Checkbox>> Controls(MapView view, string labelClass = "legend-choice") =>
            _renderer.Dispatcher.InvokeAsync(() => ReadControls(view, labelClass));

        private static List<Checkbox> ReadControls(MapView view, string labelClass = "legend-choice")
        {
            using var builder = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [builder]);
            var frames = builder.GetFrames();
            var result = new List<Checkbox>();
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "label") continue;
                var isMatchingClass = false;
                for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                    if (frames.Array[j].AttributeName == "class" && frames.Array[j].AttributeValue is string classes)
                        isMatchingClass = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(labelClass, StringComparer.Ordinal);
                if (!isMatchingClass) continue;
                var end = i + frames.Array[i].ElementSubtreeLength;
                var input = Enumerable.Range(i + 1, end - i - 1).Single(index =>
                    frames.Array[index].FrameType == RenderTreeFrameType.Element && frames.Array[index].ElementName == "input");
                string? label = null;
                string? type = null;
                object? callback = null;
                var isChecked = false;
                for (var j = input + 1; j < end && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == "type") type = attribute.AttributeValue as string;
                    if (attribute.AttributeName == "aria-label") label = attribute.AttributeValue as string;
                    if (attribute.AttributeName == "checked") isChecked = attribute.AttributeValue is true;
                    if (attribute.AttributeName == "onchange") callback = attribute.AttributeValue;
                }
                Assert.Equal("checkbox", type);
                Assert.NotNull(label);
                result.Add(new(label, isChecked, Assert.IsAssignableFrom<object>(callback)));
            }
            return result;
        }

        public Task Change(MapView view, string label, bool value, string labelClass = "legend-choice") => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var callback = Assert.Single(ReadControls(view, labelClass), control => control.Label == label).Callback;
            var args = new ChangeEventArgs { Value = value };
            switch (callback)
            {
                case EventCallback<ChangeEventArgs> typed: await typed.InvokeAsync(args); break;
                case EventCallback untyped: await untyped.InvokeAsync(args); break;
                case MulticastDelegate handler:
                    if (handler.DynamicInvoke(args) is Task task) await task;
                    break;
                default: throw new InvalidOperationException("Unexpected Razor onchange callback type: " + callback.GetType().Name);
            }
            await view.SetParametersAsync(ParameterView.Empty);
        });

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_html!()));

        public async Task Export(string html, string? suffix = null)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_MAP_LEGEND_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"map-legend-{_language}{(suffix is null ? "" : "-" + suffix)}.html"), "<!doctype html><html lang=\"" + _language
                + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\">"
                + "<style>html,body{height:100%;margin:0}.mapview{height:100vh}"
                + "#map{background:linear-gradient(135deg,#1b302c,#253c32)}</style></head><body>" + html + "</body></html>");
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class CaptureMapView : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class LegendJs : IJSRuntime
    {
        private const string Map = """
            {"groups":[
              {"name":"Pets","categories":[
                {"index":0,"name":"Cogni","count":2,"hidden":false,"color":"#f97316"},
                {"index":1,"name":"Fera","count":1,"hidden":false,"color":"#eab308"}]},
              {"name":"Resources","categories":[
                {"index":2,"name":"Gem","de":"Edelstein","count":3,"hidden":false,"color":"#60a5fa","kinds":[
                  {"name":"Ruby","de":"Rubin","count":1},{"name":"Diamond","de":"Diamant","count":2}]},
                {"index":3,"name":"Herb","de":"Kraut","count":1,"hidden":true,"color":"#4ade80"}]},
              {"name":"Locations","categories":[
                {"index":4,"name":"Kibelisk","count":1,"hidden":true,"color":"#a78bfa"}]}],"regions":1,"markers":8}
            """;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => typeof(TValue) == typeof(string)
            ? ValueTask.FromResult((TValue)(object)(identifier == "soulcrestMap.show" ? Map : "[]"))
            : ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
#pragma warning restore BL0006
