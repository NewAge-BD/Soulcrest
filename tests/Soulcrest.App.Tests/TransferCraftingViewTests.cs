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
public sealed class TransferCraftingViewTests
{
    [Theory]
    [InlineData(false, "de", "Kein Einkauf erforderlich.")]
    [InlineData(false, "en", "No purchase required.")]
    [InlineData(true, "de", "Kein Einkauf erforderlich.")]
    [InlineData(true, "en", "No purchase required.")]
    public async Task EmptyShoppingListsDistinguishCompleteOwnedStockFromUnknownOrLoading(bool transfer, string language, string noPurchase)
    {
        await using var fixture = new Fixture(language);
        var parameters = new Dictionary<string, object?> { [nameof(CraftingCalculatorView.Catalog)] = Catalog() };
        var state = State() with { SelectedRecipeId = transfer ? 100 : 200, Result = new() { Status = CraftingPlanStatus.Complete, ExpectedCost = 0 } };
        foreach (var busy in new[] { false, true })
        {
            parameters[nameof(CraftingCalculatorView.State)] = state with { Busy = busy };
            var html = WebUtility.HtmlDecode(transfer ? await fixture.Render<TransferCraftingView>(parameters) : await fixture.Render<CraftingCalculatorView>(parameters));
            Assert.Contains(noPurchase, html);
            Assert.DoesNotContain(UiText.T("Der Bedarf wird berechnet …"), html);
        }
        parameters[nameof(CraftingCalculatorView.State)] = state with { Result = new() { Status = CraftingPlanStatus.Unknown } };
        var unknown = WebUtility.HtmlDecode(transfer ? await fixture.Render<TransferCraftingView>(parameters) : await fixture.Render<CraftingCalculatorView>(parameters));
        Assert.DoesNotContain(noPurchase, unknown);
        Assert.Contains(UiText.T(transfer ? "Der Materialbedarf erscheint nach einer vollständigen Kostenrechnung." : "Der vollständige Bedarf erscheint nach einer belegten Kalkulation."), unknown);
        parameters[nameof(CraftingCalculatorView.State)] = state with { Result = null, Busy = true };
        var loading = WebUtility.HtmlDecode(transfer ? await fixture.Render<TransferCraftingView>(parameters) : await fixture.Render<CraftingCalculatorView>(parameters));
        Assert.Contains(UiText.T("Der Bedarf wird berechnet …"), loading); Assert.DoesNotContain(noPurchase, loading);
    }

