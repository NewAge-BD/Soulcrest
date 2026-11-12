using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ItemControlAndCraftingNameTests
{
    private static int KnownItem => CraftingCatalog.Default.Items.First(item => ItemNamePresentation.ForItem(item.Id).IsKnown).Id;
    private static ItemSelectOption[] Options => [new(1, KnownItem, "Alpha"), new(2, KnownItem, "Beta"), new(3, int.MaxValue, "Gamma <unsafe>")];

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SelectRendersKnownNameColorsAndNeutralUnknownsWithoutNativeOptions(string language)
    {
        await using var fixture = new Fixture(language);
        ItemSelect? control = null;
        var root = await fixture.Render<CaptureSelect>(new() { ["Options"] = Options, ["Value"] = 1,
            ["Label"] = "Synthetic choice", ["Captured"] = (Action<ItemSelect>)(value => control = value) });
        var html = await fixture.Html(root);
        Assert.Contains("role=\"combobox\"", html); Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("color:" + ItemNamePresentation.ForItem(KnownItem).CssColor, html);
        Assert.DoesNotContain("<select", html); Assert.DoesNotContain("<option", html);
        await fixture.Event(control!, "role", "combobox", "onclick", new MouseEventArgs());
        html = await fixture.Html(root);
        Assert.Contains("role=\"listbox\"", html); Assert.Contains("aria-expanded=\"true\"", html);
        Assert.Equal(3, Regex.Matches(html, "role=\"option\"").Count);
        var unknown = Regex.Match(html, "<div(?=[^>]*data-item-select-option=\"3\")[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("Gamma &lt;unsafe&gt;", unknown);
        Assert.DoesNotContain("data-item-grade", unknown); Assert.DoesNotContain("color:", unknown);
        Assert.DoesNotContain("<unsafe>", html);
    }

    [Fact]
    public async Task KeyboardNavigationCommitsExactValuesAndEscapeKeepsThePreviousChoice()
    {
        await using var fixture = new Fixture("en");
        ItemSelect? control = null; var values = new List<int>();
        var root = await fixture.Render<CaptureSelect>(new() { ["Options"] = Options, ["Value"] = 1, ["Label"] = "Synthetic choice",
            ["ValueChanged"] = EventCallback.Factory.Create<int>(this, value => values.Add(value)),
            ["Captured"] = (Action<ItemSelect>)(value => control = value) });
        Task Key(string key) => fixture.Event(control!, "role", "combobox", "onkeydown", new KeyboardEventArgs { Key = key });
        await Key("ArrowDown"); await Key("ArrowDown"); await Key("Escape");
        Assert.Empty(values); Assert.DoesNotContain("role=\"listbox\"", await fixture.Html(root));
        await Key("ArrowDown"); await Key("End"); await Key("Enter");
        Assert.Equal(new[] { 3 }, values); Assert.DoesNotContain("role=\"listbox\"", await fixture.Html(root));
        await Key("ArrowDown"); await Key("b"); await Key("Tab");
        Assert.Equal(new[] { 3, 2 }, values);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RichProducerLabelsColorOnlyTheItemAndKeepTheirAccessibleLocalizedText(string language)
    {
        await using var fixture = new Fixture(language);
        const string itemText = "Alpha <unsafe>";
        var label = UiText.F("Herstellungsweg für {0}", itemText);
        var root = await fixture.Render<ItemSelect>(new()
        {
            [nameof(ItemSelect.Options)] = Options, [nameof(ItemSelect.Label)] = label,
            [nameof(ItemSelect.LabelContent)] = (RenderFragment)(builder =>
            {
                builder.OpenComponent<ItemNameFormat>(0);
                builder.AddAttribute(1, nameof(ItemNameFormat.FormatKey), "Herstellungsweg für {0}");
                builder.AddAttribute(2, nameof(ItemNameFormat.ItemId), KnownItem);
                builder.AddAttribute(3, nameof(ItemNameFormat.Text), itemText);
                builder.CloseComponent();
            })
        });
        var html = await fixture.Html(root);
        Assert.Contains("aria-label=\"" + label + "\"", WebUtility.HtmlDecode(html));
        Assert.Single(Regex.Matches(html, "data-item-name=\"true\""));
        Assert.Contains("color:" + ItemNamePresentation.ForItem(KnownItem).CssColor, html);
        Assert.Contains("Alpha &lt;unsafe&gt;", html);
        Assert.DoesNotContain("<unsafe>", html);
        Assert.Contains(label, WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")));
    }

    [Fact]
    public async Task PointerSelectionAndDisabledStatePreserveTheSameCallbackContract()
    {
        await using var fixture = new Fixture("en");
        ItemSelect? control = null; var selected = 0;
        var root = await fixture.Render<CaptureSelect>(new() { ["Options"] = Options, ["Value"] = 1, ["Label"] = "Synthetic choice",
            ["ValueChanged"] = EventCallback.Factory.Create<int>(this, value => selected = value),
            ["Captured"] = (Action<ItemSelect>)(value => control = value) });
        await fixture.Event(control!, "role", "combobox", "onclick", new MouseEventArgs());
        await fixture.Event(control!, "data-item-select-option", "2", "onclick", new MouseEventArgs());
        Assert.Equal(2, selected); Assert.DoesNotContain("role=\"listbox\"", await fixture.Html(root));
        ItemSelect? disabled = null;
        var disabledRoot = await fixture.Render<CaptureSelect>(new() { ["Options"] = Options, ["Value"] = 1, ["Label"] = "Synthetic choice", ["Disabled"] = true,
            ["ValueChanged"] = EventCallback.Factory.Create<int>(this, _ => selected = -1),
            ["Captured"] = (Action<ItemSelect>)(value => disabled = value) });
        await fixture.Event(disabled!, "role", "combobox", "onclick", new MouseEventArgs());
        Assert.DoesNotContain("role=\"listbox\"", await fixture.Html(disabledRoot)); Assert.Equal(2, selected);
    }

    [Fact]
    public async Task TooltipKeepsEscapedColoredContentAvailableForHoverAndKeyboardWithoutJavascript()
    {
        await using var fixture = new Fixture("en");
        ItemTooltip? tooltip = null;
        var root = await fixture.Render<CaptureTooltip>(new() { ["ItemId"] = KnownItem, ["Text"] = "Ore <unsafe>", ["Id"] = "synthetic-tooltip",
            ["Captured"] = (Action<ItemTooltip>)(value => tooltip = value), ["ChildContent"] = (RenderFragment)(builder => builder.AddContent(0, "icon")) });
        var html = await fixture.Html(root);
        Assert.Contains("aria-describedby=\"synthetic-tooltip\"", html); Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("role=\"tooltip\"", html); Assert.Contains("Ore &lt;unsafe&gt;", html);
        Assert.Contains("color:" + ItemNamePresentation.ForItem(KnownItem).CssColor, html);
        Assert.DoesNotContain("title=", html); Assert.DoesNotContain("<unsafe>", html);
        await fixture.Event(tooltip!, "data-item-tooltip", "", "onfocusin", new FocusEventArgs());
        Assert.DoesNotContain(" hidden", TooltipTag(await fixture.Html(root)));
        await fixture.Event(tooltip!, "data-item-tooltip", "", "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.Contains(" hidden", TooltipTag(await fixture.Html(root)));
        await fixture.Event(tooltip!, "data-item-tooltip", "", "onfocusout", new FocusEventArgs());
        await fixture.Event(tooltip!, "data-item-tooltip", "", "onmouseenter", new MouseEventArgs());
        Assert.DoesNotContain(" hidden", TooltipTag(await fixture.Html(root)));
    }

    [Fact]
    public async Task TooltipCanStayInsideAnExistingButtonAndContainMultipleNames()
    {
        await using var fixture = new Fixture("en");
        var root = await fixture.Render<ItemTooltip>(new() { ["Focusable"] = false, ["ParentFocus"] = true,
            ["Id"] = "synthetic-parent-tooltip", ["Label"] = "Two items", ["ChildContent"] = (RenderFragment)(builder => builder.AddContent(0, "icon")),
            ["TooltipContent"] = (RenderFragment)(builder =>
            {
                builder.OpenComponent<ItemName>(0); builder.AddAttribute(1, "ItemId", KnownItem); builder.AddAttribute(2, "Text", "First item"); builder.CloseComponent();
                builder.AddContent(3, " × 2 · ");
                builder.OpenComponent<ItemName>(4); builder.AddAttribute(5, "ItemId", int.MaxValue); builder.AddAttribute(6, "Text", "Second item"); builder.CloseComponent();
            }) });
        var html = await fixture.Html(root);
        Assert.Contains("data-parent-focus=\"true\"", html); Assert.DoesNotContain("tabindex=", html);
        Assert.DoesNotContain("<button", html); Assert.Contains("role=\"tooltip\"", html);
        Assert.Equal(2, Regex.Matches(html, "data-item-name=\"true\"").Count);
        Assert.Contains("First item", html); Assert.Contains("Second item", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ProductionCraftingHeadingsAndIngredientNamesUseTheSharedItemPalette(string language)
    {
        await using var fixture = new Fixture(language);
        var catalog = CraftingCatalog.Default; var recipe = catalog.FindRecipe(524084001)!;
        var state = new CraftingCalculatorSnapshot { Profession = "Cooking", Subcategory = "Food", SelectedRecipeId = recipe.Id,
            Faction = "Asmodians", ObservedLevels = CraftingCatalog.Professions.ToImmutableDictionary(profession => profession, _ => 100) };
        var root = await fixture.Render<CraftingCalculatorView>(new() { ["Catalog"] = catalog, ["State"] = state });
        var html = WebUtility.HtmlDecode(await fixture.Html(root));
        var name = CraftingCalculatorPresentation.Name(catalog.FindItem(recipe.OutputItemId));
        var color = ItemNamePresentation.ForItem(recipe.OutputItemId).CssColor;
        Assert.NotNull(color);
        Assert.Matches("<h2><span(?=[^>]*data-item-name=\"true\")(?=[^>]*style=\"color:" + Regex.Escape(color) + "\")[^>]*>" + Regex.Escape(name) + "</span></h2>", html);
        foreach (var ingredient in recipe.Ingredients)
        {
            var ingredientName = CraftingCalculatorPresentation.Name(catalog.FindItem(ingredient.ItemId));
            var ingredientColor = ItemNamePresentation.ForItem(ingredient.ItemId).CssColor;
            Assert.NotNull(ingredientColor);
            Assert.Matches("<span(?=[^>]*data-item-name=\"true\")(?=[^>]*style=\"color:" + Regex.Escape(ingredientColor) + "\")[^>]*>" + Regex.Escape(ingredientName) + "</span>", html);
        }
        Assert.DoesNotContain("<select", html); Assert.Contains("role=\"combobox\"", html);
    }

    private static string TooltipTag(string html) => Regex.Match(html, "<span(?=[^>]*role=\"tooltip\")[^>]*>").Value;
    public sealed class CaptureSelect : ItemSelect
    {
        [Parameter] public Action<ItemSelect>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }
    public sealed class CaptureTooltip : ItemTooltip
    {
        [Parameter] public Action<ItemTooltip>? Captured { get; set; }
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
        public Task<HtmlRootComponent> Render<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        public Task<string> Html(HtmlRootComponent root) => _renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
#pragma warning disable BL0006
        public Task Event<T>(IComponent component, string attribute, string value, string name, T args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            (component is ItemSelect ? typeof(ItemSelect) : typeof(ItemTooltip)).GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                var match = frames.Array[index];
                if (match.FrameType != RenderTreeFrameType.Attribute || match.AttributeName != attribute || value.Length > 0 && Convert.ToString(match.AttributeValue, CultureInfo.InvariantCulture) != value) continue;
                var element = index; while (element >= 0 && frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                for (var eventIndex = element + 1; element >= 0 && eventIndex < frames.Count && frames.Array[eventIndex].FrameType == RenderTreeFrameType.Attribute; eventIndex++)
                {
                    var frame = frames.Array[eventIndex]; if (frame.AttributeName != name) continue;
                    if (frame.AttributeValue is EventCallback<T> typed) await typed.InvokeAsync(args);
                    else if (frame.AttributeValue is EventCallback callback) await callback.InvokeAsync(args);
                    else if (frame.AttributeValue is MulticastDelegate handler)
                    { await new EventCallback((IHandleEvent)component, handler).InvokeAsync(args); }
                    else throw new InvalidOperationException("Unexpected item control callback.");
                    return;
                }
            }
            throw new InvalidOperationException("Item control callback not found.");
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previous; }
    }
}
