using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
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

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ProgressionDensityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DensityDefaultsOffAndTheExplicitChoiceSurvivesGoalsKindsAndRestart(bool enabled)
    {
        await using var fixture = new Fixture();
        Assert.False(fixture.Settings.Current.ProgressionPetsHighestDensity);
        Assert.False(new AppSettings().ProgressionPetsHighestDensity);
        await fixture.Render();
        await fixture.ChangeDensity(enabled);
        foreach (var goal in new[] { "5", "30", "105", "closest" })
        {
            await fixture.Invoke("SetProgressionGoal", new ChangeEventArgs { Value = goal });
            Assert.Equal(enabled, fixture.Settings.Current.ProgressionPetsHighestDensity);
            Assert.Equal(enabled, fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression").Arguments[7]);
        }
        await fixture.Invoke("SetProgressionKind", "pets", false);
        Assert.DoesNotContain("id=\"progression-highest-density\"", await fixture.Html());
        await fixture.Invoke("SetProgressionKind", "pets", true);
        Assert.Contains("id=\"progression-highest-density\"", await fixture.Html());
        Assert.Equal(enabled, new SettingsService().Current.ProgressionPetsHighestDensity);
    }

    [Theory]
    [InlineData("de", "Höchste Mob-Dichte zuerst", "200 Metern")]
    [InlineData("en", "Highest Mob Density first", "200 metres")]
    public async Task DensityCheckboxAndHelpRenderInBothLanguagesAndClickPushesTheNewState(string language, string label, string radius)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        var before = await fixture.Html();
        Assert.Contains(label, before);
        Assert.Contains(radius, before);
        Assert.Contains("for=\"progression-highest-density\"", before);
        Assert.DoesNotContain("checked", DensityInput(before));
        await fixture.ChangeDensity(true);
        Assert.Contains("checked", DensityInput(await fixture.Html()));
        Assert.True(fixture.Settings.Current.ProgressionPetsHighestDensity);
        Assert.Equal(true, fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression").Arguments[7]);
        await fixture.ChangeDensity(false);
        Assert.DoesNotContain("checked", DensityInput(await fixture.Html()));
        Assert.Equal(false, fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression").Arguments[7]);
    }

    [Fact]
    public async Task InteropUsesMonsterCountsForTheExactMapAndProgressUpdatesRefreshItsSoulFilter()
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        await fixture.ChangeDensity(true);
        var initial = fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression");
        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>((string)initial.Arguments[8]!)!;
        Assert.Equal(55, counts["a"]); // one symbol, 55 monsters, 999 unrelated-map monsters
        Assert.Equal(0, counts["unknown"]);
        await fixture.Invoke("SwitchMap", "verteron", null);
        var destination = fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression");
        Assert.Equal(3, JsonSerializer.Deserialize<Dictionary<string, int>>((string)destination.Arguments[8]!)!["a"]);
        fixture.MarkReady();
        fixture.Progress.SetSouls("a", 105);
        await fixture.Html(); // drain the component dispatcher after the progress event
        var progressed = fixture.Js.Calls.Last(c => c.Name == "soulcrestMap.setProgression");
        Assert.Equal(105, JsonSerializer.Deserialize<Dictionary<string, int>>((string)progressed.Arguments[2]!)!["a"]);
    }

    [Theory]
    [InlineData(MapSurface.Minimap)]
    [InlineData(MapSurface.Worldmap)]
    public async Task SelectedSouthernPointReachesTheSharedOverlayAndOldMapCallbacksAreIgnored(MapSurface surface)
    {
        await using var fixture = new Fixture();
        await fixture.Render();
        await fixture.ChangeDensity(true);
        await fixture.Callback("altgard", "a", 1000, 1200, "Southern farm");
        var target = Assert.IsType<MapTarget>(fixture.Targets.Progression);
        Assert.Equal("a", target.PetId);
        Assert.Equal(1000, target.X);
        Assert.Equal(1200, target.Y);
        var prepared = RouteOverlayForm.PrepareOverlay("altgard", surface, "en", fixture.Settings.Current,
            fixture.Progress, fixture.Targets, (_, _) => null, (_, _) => null);
        var overlayTarget = Assert.Single(prepared.Targets).Target;
        Assert.Equal(target.X, overlayTarget.X);
        Assert.Equal(target.Y, overlayTarget.Y);
        await fixture.Callback("verteron", "a", 0, 0, "Stale north");
        Assert.Same(target, fixture.Targets.Progression);
        Assert.Equal(0, fixture.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
    }

    private static string DensityInput(string html) => Regex.Match(html, "<input\\b[^>]*id=\"progression-highest-density\"[^>]*>").Value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-density-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private MapView? _view;
        public ServiceProvider Provider { get; }
        public SettingsService Settings => Provider.GetRequiredService<SettingsService>();
        public MapTargetsService Targets => Provider.GetRequiredService<MapTargetsService>();
        public ProgressService Progress => Provider.GetRequiredService<ProgressService>();
        public RecordingJs Js { get; } = new();

        public Fixture(string language = "en")
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile, Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"),
                Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (File.Exists(path)) File.Delete(path);
            }
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian","metersPerPixel":1},{"id":"verteron","label":"Verteron","group":"Elyos","metersPerPixel":2}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(new ProgressService(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "a", En = "Synthetic Pet A", Genus = "fera", Spawns = new() { ["altgard"] = 1 },
                    Monsters = new() { ["altgard"] = 55, ["verteron"] = 3, ["abyss-reshanta-a"] = 999 } },
                new PetDefinition { Id = "unknown", En = "Synthetic unknown", Genus = "fera" }
            ])));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(sp => new TrackerService(sp.GetRequiredService<SettingsService>(), network: null));
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton<IJSRuntime>(Js);
            Provider = services.BuildServiceProvider();
            Settings.Update(s => { s.LootTrackingEnabled = false; s.UiLanguage = s.NameLanguage = language; s.LastMap = "altgard"; s.FollowPlayer = false;
                s.ProgressionEnabled = true; s.ProgressionKinds = ["pets"]; s.ShowRoutesInGame = true; });
            _renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task Render() => _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(view => _view = view) })));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => System.Net.WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task Invoke(string name, params object?[] arguments) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(MapView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, arguments)!;
            await _view!.SetParametersAsync(ParameterView.Empty);
        });
        public void MarkReady() => typeof(MapView).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, true);
        public Task Callback(string map, string pet, double x, double y, string name) => _renderer.Dispatcher.InvokeAsync(() => _view!.OnProgressionTarget(map, pet, x, y, name));

        // Invoke the checkbox's actual rendered event, including its captured component instance.
#pragma warning disable BL0006
        public Task ChangeDensity(bool enabled) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).First(i => frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "id" && Equals(frames.Array[i].AttributeValue, "progression-highest-density"));
            var element = index;
            while (frames.Array[element].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) element--;
            var callback = Enumerable.Range(element + 1, frames.Array[element].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .First(frame => frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeValue;
            var change = new ChangeEventArgs { Value = enabled };
            switch (callback)
            {
                case EventCallback<ChangeEventArgs> typed: await typed.InvokeAsync(change); break;
                case EventCallback untyped: await untyped.InvokeAsync(change); break;
                case MulticastDelegate handler: if (handler.DynamicInvoke(change) is Task task) await task; break;
                default: throw new InvalidOperationException("Unexpected density checkbox callback.");
            }
            await _view!.SetParametersAsync(ParameterView.Empty);
        });
#pragma warning restore BL0006

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
                if (bytes is null) { if (File.Exists(path)) File.Delete(path); } else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
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
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
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
                ? ValueTask.FromResult((TValue)(object)(identifier == "soulcrestMap.show" ? """{"groups":[],"regions":0,"markers":0}""" : "[]"))
                : ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
