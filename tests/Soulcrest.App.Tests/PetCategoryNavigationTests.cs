using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetCategoryNavigationTests
{
    [Theory]
    [InlineData("cogni", "cogni")]
    [InlineData("FERA", "fera")]
    [InlineData(" natura ", "natura")]
    [InlineData("varian", "varian")]
    public void ValidCategoriesNavigateInRamAndAreConsumedExactlyOnce(string requested, string expected)
    {
        var ui = new UiState(); ui.ShowCombatScoreSection("pets");
        var changes = 0; ui.Changed += () => changes++;
        ui.ShowPetCategory(requested);
        Assert.Equal("pets", ui.Tab); Assert.Equal("pets", ui.CombatScoreSection);
        Assert.Equal(1, changes); Assert.Equal(expected, ui.TakePendingPetCategory()); Assert.Null(ui.TakePendingPetCategory());
    }

    [Theory]
    [InlineData("")] [InlineData("special")] [InlineData("unknown")] [InlineData("Might")] [InlineData("future")] [InlineData(null)]
    public void InvalidCategoriesDoNotNavigateRaiseEventsOrReplaceAnEarlierRequest(string? invalid)
    {
        var ui = new UiState(); ui.ShowPetCategory("cogni"); ui.Navigate("map");
        var changes = 0; ui.Changed += () => changes++;
        ui.ShowPetCategory(invalid!);
        Assert.Equal("map", ui.Tab); Assert.Equal(0, changes); Assert.Equal("cogni", ui.TakePendingPetCategory());
    }

    [Fact]
    public void TheLastExplicitCategoryRequestWinsWithoutLeavingASecondPendingRequest()
    {
        var ui = new UiState(); ui.ShowPetCategory("cogni"); ui.ShowPetCategory("varian");
        Assert.Equal("varian", ui.TakePendingPetCategory()); Assert.Null(ui.TakePendingPetCategory());
    }

    [Theory]
    [InlineData("STR", "cogni")]
    [InlineData("DEX", "fera")]
    [InlineData("AGI", "natura")]
    [InlineData("CON", "varian")]
    public async Task OwnedStatCategoryTargetsOpenTheMatchingMainPetCategoryOnMount(string statId, string genus)
    {
        Assert.Contains(CombatPetOwnedEffectCatalog.Effects(genus, 3)!, effect => effect.StatId == statId);
        await using var f = new Fixture();
        f.Ui.ShowPetCategory(genus);
        var html = await f.Render();
        AssertCategory(html, f, genus);
        Assert.Null(f.Ui.TakePendingPetCategory());
        f.AssertUnchanged();
    }

    [Fact]
    public async Task AnAlreadyOpenPetViewConsumesANewRequestAndClearsConflictingFiltersAndOldSelection()
    {
        await using var f = new Fixture(); await f.Render();
        await f.SetFilters(new() { ["_query"] = "no matching pet", ["_map"] = "old-map", ["_source"] = "dungeon",
            ["_status"] = "done", ["_level"] = "max", ["_genus"] = "varian", ["_selectedId"] = f.Pet("varian").Id });
        Assert.Empty(CardIds(await f.Html()));
        await f.Navigate("cogni");
        AssertCategory(await f.Html(), f, "cogni");
        foreach (var field in new[] { "_query", "_map", "_source", "_status", "_level" }) Assert.Equal("", f.State(field));
        Assert.Null(f.State("_selectedId")); Assert.Null(f.Ui.TakePendingPetCategory());
        f.AssertUnchanged();
    }

    [Fact]
    public async Task OrdinaryNavigationAndInvalidRequestsDoNotReplayTheConsumedCategoryOrClearUserFilters()
    {
        await using var f = new Fixture(); await f.Render(); await f.Navigate("fera");
        await f.SetFilters(new() { ["_query"] = "keep this search", ["_genus"] = "varian" });
        await f.OnRenderer(() => f.Ui.Navigate("pets"));
        await f.OnRenderer(() => f.Ui.ShowPetCategory("special"));
        Assert.Equal("keep this search", f.State("_query")); Assert.Equal("varian", f.State("_genus"));
        Assert.Contains("data-pet-genus=\"varian\"", await f.Html());
        Assert.Null(f.Ui.TakePendingPetCategory()); f.AssertUnchanged();
    }

    [Fact]
    public async Task DisposedViewsCannotConsumeARequestIntendedForTheirReplacement()
    {
        await using var f = new Fixture(); await f.Render();
        await f.OnRenderer(() => f.View!.Dispose());
        f.Ui.ShowPetCategory("natura");
        Assert.Equal("natura", f.Ui.TakePendingPetCategory());
        f.AssertUnchanged();
    }

    private static string[] CardIds(string html) => Regex.Matches(html, "data-pet-id=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();
    private static void AssertCategory(string html, Fixture fixture, string genus)
    {
        Assert.Equal("pets", fixture.Ui.Tab);
        Assert.Contains("data-pet-genus=\"" + genus + "\"", html);
        Assert.Contains("pets-g-" + genus + " is-active", html);
        Assert.Equal(new[] { fixture.Pet(genus).Id }, CardIds(html));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-category-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups;
        private readonly Dictionary<string, byte[]?> _before;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private readonly IReadOnlyList<PetDefinition> _pets;
        public UiState Ui { get; } = new();
        public PetsView? View { get; private set; }
        public PetDefinition Pet(string genus) => _pets.Single(pet => pet.Genus == genus);
        public Fixture()
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[]}""");
            _pets = new[] { "cogni", "fera", "natura", "varian" }.Select(genus =>
            {
                var source = PetIdTable.All.Values.First(pet => pet.Genus.Equals(genus, StringComparison.OrdinalIgnoreCase));
                return new PetDefinition { Id = source.CatalogId ?? PetCatalog.Slug(source.En), En = source.En, Genus = genus,
                    NumericId = source.Id.ToString(CultureInfo.InvariantCulture) };
            }).ToArray();
            _backups = new[] { AppPaths.SettingsFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile }
                .ToDictionary(path => path, Bytes);
            JsonFile.Save(AppPaths.LearnedPetsFile, new List<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var path = Path.Combine(_folder, "progress.json");
            JsonFile.Save(path, new ProgressProfile { SchemaVersion = 3 });
            var progress = new ProgressService(path, new PetCatalog(_pets));
            var settings = new SettingsService(); settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = "en";
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(progress); services.AddSingleton(settings);
            services.AddSingleton(new NetworkLootService(progress)); services.AddSingleton(Ui);
            services.AddSingleton(new PetDungeonMapService(progress, [], _ => false));
            _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
            _before = _backups.Keys.Append(path).ToDictionary(file => file, Bytes);
        }
        private static byte[]? Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
        public void AssertUnchanged()
        {
            foreach (var (path, bytes) in _before) Assert.Equal(bytes, Bytes(path));
        }
        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CaptureView.Capture)] = (Action<PetsView>)(view => View = view) })));
            return await Html();
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public object? State(string field) => typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View);
        public Task SetFilters(Dictionary<string, string> values) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            foreach (var (field, value) in values)
                typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(View, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, null);
        });
        public Task Navigate(string genus) => OnRenderer(() => Ui.ShowPetCategory(genus));
        public Task OnRenderer(Action action) => _renderer.Dispatcher.InvokeAsync(action);
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData); UiText.Language = _previousLanguage;
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_folder, recursive: true);
        }
    }
    private sealed class CaptureView : ComponentBase
    {
        [Parameter] public Action<PetsView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PetsView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((PetsView)value));
            builder.CloseComponent();
        }
    }
}
