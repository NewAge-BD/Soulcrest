using System.Net;
using System.Reflection;
using System.Text.Json;
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
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

#pragma warning disable BL0006
[Collection("Settings file")]
public sealed class MapMonsterNavigationTests
{
    [Theory]
    [InlineData(false, "altgard")]
    [InlineData(true, "verteron")]
    public async Task ChoosingOneLocalizedResultFocusesAllItsNamesAndPrefersFreshPlayerMapWithoutChangingFilters(bool trackingEnabled, string expectedMap)
    {
        await using var fixture = new Fixture();
        var view = await fixture.Render();
        var tracking = fixture.Provider.GetRequiredService<MapTrackingService>();
        var at = DateTimeOffset.UtcNow;
        Property(tracking, nameof(MapTrackingService.Running), trackingEnabled);
        Property(tracking, nameof(MapTrackingService.Found), true);
        Property(tracking, nameof(MapTrackingService.Position), new PlayerPosition("verteron", 100, 200, at));
        Property(tracking, nameof(MapTrackingService.DetectedMap), new DetectedMapContext("verteron", false, at));
        var filters = fixture.FilterSignature();
        var progressBefore = File.ReadAllBytes(fixture.ProgressPath);
        fixture.Js.Calls.Clear();
        await fixture.Search(view, "Zwilling");
        var html = await fixture.Html();
        Assert.Contains("Zwillingsmonster", html);
        Assert.DoesNotContain("Twin East", html);
        await fixture.ChooseResult(view);

        var focus = Assert.Single(fixture.Js.Calls, call => call.Name == "soulcrestMap.focusMonster");
        Assert.Equal(new[] { "Twin East", "Twin West" }, Assert.IsAssignableFrom<IEnumerable<string>>(Assert.Single(focus.Arguments)));
        var showIndex = fixture.Js.Calls.FindIndex(call => call.Name == "soulcrestMap.show");
        var focusIndex = fixture.Js.Calls.FindIndex(call => call.Name == "soulcrestMap.focusMonster");
        if (trackingEnabled)
        {
            Assert.True(showIndex >= 0 && focusIndex > showIndex);
            Assert.Equal(expectedMap, fixture.Js.Calls[showIndex].Arguments[0]);
        }
        else Assert.Equal(-1, showIndex);
        Assert.Equal(expectedMap, fixture.Settings.Current.LastMap);
        Assert.Equal(filters, fixture.FilterSignature());
        Assert.Equal(progressBefore, File.ReadAllBytes(fixture.ProgressPath));
        Assert.Equal(trackingEnabled, tracking.Running);
        Assert.True(tracking.Found);
        Assert.Equal("verteron", tracking.Position!.MapId);
        Assert.Equal(0, fixture.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
        Assert.DoesNotContain(fixture.Js.Calls, call => call.Name is "soulcrestMap.flyToMarker" or "soulcrestMap.flyToTarget");
        html = await fixture.Html();
        Assert.Contains("data-monster-search=\"Twin East\"", html);
        Assert.DoesNotContain("class=\"searchresults\"", html);
        await fixture.Export(html, "selected-" + expectedMap);
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            view.OnNameFocused("Stale NPC callback", 999);
            view.OnPetFocused("fixture-pet", 999, 999);
        });
        Assert.Equal(html, await fixture.Html()); // late marker callbacks cannot replace the selected monster card

