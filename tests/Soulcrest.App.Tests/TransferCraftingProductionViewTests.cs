using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Production rendering with native recipes and icons, anonymous manual prices and explicit fixture routes.</summary>
[Collection("Settings file")]
public sealed class TransferCraftingProductionViewTests
{
    private const int RootRecipeId = 221043012;
    private const int TransferGearId = 110430100;
    private const int ConsumedGearId = 110430014;
    private const int OwnedRawMaterialId = 610730023;
    private const int Samples = 2_000;
    private static readonly Lazy<Scenarios> Examples = new(CreateScenarios);

    [Fact]
    public void NativeHandicraftingChainIsCraftableAndFiniteOwnedStockReducesOnlyCashCosts()
    {
        var examples = Examples.Value;
        var root = examples.Catalog.FindRecipe(RootRecipeId)!;
        Assert.Equal("Asmodians", root.Faction);
        Assert.Equal("Handicrafting", root.Profession);
        Assert.True(root.IsTransferCrafting);
        Assert.Null(root.SuccessRates);
        Assert.False(root.MechanicsQualified);
        foreach (var scenario in new[] { examples.Owned, examples.Chain, examples.OwnedWithoutRawStock })
        {
            Assert.Equal(CraftingPlanStatus.Complete, scenario.Result.Status);
            Assert.Equal(Samples, scenario.Result.Samples);
            Assert.NotNull(scenario.Result.ExpectedCost);
            Assert.NotNull(scenario.Result.P90Cost);
            Assert.All(scenario.Result.Shopping, row => Assert.False(examples.Catalog.FindItem(row.ItemId)!.IsEquipment));
            Assert.All(scenario.Request.BuyPrices.Values, price =>
            {
                Assert.False(examples.Catalog.FindItem(price.ItemId)!.IsEquipment);
                Assert.Equal(100m, price.UnitPrice);
                Assert.Equal(CraftingPriceSource.Manual, price.Source);
            });
        }
        Assert.Equal(7_100m, examples.OwnedWithoutRawStock.Result.ExpectedCost);
        Assert.Equal(4_200m, examples.Owned.Result.ExpectedCost);
        Assert.Equal(4_200m, examples.Owned.Result.P90Cost);
        Assert.True(examples.Chain.Result.ExpectedCost > examples.OwnedWithoutRawStock.Result.ExpectedCost);
        Assert.DoesNotContain(examples.Owned.Result.Shopping, row => row.ItemId == OwnedRawMaterialId);
        Assert.Contains(examples.Chain.Result.Steps, row => row.RecipeId == 221046001 && row.ExpectedAttempts > 0);
        Assert.Empty(examples.Chain.Request.OwnedItems);
        Assert.Null(examples.Catalog.FindRecipe(241043012)!.SuccessRates);
    }

