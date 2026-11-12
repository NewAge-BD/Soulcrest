using System.Collections.Immutable;
using System.Net;
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
public sealed class CraftingRecoveryViewTests
{
    [Theory]
    [InlineData("de")] [InlineData("en")]
    public async Task PriceOutageAndLowLevelAreExplainedEvenWithoutACalculation(string language)
    {
        var html = WebUtility.HtmlDecode(await Render(language, LowLevelState()));
        Assert.Contains("data-crafting-price-warning=\"MarketQuotes\"", html);
        Assert.Contains("data-crafting-proficiency-warning", html);
        Assert.Contains(language == "de" ? "Cooking: Charakterlevel 63, benötigt 75." : "Cooking: character proficiency 63, required 75.", html);
        Assert.Contains(language == "de" ? "Fehlende Preise bleiben unbekannt." : "Missing prices remain unknown.", html);
        Assert.DoesNotContain("private-profile-never-render", html);
    }

    [Theory]
    [InlineData("de")] [InlineData("en")]
    public async Task AverageFallbackIsLabelledAndCacheBasedMetricsSurviveTheRefreshWarning(string language)
    {
        var state = LowLevelState() with
        {
            SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Cooking", 100),
            Result = new() { Status = CraftingPlanStatus.Complete, ExpectedCost = 12345, ExpectedProfit = 43210 },
            BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(1,
                new(1, 100, CraftingPriceSource.ExchangeAverage, DateTimeOffset.UtcNow, 11301)
                { AverageRangeStart = DateTimeOffset.UtcNow.AddHours(-24), AverageSamples = 24 })
        };
        var html = WebUtility.HtmlDecode(await Render(language, state));
        Assert.Contains("data-crafting-average-fallback", html);
        Assert.Contains(language == "de" ? "Ersatzpreis" : "fallback", html);
        Assert.Contains(language == "de" ? "12.345" : "12,345", html);
        Assert.Contains(language == "de" ? "43.210" : "43,210", html);
        Assert.Contains("data-crafting-proficiency-warning", html);
        Assert.DoesNotContain("data-crafting-reference-simulation", html);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")] [InlineData("en")]
    public async Task ExportProductionRecoveryView(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_RECOVERY_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var html = await Render(language, LowLevelState());
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "crafting-calculator.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "recovery-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
            + "\"><style>" + css + "\nhtml,body{height:auto;min-height:100%;margin:0}</style></head><body>" + html + "</body></html>");
    }

    private static CraftingCalculatorSnapshot LowLevelState() => new()
    {
        SelectedRecipeId = 524084005, TargetItemId = 510715029, Profession = "Cooking", Subcategory = "Food",
        ProfileId = "private-profile-never-render", ProficiencyFresh = true,
        Faction = "Asmodians", ServerName = "Example market",
        ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Cooking", 63),
        Status = "Die Kalkulation ist noch nicht vollständig.",
        PriceRefreshFailure = new(CraftingFailureStage.MarketQuotes, CraftingFailureKind.Provider, 503),
        Result = new() { Status = CraftingPlanStatus.Unknown, Issues = [new("ProficiencyTooLow")] }
    };

    private static async Task<string> Render(string language, CraftingCalculatorSnapshot state)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<CraftingCalculatorView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CraftingCalculatorView.Catalog)] = CraftingCatalog.Default, [nameof(CraftingCalculatorView.State)] = state }))).ToHtmlString());
        }
        finally { UiText.Language = previous; }
    }
}
