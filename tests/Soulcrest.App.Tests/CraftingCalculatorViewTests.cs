using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CraftingCalculatorViewTests
{
    [Theory]
    [InlineData("de", "Beispielklinge", "Ziel-Craftvorgänge", "Erwarteter Profit")]
    [InlineData("en", "Example blade", "Target crafting attempts", "Expected profit")]
    public async Task LocalizedPureViewShowsTargetAttemptsSeparateComboAndWholePlanMetrics(string language,
        string item, string quantityLabel, string profitLabel)
    {
        await using var fixture = new Fixture(language);
        var state = State() with
        {
            Result = new()
            {
                Status = CraftingPlanStatus.Complete, Samples = 100_000, ExpectedCost = 100, P90Cost = 300,
                ExpectedNetRevenue = 880, ExpectedProfit = 780,
                Shopping = [new(2, 2, 50, 100, CraftingPriceSource.Manual)],
                Steps = [new(1, 2, 1, 1, .5m, .25m)], Outputs = [new(1, .6m, 528m), new(4, .4m, 352m)], Byproducts = [new(3, .5m, 44m)]
            },
            ProfileId = "SYNTHETIC-PRIVATE-PROFILE-NEVER-RENDER", SimulatedLevels = new Dictionary<string, int> { ["Blacksmithing"] = 50 }.ToImmutableDictionary()
        };
        var html = WebUtility.HtmlDecode(await fixture.Render<CraftingCalculatorView>(new()
        {
            [nameof(CraftingCalculatorView.Catalog)] = Catalog(), [nameof(CraftingCalculatorView.State)] = state
        }));
        Assert.Contains(item, html); Assert.Contains(quantityLabel, html); Assert.Contains(profitLabel, html);
        Assert.DoesNotContain("craft-budget", html); Assert.DoesNotContain("90-%", html); Assert.DoesNotContain("90%", html);
        Assert.Contains("data-crafting-calculator", html); Assert.Contains("data-crafting-combo=\"4\"", html);
        Assert.Equal(6, Regex.Matches(html, "data-crafting-profession=").Count);
        Assert.Contains("100,000", language == "en" ? html : html.Replace("100.000", "100,000"));
        Assert.DoesNotContain(state.ProfileId, html);
        // COMBO stays automatic. Checkboxes only select parts for the requested batch source edit.
        Assert.All(Regex.Matches(html, "<input(?=[^>]*type=\"checkbox\")[^>]*>").Cast<Match>(),
            checkbox => Assert.Contains("data-crafting-map-selection", checkbox.Value));
        Assert.Contains("data-crafting-output=\"1\"", html); Assert.Contains("data-crafting-output=\"4\"", html);
        Assert.Contains(language == "de" ? "Fehlschläge zählen mit" : "Failures count", html);
        Assert.DoesNotContain(language == "de" ? ">Zielmenge<" : ">Target quantity<", html);
        Assert.Contains("value=\"50\"", html); // Simulation is displayed separately from observed proficiency.
    }

    [Fact]
    public async Task PureViewEventsDeliverRecipeTargetAttemptsSearchAndSimulatedLevel()
    {
        await using var fixture = new Fixture("de");
        CraftingCalculatorView? component = null;
        var selected = 0; var target = 0; var quantity = 0; var samples = 0; var query = ""; var profession = "";
        CraftingLevelSelection? level = null;
        await fixture.Render<CaptureCalculator>(new()
        {
            [nameof(CaptureCalculator.Captured)] = (Action<CraftingCalculatorView>)(value => component = value),
            [nameof(CraftingCalculatorView.Catalog)] = Catalog(), [nameof(CraftingCalculatorView.State)] = State(),
            [nameof(CraftingCalculatorView.ProfessionChanged)] = EventCallback.Factory.Create<string>(this, value => profession = value),
            [nameof(CraftingCalculatorView.RecipeSelected)] = EventCallback.Factory.Create<int>(this, value => selected = value),
            [nameof(CraftingCalculatorView.TargetChanged)] = EventCallback.Factory.Create<int>(this, value => target = value),
            [nameof(CraftingCalculatorView.AttemptsChanged)] = EventCallback.Factory.Create<int>(this, value => quantity = value),
            [nameof(CraftingCalculatorView.SimulationSamplesChanged)] = EventCallback.Factory.Create<int>(this, value => samples = value),
            [nameof(CraftingCalculatorView.QueryChanged)] = EventCallback.Factory.Create<string>(this, value => query = value),
            [nameof(CraftingCalculatorView.LevelChanged)] = EventCallback.Factory.Create<CraftingLevelSelection>(this, value => level = value)
        });
        await fixture.Event(component!, "data-crafting-profession", "Alchemy", "onclick", new MouseEventArgs());
        await fixture.Event(component!, "data-crafting-recipe", "1", "onclick", new MouseEventArgs());
        await fixture.Event(component!, "data-crafting-target-quantity", "true", "onchange", new ChangeEventArgs { Value = "17" });
        await fixture.Event(component!, "type", "search", "oninput", new ChangeEventArgs { Value = "Werkstoff" });
        await fixture.Event(component!, "aria-label", UiText.F("Rechenlevel für {0}", "Blacksmithing"), "onchange", new ChangeEventArgs { Value = "63" });
        await fixture.Event(component!, "data-crafting-target-variant", "", nameof(ItemSelect.ValueChanged), 4);
        Assert.Equal("Alchemy", profession); Assert.Equal(1, selected); Assert.Equal(4, target);
        await fixture.Event(component!, "data-crafting-simulation-samples", "true", "onchange", new ChangeEventArgs { Value = "2500" });
        Assert.Equal(2500, samples);
        Assert.Equal(17, quantity); Assert.Equal("Werkstoff", query); Assert.Equal(new("Blacksmithing", 63), level);
    }

    [Fact]
    public async Task TreeShowsNestedSchemaWhileSourceAndReplacementRemainExplicitCallbacks()
    {
        await using var fixture = new Fixture("en");
        CraftingRecipeTree? component = null; CraftingSourceSelection? source = null; CraftingReplacementSelection? replacement = null;
        var html = await fixture.Render<CaptureTree>(new()
        {
            [nameof(CaptureTree.Captured)] = (Action<CraftingRecipeTree>)(value => component = value),
            [nameof(CraftingRecipeTree.Catalog)] = Catalog(), [nameof(CraftingRecipeTree.Recipe)] = Catalog().FindRecipe(1),
            [nameof(CraftingRecipeTree.State)] = State(), [nameof(CraftingRecipeTree.ExpandAll)] = true,
            [nameof(CraftingRecipeTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, value => source = value),
            [nameof(CraftingRecipeTree.ReplacementChanged)] = EventCallback.Factory.Create<CraftingReplacementSelection>(this, value => replacement = value)
        });
        Assert.Contains("data-crafting-node=\"2\"", html); Assert.Contains("data-crafting-combo=\"4\"", html);
        Assert.DoesNotContain("value=\"buy\"", html); Assert.Contains("data-crafting-owned-quantity=\"2\"", html); Assert.Contains("COMBO", html);
        await fixture.Event(component!, "data-crafting-use-subrecipe", "2", "onclick", new MouseEventArgs());
        await fixture.Event(component!, "data-crafting-replacement", "1:2", nameof(ItemSelect.ValueChanged), 5);
        Assert.Equal(new(2, 2), source); Assert.Equal(new(1, 2, 5), replacement);
    }

    [Fact]
    public async Task UnknownEconomicsRemainDashesAndRecursiveSchemaTerminates()
    {
        await using var fixture = new Fixture("en");
        var html = WebUtility.HtmlDecode(await fixture.Render<CraftingCalculatorView>(new()
        {
            [nameof(CraftingCalculatorView.Catalog)] = Catalog(), [nameof(CraftingCalculatorView.State)] = State() with
            { Result = new() { Status = CraftingPlanStatus.Unknown, Issues = [new("MechanicsUnqualified", RecipeId: 1)] } }
        }));
        var metrics = Regex.Match(html, "<div class=\"craft-metrics[^\"]*\">(.*?)<div class=\"craft-status", RegexOptions.Singleline).Value;
        Assert.Equal(4, Regex.Matches(metrics, "<strong>— <small>Kina</small></strong>").Count);
        Assert.DoesNotContain("0 <small>Kina", metrics);
        html = await fixture.Render<CraftingRecipeTree>(new()
        {
            [nameof(CraftingRecipeTree.Catalog)] = Catalog(), [nameof(CraftingRecipeTree.Recipe)] = Catalog().FindRecipe(1),
            [nameof(CraftingRecipeTree.State)] = State(), [nameof(CraftingRecipeTree.Ancestors)] = ImmutableHashSet.Create(1)
        });
        Assert.Contains("data-crafting-tree-boundary", html); Assert.DoesNotContain("data-crafting-node=", html);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionCraftingViewWithAnonymousManualPrices(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var catalog = CraftingCatalog.Default;
        var levels = CraftingCatalog.Professions.ToImmutableDictionary(profession => profession, _ => 100);
        var recipe = catalog.Recipes.Where(row => row.Profession == "Blacksmithing" && row.Subcategory == "Greatsword"
                && !catalog.IsComboRecipe(row.Id) && row.MechanicsQualified && row.Faction == "Asmodians")
            .OrderBy(row => row.RequiredProficiency).First();
        // Anonymous illustrative prices, deliberately distinct from a live market valuation.
        var prices = catalog.Items.ToImmutableDictionary(row => row.Id,
            row => new CraftingPrice(row.Id, row.Id == recipe.OutputItemId ? 40_000m : row.Id switch
            { 610710001 => 3_500m, 610530001 => 600m, 610610013 => 5_000m, 610620013 => 1_400m, 630541001 => 400m, _ => 2_000m }, CraftingPriceSource.Manual));
        if (recipe.ComboRecipeId is { } comboId && catalog.FindRecipe(comboId) is { } combo)
            prices = prices.SetItem(combo.OutputItemId, new(combo.OutputItemId, 70_000m, CraftingPriceSource.Manual));
        var request = new CraftingPlanRequest
        {
            RecipeId = recipe.Id, TargetAttempts = 100, Samples = 50_000, Faction = "Asmodians", ProficiencyLevels = levels,
            BuyPrices = prices, SellPrices = prices
        };
        var result = new CraftingCalculator(catalog).Calculate(request);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        await using var fixture = new Fixture(language);
        var html = await fixture.Render<CraftingCalculatorView>(new()
        {
            [nameof(CraftingCalculatorView.Catalog)] = catalog,
            [nameof(CraftingCalculatorView.State)] = new CraftingCalculatorSnapshot
            {
                SelectedRecipeId = recipe.Id, TargetItemId = recipe.OutputItemId, TargetAttempts = 100,
                Faction = "Asmodians", ObservedLevels = levels, SimulatedLevels = levels, BuyPrices = prices, SellPrices = prices,
                ServerName = language == "de" ? "Beispielmarkt" : "Example market", Result = result, Status = "Beispielrechnung mit manuellen Preisen."
            }
        });
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "crafting-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
            + "\"><style>" + css + "\nhtml,body{height:auto;min-height:100%;margin:0}</style></head><body>" + html + "</body></html>");
    }

    private static CraftingCalculatorSnapshot State() => new()
    {
        SelectedRecipeId = 1, TargetItemId = 1, Faction = "Asmodians", ServerName = "Synthetic market",
        Profession = "Blacksmithing", Subcategory = "Greatsword", ProficiencyFresh = true,
        ObservedLevels = new Dictionary<string, int> { ["Blacksmithing"] = 41, ["Armorsmithing"] = 27,
            ["Handicrafting"] = 100, ["Alchemy"] = 100, ["Cooking"] = 63 }.ToImmutableDictionary()
    };
    private static CraftingCatalog Catalog() => new("synthetic-ui", [
        new() { Id = 1, NameEn = "Example blade", NameDe = "Beispielklinge", IsTradable = true },
        new() { Id = 2, NameEn = "Example ingot", NameDe = "Beispielbarren", IsTradable = true },
        new() { Id = 3, NameEn = "Example ore", NameDe = "Beispielerz", IsTradable = true },
        new() { Id = 4, NameEn = "Splendent example blade", NameDe = "Glänzende Beispielklinge", IsTradable = true },
        new() { Id = 5, NameEn = "Alternative ingot", NameDe = "Alternativer Barren", IsTradable = true }
    ], [
        new() { Id = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Asmodians", RequiredProficiency = 20,
            OutputItemId = 1, OutputQuantity = 1, Ingredients = [new() { ItemId = 2, Quantity = 2, ReplacementItemIds = [5] }],
            ComboRecipeId = 4, ComboActivationChance = .25m, SuccessRates = Enumerable.Repeat(.8m, 81).ToImmutableArray(), CraftingFee = 0, MechanicsQualified = true },
        new() { Id = 2, Profession = "Blacksmithing", Subcategory = "Materials", RequiredProficiency = 1,
            OutputItemId = 2, OutputQuantity = 1, Ingredients = [new() { ItemId = 3, Quantity = 3 }],
            SuccessRates = Enumerable.Repeat(.99m, 100).ToImmutableArray(), CraftingFee = 0, MechanicsQualified = true },
        new() { Id = 4, Profession = "Blacksmithing", Subcategory = "Greatsword", RequiredProficiency = 20,
            OutputItemId = 4, OutputQuantity = 1, SuccessRates = Enumerable.Repeat(.8m, 81).ToImmutableArray(), CraftingFee = 0, MechanicsQualified = true }
    ]);

    public sealed class CaptureCalculator : CraftingCalculatorView
    {
        [Parameter] public Action<CraftingCalculatorView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CaptureTree : CraftingRecipeTree
    {
        [Parameter] public Action<CraftingRecipeTree>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previous;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        public Fixture(string language)
        {
            _previous = UiText.Language; UiText.Language = language;
            _provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
#pragma warning disable BL0006
        public Task Event<T>(IComponent component, string attribute, string value, string eventName, T args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            var declaring = component is CraftingCalculatorView ? typeof(CraftingCalculatorView) : typeof(CraftingRecipeTree);
            declaring.GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var match = frames.Array[index];
                if (match.FrameType == RenderTreeFrameType.Component && match.ComponentType == typeof(ItemSelect))
                {
                    var attributes = new Dictionary<string, object?>();
                    for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == RenderTreeFrameType.Attribute; next++)
                        attributes[frames.Array[next].AttributeName] = frames.Array[next].AttributeValue;
                    if (attributes.TryGetValue(attribute, out var found) && (value.Length == 0 || Convert.ToString(found, CultureInfo.InvariantCulture) == value)
                        && attributes.TryGetValue(eventName, out var handler) && handler is EventCallback<T> selected)
                    { await selected.InvokeAsync(args); return; }
                }
                if (match.FrameType != RenderTreeFrameType.Attribute || match.AttributeName != attribute
                    || Convert.ToString(match.AttributeValue, CultureInfo.InvariantCulture) != value) continue;
                var element = index;
                while (element >= 0 && frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                if (element < 0) continue;
                for (var eventIndex = element + 1; eventIndex < frames.Count && frames.Array[eventIndex].FrameType == RenderTreeFrameType.Attribute; eventIndex++)
                {
                    var frame = frames.Array[eventIndex];
                    if (frame.AttributeName != eventName) continue;
                    if (frame.AttributeValue is EventCallback<T> typed) await typed.InvokeAsync(args);
                    else if (frame.AttributeValue is EventCallback callback) await callback.InvokeAsync(args);
                    else if (frame.AttributeValue is MulticastDelegate handler)
                    { if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task; }
                    else throw new InvalidOperationException("Unexpected crafting callback.");
                    return;
                }
            }
            throw new InvalidOperationException("Crafting event callback not found.");
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previous;
        }
    }
}