    [Theory]
    [InlineData("de", "Erwartete Herstellungskosten", "Zielmenge")]
    [InlineData("en", "Expected crafting costs", "Target quantity")]
    public async Task CostOnlyViewKeepsExistingCostsDuringRefreshAndNeverDisplaysSaleCredits(string language, string costs, string quantity)
    {
        await using var fixture = new Fixture(language);
        var state = State() with { Busy = true, Result = Complete(), ProfileId = "SYNTHETIC-PRIVATE-TRANSFER-PROFILE" };
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingView>(new()
        {
            [nameof(TransferCraftingView.Catalog)] = Catalog(), [nameof(TransferCraftingView.State)] = state,
            [nameof(TransferCraftingView.Quantity)] = 2
        }));
        Assert.Contains(costs, html); Assert.Contains(quantity, html);
        Assert.Matches("data-transfer-expected-cost(?:=\"\")?>123", html);
        Assert.DoesNotContain("data-transfer-p90-cost", html); Assert.DoesNotContain("90-%", html); Assert.DoesNotContain("90%", html);
        Assert.Contains("data-transfer-shopping=\"4\"", html); Assert.Contains("data-transfer-byproduct=\"9\"", html);
        Assert.DoesNotContain("data-transfer-shopping=\"2\"", html);
        Assert.DoesNotContain("77,777,777", html); Assert.DoesNotContain("77.777.777", html);
        Assert.DoesNotContain("999999", html); Assert.DoesNotContain("999,999", html); Assert.DoesNotContain("999.999", html);
        Assert.DoesNotContain(UiText.T("Erwarteter Profit"), html); Assert.DoesNotContain(UiText.T("Nettoerlös des Ziels"), html);
        Assert.DoesNotContain(state.ProfileId, html);
        Assert.Contains("craft-spinner", html); Assert.Empty(state.SimulatedLevels);
    }

    [Fact]
    public async Task ProtectedEquipmentRequiresAnExactChoiceBeforeItsOwnershipCanBeEntered()
    {
        await using var fixture = new Fixture("en");
        TransferCraftingTree? tree = null;
        var parameters = TreeParameters(Catalog(), State(explicitChoice: false));
        parameters[nameof(CaptureTree.Captured)] = (Action<TransferCraftingTree>)(value => tree = value);
        var html = await fixture.Render<CaptureTree>(parameters);
        Assert.Contains("data-transfer-selection-required", html);
        Assert.DoesNotContain("data-transfer-owned=\"2\"", html); Assert.DoesNotContain("data-transfer-owned=\"3\"", html);
        var selectParameters = await fixture.SelectParameters(tree!, "data-transfer-replacement", "100:2");
        ItemSelect? select = null;
        selectParameters[nameof(CaptureSelect.Captured)] = (Action<ItemSelect>)(value => select = value);
        var selectRoot = await fixture.RenderRoot<CaptureSelect>(selectParameters);
        await fixture.Event(select!, "role", "combobox", "onclick", new MouseEventArgs());
        var options = WebUtility.HtmlDecode(await fixture.Html(selectRoot));
        Assert.Contains("role=\"listbox\"", options);
        Assert.Contains("from normal crafting", options); Assert.Contains("from transfer crafting", options);
        Assert.DoesNotContain("data-transfer-automatic-producer=\"200\"", html);
    }

    [Fact]
    public async Task EquipmentNeverShowsBuyPricesAndSingleProducerIsAutomaticWithoutAnEvent()
    {
        await using var fixture = new Fixture("en");
        var events = 0; var state = State();
        var parameters = TreeParameters(Catalog(), state);
        parameters[nameof(TransferCraftingTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, _ => events++);
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingTree>(parameters));
        Assert.Equal(0, events);
        Assert.Contains("data-transfer-automatic-producer=\"200\"", html);
        Assert.Contains("data-transfer-node=\"200\"", html);
        Assert.DoesNotContain("data-transfer-material-source=\"2\"", html);
        Assert.DoesNotContain("data-transfer-material-source=\"5\"", html);
        Assert.DoesNotContain("data-transfer-material-source=\"6\"", html);
        Assert.DoesNotContain("77,777,777", html);
        Assert.Contains("Protected transfer equipment", html); Assert.Contains("Consumed equipment", html);
        Assert.Contains("data-transfer-producer-missing", html);
        Assert.Equal(10, state.Level("Blacksmithing")); Assert.Empty(state.SimulatedLevels);
        Assert.Contains("100.000000", SuccessNode(html, 100));
        Assert.Contains("80.000000", SuccessNode(html, 200));
    }

    [Fact]
    public async Task MultipleEquipmentProducersRemainUnselectedUntilTheUserChoosesOne()
    {
        await using var fixture = new Fixture("en");
        var catalog = Catalog(ambiguous: true);
        var html = await fixture.Render<TransferCraftingTree>(TreeParameters(catalog, State()));
        Assert.Contains("data-transfer-producer=\"2\"", html);
        Assert.DoesNotContain("data-transfer-node=\"200\"", html); Assert.DoesNotContain("data-transfer-node=\"201\"", html);
        var state = State() with { RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(2, 201) };
        html = await fixture.Render<TransferCraftingTree>(TreeParameters(catalog, state));
        Assert.Contains("data-transfer-node=\"201\"", html); Assert.DoesNotContain("data-transfer-node=\"200\"", html);
    }

    [Fact]
    public async Task ExactEquipmentSelectionOwnershipAndCraftSourceHaveIndependentCallbacks()
    {
        await using var fixture = new Fixture("de");
        TransferCraftingTree? tree = null;
        CraftingReplacementSelection? replacement = null; CraftingOwnershipSelection? ownership = null; CraftingSourceSelection? source = null;
        var parameters = TreeParameters(Catalog(ambiguous: true, materialRoutes: true), State() with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(4, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(4, 500)
        });
        parameters[nameof(CaptureTree.Captured)] = (Action<TransferCraftingTree>)(value => tree = value);
        parameters[nameof(TransferCraftingTree.ReplacementChanged)] = EventCallback.Factory.Create<CraftingReplacementSelection>(this, value => replacement = value);
        parameters[nameof(TransferCraftingTree.OwnershipChanged)] = EventCallback.Factory.Create<CraftingOwnershipSelection>(this, value => ownership = value);
        parameters[nameof(TransferCraftingTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, value => source = value);
        await fixture.Render<CaptureTree>(parameters);
        await fixture.Select(tree!, "data-transfer-replacement", "100:2", 3);
        Assert.Equal(new CraftingReplacementSelection(100, 2, 3), replacement);
        await fixture.Event(tree!, "data-transfer-owned", "2", "onchange", new ChangeEventArgs { Value = true });
        Assert.Equal(new CraftingOwnershipSelection(2, true), ownership);
        await fixture.Select(tree!, "data-transfer-producer", "2", 201);
        Assert.Equal(new CraftingSourceSelection(2, 201), source);
        await fixture.Event(tree!, "data-transfer-buy-deficit", "4", "onclick", new MouseEventArgs());
        Assert.Equal(new CraftingSourceSelection(4, null), source);
    }

    [Fact]
    public async Task ManualOwnershipMeansOneCopyAndMissingProducerStaysExplicit()
    {
        await using var fixture = new Fixture("en");
        var state = State() with { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 1).Add(6, 1) };
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingTree>(TreeParameters(Catalog(), state)));
        Assert.Contains("Already owned (1×)", html); Assert.Contains("The owned copy is used first", html);
        Assert.Contains("No proven crafting route; the equipment must already be owned.", html);
        Assert.Matches("data-transfer-owned=\"2\"[^>]*checked", html);
        Assert.Contains("data-transfer-producer-missing", html);
    }

    [Fact]
    public async Task OneOwnedCopyKeepsUnusedProducerSchemasClosedWithoutRouteOrLevelWarnings()
    {
        await using var fixture = new Fixture("en");
        var state = State() with
        {
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 1).Add(5, 1).Add(6, 1),
            ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 0)
        };
        var parameters = TreeParameters(Catalog(ambiguous: true), state);
        parameters[nameof(TransferCraftingTree.Quantity)] = 1;
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingTree>(parameters));
        Assert.DoesNotContain("data-transfer-producer=\"2\"", html);
        Assert.DoesNotContain("data-transfer-producer-missing", html);
        Assert.DoesNotContain("tc-requirement", html);
        foreach (var recipe in new[] { 200, 201, 300 })
        {
            var details = Regex.Match(html, "<details[^>]*data-transfer-owned-preview=\"" + recipe + "\"[^>]*>").Value;
            Assert.NotEmpty(details); Assert.DoesNotContain("open", details);
        }
        Assert.Contains(UiText.T("Nur Schema · vorhandene Kopie wird verwendet"), html);
        Assert.Contains("The owned copy is used first", html);
        Assert.DoesNotContain("data-transfer-material-source=\"2\"", html);
        Assert.DoesNotContain("77,777,777", html);
    }

    [Fact]
    public async Task MoreThanOneRequestedCopyRestoresActiveProducerRoutesAndRequirements()
    {
        await using var fixture = new Fixture("en");
        var state = State() with
        {
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(2, 1).Add(5, 1).Add(6, 1),
            ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 0),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(2, 200)
        };
        var html = await fixture.Render<TransferCraftingTree>(TreeParameters(Catalog(ambiguous: true), state));
        Assert.Contains("data-transfer-producer=\"2\"", html);
        Assert.Contains("data-transfer-producer-missing", html);
        Assert.Contains("data-transfer-node=\"200\"", html);
        Assert.Contains("tc-requirement", html);
        Assert.Matches("<details[^>]*open(?:=\"\")?[^>]*>", html);
        Assert.DoesNotContain("data-transfer-owned-preview", html);
        Assert.Matches("data-transfer-owned=\"2\"[^>]*checked", html);
    }

    [Theory]
    [InlineData(CraftingPriceSource.Npc)]
    [InlineData(CraftingPriceSource.Exchange)]
    [InlineData(CraftingPriceSource.ExchangeAverage)]
    public async Task NonEquipmentMaterialsKeepTheirExplicitPriceSource(CraftingPriceSource source)
    {
        await using var fixture = new Fixture("en");
        var state = State();
        state = state with { BuyPrices = state.BuyPrices.SetItem(4, new(4, 3m, source)) };
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingTree>(TreeParameters(Catalog(), state)));
        Assert.Contains(CraftingCalculatorPresentation.Source(source), html);
        Assert.Contains("data-transfer-owned-quantity=\"4\"", html);
        Assert.DoesNotContain("data-transfer-material-source", html);
        Assert.DoesNotContain("data-transfer-material-source=\"2\"", html);
    }

    [Fact]
    public async Task RawMaterialStockAcceptsOnlyBoundedWholeQuantitiesAndKeepsGearOwnershipSeparate()
    {
        await using var fixture = new Fixture("en");
        TransferCraftingTree? tree = null; CraftingOwnedQuantitySelection? stock = null;
        var state = State() with { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(4, 23) };
        var parameters = TreeParameters(Catalog(), state);
        parameters[nameof(CaptureTree.Captured)] = (Action<TransferCraftingTree>)(value => tree = value);
        parameters[nameof(TransferCraftingTree.OwnedQuantityChanged)] = EventCallback.Factory.Create<CraftingOwnedQuantitySelection>(this, value => stock = value);
        var html = await fixture.Render<CaptureTree>(parameters);
        Assert.Matches("data-transfer-owned-quantity=\"4\"[^>]*value=\"23\"", html);
        Assert.DoesNotContain("data-transfer-owned-quantity=\"2\"", html);
        Assert.Contains("data-transfer-owned=\"2\"", html);
        await fixture.Event(tree!, "data-transfer-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = "1000000000" });
        Assert.Equal(new CraftingOwnedQuantitySelection(4, 1_000_000_000), stock);
        foreach (var invalid in new[] { "-1", "1000000001", "1.5", "" })
        {
            stock = null;
            await fixture.Event(tree!, "data-transfer-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = invalid });
            Assert.Null(stock);
        }
        await fixture.Event(tree!, "data-transfer-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = "0" });
        Assert.Equal(new CraftingOwnedQuantitySelection(4, 0), stock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOwnedQuantitiesShowALocalAlertUntilAValidWholeQuantityIsEntered(bool transfer)
    {
        await using var fixture = new Fixture("en");
        IComponent? tree = null; CraftingOwnedQuantitySelection? stock = null;
        var catalog = Catalog();
        var parameters = new Dictionary<string, object?>
        {
            [nameof(CraftingRecipeTree.Catalog)] = catalog,
            [nameof(CraftingRecipeTree.Recipe)] = catalog.FindRecipe(100),
            [nameof(CraftingRecipeTree.State)] = State(),
            [nameof(CraftingRecipeTree.OwnedQuantityChanged)] = EventCallback.Factory.Create<CraftingOwnedQuantitySelection>(this, value => stock = value)
        };
        parameters["Captured"] = transfer ? (object)(Action<TransferCraftingTree>)(value => tree = value)
            : (Action<CraftingRecipeTree>)(value => tree = value);
        var root = transfer ? await fixture.RenderRoot<CaptureTree>(parameters) : await fixture.RenderRoot<CaptureNormalTree>(parameters);
        var prefix = transfer ? "data-transfer" : "data-crafting";
        Assert.DoesNotContain(prefix + "-owned-error", await fixture.Html(root));
        foreach (var invalid in new[] { "-1", "1000000001", "2.5", "not-an-integer" })
        {
            stock = null;
            await fixture.Event(tree!, prefix + "-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = invalid });
            var html = WebUtility.HtmlDecode(await fixture.Html(root));
            Assert.Contains("role=\"alert\"", html);
            Assert.Contains(prefix + "-owned-error=\"4\"", html);
            Assert.Contains(UiText.T("Die vorhandene Menge muss eine ganze Zahl zwischen 0 und 1.000.000.000 sein."), html);
            Assert.Null(stock);
            await fixture.Event(tree!, prefix + "-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = "7" });
            Assert.Equal(new CraftingOwnedQuantitySelection(4, 7), stock);
            Assert.DoesNotContain(prefix + "-owned-error", await fixture.Html(root));
        }
    }

    [Fact]
    public async Task AnExplicitlyNormalComboDoesNotInheritTheTransferParentsGuaranteedSuccessDisplay()
    {
        await using var fixture = new Fixture("en");
        var catalog = Catalog();
        catalog = new(catalog.MetadataBuild, catalog.Items, catalog.Recipes.Select(recipe => recipe.Id != 101 ? recipe : recipe with
        {
            IsTransferCrafting = false, MechanicsQualified = true, RequiredProficiency = 5,
            SuccessRates = Enumerable.Repeat(.5m, 96).ToImmutableArray()
        }));
        var html = WebUtility.HtmlDecode(await fixture.Render<TransferCraftingTree>(TreeParameters(catalog, State())));
        Assert.Contains("100.000000", SuccessNode(html, 100));
        var combo = Regex.Match(html, "data-transfer-combo=\"101\".*?</small>", RegexOptions.Singleline).Value;
        Assert.Contains("50.000000", combo); Assert.DoesNotContain("100.000000", combo);
    }

    [Fact]
    public async Task RawMaterialCraftingUsesAnExplicitSubrecipeActionAndCanReturnToBuyingOnlyTheDeficit()
    {
        await using var fixture = new Fixture("en");
        TransferCraftingTree? tree = null; CraftingSourceSelection? source = null;
        var parameters = TreeParameters(Catalog(materialRoutes: true), State());
        parameters[nameof(CaptureTree.Captured)] = (Action<TransferCraftingTree>)(value => tree = value);
        parameters[nameof(TransferCraftingTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, value => source = value);
        var html = await fixture.Render<CaptureTree>(parameters);
        Assert.DoesNotContain("value=\"buy\"", html);
        Assert.Contains("data-transfer-use-subrecipe=\"4\"", html);
        await fixture.Event(tree!, "data-transfer-use-subrecipe", "4", "onclick", new MouseEventArgs());
        Assert.Equal(new CraftingSourceSelection(4, 500), source);
        var active = State() with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(4, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(4, 500)
        };
        parameters[nameof(TransferCraftingTree.State)] = active;
        html = await fixture.Render<CaptureTree>(parameters);
        Assert.Contains("data-transfer-buy-deficit=\"4\"", html);
        await fixture.Event(tree!, "data-transfer-buy-deficit", "4", "onclick", new MouseEventArgs());
        Assert.Equal(new CraftingSourceSelection(4, null), source);
    }

    [Fact]
    public async Task AmbiguousRawMaterialRoutesHaveNoAutomaticChoiceOrBuyOption()
    {
        await using var fixture = new Fixture("en");
        TransferCraftingTree? tree = null; CraftingSourceSelection? source = null;
        var parameters = TreeParameters(Catalog(materialRoutes: true, multipleMaterialRoutes: true), State());
        parameters[nameof(CaptureTree.Captured)] = (Action<TransferCraftingTree>)(value => tree = value);
        parameters[nameof(TransferCraftingTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, value => source = value);
        var html = await fixture.Render<CaptureTree>(parameters);
        Assert.Contains("data-transfer-material-producer=\"4\"", html);
        Assert.DoesNotContain("data-transfer-node=\"500\"", html); Assert.DoesNotContain("data-transfer-node=\"501\"", html);
        Assert.DoesNotContain("data-transfer-use-subrecipe=\"4\"", html); Assert.DoesNotContain("value=\"buy\"", html);
        await fixture.Select(tree!, "data-transfer-material-producer", "4", 501);
        Assert.Equal(new CraftingSourceSelection(4, 501), source);
        source = null;
        await fixture.Select(tree!, "data-transfer-material-producer", "4", 200);
        Assert.Null(source);
    }

    [Fact]
    public async Task NormalRecipeTreeUsesTheSameStockInputAndExplicitSubrecipeActions()
    {
        await using var fixture = new Fixture("en");
        CraftingRecipeTree? tree = null; CraftingOwnedQuantitySelection? stock = null; CraftingSourceSelection? source = null;
        var catalog = Catalog(materialRoutes: true);
        var parameters = new Dictionary<string, object?>
        {
            [nameof(CaptureNormalTree.Captured)] = (Action<CraftingRecipeTree>)(value => tree = value),
            [nameof(CraftingRecipeTree.Catalog)] = catalog, [nameof(CraftingRecipeTree.Recipe)] = catalog.FindRecipe(100),
            [nameof(CraftingRecipeTree.State)] = State() with { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(4, 17) },
            [nameof(CraftingRecipeTree.OwnedQuantityChanged)] = EventCallback.Factory.Create<CraftingOwnedQuantitySelection>(this, value => stock = value),
            [nameof(CraftingRecipeTree.SourceChanged)] = EventCallback.Factory.Create<CraftingSourceSelection>(this, value => source = value)
        };
        var html = await fixture.Render<CaptureNormalTree>(parameters);
        Assert.Matches("data-crafting-owned-quantity=\"4\"[^>]*value=\"17\"", html);
        Assert.DoesNotContain("value=\"buy\"", html);
        await fixture.Event(tree!, "data-crafting-owned-quantity", "4", "onchange", new ChangeEventArgs { Value = "42" });
        Assert.Equal(new CraftingOwnedQuantitySelection(4, 42), stock);
        await fixture.Event(tree!, "data-crafting-use-subrecipe", "4", "onclick", new MouseEventArgs());
        Assert.Equal(new CraftingSourceSelection(4, 500), source);
        parameters[nameof(CraftingRecipeTree.State)] = State() with
        {
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(4, CraftingAcquisition.Craft),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(4, 500)
        };
        html = await fixture.Render<CaptureNormalTree>(parameters);
        Assert.Contains("data-crafting-buy-deficit=\"4\"", html);
        await fixture.Event(tree!, "data-crafting-buy-deficit", "4", "onclick", new MouseEventArgs());
        Assert.Equal(new CraftingSourceSelection(4, null), source);
        parameters[nameof(CraftingRecipeTree.Catalog)] = Catalog(materialRoutes: true, multipleMaterialRoutes: true);
        parameters[nameof(CraftingRecipeTree.State)] = State();
        html = await fixture.Render<CaptureNormalTree>(parameters);
        Assert.Contains("data-crafting-producer=\"4\"", html);
        Assert.DoesNotContain("data-crafting-node=\"500\"", html); Assert.DoesNotContain("data-crafting-node=\"501\"", html);
        Assert.DoesNotContain("data-crafting-use-subrecipe=\"4\"", html); Assert.DoesNotContain("value=\"buy\"", html);
        await fixture.Select(tree!, "data-crafting-producer", "4", 501);
        Assert.Equal(new CraftingSourceSelection(4, 501), source);
        source = null;
        await fixture.Select(tree!, "data-crafting-producer", "4", 200);
        Assert.Null(source);
    }

    [Fact]
    public async Task QuantityEventsAndTreeBoundariesDoNotDependOnNormalCalculatorQuantityFields()
    {
        await using var fixture = new Fixture("en");
        TransferCraftingView? view = null; var quantity = 0;
        await fixture.Render<CaptureView>(new()
        {
            [nameof(CaptureView.Captured)] = (Action<TransferCraftingView>)(value => view = value),
            [nameof(TransferCraftingView.Catalog)] = Catalog(), [nameof(TransferCraftingView.State)] = State(),
            [nameof(TransferCraftingView.QuantityChanged)] = EventCallback.Factory.Create<int>(this, value => quantity = value)
        });
        await fixture.Event(view!, "id", "transfer-target-quantity", "onchange", new ChangeEventArgs { Value = "4" });
        Assert.Equal(4, quantity);
        await fixture.Event(view!, "id", "transfer-target-quantity", "onchange", new ChangeEventArgs { Value = "0" });
        Assert.Equal(4, quantity);
        var parameters = TreeParameters(Catalog(), State());
        parameters[nameof(TransferCraftingTree.Ancestors)] = ImmutableHashSet.Create(100);
        var html = await fixture.Render<TransferCraftingTree>(parameters);
        Assert.Contains("data-transfer-tree-boundary", html); Assert.DoesNotContain("data-transfer-node=", html);
        parameters[nameof(TransferCraftingTree.Ancestors)] = ImmutableHashSet<int>.Empty;
        parameters[nameof(TransferCraftingTree.Depth)] = 12;
        html = await fixture.Render<TransferCraftingTree>(parameters);
        Assert.Contains("data-transfer-tree-boundary", html);
    }

    private static string SuccessNode(string html, int recipeId) => Regex.Match(html,
        "data-transfer-success=\"" + recipeId + "\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Value;

    private static Dictionary<string, object?> TreeParameters(CraftingCatalog catalog, CraftingCalculatorSnapshot state) => new()
    {
        [nameof(TransferCraftingTree.Catalog)] = catalog, [nameof(TransferCraftingTree.Recipe)] = catalog.FindRecipe(100),
        [nameof(TransferCraftingTree.State)] = state, [nameof(TransferCraftingTree.Quantity)] = 2
    };
    private static CraftingCalculatorSnapshot State(bool explicitChoice = true) => new()
    {
        SelectedRecipeId = 100, Faction = "Elyos", Status = "Kalkulation aktualisiert.",
        ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 10),
        ReplacementChoices = explicitChoice ? ImmutableDictionary<CraftingIngredientKey, int>.Empty.Add(new(100, 2), 2) : ImmutableDictionary<CraftingIngredientKey, int>.Empty,
        BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(2, new(2, 77_777_777m, CraftingPriceSource.Exchange))
            .Add(4, new(4, 3m, CraftingPriceSource.Npc))
    };
    private static CraftingPlanResult Complete() => new()
    {
        Status = CraftingPlanStatus.Complete, Samples = 100_000, ExpectedCost = 123, P90Cost = 200,
        ExpectedProfit = 999_999, ExpectedFees = 999_999,
        Shopping = [new(4, 41, 3, 123, CraftingPriceSource.Npc), new(2, 1, 77_777_777, 77_777_777, CraftingPriceSource.Exchange)],
        Steps = [new(100, 2, 2, 0, 1m, .25m)], Byproducts = [new(9, .25m, 999_999m)]
    };
    private static CraftingCatalog Catalog(bool ambiguous = false, bool materialRoutes = false, bool multipleMaterialRoutes = false)
    {
        var items = Enumerable.Range(1, 10).Select(id => new CraftingItem
        {
            Id = id, NameEn = id is 2 or 3 ? "Template armor" : "Synthetic item " + id,
            NameDe = id is 2 or 3 ? "Vorlagenrüstung" : "Synthetisches Item " + id,
            IsEquipment = id is not (4 or 10), IsTradable = true
        });
        var recipes = new List<CraftingRecipe>
        {
            new() { Id = 100, OutputItemId = 1, OutputQuantity = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Elyos",
                RequiredProficiency = 80, IsTransferCrafting = true, TransferItemIds = [2, 3],
                Ingredients = [new() { ItemId = 2, Quantity = 1, ReplacementItemIds = [3] }, new() { ItemId = 5, Quantity = 1 },
                    new() { ItemId = 6, Quantity = 1 }, new() { ItemId = 4, Quantity = 2 }],
                ComboRecipeId = 101, ComboActivationChance = .25m, CraftingFee = 0, MechanicsQualified = false },
            new() { Id = 101, OutputItemId = 9, OutputQuantity = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Elyos", CraftingFee = 0 },
            Producer(200, 2, 2), Producer(300, 5, 1),
            Producer(400, 3, 2) with { IsTransferCrafting = true, MechanicsQualified = false }
        };
        if (ambiguous) recipes.Add(Producer(201, 2, 3));
        if (materialRoutes) recipes.Add(Producer(500, 4, 2) with { Ingredients = [new() { ItemId = 10, Quantity = 2 }] });
        if (multipleMaterialRoutes) recipes.Add(Producer(501, 4, 3) with { Ingredients = [new() { ItemId = 10, Quantity = 3 }] });
        return new("synthetic-transfer-ui", items, recipes);
    }
    private static CraftingRecipe Producer(int id, int itemId, int materialQuantity) => new()
    {
        Id = id, OutputItemId = itemId, OutputQuantity = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Elyos",
        RequiredProficiency = 5, Ingredients = [new() { ItemId = 4, Quantity = materialQuantity }],
        SuccessRates = Enumerable.Repeat(.8m, 96).ToImmutableArray(), CraftingFee = 0, MechanicsQualified = true
    };
    public sealed class CaptureView : TransferCraftingView
    {
        [Parameter] public Action<TransferCraftingView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CaptureTree : TransferCraftingTree
    {
        [Parameter] public Action<TransferCraftingTree>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CaptureNormalTree : CraftingRecipeTree
    {
        [Parameter] public Action<CraftingRecipeTree>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CaptureSelect : ItemSelect
    {
        [Parameter] public Action<ItemSelect>? Captured { get; set; }
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
        public Task<HtmlRootComponent> RenderRoot<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        public Task<string> Html(HtmlRootComponent root) => _renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public Task Select(IComponent component, string attribute, string value, int selected) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ReadSelectParameters(component, attribute, value);
            Assert.IsType<EventCallback<int>>(parameters[nameof(ItemSelect.ValueChanged)]);
            await ((EventCallback<int>)parameters[nameof(ItemSelect.ValueChanged)]!).InvokeAsync(selected);
        });
        public Task<Dictionary<string, object?>> SelectParameters(IComponent component, string attribute, string value) =>
            _renderer.Dispatcher.InvokeAsync(() => ReadSelectParameters(component, attribute, value));
#pragma warning disable BL0006
        private static Dictionary<string, object?> ReadSelectParameters(IComponent component, string attribute, string value)
        {
            using var builder = new RenderTreeBuilder();
            var type = component is CraftingRecipeTree ? typeof(CraftingRecipeTree) : typeof(TransferCraftingTree);
            type.GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames.Array[index].FrameType != RenderTreeFrameType.Component || frames.Array[index].ComponentType != typeof(ItemSelect)) continue;
                var parameters = new Dictionary<string, object?>();
                for (var child = index + 1; child < frames.Count && frames.Array[child].FrameType == RenderTreeFrameType.Attribute; child++)
                    parameters[frames.Array[child].AttributeName] = frames.Array[child].AttributeValue;
                if (parameters.TryGetValue(attribute, out var candidate) && Convert.ToString(candidate, CultureInfo.InvariantCulture) == value) return parameters;
            }
            throw new InvalidOperationException("Item selection component not found.");
        }
#pragma warning restore BL0006
#pragma warning disable BL0006
        public Task Event<T>(IComponent component, string attribute, string value, string eventName, T args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            var type = component is ItemSelect ? typeof(ItemSelect) : component is TransferCraftingView ? typeof(TransferCraftingView)
                : component is CraftingRecipeTree ? typeof(CraftingRecipeTree) : typeof(TransferCraftingTree);
            type.GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var match = frames.Array[index];
                if (match.FrameType != RenderTreeFrameType.Attribute || match.AttributeName != attribute
                    || Convert.ToString(match.AttributeValue, CultureInfo.InvariantCulture) != value) continue;
                var element = index;
                while (element >= 0 && frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                for (var eventIndex = element + 1; element >= 0 && eventIndex < frames.Count && frames.Array[eventIndex].FrameType == RenderTreeFrameType.Attribute; eventIndex++)
                {
                    var frame = frames.Array[eventIndex];
                    if (frame.AttributeName != eventName) continue;
                    if (frame.AttributeValue is EventCallback<T> typed) await typed.InvokeAsync(args);
                    else if (frame.AttributeValue is EventCallback callback) await callback.InvokeAsync(args);
                    else if (frame.AttributeValue is MulticastDelegate handler)
                    { await new EventCallback((IHandleEvent)component, handler).InvokeAsync(args); }
                    else throw new InvalidOperationException("Unexpected transfer callback.");
                    return;
                }
            }
            throw new InvalidOperationException("Transfer callback not found.");
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previous; }
    }
}