        fixture.Js.Calls.Clear();
        // This is the actual EventCallback<string> passed from MapView to the card's map buttons.
        await fixture.ChangeMonsterMap(view, "abyss");
        showIndex = fixture.Js.Calls.FindIndex(call => call.Name == "soulcrestMap.show");
        var petIndex = fixture.Js.Calls.FindIndex(call => call.Name == "soulcrestMap.focusPet");
        Assert.True(showIndex >= 0 && petIndex > showIndex);
        Assert.Equal("abyss", fixture.Js.Calls[showIndex].Arguments[0]);
        Assert.Equal(new[] { "fixture-pet", "fixture-legacy" }, Assert.IsAssignableFrom<IEnumerable<string>>(fixture.Js.Calls[petIndex].Arguments[0]));
        Assert.Equal(true, fixture.Js.Calls[petIndex].Arguments[1]);
        Assert.Equal("abyss", fixture.Settings.Current.LastMap);
        Assert.Equal(filters, fixture.FilterSignature());
        Assert.Equal(progressBefore, File.ReadAllBytes(fixture.ProgressPath));
        Assert.Equal(trackingEnabled, tracking.Running);
        html = await fixture.Html();
        Assert.Contains("data-monster-search=\"Twin East\"", html);
        Assert.Contains("2 Pet-Spawns · 3 Quellmonster auf Abyss", html);
        await fixture.Export(html, "variant-abyss");
        fixture.Js.Calls.Clear();
        await fixture.ChangeMonsterMap(view, "unrelated");
        Assert.Empty(fixture.Js.Calls);
        Assert.Equal("abyss", fixture.Settings.Current.LastMap);
    }

    [Fact]
    public async Task SameNamedNpcKeepsItsOwnResultWhileAnExplicitAliasPetDuplicateIsRemoved()
    {
        await using var fixture = new Fixture();
        var view = await fixture.Render();
        fixture.Js.SearchResponse = """[{"I":0,"Name":"Zwillingsmonster","Cat":"Normal","Monster":true},{"I":42,"Name":"Zwillingsmonster","Cat":"Quest NPC","Group":"NPCs"},{"I":43,"Name":"Zwillingsmonster","Cat":"Fera","Group":"Pets","PetId":"fixture-legacy"}]""";
        await fixture.Search(view, "Zwilling");
        fixture.Js.Calls.Clear();
        await fixture.ChooseResult(view, resultIndex: 1, expectedCount: 2);
        var fly = Assert.Single(fixture.Js.Calls, call => call.Name == "soulcrestMap.flyToMarker");
        Assert.Equal(42, Assert.Single(fly.Arguments));
        Assert.DoesNotContain(fixture.Js.Calls, call => call.Name is "soulcrestMap.focusMonster" or "soulcrestMap.focusPet");
        Assert.DoesNotContain("data-monster-search", await fixture.Html());
    }

    private static void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static Task Call(MapView view, string name) => (Task)typeof(MapView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, name == "ShowMap" ? [null] : null)!;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-monster-navigation-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private Func<string>? _html;
        public string ProgressPath => Path.Combine(_folder, "progress.json");
        public ServiceProvider Provider { get; }
        public HtmlRenderer Renderer { get; }
        public RecordingJs Js { get; } = new();
        public SettingsService Settings => Provider.GetRequiredService<SettingsService>();

        public Fixture()
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"},{"id":"abyss","label":"Abyss","group":"Abyss"}]}""");
            WriteMap("altgard", "Twin East");
            WriteMap("verteron", "Twin West");
            WriteMap("abyss", "Other Variant");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"), Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                File.Delete(path);
            }
            File.WriteAllText(ProgressPath, """{"SchemaVersion":3,"pets":{}}""");
            var catalog = new PetCatalog([new PetDefinition { Id = "fixture-pet", En = "Fixture Pet", De = "Beispiel-Pet", Genus = "fera",
                Monsters = new() { ["altgard"] = 1, ["verteron"] = 1, ["abyss"] = 1 } }],
                new Dictionary<string, string> { ["fixture-legacy"] = "fixture-pet" });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(new ProgressService(ProgressPath, catalog));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IJSRuntime>(Js);
            Provider = services.BuildServiceProvider();
            Settings.Update(settings =>
            {
                settings.UiLanguage = settings.NameLanguage = UiText.Language = "de";
                settings.LastMap = "altgard";
                settings.FollowPlayer = false;
                settings.ProgressionEnabled = false;
                settings.HideLevel1 = true;
                settings.HideLevel2 = false;
                settings.HideLevel3 = true;
                settings.ExclusivePetsOnly = true;
                settings.MapCategories = new() { ["altgard"] = ["Pets/Fera"], ["verteron"] = ["Monsters/Normal"] };
                settings.MapHiddenKinds = new() { ["altgard"] = ["Resources/Gem/Ruby"] };
                settings.MapMinimapLayers = new() { ["altgard"] = new(["Pets/Fera"], []) };
                settings.MapWorldmapLayers = new() { ["altgard"] = new([], ["Resources/Gem/Ruby"]) };
            });
            Renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        private void WriteMap(string map, string name)
        {
            var folder = Path.Combine(_folder, map);
            Directory.CreateDirectory(folder);
            var data = JsonSerializer.Serialize(new { categories = new[] { new { group = "Monsters", name = "Normal", icon = 0 } },
                icons = new[] { "icons/fixture.png" }, markers = new object?[][] { [0, 100, 200, name, name == "Other Variant" ? "Andere Variante" : "Zwillingsmonster", 0, map == "abyss" ? "fixture-legacy" : "fixture-pet"] } });
            File.WriteAllText(Path.Combine(folder, "data.js"), "window.SoulcrestMaps[\"" + map + "\"] = " + data + ";");
        }

        public string FilterSignature() => JsonSerializer.Serialize(new { Settings.Current.FollowPlayer, Settings.Current.ProgressionEnabled,
            Settings.Current.HideLevel1, Settings.Current.HideLevel2, Settings.Current.HideLevel3, Settings.Current.ExclusivePetsOnly,
            Settings.Current.MapCategories, Settings.Current.MapHiddenKinds, Settings.Current.MapMinimapLayers, Settings.Current.MapWorldmapLayers });

        public async Task<MapView> Render()
        {
            MapView? view = null;
            await Renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await Renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                    { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(value => view = value) }));
                _html = root.ToHtmlString;
                await Call(view!, "ShowMap");
            });
            return Assert.IsType<MapView>(view);
        }
        public Task Search(MapView view, string query) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(MapView).GetField("_query", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, query);
            await Call(view, "Search");
            await view.SetParametersAsync(ParameterView.Empty);
        });
        public Task ChooseResult(MapView view, int resultIndex = 0, int expectedCount = 1) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [tree]);
            var frames = tree.GetFrames();
            var container = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "class" && Equals(frames.Array[i].AttributeValue, "searchresults")) - 1;
            var end = container + frames.Array[container].ElementSubtreeLength;
            var callbacks = Enumerable.Range(container, end - container).Where(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "onclick").Select(i => frames.Array[i].AttributeValue).ToArray();
            Assert.Equal(expectedCount, callbacks.Length);
            var callback = callbacks[resultIndex];
            switch (callback)
            {
                case EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed: await typed.InvokeAsync(new()); break;
                case EventCallback untyped: await untyped.InvokeAsync(); break;
                case MulticastDelegate handler: if (handler.DynamicInvoke() is Task task) await task; break;
                default: throw new InvalidOperationException("Unexpected result callback type: " + callback?.GetType().Name);
            }
            await view.SetParametersAsync(ParameterView.Empty);
        });
        public Task ChangeMonsterMap(MapView view, string map) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Component
                && frames.Array[i].ComponentType == typeof(MonsterSearchCard));
            var end = index + frames.Array[index].ComponentSubtreeLength;
            var callback = Assert.Single(Enumerable.Range(index + 1, end - index - 1).Select(i => frames.Array[i]),
                frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == nameof(MonsterSearchCard.MapChanged));
            await Assert.IsType<EventCallback<string>>(callback.AttributeValue).InvokeAsync(map);
            await view.SetParametersAsync(ParameterView.Empty);
        });
        public Task<string> Html() => Renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_html!()));
        public async Task Export(string html, string suffix)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_MONSTER_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "monster-map-de-" + suffix + ".html"), "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\"><link rel=\"stylesheet\" href=\"/css/monster-search.css\"><style>html,body{height:100%;margin:0}.mapview{height:100vh}#map{background:linear-gradient(135deg,#1b302c,#253c32)}</style></head><body>" + html + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups) { if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes); }
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
    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public string SearchResponse { get; set; } = """[{"I":0,"Name":"Zwillingsmonster","Cat":"Normal","Monster":true}]""";
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            if (typeof(TValue) == typeof(string))
            {
                var value = identifier switch { "soulcrestMap.show" => """{"groups":[],"regions":0,"markers":3}""",
                    "soulcrestMap.search" => SearchResponse, "soulcrestMap.focusPet" => """{"pets":2,"monsters":3}""", _ => "[]" };
                return ValueTask.FromResult((TValue)(object)value);
            }
            if (typeof(TValue) == typeof(int) && identifier == "soulcrestMap.focusMonster") return ValueTask.FromResult((TValue)(object)2);
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
#pragma warning restore BL0006
