using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;
using MapView = Soulcrest.App.Components.CraftingDependencyMap;

namespace Soulcrest.App.Tests;

/// <summary>Production dependency-map rendering; manual scenario coverage never impersonates observed crafts.</summary>
[Collection("Settings file")]
public sealed class CraftingDependencyMapViewTests
{
    private const int RootItemId = 1;
    private const int IntermediateItemId = 2;
    private const int SharedItemId = 3;
    private const int MaceRecipeId = 121063001;
    private const int MaceItemId = 110730019;
    private const int Samples = 100;
    private static readonly Lazy<CraftingCalculatorSnapshot> MaceExample = new(CreateMaceExample);

    [Fact]
    public async Task BatchCraftSelectionDeliversOneCallbackWithExactProducersAndExcludesTheTarget()
    {
        var basis = SharedCatalog();
        var catalog = new CraftingCatalog("synthetic-callbacks", basis.Items, basis.Recipes.Append(Recipe(300, SharedItemId, [])));
        var batches = new List<ImmutableArray<CraftingSourceSelection>>();
        var singles = 0;
        await using var fixture = new Fixture("de");
        var captured = await fixture.Capture(new()
        {
            [nameof(MapView.Catalog)] = catalog, [nameof(MapView.State)] = SharedState(catalog, 0),
            [nameof(MapView.SourcesChanged)] = EventCallback.Factory.Create<ImmutableArray<CraftingSourceSelection>>(this, value => batches.Add(value)),
            [nameof(MapView.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, _ => singles++)
        });
        await fixture.Invoke(captured.Component, "ToggleAll");
        await fixture.Invoke(captured.Component, "ChangeBatch", true);
        Assert.Equal(new[] { new CraftingSourceSelection(IntermediateItemId, 200), new CraftingSourceSelection(SharedItemId, 300) },
            Assert.Single(batches).OrderBy(row => row.ItemId).ToArray());
        Assert.Equal(0, singles);
    }

    [Fact]
    public async Task AmbiguousCraftSelectionShowsTheProducerPickerWithoutChoosingOrEmittingASource()
    {
        var basis = SharedCatalog();
        var catalog = new CraftingCatalog("synthetic-ambiguous", basis.Items,
            basis.Recipes.Append(Recipe(201, IntermediateItemId, [(SharedItemId, 4)])));
        var state = SharedState(catalog, 0) with { Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty,
            RecipeChoices = ImmutableDictionary<int, int>.Empty, Result = null };
        var callbacks = 0;
        await using var fixture = new Fixture("de");
        var captured = await fixture.Capture(new()
        {
            [nameof(MapView.Catalog)] = catalog, [nameof(MapView.State)] = state,
            [nameof(MapView.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, _ => callbacks++)
        });
        await fixture.Invoke(captured.Component, "Craft", IntermediateItemId);
        var html = WebUtility.HtmlDecode(await fixture.Html(captured.Output));
        Assert.Equal(0, callbacks);
        Assert.Contains("data-crafting-map-producer=\"" + IntermediateItemId + "\"", html);
        Assert.Contains(UiText.T("Wähle zuerst den genauen Herstellungsweg für dieses Teil aus."), html);
        Assert.Empty(state.RecipeChoices);
    }

    [Theory]
    [InlineData("de", 4, 80, false)]
    [InlineData("en", 4, 80, false)]
    [InlineData("de", 5, 100, true)]
    [InlineData("en", 5, 100, true)]
    public async Task SharedInputsHaveOneNodeAndUseTheWholePlanDemand(string language, int stock, int percent, bool complete)
    {
        var catalog = SharedCatalog();
        var state = SharedState(catalog, stock);
        await using var fixture = new Fixture(language);
        var html = WebUtility.HtmlDecode(await fixture.RenderMap(catalog, state));

        Assert.Contains("data-crafting-dependency-map", html);
        Assert.Contains("data-crafting-map-stock-source=\"manual\"", html);
        Assert.Contains(UiText.T("Manueller Planbestand"), html);
        Assert.Single(Regex.Matches(html, "data-crafting-map-item=\"" + SharedItemId + "\""));
        Assert.Single(Regex.Matches(html, "data-crafting-map-item=\"" + IntermediateItemId + "\""));
        var progress = ProgressTag(html, SharedItemId);
        Assert.Contains("role=\"progressbar\"", progress);
        Assert.Contains("data-progress-known=\"true\"", progress);
        Assert.Equal((decimal)percent, DecimalAttribute(progress, "aria-valuenow"));
        Assert.Contains("data-progress-complete=\"" + complete.ToString().ToLowerInvariant() + "\"", progress);
        Assert.Equal(complete, Regex.IsMatch(Attribute(progress, "class"), @"\bcomplete\b"));
        Assert.False(string.IsNullOrWhiteSpace(Attribute(progress, "aria-label")));
        Assert.Equal(5m - stock, state.Result!.Shopping.Sum(row => row.ExpectedQuantity));
        AssertAccessibleButtons(html);

        var stockField = Assert.Single(Regex.Matches(html,
            "<input(?=[^>]*data-crafting-map-owned-quantity=\"" + SharedItemId + "\")[^>]*>").Cast<Match>()).Value;
        Assert.Contains("type=\"number\"", stockField);
        Assert.Contains("value=\"" + stock + "\"", stockField);
        Assert.False(string.IsNullOrWhiteSpace(Attribute(stockField, "aria-label")));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task OwnedTargetDoesNotInventAnObservedSuccessfulCraft(string language)
    {
        var catalog = SharedCatalog();
        var state = SharedState(catalog, 5) with
        {
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(SharedItemId, 5).Add(RootItemId, 100)
        };
        await using var fixture = new Fixture(language);
        var html = WebUtility.HtmlDecode(await fixture.RenderMap(catalog, state));
        var target = ProgressTag(html, RootItemId);

        Assert.Contains("data-progress-known=\"false\"", target);
        Assert.DoesNotContain("aria-valuenow=", target);
        Assert.DoesNotMatch(@"\bcomplete\b", Attribute(target, "class"));
        Assert.Contains("data-crafting-live-events=\"unavailable\"", html);
        Assert.Contains("data-crafting-loss-source=\"unavailable\"", html);
        Assert.Contains(UiText.T("Craft-Erkennung nicht verfügbar"), html);
        Assert.DoesNotContain("Demo-Ereignis", html);
        Assert.DoesNotContain("Demo-Replay", html);
        Assert.DoesNotContain("Nächstes Demo", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ACalculationGapKeepsMaterialCoverageUnknownInsteadOfCompletingIt(string language)
    {
        var catalog = SharedCatalog();
        var state = SharedState(catalog, 5) with { Result = null, Busy = true };
        await using var fixture = new Fixture(language);
        var html = WebUtility.HtmlDecode(await fixture.RenderMap(catalog, state));
        var material = ProgressTag(html, SharedItemId);

        Assert.Contains("data-progress-known=\"false\"", material);
        Assert.DoesNotContain("aria-valuenow=", material);
        Assert.DoesNotMatch(@"\bcomplete\b", Attribute(material, "class"));
        Assert.Contains("data-crafting-map-stock-source=\"manual\"", html);
        AssertAccessibleButtons(html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RealMaceProductionViewKeepsExactComboPrecursorsAndUnknownObservedLoss(string language)
    {
        var state = MaceExample.Value;
        Assert.Equal(CraftingPlanStatus.Complete, state.Result!.Status);
        await using var fixture = new Fixture(language);
        var html = WebUtility.HtmlDecode(await fixture.RenderView(CraftingCatalog.Default, state));

        Assert.Contains("data-crafting-dependency-map", html);
        foreach (var itemId in new[] { MaceItemId, 110730014, 110740014, 110750016 })
        {
            Assert.Single(Regex.Matches(html, "data-crafting-map-item=\"" + itemId + "\""));
            Assert.Single(Regex.Matches(html, "data-crafting-map-progress=\"" + itemId + "\""));
        }
        var target = ProgressTag(html, MaceItemId);
        Assert.Contains("data-progress-known=\"false\"", target);
        Assert.DoesNotContain("aria-valuenow=", target);
        Assert.Contains("data-crafting-live-events=\"unavailable\"", html);
        var loss = Regex.Match(html,
            "<div(?=[^>]*data-crafting-observed-loss=\"unavailable\")[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(loss);
        Assert.Contains(UiText.T("Tatsächliche Fehlcraft-Verluste"), loss);
        var value = Regex.Match(loss, "<strong[^>]*>(.*?)</strong>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("—", value);
        Assert.DoesNotContain("0", Regex.Replace(value, "<[^>]+>", ""));
        Assert.Contains(UiText.T("Noch nicht beobachtet"), loss);
        Assert.Contains(UiText.T("Beispielrechnung mit manuellen Preisen."), html);
        Assert.DoesNotContain("Estimated 90% budget", html);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionDependencyMapWithAnonymousManualExample(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_DEPENDENCY_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-12-crafting-dependency-map"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[]
        {
            "css/app.css", "combat-score-calculator.css", "crafting-calculator.css", "transfer-crafting.css", "crafting-dependency-map.css"
        }.Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        var state = MaceExample.Value;
        Assert.Equal(CraftingPlanStatus.Complete, state.Result!.Status);
        await using var fixture = new Fixture(language);
        var html = await fixture.RenderView(CraftingCatalog.Default, state);
        Assert.Contains("data-crafting-dependency-map", html);
        Assert.Contains("data-crafting-observed-loss=\"unavailable\"", html);
        Assert.Contains(WebUtility.HtmlEncode(UiText.T("Beispielrechnung mit manuellen Preisen.")), html);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "crafting-dependency-map-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
            + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
            + "\"><style>" + css
            + "\nhtml,body,#app{height:100%;width:100%;margin:0;overflow:hidden}"
            + ".crafting-dependency-viewport{height:100vh;width:100%;min-height:0;overflow:hidden}"
            + "</style></head><body><div id=\"app\"><main class=\"crafting-dependency-viewport\">"
            + html + "</main></div></body></html>");
    }

    private static string ProgressTag(string html, int itemId) => Assert.Single(Regex.Matches(html,
        "<[a-z][a-z0-9]*(?=[^>]*data-crafting-map-progress=\"" + itemId + "\")[^>]*>", RegexOptions.IgnoreCase).Cast<Match>()).Value;

    private static string Attribute(string tag, string name) => WebUtility.HtmlDecode(Regex.Match(tag,
        "(?:^|\\s)" + Regex.Escape(name) + "=\"([^\"]*)\"").Groups[1].Value);

    private static decimal DecimalAttribute(string tag, string name) => decimal.Parse(Attribute(tag, name), CultureInfo.InvariantCulture);

    private static void AssertAccessibleButtons(string html)
    {
        var buttons = Regex.Matches(html, "<button[^>]*>.*?</button>", RegexOptions.Singleline);
        Assert.NotEmpty(buttons);
        foreach (Match button in buttons)
        {
            Assert.Contains("type=\"button\"", button.Value);
            var visibleText = WebUtility.HtmlDecode(Regex.Replace(button.Value, "<[^>]+>", "")).Trim();
            Assert.True(!string.IsNullOrWhiteSpace(visibleText) || !string.IsNullOrWhiteSpace(Attribute(button.Value, "aria-label")),
                "Every map button needs visible text or an accessible name.");
        }
    }

    private static CraftingCatalog SharedCatalog() => new("synthetic-map-coverage",
        Enumerable.Range(1, 3).Select(id => new CraftingItem
        {
            Id = id, NameEn = id == SharedItemId ? "Shared material" : "Example part " + id,
            NameDe = id == SharedItemId ? "Gemeinsames Material" : "Beispielteil " + id,
            IsEquipment = id != SharedItemId, IsTradable = true
        }),
        [Recipe(100, RootItemId, [(SharedItemId, 2), (IntermediateItemId, 1)]), Recipe(200, IntermediateItemId, [(SharedItemId, 3)])]);

    private static CraftingRecipe Recipe(int id, int output, (int Id, int Quantity)[] ingredients) => new()
    {
        Id = id, Profession = "Blacksmithing", Subcategory = "Mace", Faction = "Asmodians", OutputItemId = output,
        OutputQuantity = 1, RequiredProficiency = 100, SuccessRates = [1m], MechanicsQualified = true, CraftingFee = 0,
        Ingredients = ingredients.Select(row => new CraftingIngredient { ItemId = row.Id, Quantity = row.Quantity }).ToImmutableArray()
    };

    private static CraftingCalculatorSnapshot SharedState(CraftingCatalog catalog, long stock)
    {
        var request = new CraftingPlanRequest
        {
            RecipeId = 100, TargetItemId = RootItemId, Samples = 1, Faction = "Asmodians",
            ProficiencyLevels = Levels, Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(IntermediateItemId, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(IntermediateItemId, 200),
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(SharedItemId, stock),
            BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(SharedItemId, new(SharedItemId, 10, CraftingPriceSource.Manual)),
            SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(RootItemId, new(RootItemId, 100, CraftingPriceSource.Manual))
        };
        var result = new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        return new()
        {
            Profession = "Blacksmithing", Subcategory = "Mace", SelectedRecipeId = 100, TargetItemId = RootItemId,
            Faction = "Asmodians", SimulatedLevels = Levels, Acquisition = request.Acquisition, RecipeChoices = request.RecipeChoices,
            OwnedItems = request.OwnedItems, BuyPrices = request.BuyPrices, SellPrices = request.SellPrices, Result = result,
            SimulationSamples = 1
        };
    }

    private static CraftingCalculatorSnapshot CreateMaceExample()
    {
        var catalog = CraftingCatalog.Default;
        var producers = new[] { 141064001, 141065001, 141066001 }
            .ToImmutableDictionary(id => catalog.FindRecipe(id)!.OutputItemId, id => id);
        var prices = catalog.Items.ToImmutableDictionary(item => item.Id,
            item => new CraftingPrice(item.Id, 100m, CraftingPriceSource.Manual));
        var sales = catalog.Recipes.Select(recipe => recipe.OutputItemId).Distinct()
            .ToImmutableDictionary(id => id, id => new CraftingPrice(id, 5_000_000m, CraftingPriceSource.Manual));
        var owned = new Dictionary<int, long>
        {
            [110750016] = 1, [610710006] = 8, [610530010] = 4, [610511013] = 5, [610620016] = 3, [610610016] = 5
        }.ToImmutableDictionary();
        var request = new CraftingPlanRequest
        {
            RecipeId = MaceRecipeId, TargetItemId = MaceItemId, Faction = "Asmodians", Samples = Samples,
            ProficiencyLevels = Levels, Acquisition = producers.Keys.ToImmutableDictionary(id => id, _ => CraftingAcquisition.Craft),
            RecipeChoices = producers, OwnedItems = owned, BuyPrices = prices, SellPrices = sales,
            MaximumTotalAttempts = 50_000_000, MaximumAttemptsPerSample = 1_000_000
        };
        var result = new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        return new()
        {
            Profession = "Blacksmithing", Subcategory = "Mace", SelectedRecipeId = MaceRecipeId, TargetItemId = MaceItemId,
            Faction = "Asmodians", SimulatedLevels = Levels, Acquisition = request.Acquisition, RecipeChoices = producers,
            OwnedItems = owned, BuyPrices = prices, SellPrices = sales, Result = result, SimulationSamples = Samples,
            Status = "Beispielrechnung mit manuellen Preisen.", RequiredPriceCount = prices.Count,
            SalesTaxPercent = request.SalesTaxPercent, RegistrationFeePercent = request.RegistrationFeePercent
        };
    }

    private static ImmutableDictionary<string, int> Levels { get; } = CraftingCatalog.Professions
        .ToImmutableDictionary(profession => profession, _ => 100);

    public sealed class CaptureMap : MapView
    {
        [Parameter] public Action<CaptureMap>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        public void Refresh() => StateHasChanged();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previous;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        public Fixture(string language)
        {
            _previous = UiText.Language;
            UiText.Language = language;
            _provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> RenderMap(CraftingCatalog catalog, CraftingCalculatorSnapshot state) => Render<Soulcrest.App.Components.CraftingDependencyMap>(catalog, state);
        public Task<string> RenderView(CraftingCatalog catalog, CraftingCalculatorSnapshot state) => Render<CraftingCalculatorView>(catalog, state);
        public Task<(CaptureMap Component, HtmlRootComponent Output)> Capture(Dictionary<string, object?> parameters) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            CaptureMap? component = null;
            parameters[nameof(CaptureMap.Captured)] = (Action<CaptureMap>)(value => component = value);
            var output = await _renderer.RenderComponentAsync<CaptureMap>(ParameterView.FromDictionary(parameters));
            return (component!, output);
        });
        public Task Invoke(CaptureMap component, string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(MapView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args);
            if (result is Task task) await task;
            component.Refresh();
        });
        public Task<string> Html(HtmlRootComponent output) => _renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        private Task<string> Render<T>(CraftingCatalog catalog, CraftingCalculatorSnapshot state) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(async () =>
                (await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CraftingCalculatorView.Catalog)] = catalog, [nameof(CraftingCalculatorView.State)] = state
                }))).ToHtmlString());
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previous;
        }
    }
}