    [Theory]
    [InlineData("de", "Erwartete Herstellungskosten", "Zielmenge", "Manueller Preis")]
    [InlineData("en", "Expected crafting costs", "Target quantity", "Manual price")]
    public async Task FullProductionViewLocalizesOwnedAndRecursiveScenariosWithoutSaleValuations(
        string language, string costLabel, string quantityLabel, string priceSource)
    {
        var examples = Examples.Value;
        await using var fixture = new Fixture(language);
        foreach (var scenario in new[] { examples.Owned, examples.Chain })
        {
            var state = State(scenario, language);
            var html = WebUtility.HtmlDecode(await fixture.Render(examples.Catalog, state));
            Assert.Contains("data-crafting-calculator", html);
            Assert.Contains("data-crafting-profession=\"Transfercrafting\"", html);
            Assert.Contains("data-crafting-subcategory=\"Bow\"", html);
            Assert.Contains("data-transfer-crafting=\"" + RootRecipeId + "\"", html);
            Assert.Equal(5, Regex.Matches(html, "class=\"craft-proficiency(?:[ \"])").Count);
            Assert.Contains(costLabel, html);
            Assert.Contains(quantityLabel, html);
            Assert.Contains(priceSource, html);
            Assert.Contains(language == "de" ? "2.000 Simulationsläufe" : "2,000 simulation runs", html);
            Assert.Contains(language == "de" ? "100,000000 %" : "100.000000 %", html);
            Assert.Matches("id=\"transfer-target-quantity\"[^>]*value=\"1\"", html);
            Assert.Contains("src=\"" + examples.Catalog.FindItem(examples.Catalog.FindRecipe(RootRecipeId)!.OutputItemId)!.Icon + "\"", html);
            Assert.DoesNotContain("data-transfer-selection-required", html);
            Assert.DoesNotContain("data-transfer-producer-missing", html);
            Assert.DoesNotContain("tc-requirement", html);
            Assert.DoesNotContain(language == "de" ? "Erwarteter Profit" : "Expected profit", html);
            Assert.Empty(state.SimulatedLevels);
            foreach (var (profession, level) in Levels)
                Assert.Equal(level, state.Level(profession));
            var expected = Regex.Escape(CraftingCalculatorPresentation.Number(scenario.Result.ExpectedCost));
            Assert.Matches("data-transfer-expected-cost(?:=\"\")?>" + expected + " <small>Kina", html);
        }
        var ownedHtml = await fixture.Render(examples.Catalog, State(examples.Owned, language));
        Assert.Matches("data-transfer-owned=\"" + TransferGearId + "\"[^>]*checked", ownedHtml);
        Assert.Matches("data-transfer-owned=\"" + ConsumedGearId + "\"[^>]*checked", ownedHtml);
        Assert.Matches("data-transfer-owned-quantity=\"" + OwnedRawMaterialId + "\"[^>]*value=\"50\"", ownedHtml);
        var chainHtml = await fixture.Render(examples.Catalog, State(examples.Chain, language));
        Assert.Contains("data-transfer-node=\"221046001\"", chainHtml);
        Assert.Matches("data-transfer-owned-quantity=\"" + OwnedRawMaterialId + "\"[^>]*value=\"0\"", chainHtml);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionTransferViewsWithAnonymousManualPrices(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_TRANSFER_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-10-transfer-crafting"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css", "transfer-crafting.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        var examples = Examples.Value;
        await using var fixture = new Fixture(language);
        foreach (var (name, scenario) in new[] { ("owned", examples.Owned), ("chain", examples.Chain) })
        {
            Assert.Equal(CraftingPlanStatus.Complete, scenario.Result.Status);
            var html = await fixture.Render(examples.Catalog, State(scenario, language));
            await File.WriteAllTextAsync(Path.Combine(folder, "transfer-" + name + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
                + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
                + "\"><style>" + css + "\nhtml,body{height:auto;min-height:100%;margin:0}</style></head><body>"
                + html + "</body></html>");
        }
    }

    private static ImmutableDictionary<string, int> Levels { get; } = new Dictionary<string, int>
    {
        ["Blacksmithing"] = 41, ["Armorsmithing"] = 27, ["Handicrafting"] = 100, ["Alchemy"] = 100, ["Cooking"] = 63
    }.ToImmutableDictionary();

    private sealed record Scenario(TransferCraftingPlanRequest Request, CraftingPlanResult Result);
    private sealed record Scenarios(CraftingCatalog Catalog, Scenario Owned, Scenario Chain, Scenario OwnedWithoutRawStock);

    private static Scenarios CreateScenarios()
    {
        var catalog = CraftingCatalog.Default;
        // Every route is fixture input, not a product default or a name-based inference.
        var routes = new Dictionary<int, int>
        {
            [110430100] = 221043011, [110430019] = 221043001, [110430021] = 241043001,
            [110430014] = 241044001, [110440014] = 241045001, [110450016] = 241046001
        }.ToImmutableDictionary();
        var replacements = new Dictionary<CraftingIngredientKey, int>();
        foreach (var recipeId in new[] { RootRecipeId, 221043011, 221043001, 221044001, 221045001, 221046001 })
            foreach (var ingredient in catalog.FindRecipe(recipeId)!.Ingredients)
                replacements[new(recipeId, ingredient.ItemId)] = ingredient.ItemId;
        replacements[new(RootRecipeId, 110430021)] = TransferGearId;
        var prices = catalog.Items.Where(item => item.IsEquipment == false).ToImmutableDictionary(item => item.Id,
            item => new CraftingPrice(item.Id, 100m, CraftingPriceSource.Manual));
        var request = new TransferCraftingPlanRequest
        {
            RecipeId = RootRecipeId, TargetQuantity = 1, Faction = "Asmodians", Samples = Samples,
            ProficiencyLevels = Levels, RecipeChoices = routes, ReplacementChoices = replacements.ToImmutableDictionary(),
            BuyPrices = prices
        };
        var gear = ImmutableDictionary<int, long>.Empty.Add(TransferGearId, 1).Add(ConsumedGearId, 1);
        var calculator = new TransferCraftingCalculator(catalog);
        Scenario Calculate(TransferCraftingPlanRequest value) => new(value, calculator.Calculate(value));
        return new(catalog,
            Calculate(request with { OwnedItems = gear.Add(OwnedRawMaterialId, 50) }),
            Calculate(request), Calculate(request with { OwnedItems = gear }));
    }

    private static CraftingCalculatorSnapshot State(Scenario scenario, string language) => new()
    {
        Profession = "Transfercrafting", Subcategory = "Bow", SelectedRecipeId = RootRecipeId,
        TargetItemId = scenario.Request.TargetItemId ?? Examples.Value.Catalog.FindRecipe(RootRecipeId)!.OutputItemId,
        Faction = "Asmodians", ObservedLevels = Levels, ProficiencyFresh = true,
        RecipeChoices = scenario.Request.RecipeChoices, ReplacementChoices = scenario.Request.ReplacementChoices,
        OwnedItems = scenario.Request.OwnedItems, BuyPrices = scenario.Request.BuyPrices,
        ServerName = language == "de" ? "Beispielmarkt" : "Example market",
        Status = "Beispielrechnung mit manuellen Preisen.", Result = scenario.Result
    };

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
        public Task<string> Render(CraftingCatalog catalog, CraftingCalculatorSnapshot state) => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<CraftingCalculatorView>(ParameterView.FromDictionary(new Dictionary<string, object?>
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
