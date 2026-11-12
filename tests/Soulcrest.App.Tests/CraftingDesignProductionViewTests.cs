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

/// <summary>Native production views in a constrained viewport, with anonymous, explicitly manual example prices.</summary>
[Collection("Settings file")]
public sealed class CraftingDesignProductionViewTests
{
    private const int Samples = 1_000;
    private const int CookingRecipeId = 524084001;
    private const int TransferRecipeId = 221043012;
    private const int TransferGearId = 110430100;
    private const int ConsumedGearId = 110430014;
    private const int OwnedRawMaterialId = 610730023;
    private static readonly ImmutableArray<string> TransferSlots =
    [
        "Greatsword", "Longsword", "Dagger", "Mace", "Guard", "Bow", "Staff", "Spellbook", "Orb",
        "Helm", "Pauldrons", "Top", "Legs", "Gloves", "Shoes", "Cloak", "Necklace", "Earrings", "Ring"
    ];
    private static readonly Lazy<ImmutableArray<Example>> Examples = new(CreateExamples);

    [Theory]
    [InlineData("de", "normal")]
    [InlineData("en", "normal")]
    [InlineData("de", "transfer-owned")]
    [InlineData("en", "transfer-owned")]
    [InlineData("de", "transfer-chain")]
    [InlineData("en", "transfer-chain")]
    public async Task ProductionLayoutKeepsCatalogPlanSlotsAndAccessibleFields(string language, string name)
    {
        var example = Examples.Value.Single(row => row.Name == name);
        var state = State(example, language);
        Assert.Equal(CraftingPlanStatus.Complete, state.Result!.Status);
        Assert.Equal(Samples, state.Result.Samples);
        await using var fixture = new Fixture(language);
        var html = WebUtility.HtmlDecode(await fixture.Render(state));
        Assert.Contains("data-crafting-workspace", html);
        Assert.Contains("data-crafting-catalog", html);
        Assert.Contains("data-crafting-plan-pane", html);
        Assert.True(html.IndexOf("data-crafting-catalog", StringComparison.Ordinal)
            < html.IndexOf("data-crafting-plan-pane", StringComparison.Ordinal));
        Assert.Contains("data-crafting-recipe=\"" + example.RecipeId + "\"", html);
        Assert.Contains("aria-label=\"" + UiText.T("Alle Rezepte durchsuchen") + "\"", html);
        foreach (var (profession, level) in Levels)
        {
            Assert.Contains("data-crafting-profession=\"" + profession + "\"", html);
            var label = Regex.Escape(UiText.F("Rechenlevel für {0}", profession));
            var field = Regex.Match(html, "<input(?=[^>]*aria-label=\"" + label + "\")[^>]*>").Value;
            Assert.NotEmpty(field);
            Assert.Contains("value=\"" + level + "\"", field);
        }
        Assert.Contains("data-crafting-profession=\"Transfercrafting\"", html);
        Assert.Empty(state.SimulatedLevels);
        Assert.Contains(language == "de" ? "1.000 Simulationsläufe" : "1,000 simulation runs", html);
        Assert.Contains(UiText.T("Beispielrechnung mit manuellen Preisen."), html);
        var icon = CraftingCatalog.Default.FindItem(CraftingCatalog.Default.FindRecipe(example.RecipeId)!.OutputItemId)!.Icon;
        Assert.NotNull(icon);
        Assert.Contains("src=\"" + icon + "\"", html);
        var tabs = Regex.Matches(html, "data-crafting-subcategory=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        if (example.Transfer)
        {
            Assert.Equal(TransferSlots.ToArray(), tabs);
            Assert.Contains("data-crafting-slot-tabs=\"true\"", html);
            Assert.Contains("data-transfer-crafting=\"" + example.RecipeId + "\"", html);
            Assert.DoesNotContain("data-transfer-selection-required", html);
            Assert.DoesNotContain("data-transfer-producer-missing", html);
            Assert.DoesNotContain(UiText.T("Erwarteter Profit"), html);
            var raw = Regex.Match(html, "<input(?=[^>]*data-transfer-owned-quantity=\"" + OwnedRawMaterialId + "\")[^>]*>").Value;
            Assert.NotEmpty(raw);
            Assert.Contains("aria-label=\"", raw);
            Assert.Contains("value=\"" + (name == "transfer-owned" ? 50 : 0) + "\"", raw);
            if (name == "transfer-owned")
                Assert.Matches("data-transfer-owned=\"" + TransferGearId + "\"[^>]*checked", html);
            else Assert.Contains("data-transfer-node=\"221046001\"", html);
        }
        else
        {
            Assert.Equal(new[] { "Food" }, tabs);
            Assert.DoesNotContain("data-crafting-slot-tabs=\"true\"", html);
            Assert.Contains("data-crafting-selected=\"" + CookingRecipeId + "\"", html);
            Assert.Contains("data-crafting-combo=\"544084001\"", html);
            Assert.Contains(UiText.T("Erwarteter Profit"), html);
        }
    }

    [Fact]
    public void TransferExamplesRetainThePreviouslyQualifiedRoutesAndManualStockAssumptions()
    {
        var examples = Examples.Value;
        var owned = examples.Single(row => row.Name == "transfer-owned");
        var chain = examples.Single(row => row.Name == "transfer-chain");
        Assert.Equal(4_200m, owned.Result.ExpectedCost);
        Assert.True(chain.Result.ExpectedCost > owned.Result.ExpectedCost);
        Assert.Null(CraftingCatalog.Default.FindRecipe(TransferRecipeId)!.SuccessRates);
        Assert.All(owned.BuyPrices.Values, price =>
        {
            Assert.Equal(CraftingPriceSource.Manual, price.Source);
            Assert.Equal(100m, price.UnitPrice);
            Assert.False(CraftingCatalog.Default.FindItem(price.ItemId)!.IsEquipment);
        });
        Assert.Empty(chain.OwnedItems);
        Assert.Equal(50L, owned.OwnedItems[OwnedRawMaterialId]);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionDesignViewsInAFixedViewport(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_DESIGN_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-11-crafting-calculator-design"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css", "transfer-crafting.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        await using var fixture = new Fixture(language);
        foreach (var example in Examples.Value)
        {
            Assert.Equal(CraftingPlanStatus.Complete, example.Result.Status);
            var html = await fixture.Render(State(example, language));
            await File.WriteAllTextAsync(Path.Combine(folder, "crafting-" + example.Name + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
                + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
                + "\"><style>" + css
                + "\nhtml,body,#app{height:100%;width:100%;margin:0;overflow:hidden}"
                + ".crafting-design-viewport{height:100vh;width:100%;min-height:0;overflow:hidden}"
                + "</style></head><body><div id=\"app\"><main class=\"crafting-design-viewport\">"
                + html + "</main></div></body></html>");
        }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionPreferenceScenariosInAFixedViewport(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_PREFERENCES_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-11-crafting-calculator-design", "scenarios"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css", "transfer-crafting.css", "css/app-themes.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        await using var fixture = new Fixture(language);
        foreach (var (name, state) in CreatePreferenceExamples(language))
        {
            Assert.Equal(CraftingPlanStatus.Complete, state.Result!.Status);
            var html = await fixture.Render(state);
            Assert.Contains("data-crafting-scenario-storage", html);
            Assert.Contains("data-crafting-combo-target=\"" + state.SelectedRecipeId + "\"", html);
            Assert.Contains(WebUtility.HtmlEncode(UiText.T("Szenario gespeichert.")), html);
            if (name == "combo-reference")
            {
                Assert.Contains("data-crafting-reference-simulation=\"true\"", html);
                Assert.NotNull(state.Result.TargetOutput);
                Assert.DoesNotContain("data-transfer-crafting", html);
            }
            else
            {
                Assert.Contains("data-transfer-crafting", html);
                Assert.True(state.Result.Steps.Length > 2);
                Assert.DoesNotContain(WebUtility.HtmlEncode(UiText.T("Erwarteter Profit")), html);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "crafting-" + name + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
                + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
                + "\"><style>" + css
                + "\nhtml,body,#app{height:100%;width:100%;margin:0;overflow:hidden}"
                + ".crafting-design-viewport{height:100vh;width:100%;min-height:0;overflow:hidden}"
                + "</style></head><body><div id=\"app\"><main class=\"crafting-design-viewport\">"
                + html + "</main></div></body></html>");
        }
    }

    internal static ImmutableArray<(string Name, CraftingCalculatorSnapshot State)> CreatePreferenceExamples(string language)
    {
        var catalog = CraftingCatalog.Default;
        var normal = catalog.FindRecipe(141023001)!;
        var normalBase = catalog.FindBaseRecipe(normal.Id)!;
        var required = new[] { normalBase, normal }.Max(recipe => recipe.RequiredProficiency!.Value);
        var reference = ImmutableDictionary<string, int>.Empty.Add(normal.Profession, required);
        var effective = Levels.SetItem(normal.Profession, required);
        // Explicitly choose the native COMBO producers for all three equipment precursors.
        var gearRoutes = new[] { 141024001, 141025001, 141026001 }
            .ToImmutableDictionary(id => catalog.FindRecipe(id)!.OutputItemId, id => id);
        var acquisition = gearRoutes.Keys.ToImmutableDictionary(id => id, _ => CraftingAcquisition.Craft);
        var buy = catalog.Items.Where(item => item.IsEquipment == false)
            .ToImmutableDictionary(item => item.Id, item => new CraftingPrice(item.Id, 100m, CraftingPriceSource.Manual));
        var sell = ImmutableDictionary<int, CraftingPrice>.Empty
            .Add(normalBase.OutputItemId, new(normalBase.OutputItemId, 5_000m, CraftingPriceSource.Manual))
            .Add(normal.OutputItemId, new(normal.OutputItemId, 50_000m, CraftingPriceSource.Manual));
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(new CraftingPlanRequest
        {
            RecipeId = normal.Id, TargetItemId = normal.OutputItemId, TargetOutputQuantity = 1,
            Faction = "Asmodians", ProficiencyLevels = effective, Samples = Samples, BuyPrices = buy, SellPrices = sell,
            Acquisition = acquisition, RecipeChoices = gearRoutes
        });
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        var savedAt = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var normalState = new CraftingCalculatorSnapshot
        {
            Profession = normal.Profession, Subcategory = normal.Subcategory, SelectedRecipeId = normal.Id,
            TargetItemId = normal.OutputItemId, Faction = "Asmodians", ObservedLevels = Levels, ProficiencyFresh = true,
            ReferenceLevels = reference, RequiredProficiencyLevels = reference,
            Acquisition = acquisition, RecipeChoices = gearRoutes,
            BuyPrices = result.Shopping.ToImmutableDictionary(row => row.ItemId, row => buy[row.ItemId]), SellPrices = sell,
            ScenarioStorage = new(true, false, false, savedAt, null), SimulationSamples = Samples, Result = result,
            Status = "Beispielrechnung mit manuellen Preisen.", ServerName = language == "de" ? "Beispielmarkt" : "Example market",
            RequiredPriceCount = result.Shopping.Select(row => row.ItemId).Concat(sell.Keys).Distinct().Count()
        };
        var chain = Examples.Value.Single(row => row.Name == "transfer-chain");
        var transfer = catalog.FindRecipe(catalog.FindRecipe(TransferRecipeId)!.ComboRecipeId!.Value)!;
        var transferResult = new TransferCraftingCalculator(catalog).Calculate(new TransferCraftingPlanRequest
        {
            RecipeId = transfer.Id, TargetItemId = transfer.OutputItemId, TargetQuantity = 1, Faction = "Asmodians",
            Samples = Samples, ProficiencyLevels = Levels, BuyPrices = chain.BuyPrices,
            RecipeChoices = chain.RecipeChoices, ReplacementChoices = chain.ReplacementChoices
        });
        Assert.Equal(CraftingPlanStatus.Complete, transferResult.Status);
        var usedPrices = transferResult.Shopping.Select(row => row.ItemId).Distinct()
            .ToImmutableDictionary(id => id, id => chain.BuyPrices[id]);
        var transferRequirements = chain.RecipeChoices.Values.Select(id => catalog.FindRecipe(id)!)
            .Append(catalog.FindRecipe(TransferRecipeId)!).Append(transfer)
            .GroupBy(recipe => recipe.Profession).ToImmutableDictionary(group => group.Key,
                group => group.Max(recipe => recipe.RequiredProficiency ?? 0));
        var transferState = new CraftingCalculatorSnapshot
        {
            Profession = "Transfercrafting", Subcategory = transfer.Subcategory, SelectedRecipeId = transfer.Id,
            TargetItemId = transfer.OutputItemId, Faction = "Asmodians", ObservedLevels = Levels, ProficiencyFresh = true,
            RecipeChoices = chain.RecipeChoices, ReplacementChoices = chain.ReplacementChoices,
            RequiredProficiencyLevels = transferRequirements, BuyPrices = usedPrices,
            ScenarioStorage = new(true, false, false, savedAt, null), SimulationSamples = Samples, Result = transferResult,
            Status = "Beispielrechnung mit manuellen Preisen.", ServerName = language == "de" ? "Beispielmarkt" : "Example market",
            RequiredPriceCount = usedPrices.Count
        };
        return [("combo-reference", normalState), ("transfer-combo-chain", transferState)];
    }

    private static ImmutableDictionary<string, int> Levels { get; } = new Dictionary<string, int>
    {
        ["Blacksmithing"] = 41, ["Armorsmithing"] = 27, ["Handicrafting"] = 100, ["Alchemy"] = 100, ["Cooking"] = 63
    }.ToImmutableDictionary();
    private sealed record Example(string Name, int RecipeId, bool Transfer, CraftingPlanResult Result,
        ImmutableDictionary<int, CraftingPrice> BuyPrices, ImmutableDictionary<int, CraftingPrice> SellPrices,
        ImmutableDictionary<int, int> RecipeChoices, ImmutableDictionary<CraftingIngredientKey, int> ReplacementChoices,
        ImmutableDictionary<int, long> OwnedItems);

    private static ImmutableArray<Example> CreateExamples()
    {
        var catalog = CraftingCatalog.Default;
        var prices = catalog.Items.Where(item => item.IsEquipment == false).ToImmutableDictionary(item => item.Id,
            item => new CraftingPrice(item.Id, 100m, CraftingPriceSource.Manual));
        var cooking = catalog.FindRecipe(CookingRecipeId)!;
        var cookingPrices = cooking.Ingredients.Select(ingredient => ingredient.ItemId).Distinct()
            .ToImmutableDictionary(itemId => itemId, itemId => prices[itemId]);
        // Native base and COMBO both output item 510715025 (quantities 1 and 2).
        var sales = ImmutableDictionary<int, CraftingPrice>.Empty.Add(cooking.OutputItemId,
            new(cooking.OutputItemId, 40_000m, CraftingPriceSource.Manual));
        var normalResult = new CraftingCalculator(catalog).Calculate(new CraftingPlanRequest
        {
            RecipeId = CookingRecipeId, Faction = "Asmodians", ProficiencyLevels = Levels, Samples = Samples,
            BuyPrices = cookingPrices, SellPrices = sales
        });
        // These exact routes, gear choices and material stock are copied from the existing native production fixture.
        var routes = new Dictionary<int, int>
        {
            [110430100] = 221043011, [110430019] = 221043001, [110430021] = 241043001,
            [110430014] = 241044001, [110440014] = 241045001, [110450016] = 241046001
        }.ToImmutableDictionary();
        var replacements = new Dictionary<CraftingIngredientKey, int>();
        foreach (var recipeId in new[] { TransferRecipeId, 221043011, 221043001, 221044001, 221045001, 221046001 })
            foreach (var ingredient in catalog.FindRecipe(recipeId)!.Ingredients)
                replacements[new(recipeId, ingredient.ItemId)] = ingredient.ItemId;
        replacements[new(TransferRecipeId, 110430021)] = TransferGearId;
        var transfer = new TransferCraftingPlanRequest
        {
            RecipeId = TransferRecipeId, TargetQuantity = 1, Faction = "Asmodians", Samples = Samples,
            ProficiencyLevels = Levels, RecipeChoices = routes, ReplacementChoices = replacements.ToImmutableDictionary(), BuyPrices = prices
        };
        var owned = transfer with
        {
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(TransferGearId, 1).Add(ConsumedGearId, 1).Add(OwnedRawMaterialId, 50)
        };
        var calculator = new TransferCraftingCalculator(catalog);
        Example TransferExample(string name, TransferCraftingPlanRequest request) => new(name, TransferRecipeId, true,
            calculator.Calculate(request), prices, ImmutableDictionary<int, CraftingPrice>.Empty, routes,
            request.ReplacementChoices, request.OwnedItems);
        return
        [
            new("normal", CookingRecipeId, false, normalResult, cookingPrices, sales, ImmutableDictionary<int, int>.Empty,
                ImmutableDictionary<CraftingIngredientKey, int>.Empty, ImmutableDictionary<int, long>.Empty),
            TransferExample("transfer-owned", owned), TransferExample("transfer-chain", transfer)
        ];
    }

    private static CraftingCalculatorSnapshot State(Example example, string language) => new()
    {
        Profession = example.Transfer ? "Transfercrafting" : "Cooking", Subcategory = example.Transfer ? "Bow" : "Food",
        SelectedRecipeId = example.RecipeId, TargetItemId = CraftingCatalog.Default.FindRecipe(example.RecipeId)!.OutputItemId,
        Faction = "Asmodians", ObservedLevels = Levels, ProficiencyFresh = true, RecipeChoices = example.RecipeChoices,
        ReplacementChoices = example.ReplacementChoices, OwnedItems = example.OwnedItems, BuyPrices = example.BuyPrices,
        SellPrices = example.SellPrices, ServerName = language == "de" ? "Beispielmarkt" : "Example market",
        RequiredPriceCount = example.Transfer ? 0 : example.BuyPrices.Keys.Concat(example.SellPrices.Keys).Distinct().Count(),
        Status = "Beispielrechnung mit manuellen Preisen.", SimulationSamples = Samples, Result = example.Result
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
        public Task<string> Render(CraftingCalculatorSnapshot state) => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<CraftingCalculatorView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CraftingCalculatorView.Catalog)] = CraftingCatalog.Default, [nameof(CraftingCalculatorView.State)] = state
            }))).ToHtmlString());
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previous;
        }
    }
}
