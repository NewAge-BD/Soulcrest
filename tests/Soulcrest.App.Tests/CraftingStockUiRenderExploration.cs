using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in, offline HTML of the production components; all stock/price inputs are anonymous examples.</summary>
[Collection("Settings file")]
public sealed class CraftingStockUiRenderExploration
{
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const int Ore = 610620013;
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Lazy<CraftingCalculatorSnapshot> Example = new(CreateStockExample);

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionStockCalculatorAndResourceDialog(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var directory = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_STOCK_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-12-crafting-progress"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest")),
            Path.GetFullPath(AppPaths.DataDirectory));
        var mapDirectory = TestPrerequisites.MapDataDirectory;
        Assert.NotNull(mapDirectory);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(mapDirectory, "manifest.json")));
        var maps = manifest.RootElement.GetProperty("maps").EnumerateArray().Select(row => new MapInfo(
            row.GetProperty("id").GetString()!, row.GetProperty("label").GetString()!,
            row.TryGetProperty("group", out var group) ? group.GetString() ?? "" : "")).ToArray();
        var sources = new ResourceMapSources(mapDirectory, maps);
        Assert.NotEmpty(sources.ForItem(Ore));
        var settings = new SettingsService();
        settings.Current.UiLanguage = language; settings.Current.NameLanguage = language;
        settings.Current.ResourceMapDontShowAgain = false;
        settings.Current.ResourceMapDefaultSelection = ResourceMapSelection.Default;
        settings.Current.MapCategories = []; settings.Current.MapHiddenKinds = [];
        settings.Current.MapMinimapLayers = []; settings.Current.MapWorldmapLayers = [];
        using var resources = new ResourceMapService(settings, sources, new UiState(), () => null);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var provider = new ServiceCollection().AddLogging().AddSingleton(resources)
                .AddSingleton<IJSRuntime>(new RejectingJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var state = Example.Value;
            Assert.Equal(CraftingPlanStatus.Complete, state.Result!.Status);
            var stock = await Render<CraftingStockPanel>(new() { [nameof(CraftingStockPanel.State)] = state.Stock });
            var calculator = await Render<CraftingCalculatorView>(new()
            {
                [nameof(CraftingCalculatorView.Catalog)] = CraftingCatalog.Default,
                [nameof(CraftingCalculatorView.State)] = state
            });
            Assert.Contains("data-items-in-stock", stock);
            Assert.Contains("data-stock-complete=\"false\"", stock);
            Assert.Contains("data-stock-scope=\"CharacterStorage\" data-stock-completeness=\"Unknown\"", stock);
            Assert.Contains("data-stock-scope=\"ServerStorage\" data-stock-completeness=\"Unknown\"", stock);
            Assert.Contains("data-show-resource-map", calculator);
            Assert.Contains("data-stock-origin=\"Automatic\"", calculator);
            Assert.DoesNotContain(Profile, stock + calculator);
            var body = "<div class=\"craft-live-wrap\">" + stock + calculator + "</div>";
            Directory.CreateDirectory(directory);
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css",
                "transfer-crafting.css", "crafting-dependency-map.css", "resource-map.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            await Save("crafting-stock-calculator-" + language, body);
            resources.Open(Ore, CraftingCalculatorPresentation.Name(CraftingCatalog.Default.FindItem(Ore)));
            Assert.NotNull(resources.Pending);
            var dialog = await Render<ResourceMapDialog>([]);
            Assert.Contains("role=\"dialog\"", dialog); Assert.Contains("resource-map-world", dialog);
            Assert.DoesNotContain(Profile, dialog);
            await Save("resource-map-dialog-" + language, body + dialog);

            Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
            Task Save(string filename, string content) => File.WriteAllTextAsync(Path.Combine(directory, filename + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<meta name=\"render-source\" content=\"Production HtmlRenderer; anonymous example inventory and manual prices; no game recording\">"
                + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri) + "\">"
                + "<style>" + css + "\nhtml,body,#app{height:100%;width:100%;margin:0;overflow:hidden}</style>"
                + "</head><body><div id=\"app\">" + content + "</div>"
                + "<script>document.querySelector('[data-items-in-stock]').open=true;</script></body></html>");
        }
        finally { UiText.Language = previous; }
    }

    private static CraftingCalculatorSnapshot CreateStockExample()
    {
        var state = CraftingDependencyMapViewTests.CreateMaceExample();
        var owned = state.OwnedItems.Add(Ore, 12);
        var observed = owned.Where(row => row.Key is not (610530010 or 610511013)).ToImmutableDictionary(row => row.Key,
            row => new CraftingStockItem(row.Key, row.Value, StockFreshness.Fresh, StockCompleteness.Partial, StockOrigin.Automatic) { ObservedAt = At });
        var effective = observed
            .Add(610530010, new(610530010, owned[610530010], StockFreshness.Historical, StockCompleteness.Unknown, StockOrigin.LegacyManual) { ObservedAt = At.AddDays(-1) })
            .Add(610511013, new(610511013, owned[610511013], StockFreshness.Unknown, StockCompleteness.Unknown, StockOrigin.ManualOverride));
        return state with
        {
            ProfileId = Profile, OwnedItems = owned,
            Stock = new()
            {
                ProfileId = Profile, EffectiveItems = effective, ObservedItems = observed,
                ObservedMinimumItems = observed.ToImmutableDictionary(row => row.Key, row => row.Value.Quantity),
                ManualItems = ImmutableDictionary<int, long>.Empty.Add(610530010, owned[610530010]).Add(610511013, owned[610511013]),
                IsFresh = true, IsComplete = false, ObservedAt = At,
                Scopes = [new(StockScope.Bag, StockCompleteness.Partial), new(StockScope.CharacterStorage, StockCompleteness.Unknown),
                    new(StockScope.ServerStorage, StockCompleteness.Unknown)]
            }
        };
    }

    private sealed class RejectingJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static export must not invoke browser or game integration.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
