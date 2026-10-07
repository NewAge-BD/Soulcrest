using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>User request 2026-10-06: one tab "Scan Exploration Progress" for sealed dungeons, strongholds and Kibelisks.</summary>
[Collection("Settings file")] // writes settings.json of the test data folder
public sealed class ExplorationProgressViewTests
{
    [Fact]
    public async Task OneTabListsAllThreeKinds()
    {
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
        services.AddSingleton<ExplorationScanService>();
        services.AddSingleton<CharacterDetectionService>();
        services.AddSingleton<OcrLanguageInstaller>();
        services.AddSingleton<UiState>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = (settings.Current.UiLanguage, settings.Current.LastMap);
        settings.Update(s => { s.UiLanguage = "en"; s.LastMap = "altgard"; });
        UiText.Language = "en";
        provider.GetRequiredService<UiState>().Navigate("exploration");
        try
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.Main>()).ToHtmlString());
            html = System.Net.WebUtility.HtmlDecode(html);
            Assert.Contains("Scan Exploration Progress", html);
            Assert.DoesNotContain(">Stronghold</button>", html); // the old single tabs are gone
            if (provider.GetRequiredService<ExplorationService>().Places.Any(p => p.Map == "altgard"))
            {
                Assert.Contains("<h3>Sealed Dungeons", html);
                Assert.Contains("<h3>Stronghold", html);
                Assert.Contains("<h3>Kibelisk", html);
            }
        }
        finally
        {
            settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.LastMap = previous.LastMap; });
            UiText.Language = previous.UiLanguage;
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
