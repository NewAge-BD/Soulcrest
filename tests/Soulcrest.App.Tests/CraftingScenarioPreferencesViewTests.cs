using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
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
public sealed class CraftingScenarioPreferencesViewTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EquipmentComboCardsShowConcreteTargetsBaseIngredientsAndExactSelection(string language)
    {
        await using var fixture = new Fixture(language);
        var catalog = Catalog();
        var html = await fixture.Render<CraftingCalculatorView>(catalog, State());
        var cards = Cards(html);
        Assert.Equal(new[] { 100, 101, 102 }, cards.Keys.Order().ToArray());
        Assert.Contains("aria-pressed=\"false\"", cards[100]);
        Assert.Contains("aria-pressed=\"true\"", cards[101]);
        Assert.Contains("aria-pressed=\"false\"", cards[102]);
        Assert.Contains(language == "de" ? "COMBO-Schwert" : "Combo sword", cards[101]);
        Assert.Contains("synthetic/combo.png", cards[101]);
        Assert.Contains("synthetic/ore.png", cards[101]);
        Assert.Contains("<b>2</b>", cards[101]);
        Assert.Contains(UiText.T("COMBO-Aktivierung") + " 25 %", cards[101]);
        Assert.Contains(UiText.T("COMBO-Aktivierung") + " 10 %", cards[102]);
        Assert.Contains("data-crafting-combo-target=\"101\"", cards[101]);
        Assert.DoesNotContain("data-crafting-combo-target", cards[100]);
        Assert.Contains(UiText.T("Zielmenge COMBO"), html);
        Assert.Contains(100, fixture.VariantIds);
        Assert.Contains(101, fixture.VariantIds);
        Assert.Contains(102, fixture.VariantIds);
        Assert.Contains(UiText.F("{0} COMBO-Zielitems: Versuche bis zur Zielmenge; Basis- und COMBO-Fehlschläge werden berücksichtigt.", "1"), html);
        var targetRow = Regex.Match(html, "<div[^>]*data-crafting-combo-output[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("data-crafting-output=\"2\"", targetRow);
        Assert.Contains("140", targetRow);
        Assert.DoesNotContain("99999", targetRow);
        Assert.Contains(UiText.T("COMBO-Ziel · Nebenprodukte separat"), html);
        var netMetric = Regex.Match(html, "<span>" + Regex.Escape(UiText.T("Erwarteter Nettoerlös")) + "</span>\\s*<strong>(.*?)<small>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("140", netMetric); Assert.DoesNotContain("—", netMetric);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CookingDoubleOutputKeepsItsSingleBaseCard(string language)
    {
        await using var fixture = new Fixture(language);
        var state = State() with { Profession = "Cooking", Subcategory = "Food", SelectedRecipeId = 201, TargetItemId = 4 };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        var cards = Cards(html);
        Assert.Equal(new[] { 200 }, cards.Keys.ToArray());
        Assert.Contains("aria-pressed=\"true\"", cards[200]);
        Assert.DoesNotContain("data-crafting-combo-target", cards[200]);
        Assert.DoesNotContain(UiText.T("Zielmenge COMBO"), html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task WholeChainReferenceNoticeKeepsCharacterAndInputLevelsUnchanged(string language)
    {
        await using var fixture = new Fixture(language);
        var state = State() with
        {
            ReferenceLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 65),
            RequiredProficiencyLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 65)
        };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        var field = Regex.Match(html, "<input(?=[^>]*aria-label=\"" + Regex.Escape(UiText.F("Rechenlevel für {0}", "Blacksmithing")) + "\")[^>]*>").Value;
        Assert.Contains("value=\"41\"", field);
        Assert.Contains("data-crafting-reference-level=\"Blacksmithing\"", html);
        Assert.Contains(UiText.F("Referenz {0}", 65), html);
        Assert.Contains("data-crafting-reference-simulation=\"true\"", html);
        Assert.Contains(CraftingCalculatorPresentation.ReferenceSimulationHint(state)!, html);
        Assert.Contains(UiText.F("{0}: Charakterlevel {1}, benötigt {2}.", "Blacksmithing", 41, 65), html);
        Assert.Contains(language == "de" ? "60,000000 %" : "60.000000 %", html);
        Assert.Equal(41, state.Level("Blacksmithing"));
        Assert.Equal(41, state.ObservedLevels["Blacksmithing"]);
        Assert.Empty(state.SimulatedLevels);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ManualHighSimulationStillWarnsAboutTheRealCharacterRequirement(string language)
    {
        await using var fixture = new Fixture(language);
        var state = State() with
        {
            SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 100),
            RequiredProficiencyLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 65)
        };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        Assert.Contains("data-crafting-proficiency-requirement", html);
        Assert.Contains(UiText.F("{0}: Charakterlevel {1}, benötigt {2}.", "Blacksmithing", 41, 65), html);
        Assert.DoesNotContain("data-crafting-reference-simulation=\"true\"", html);
        Assert.Contains(UiText.T("Simulation"), html);
        Assert.Equal(100, state.Level("Blacksmithing"));
        Assert.Equal(41, state.ObservedLevels["Blacksmithing"]);
    }

    [Theory]
    [InlineData("de", "saving")]
    [InlineData("en", "saving")]
    [InlineData("de", "saved")]
    [InlineData("en", "saved")]
    [InlineData("de", "readonly")]
    [InlineData("en", "readonly")]
    [InlineData("de", "unavailable")]
    [InlineData("en", "unavailable")]
    [InlineData("de", "error")]
    [InlineData("en", "error")]
    public async Task ScenarioPersistenceStatusDoesNotSuppressTheExistingCalculation(string language, string status)
    {
        await using var fixture = new Fixture(language);
        var saved = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var storage = new CraftingScenarioStorageState(status != "unavailable", status == "readonly", status is "saving" or "error",
            status == "saved" ? saved : null, status == "error" ? CraftingScenarioStore.SaveError : null);
        var state = State() with { ScenarioStorage = storage };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        var row = Regex.Match(html, "<p[^>]*data-crafting-scenario-storage[^>]*>.*?</p>", RegexOptions.Singleline).Value;
        var key = status switch
        {
            "saving" => "Szenario wird gespeichert …", "saved" => "Szenario gespeichert.",
            "error" => CraftingScenarioStore.SaveError, _ => "Szenario nur für diese Sitzung."
        };
        Assert.Contains("role=\"status\"", row);
        Assert.Contains(UiText.T(key), row);
        Assert.Contains("123", html);
        Assert.Contains("data-crafting-combo-output", html);
        Assert.DoesNotContain("class=\"craft-status error\"", html);
        if (status == "saved") Assert.Contains("datetime=\"2026-11-11T12:00:00.0000000+00:00\"", row);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CompletedCostOnlyComboShowsCoveredShoppingAndUnknownProfit(string language)
    {
        await using var fixture = new Fixture(language);
        var state = State() with { Result = new() { Status = CraftingPlanStatus.Unknown, Samples = 100_000,
            ExpectedCost = 0, TargetOutput = new(2, 1, null), Issues = [new("TargetPriceMissing", 2)] } };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        Assert.Contains(UiText.T("Kein Einkauf erforderlich."), html);
        Assert.Contains("data-crafting-combo-output", html);
        Assert.Contains("0 <small>Kina", html);
        Assert.Contains("— <small>Kina", html);
        Assert.DoesNotContain("class=\"craft-status error\"", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TransferComboTargetUsesTheSelectedProductButTheExactBaseSchema(string language)
    {
        await using var fixture = new Fixture(language);
        var state = State() with { Profession = "Transfercrafting", Subcategory = "Greatsword", SelectedRecipeId = 301, TargetItemId = 6 };
        var html = await fixture.Render<CraftingCalculatorView>(Catalog(), state);
        var cards = Cards(html);
        Assert.Equal(new[] { 300, 301 }, cards.Keys.Order().ToArray());
        Assert.Contains("aria-pressed=\"true\"", cards[301]);
        Assert.Contains("aria-pressed=\"false\"", cards[300]);
        Assert.Contains("data-transfer-crafting=\"301\"", html);
        Assert.Matches("<h2><span[^>]*data-item-name=\"true\"[^>]*>\\s*" + Regex.Escape(language == "de" ? "Transfer-COMBO-Schwert" : "Transfer combo sword") + "\\s*</span></h2>", html);
        Assert.Contains(UiText.T("Zielmenge COMBO"), html);
        Assert.Contains("data-transfer-node=\"300\"", html);
        Assert.DoesNotContain(UiText.T("Erwarteter Profit"), html);
        Assert.DoesNotContain(UiText.T("Erwarteter Nettoerlös"), html);
    }

    private static Dictionary<int, string> Cards(string html) => Regex.Matches(html,
        "<button(?=[^>]*data-crafting-recipe=\"(\\d+)\")[^>]*>.*?</button>", RegexOptions.Singleline)
        .ToDictionary(match => int.Parse(match.Groups[1].Value), match => match.Value);

    private static CraftingCalculatorSnapshot State() => new()
    {
        Profession = "Blacksmithing", Subcategory = "Greatsword", SelectedRecipeId = 101, TargetItemId = 2,
        Faction = "Elyos", ProficiencyFresh = true,
        ObservedLevels = CraftingCatalog.Professions.ToImmutableDictionary(profession => profession, profession => profession == "Blacksmithing" ? 41 : 100),
        ScenarioStorage = new(true, false, false, null, null),
        Result = new() { Status = CraftingPlanStatus.Complete, ExpectedCost = 123, P90Cost = 200,
            ExpectedProfit = 17, ExpectedFees = 20, TargetOutput = new(2, 1, 140), Byproducts = [new(1, 7, 99_999)] }
    };

    private static CraftingCatalog Catalog()
    {
        var names = new[] { ("Base sword", "Basis-Schwert"), ("Combo sword", "COMBO-Schwert"), ("Ore", "Erz"),
            ("Food", "Gericht"), ("Transfer sword", "Transfer-Schwert"), ("Transfer combo sword", "Transfer-COMBO-Schwert"),
            ("Template", "Vorlage"), ("Double combo sword", "Doppel-COMBO-Schwert") };
        var icons = new[] { "base", "combo", "ore", "food", "transfer", "transfer-combo", "template", "double-combo" };
        var items = names.Select((name, index) => new CraftingItem { Id = index + 1, NameEn = name.Item1, NameDe = name.Item2,
            Icon = "synthetic/" + icons[index] + ".png", IsTradable = true, IsEquipment = index is not (2 or 3) });
        CraftingRecipe Recipe(int id, int output, int required, int? combo = null, decimal activation = 0) => new()
        {
            Id = id, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Elyos", OutputItemId = output,
            OutputQuantity = 1, RequiredProficiency = required, ComboRecipeId = combo, ComboActivationChance = activation,
            CraftingFee = 0, MechanicsQualified = true, SuccessRates = Enumerable.Repeat(id == 100 ? .8m : .6m, 101).ToImmutableArray()
        };
        return new("synthetic-scenario-ui", items,
        [
            Recipe(100, 1, 55, 101, .25m) with { Ingredients = [new() { ItemId = 3, Quantity = 2 }] },
            Recipe(101, 2, 60, 102, .4m), Recipe(102, 8, 65),
            Recipe(200, 4, 1, 201, .3m) with { Profession = "Cooking", Subcategory = "Food", Ingredients = [new() { ItemId = 3, Quantity = 1 }] },
            Recipe(201, 4, 5) with { Profession = "Cooking", Subcategory = "Food", OutputQuantity = 2 },
            Recipe(300, 5, 0, 301, .25m) with { IsTransferCrafting = true, MechanicsQualified = false, SuccessRates = null,
                TransferItemIds = [7], Ingredients = [new() { ItemId = 7, Quantity = 1 }, new() { ItemId = 3, Quantity = 2 }] },
            Recipe(301, 6, 0) with { IsTransferCrafting = true, MechanicsQualified = false, SuccessRates = null }
        ]);
    }

    public sealed class CapturedView : CraftingCalculatorView
    {
        [Parameter] public Action<CraftingCalculatorView>? Captured { get; set; }
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
        public IReadOnlyList<int> VariantIds { get; private set; } = [];
        public Task<string> Render<T>(CraftingCatalog catalog, CraftingCalculatorSnapshot state) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                CraftingCalculatorView? captured = null;
                var root = await _renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["Catalog"] = catalog, ["State"] = state, ["Captured"] = (Action<CraftingCalculatorView>)(value => captured = value) }));
                ReadVariants(captured!);
                return WebUtility.HtmlDecode(root.ToHtmlString());
            });
#pragma warning disable BL0006
        private void ReadVariants(CraftingCalculatorView component)
        {
            using var builder = new RenderTreeBuilder();
            typeof(CraftingCalculatorView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames.Array[index].FrameType != RenderTreeFrameType.Component || frames.Array[index].ComponentType != typeof(ItemSelect)) continue;
                var attributes = new Dictionary<string, object?>();
                for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == RenderTreeFrameType.Attribute; next++)
                    attributes[frames.Array[next].AttributeName] = frames.Array[next].AttributeValue;
                if (attributes.ContainsKey("data-crafting-target-variant"))
                    VariantIds = Assert.IsAssignableFrom<IReadOnlyList<ItemSelectOption>>(attributes[nameof(ItemSelect.Options)]).Select(option => option.Value).ToArray();
            }
        }
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previous;
        }
    }
}
