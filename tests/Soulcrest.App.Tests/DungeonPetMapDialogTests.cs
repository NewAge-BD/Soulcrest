using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DungeonPetMapDialogTests
{
    [Theory]
    [InlineData("de", "Ausgewähltes Pet", "Weitere Pets", "Auf Global verfügbar")]
    [InlineData("en", "Selected pet", "Other pets", "Available on Global")]
    public async Task ReleasedMapWithMostSelectedPinsIsPreferredAndOriginalVariantsStayVisible(
        string language, string selectedLabel, string otherLabel, string availableLabel)
    {
        await using var fixture = new Fixture(language);
        var selection = Selection();
        var html = await fixture.Render(selection);
        Assert.Contains("data-dungeon-pet-dialog=\"synthetic-pet\"", html);
        Assert.Contains("data-dungeon-map-scene=\"available-dense\"", html);
        Assert.Contains("Lv.45", html);
        Assert.Contains("data-dungeon-map-availability=\"available\"", html);
        Assert.Contains(selectedLabel, html);
        Assert.Contains(otherLabel, html);
        Assert.Contains(availableLabel, html);
        Assert.Contains("aion2.gaming.tools", html);
        Assert.DoesNotContain("900001", html); // Native IDs are keys, not the user's map labels.
        Assert.Empty(fixture.Js.Calls);
        await fixture.Initialize();
        Assert.Equal(new[] { "soulcrestDungeonMaps.openDialog", "soulcrestDungeonMaps.create" }, fixture.Js.Calls.Select(call => call.Identifier));
        var scene = Assert.IsType<DungeonPetMapScene>(fixture.Js.Calls[1].Args[1]);
        Assert.Equal("available-dense", scene.Key);
        Assert.Equal(2, scene.Pins.Count(pin => pin.Selected));
        Assert.Contains(scene.Pins, pin => !pin.Selected);
        await fixture.View.OnDungeonMapReady(scene.Key);
        Assert.Contains("aria-busy=\"false\"", await fixture.Html());
        Assert.Equal(new[] { DungeonPetMapDisplayState.Loading, DungeonPetMapDisplayState.Ready }, fixture.DisplayStates);
    }

    [Fact]
    public async Task SceneChangesUseOnlyTheDedicatedControllerAndIgnoreOldImageCallbacks()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render(Selection());
        await fixture.Initialize();
        await fixture.Change("upcoming");
        await fixture.Initialize();
        var call = Assert.Single(fixture.Js.Calls, call => call.Identifier == "soulcrestDungeonMaps.setScene");
        Assert.Equal("upcoming", Assert.IsType<DungeonPetMapScene>(call.Args[1]).Key);
        await fixture.View.OnDungeonMapReady("available-dense");
        await fixture.View.OnDungeonMapError("available-dense", "private-error-must-not-leak");
        Assert.Equal(new[] { DungeonPetMapDisplayState.Loading, DungeonPetMapDisplayState.Loading }, fixture.DisplayStates);
        var html = await fixture.Html();
        Assert.Contains("data-dungeon-map-scene=\"upcoming\"", html);
        Assert.Contains("data-dungeon-map-availability=\"upcoming\"", html);
        Assert.Contains("aria-busy=\"true\"", html);
        Assert.DoesNotContain("private-error", html);
        await fixture.View.OnDungeonMapReady("upcoming");
        await fixture.Invoke("FitSelected");
        Assert.Contains(fixture.Js.Calls, call => call.Identifier == "soulcrestDungeonMaps.fitSelected");
        await fixture.Change("not-in-this-pet");
        Assert.Contains("data-dungeon-map-scene=\"upcoming\"", await fixture.Html());
        Assert.All(fixture.Js.Calls, call => Assert.StartsWith("soulcrestDungeonMaps.", call.Identifier));
    }

    [Fact]
    public async Task CurrentImageErrorsAreLocalizedAndEscapeCloseIsDeliveredOnce()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render(Selection());
        await fixture.Initialize();
        await fixture.View.OnDungeonMapError("available-dense", "private-path-192.0.2.5");
        var html = await fixture.Html();
        Assert.Contains("The dungeon map could not be loaded.", html);
        Assert.DoesNotContain("private-path", html);
        Assert.Contains("role=\"alert\"", html);
        await fixture.View.OnDungeonDialogClosed();
        await fixture.View.OnDungeonDialogClosed();
        await fixture.Change("upcoming");
        Assert.Equal(1, fixture.CloseCalls);
        Assert.Contains(DungeonPetMapDisplayState.Error, fixture.DisplayStates);
        Assert.Equal(DungeonPetMapDisplayState.Closed, fixture.DisplayStates[^1]);
        Assert.Contains("data-dungeon-map-scene=\"available-dense\"", await fixture.Html());
        await fixture.View.DisposeAsync();
        var count = fixture.Js.Calls.Count;
        await fixture.View.DisposeAsync();
        Assert.Equal(count, fixture.Js.Calls.Count);
        Assert.Equal("soulcrestDungeonMaps.dispose", fixture.Js.Calls[^1].Identifier);
        await fixture.View.OnDungeonMapReady("available-dense");
        Assert.Equal(1, fixture.CloseCalls);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RealCatalogueScenesRenderWithoutStartingTrackingOrChangingProgress(string language)
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-dungeon-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identity = PetIdTable.Find(1028)!;
            var pet = new PetDefinition { Id = identity.CatalogId!, En = identity.En, Genus = identity.Genus.ToLowerInvariant(), NumericId = "1028" };
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DUNGEON_MAP_UI_PREVIEW")))
            {
                var real = PetCatalog.Load(Path.Combine(Repository(), "imports", "generated", "mapdata", "pets.json")).Find(pet.Id);
                pet = pet with { Icon = real?.Icon, De = real?.De };
            }
            var path = Path.Combine(directory, "progress.json");
            var progress = new ProgressService(path, new PetCatalog([pet]));
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var maps = new PetDungeonMapService(progress, PetDungeonMapCatalog.All, _ => true);
            await using var fixture = new Fixture(language);
            var selection = maps.For(pet, language);
            Assert.NotEmpty(selection.Scenes);
            var html = await fixture.Render(selection);
            Assert.Contains(WebUtility.HtmlDecode(pet.DisplayName(language)), html);
            await fixture.Initialize();
            var selected = Assert.IsType<DungeonPetMapScene>(fixture.Js.Calls.Single(call => call.Identifier == "soulcrestDungeonMaps.create").Args[1]);
            Assert.Contains(selected.Pins, pin => pin.Selected && pin.PetNumber == 1028);
            await fixture.View.OnDungeonMapReady(selected.Key);
            await fixture.Export(selection, selected, "dungeon-pet-map");
            var other = selection.Scenes.FirstOrDefault(scene => scene.Key != selected.Key);
            if (other is not null)
            {
                await fixture.Change(other.Key);
                await fixture.Initialize();
                await fixture.View.OnDungeonMapReady(other.Key);
                await fixture.Export(selection, other, "dungeon-pet-map-variant");
            }
            Assert.Equal(before, File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static DungeonPetMapSelection Selection()
    {
        DungeonPetMapPin[] pins = [new("selected-a", 1028, "Synthetic Pet", "Source A", 100, 150, true),
            new("selected-b", 1028, "Synthetic Pet", "Source B", 400, 250, true),
            new("other", 1009, "Other Pet", "Source C", 700, 400, false)];
        return new("synthetic-pet", 1028, "Synthetic Pet", null,
            [new("upcoming", "Future Dungeon", "Lv.45", 900001, PetGlobalAvailability.Upcoming, "2026-11-05", "/fixture.png", 800, 500, pins),
             new("available-sparse", "First Dungeon", null, 900002, PetGlobalAvailability.Available, "2026-11-05", "/fixture.png", 800, 500, [pins[0]]),
             new("available-dense", "Second Dungeon", "Lv.20", 900003, PetGlobalAvailability.Available, "2026-11-05", "/fixture.png", 800, 500, pins)], null);
    }

    private static string Repository()
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "src", "Soulcrest.App", "wwwroot", "index.html"))) return folder.FullName;
        throw new DirectoryNotFoundException("Offline map preview requires repository assets.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        public RecordingJs Js { get; } = new();
        public DungeonPetMapDialog View { get; private set; } = default!;
        public int CloseCalls { get; private set; }
        public List<DungeonPetMapDisplayState> DisplayStates { get; } = [];

        internal Fixture(string language)
        {
            _language = UiText.Language = language;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IJSRuntime>(Js);
            _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal async Task<string> Render(DungeonPetMapSelection selection)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(Host.Selection)] = selection,
                    [nameof(Host.Capture)] = (Action<DungeonPetMapDialog>)(view => View = view),
                    [nameof(Host.Closed)] = (Action)(() => CloseCalls++),
                    [nameof(Host.DisplayChanged)] = (Action<DungeonPetMapDisplayState>)(state => DisplayStates.Add(state)) })));
            return await Html();
        }
        internal Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        internal Task Initialize() => Invoke("OnAfterRenderAsync", true);
        internal Task Change(string key) => Invoke("ChangeScene", new ChangeEventArgs { Value = key });
        internal Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(DungeonPetMapDialog).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, null);
        });
        internal async Task Export(DungeonPetMapSelection selection, DungeonPetMapScene scene, string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_DUNGEON_MAP_UI_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            var repository = Repository();
            var assets = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var mapBase = new Uri(Path.Combine(repository, "imports", "generated", "mapdata") + Path.DirectorySeparatorChar).AbsoluteUri;
            string Local(string url) => url.Replace("https://mapdata.soulcrest/", mapBase, StringComparison.Ordinal);
            var raw = Local(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
            var json = JsonSerializer.Serialize(scene with { ImageUrl = Local(scene.ImageUrl) }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string Asset(string path) => new Uri(Path.Combine(assets, path)).AbsoluteUri;
            var css = string.Join("", new[] { "css/app.css", "css/app-themes.css", "lib/leaflet/leaflet.css", "css/dungeon-pet-map.css" }
                .Select(path => "<link rel=\"stylesheet\" href=\"" + Asset(path) + "\">"));
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, state + "-" + _language + ".html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" + css
                + "</head><body>" + raw + "<script src=\"" + Asset("lib/leaflet/leaflet.js") + "\"></script><script src=\"" + Asset("js/dungeon-pet-map.js")
                + "\"></script><script>const scene=" + json + ";const dialog=document.querySelector('[data-dungeon-pet-dialog]');const map=document.querySelector('[data-dungeon-map-scene]');window.soulcrestDungeonMaps.openDialog(dialog.id,'');window.soulcrestDungeonMaps.create(map.id,scene);</script></body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previousLanguage;
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public DungeonPetMapSelection Selection { get; set; } = default!;
        [Parameter] public Action<DungeonPetMapDialog> Capture { get; set; } = default!;
        [Parameter] public Action Closed { get; set; } = default!;
        [Parameter] public Action<DungeonPetMapDisplayState> DisplayChanged { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<DungeonPetMapDialog>(0);
            builder.AddAttribute(1, nameof(DungeonPetMapDialog.Selection), Selection);
            builder.AddAttribute(2, nameof(DungeonPetMapDialog.ReturnFocusId), "source-button");
            builder.AddAttribute(3, nameof(DungeonPetMapDialog.Closed), EventCallback.Factory.Create(this, Closed));
            builder.AddAttribute(4, nameof(DungeonPetMapDialog.DisplayChanged), EventCallback.Factory.Create(this, DisplayChanged));
            builder.AddComponentReferenceCapture(5, view => Capture((DungeonPetMapDialog)view));
            builder.CloseComponent();
        }
    }

    private sealed class RecordingJs : IJSRuntime
    {
        internal List<(string Identifier, object?[] Args)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(typeof(TValue) == typeof(bool) ? (TValue)(object)true : default!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
