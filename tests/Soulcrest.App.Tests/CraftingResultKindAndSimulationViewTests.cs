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

[Collection("Settings file")]
public sealed class CraftingResultKindAndSimulationViewTests
{
    [Theory]
    [InlineData("de", 100)]
    [InlineData("en", 100)]
    [InlineData("de", 101)]
    [InlineData("en", 101)]
    public async Task EquipmentCardsDistinguishNormalProductsFromComboEndProductsAndActivation(string language, int selectedId)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(State(selectedId));
        var cards = Cards(html);
        Assert.Equal(new[] { 100, 101, 102 }, cards.Keys.Order().ToArray());
        Assert.Contains("data-crafting-result-kind=\"normal\"", cards[100]);
        Assert.DoesNotContain("craft-combo-product-badge", cards[100]);
        Assert.DoesNotContain("combo-target", CardClass(cards[100]));
        Assert.Contains(UiText.T("COMBO möglich"), cards[100]);
        foreach (var id in new[] { 101, 102 })
        {
            Assert.Contains("data-crafting-result-kind=\"combo\"", cards[id]);
            Assert.Contains("combo-target", CardClass(cards[id]));
            var badge = Regex.Match(cards[id], "<span[^>]*class=\"[^\"]*craft-combo-product-badge[^\"]*\"[^>]*>\\s*(?:<span[^>]*aria-hidden=\"true\"[^>]*>.*?</span>)?\\s*[^<]*</span>", RegexOptions.Singleline).Value;
            Assert.Contains(UiText.T("COMBO-Endprodukt"), badge);
            Assert.DoesNotContain("%", badge);
            var activation = ComboActivation(cards[id]);
            Assert.Contains(UiText.T("COMBO-Aktivierung"), activation);
            Assert.Contains(id == 101 ? "25 %" : "10 %", activation);
            // 10% is the activation chain .25 * .4, independent of the .8/.6 success rates.
            Assert.DoesNotContain("40 %", activation);
        }
        foreach (var (id, card) in cards)
            Assert.Contains("aria-pressed=\"" + (id == selectedId ? "true" : "false") + "\"", card);
        if (language == "en")
        {
            Assert.NotEqual("COMBO-Endprodukt", UiText.T("COMBO-Endprodukt"));
            Assert.NotEqual("COMBO-Aktivierung", UiText.T("COMBO-Aktivierung"));
            Assert.NotEqual("COMBO möglich", UiText.T("COMBO möglich"));
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task GuaranteedTransferStagesKeepComboEndProductAndActivationSeparate(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(State(301) with { Profession = "Transfercrafting" });
        var cards = Cards(html);
        Assert.Equal(new[] { 300, 301 }, cards.Keys.Order().ToArray());
        Assert.Contains("data-crafting-result-kind=\"normal\"", cards[300]);
        Assert.DoesNotContain("craft-combo-product-badge", cards[300]);
        Assert.Contains("data-crafting-result-kind=\"combo\"", cards[301]);
        Assert.Contains("craft-combo-product-badge", cards[301]);
        Assert.Contains(UiText.T("COMBO-Endprodukt"), cards[301]);
        Assert.Contains(UiText.T("COMBO-Aktivierung"), ComboActivation(cards[301]));
        Assert.Contains("25 %", ComboActivation(cards[301]));
        Assert.Contains("100 %", cards[301]);
        Assert.Contains("aria-pressed=\"true\"", cards[301]);
        Assert.Contains("aria-pressed=\"false\"", cards[300]);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CookingDoubleOutputKeepsNormalResultKindWithoutEquipmentComboBadge(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(State(201) with { Profession = "Cooking", Subcategory = "Food" });
        var cards = Cards(html);
        Assert.Single(cards);
        Assert.Contains(200, cards.Keys);
        Assert.Contains("data-crafting-result-kind=\"normal\"", cards[200]);
        Assert.DoesNotContain("craft-combo-product-badge", cards[200]);
        Assert.Contains(UiText.T("COMBO möglich"), cards[200]);
    }

    [Theory]
    [InlineData("de", null)]
    [InlineData("en", null)]
    [InlineData("de", 1)]
    [InlineData("en", 1)]
    [InlineData("de", 2500)]
    [InlineData("en", 2500)]
    [InlineData("de", 100000)]
    [InlineData("en", 100000)]
    public async Task SimulationSampleInputDisplaysDefaultOrScenarioValueWithinSupportedLimits(string language, int? customSamples)
    {
        await using var fixture = new Fixture(language);
        var state = State(101);
        if (customSamples is { } samples) state = state with { SimulationSamples = samples };
        var html = await fixture.Render(state);
        var fields = Regex.Matches(html, "<input(?=[^>]*data-crafting-simulation-samples)[^>]*>");
        var field = Assert.Single(fields.Cast<Match>()).Value;
        Assert.Contains("type=\"number\"", field);
        Assert.Contains("min=\"1\"", field);
        Assert.Contains("max=\"100000\"", field);
        Assert.Contains("step=\"1\"", field);
        Assert.Contains("aria-label=\"" + UiText.T("Anzahl der Simulationsläufe") + "\"", field);
        Assert.Contains("value=\"" + (customSamples ?? 1000) + "\"", field);
        Assert.Contains(UiText.T("Simulationsläufe"), html);
        Assert.Equal(customSamples ?? 1000, state.SimulationSamples);
        if (language == "en") Assert.NotEqual("Simulationsläufe", UiText.T("Simulationsläufe"));
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("en", false)]
    [InlineData("de", true)]
    [InlineData("en", true)]
    public async Task SimulationConfigurationRemainsAvailableInTransferOrWithoutASelectedRecipe(string language, bool transfer)
    {
        await using var fixture = new Fixture(language);
        var state = State(301) with { Profession = transfer ? "Transfercrafting" : "Blacksmithing", SelectedRecipeId = transfer ? 301 : null };
        var html = await fixture.Render(state);
        Assert.Single(Regex.Matches(html, "<input(?=[^>]*data-crafting-simulation-samples)[^>]*>").Cast<Match>());
    }

    private static Dictionary<int, string> Cards(string html) => Regex.Matches(html,
        "<button(?=[^>]*data-crafting-recipe=\"(\\d+)\")[^>]*>.*?</button>", RegexOptions.Singleline)
        .ToDictionary(match => int.Parse(match.Groups[1].Value), match => match.Value);
    private static string CardClass(string card) => Regex.Match(card, "class=\"([^\"]+)\"").Groups[1].Value;
    private static string ComboActivation(string card) => Regex.Match(card,
        "<span[^>]*class=\"[^\"]*craft-combo-tag[^\"]*\"[^>]*>.*?</span>", RegexOptions.Singleline).Value;

    private static CraftingCalculatorSnapshot State(int selectedId) => new()
    {
        Profession = "Blacksmithing", Subcategory = "Greatsword", SelectedRecipeId = selectedId, Faction = "Elyos",
        ObservedLevels = CraftingCatalog.Professions.ToImmutableDictionary(profession => profession, _ => 100), ProficiencyFresh = true
    };

    private static CraftingCatalog Catalog()
    {
        var items = Enumerable.Range(1, 7).Select(id => new CraftingItem
        {
            Id = id, NameEn = "Synthetic product " + id, NameDe = "Synthetisches Produkt " + id,
            IsEquipment = id is not (4 or 7), IsTradable = true
        });
        CraftingRecipe Recipe(int id, int output, int? combo = null, decimal activation = 0) => new()
        {
            Id = id, OutputItemId = output, OutputQuantity = 1, Profession = "Blacksmithing", Subcategory = "Greatsword",
            Faction = "Elyos", RequiredProficiency = 1, CraftingFee = 0, MechanicsQualified = true,
            ComboRecipeId = combo, ComboActivationChance = activation,
            SuccessRates = Enumerable.Repeat(id == 100 ? .8m : .6m, 101).ToImmutableArray()
        };
        return new("synthetic-result-kind-ui", items,
        [
            Recipe(100, 1, 101, .25m) with { Ingredients = [new() { ItemId = 7, Quantity = 1 }] },
            Recipe(101, 2, 102, .4m), Recipe(102, 3),
            Recipe(200, 4, 201, .3m) with { Profession = "Cooking", Subcategory = "Food", Ingredients = [new() { ItemId = 7, Quantity = 1 }] },
            Recipe(201, 4) with { Profession = "Cooking", Subcategory = "Food", OutputQuantity = 2 },
            Recipe(300, 5, 301, .25m) with { IsTransferCrafting = true, MechanicsQualified = false, SuccessRates = null,
                TransferItemIds = [1], Ingredients = [new() { ItemId = 1, Quantity = 1 }, new() { ItemId = 7, Quantity = 1 }] },
            Recipe(301, 6) with { IsTransferCrafting = true, MechanicsQualified = false, SuccessRates = null }
        ]);
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
        public Task<string> Render(CraftingCalculatorSnapshot state) => _renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await _renderer.RenderComponentAsync<CraftingCalculatorView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Catalog"] = Catalog(), ["State"] = state }))).ToHtmlString()));
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previous;
        }
    }
}
