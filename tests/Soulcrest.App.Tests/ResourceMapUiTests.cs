using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ResourceMapUiTests
{
    [Theory]
    [InlineData(7, false, true)]
    [InlineData(8, false, false)]
    [InlineData(7, true, false)]
    public async Task OnlyKnownNonTargetInputsOfferAnExplicitMapAction(int itemId, bool target, bool expected)
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render<CraftingDependencyCard>(new()
        {
            [nameof(CraftingDependencyCard.Catalog)] = Catalog,
            [nameof(CraftingDependencyCard.State)] = new CraftingCalculatorSnapshot { TargetAttempts = 17 },
            [nameof(CraftingDependencyCard.Node)] = new CraftingDependencyNode { ItemId = itemId, IsTarget = target, IsActive = true }
        });
        Assert.Equal(expected, html.Contains("data-show-resource-map", StringComparison.Ordinal));
        Assert.DoesNotContain("data-crafting-receipt", html);
        Assert.DoesNotContain("data-crafting-track-controls", html);
        Assert.DoesNotContain("onerror=", html);
        if (itemId == 7) Assert.Contains("&lt;synthetic ore&gt;", html);
    }

    [Fact]
    public async Task MapActionOpensTheExactItemDialogWithoutChangingCalculationInputs()
    {
        await using var fixture = new Fixture();
        var state = new CraftingCalculatorSnapshot { TargetAttempts = 17, OwnedItems = ImmutableDictionary<int, long>.Empty.Add(7, 4) };
        CaptureCard? card = null;
        await fixture.Render<CaptureCard>(new()
        {
            [nameof(CaptureCard.Captured)] = (Action<CaptureCard>)(value => card = value),
            [nameof(CraftingDependencyCard.Catalog)] = Catalog,
            [nameof(CraftingDependencyCard.State)] = state,
            [nameof(CraftingDependencyCard.Node)] = new CraftingDependencyNode { ItemId = 7, IsActive = true }
        });
        await fixture.Invoke(() => typeof(CraftingDependencyCard).GetMethod("ShowOnMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, null));
        var request = Assert.IsType<ResourceMapDialogRequest>(fixture.Resources.Pending);
        Assert.Equal(7, request.ItemId);
        Assert.False(request.EditingDefaults);
        Assert.Equal(17, state.TargetAttempts);
        Assert.Equal(4, state.OwnedItems[7]);
        Assert.Null(fixture.Ui.TakePendingResourceFocus()); // No map action before confirmation.
        var html = await fixture.Render<ResourceMapDialog>(new());
        Assert.Contains("role=\"dialog\"", html);
        Assert.Contains("&lt;synthetic ore&gt;", html);
        Assert.DoesNotContain("<synthetic ore>", html);
        Assert.Contains("resource-map-world", html);
        Assert.Contains("resource-map-mini", html);
        Assert.Contains("resource-map-soulcrest", html);
    }

    [Fact]
    public async Task EditingDefaultsShowsAStandaloneSelectionWithoutGoalOrStockClaims()
    {
        await using var fixture = new Fixture();
        fixture.Resources.EditDefaults();
        var html = WebUtility.HtmlDecode(await fixture.Render<ResourceMapDialog>(new()));
        Assert.Contains(UiText.T("Standardauswahl für alle Ressourcen"), html);
        Assert.DoesNotContain("synthetic ore", html);
        Assert.DoesNotContain("data-crafting-track-controls", html);
        Assert.DoesNotContain("data-stock-origin", html);
        Assert.Null(fixture.Ui.TakePendingResourceFocus());
    }

    [Fact]
    public void DiagnosisReportsSourceAvailabilityWithoutExposingSourceIdentityOrStartingNavigation()
    {
        using var fixture = new Fixture();
        var known = DiagnosticContributors.ResourceMapLinksState(fixture.Sources, fixture.Settings.Current, true, false, DateTimeOffset.UtcNow);
        Assert.Equal(DiagnosticStatus.Healthy, known.Status);
        Assert.Equal(1, known.Facts["sourceItemKinds"]);
        Assert.Equal(true, known.Facts["serviceAvailable"]);
        Assert.Equal(false, known.Facts["dialogOpen"]);
        Assert.DoesNotContain("first", string.Join(" ", known.Facts.Values));
        Assert.Null(fixture.Resources.Pending);
        Assert.Null(fixture.Ui.TakePendingResourceFocus());
        var empty = new ResourceMapSources([], new Dictionary<string, ImmutableArray<MapLegendLayer>>());
        var waiting = DiagnosticContributors.ResourceMapLinksState(empty, fixture.Settings.Current, true, false, DateTimeOffset.UtcNow);
        Assert.Equal(DiagnosticStatus.Waiting, waiting.Status);
    }

    [Fact]
    public void MissingOptionalMapIsReportedAsUnavailableWithoutGuessingAnyLocations()
    {
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-resource-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sources = new ResourceMapSources(folder, [new("missing", "Synthetic missing map", "Synthetic")]);
            Assert.Equal("missing", Assert.Single(sources.UnavailableMaps));
            Assert.Equal(0, sources.ItemCount);
            Assert.Empty(sources.ForItem(7));
            Assert.Empty(sources.LayersFor("missing"));
            var result = DiagnosticContributors.ResourceMapLinksState(sources, new AppSettings(), true, false, DateTimeOffset.UtcNow);
            Assert.Equal(DiagnosticStatus.Warning, result.Status);
            Assert.Equal(1, result.Facts["unavailableMapCount"]);
            Assert.DoesNotContain("missing", string.Join(" ", result.Facts.Values));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static CraftingCatalog Catalog { get; } = new("synthetic-map-ui",
        [new CraftingItem { Id = 7, NameEn = "<synthetic ore>", NameDe = "<synthetic ore>" },
         new CraftingItem { Id = 8, NameEn = "Unknown source", NameDe = "Unbekannte Quelle" }], []);

    public sealed class CaptureCard : CraftingDependencyCard
    {
        [Parameter] public Action<CaptureCard>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    }

    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        internal SettingsService Settings { get; } = new();
        internal UiState Ui { get; } = new();
        internal ResourceMapSources Sources { get; } = new(
            [new(7, "first", "Resources/Ore", "Ore A", 10, 20)],
            new Dictionary<string, ImmutableArray<MapLegendLayer>> { ["first"] = [new("Resources/Ore", ["Ore A"], false)] });
        internal ResourceMapService Resources { get; }
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        internal Fixture()
        {
            Resources = new(Settings, Sources, Ui, () => null);
            _provider = new ServiceCollection().AddLogging().AddSingleton(Resources).AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        internal Task Invoke(Action action) => _renderer.Dispatcher.InvokeAsync(action);
        public void Dispose() { _renderer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _provider.Dispose(); Resources.Dispose(); }
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); Resources.Dispose(); }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
