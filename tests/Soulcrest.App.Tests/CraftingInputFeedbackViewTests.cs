using System.Collections.Immutable;
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

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CraftingInputFeedbackViewTests
{
    [Theory]
    [InlineData("de", "normal")]
    [InlineData("en", "normal")]
    [InlineData("de", "transfer")]
    [InlineData("en", "transfer")]
    [InlineData("de", "none")]
    [InlineData("en", "none")]
    [InlineData("de", "invalid")]
    [InlineData("en", "invalid")]
    public async Task InvalidProficiencyShowsAGlobalAlertAndValidInputClearsItInEveryPlanMode(string language, string mode)
    {
        await using var fixture = new Fixture(language);
        var changes = new List<CraftingLevelSelection>();
        var root = await fixture.Render(State(mode), value => changes.Add(value));
        var html = await fixture.Html(root);
        Assert.DoesNotContain("craft-input-error", html);
        Assert.Contains(UiText.F("Rechenlevel für {0}", "Blacksmithing"), html);
        var noSelection = mode is "none" or "invalid";
        if (noSelection) AssertEmptyPlan(html, language);
        else
        {
            Assert.DoesNotContain("data-crafting-plan-empty", html);
            Assert.Contains(mode == "normal" ? "data-crafting-selected=\"1\"" : "data-transfer-crafting=\"2\"", html);
        }

        await fixture.ChangeLevel("101");
        html = await fixture.Html(root);
        var alert = Regex.Match(html,
            "<p(?=[^>]*class=\"craft-input-error\")(?=[^>]*role=\"alert\")[^>]*>(.*?)</p>",
            RegexOptions.Singleline);
        Assert.True(alert.Success);
        Assert.Contains(language == "de" ? "Das Rechenlevel muss eine ganze Zahl zwischen 0 und 100 sein."
            : "Calculation proficiency must be a whole number between 0 and 100.", alert.Groups[1].Value);
        Assert.True(alert.Index < html.IndexOf("data-crafting-workspace", StringComparison.Ordinal));
        Assert.Empty(changes);
        if (noSelection) AssertEmptyPlan(html, language);

        await fixture.ChangeLevel("100");
        html = await fixture.Html(root);
        Assert.DoesNotContain("craft-input-error", html);
        Assert.Equal(new CraftingLevelSelection("Blacksmithing", 100), Assert.Single(changes));
        if (noSelection) AssertEmptyPlan(html, language);
    }

    private static void AssertEmptyPlan(string html, string language)
    {
        var empty = Regex.Match(html,
            "<p(?=[^>]*data-crafting-plan-empty)(?=[^>]*role=\"status\")[^>]*>(.*?)</p>",
            RegexOptions.Singleline);
        Assert.True(empty.Success);
        Assert.Contains(language == "de" ? "Wähle ein Rezept für die Kalkulation." : "Choose a recipe to calculate.",
            empty.Groups[1].Value);
        Assert.DoesNotContain("data-crafting-selected=", html);
        Assert.DoesNotContain("data-transfer-crafting=", html);
    }

    private static CraftingCalculatorSnapshot State(string mode) => new()
    {
        Profession = mode == "transfer" ? "Transfercrafting" : "Blacksmithing", Subcategory = "Greatsword",
        SelectedRecipeId = mode switch { "normal" => 1, "transfer" => 2, "invalid" => 999, _ => null },
        Faction = "Asmodians", ProficiencyFresh = true,
        ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 41)
    };

    private static CraftingCatalog Catalog() => new("synthetic-input-feedback",
    [
        new() { Id = 1, NameEn = "Example blade", NameDe = "Beispielklinge", IsEquipment = true, IsTradable = true },
        new() { Id = 2, NameEn = "Example upgraded blade", NameDe = "Verbesserte Beispielklinge", IsEquipment = true },
        new() { Id = 3, NameEn = "Example material", NameDe = "Beispielmaterial", IsEquipment = false, IsTradable = true }
    ],
    [
        new()
        {
            Id = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Asmodians",
            RequiredProficiency = 0, OutputItemId = 1, OutputQuantity = 1, CraftingFee = 0,
            SuccessRates = Enumerable.Repeat(1m, 101).ToImmutableArray(), MechanicsQualified = true,
            IsTransferCrafting = false, Ingredients = [new() { ItemId = 3, Quantity = 1 }]
        },
        new()
        {
            Id = 2, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Asmodians",
            RequiredProficiency = 0, OutputItemId = 2, OutputQuantity = 1, CraftingFee = 0,
            IsTransferCrafting = true, TransferItemIds = [1], Ingredients = [new() { ItemId = 1, Quantity = 1 }]
        }
    ]);

    public sealed class CaptureView : CraftingCalculatorView
    {
        [Parameter] public Action<CaptureView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        public void RenderAgain() => StateHasChanged();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previous;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private CaptureView? _view;
        public Fixture(string language)
        {
            _previous = UiText.Language;
            UiText.Language = language;
            _provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<HtmlRootComponent> Render(CraftingCalculatorSnapshot state, Action<CraftingLevelSelection> changed) =>
            _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CraftingCalculatorView.Catalog)] = Catalog(), [nameof(CraftingCalculatorView.State)] = state,
                    [nameof(CraftingCalculatorView.LevelChanged)] = EventCallback.Factory.Create(this, changed),
                    [nameof(CaptureView.Captured)] = (Action<CaptureView>)(view => _view = view)
                })));
        public Task<string> Html(HtmlRootComponent root) => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        public Task ChangeLevel(string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var method = typeof(CraftingCalculatorView).GetMethod("SetLevel", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(_view!, ["Blacksmithing", new ChangeEventArgs { Value = value }])!;
            _view!.RenderAgain();
        });
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previous;
        }
    }
}
