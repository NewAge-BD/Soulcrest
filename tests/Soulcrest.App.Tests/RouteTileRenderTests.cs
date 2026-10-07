using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The "Routen" tile offers saving a marked sequence and lists saved routes with start, repeat and delete.</summary>
[Collection("Settings file")] // writes targets.json, routes.json and settings.json of the test data folder
public sealed class RouteTileRenderTests
{
    [Fact]
    public async Task RouteTileShowsSaveButtonAndSavedRoutes()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.RoutesFile);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<UiState>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = (settings.Current.UiLanguage, settings.Current.LastMap);
        settings.Update(s => { s.UiLanguage = "de"; s.LastMap = "altgard"; });
        UiText.Language = "de";
        var targets = provider.GetRequiredService<MapTargetsService>();
        try
        {
            targets.Toggle("altgard", 100, 100, "A", "Locations", null);
            targets.Toggle("altgard", 200, 200, "B", "Locations", null, chain: true);
            var before = await Render(provider);
            Assert.Contains("Folge speichern (2)", before);

            var route = targets.SaveRoute("altgard", "Runde")!;
            targets.SetRouteRepeat(route.Id, true);
            var after = await Render(provider);
            Assert.Contains("Runde", after);
            Assert.Contains("2 Ziele", after);
            Assert.Contains("läuft", after);
            Assert.Contains("route-repeat on", after);
        }
        finally
        {
            settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.LastMap = previous.LastMap; });
            UiText.Language = previous.UiLanguage;
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.RoutesFile);
        }
    }

    private static async Task<string> Render(ServiceProvider provider)
    {
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Soulcrest.App.Components.MapView>()).ToHtmlString());
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
