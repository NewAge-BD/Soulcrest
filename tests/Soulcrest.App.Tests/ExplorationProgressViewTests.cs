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
    [Theory]
    [InlineData("en", "Scan Exploration Progress", "Sealed Dungeons", "Stronghold")]
    [InlineData("de", "Erkundungsfortschritt scannen", "Versiegelte Dungeons", "Garnisonen")]
    public async Task OneTabListsAllThreeKinds(string language, string title, string dungeons, string strongholds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<PetScanService>();
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
        var previous = (settings.Current.UiLanguage, settings.Current.NameLanguage, settings.Current.LastMap, settings.Current.TutorialDone);
        settings.Update(s => { s.UiLanguage = s.NameLanguage = language; s.LastMap = "altgard"; s.TutorialDone = true; });
        provider.GetRequiredService<UiState>().Navigate("exploration");
        try
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.Main>()).ToHtmlString());
            html = System.Net.WebUtility.HtmlDecode(html);
            Assert.Contains(title, html);
            Assert.Contains("value=\"de-DE\"", html);
            Assert.DoesNotContain(">Stronghold</button>", html); // the old single tabs are gone
            if (provider.GetRequiredService<ExplorationService>().Places.Any(p => p.Map == "altgard"))
            {
                Assert.Contains("<h3>" + dungeons, html);
                Assert.Contains("<h3>" + strongholds, html);
                Assert.Contains("<h3>Kibelisk", html);
            }
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                static string Document(string body) => "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + body + "</body></html>";
                File.WriteAllText(Path.Combine(output, "exploration-" + language + ".html"), Document(html));
                var pets = await renderer.Dispatcher.InvokeAsync(async () =>
                    (await renderer.RenderComponentAsync<Soulcrest.App.Components.PetScanView>()).ToHtmlString());
                File.WriteAllText(Path.Combine(output, "pets-" + language + ".html"), Document(pets));
            }
        }
        finally
        {
            settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.NameLanguage = previous.NameLanguage; s.LastMap = previous.LastMap; s.TutorialDone = previous.TutorialDone; });
            UiText.Language = previous.UiLanguage;
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
