using Soulcrest.App.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;

namespace Soulcrest.App.Tests;

[Collection("Settings file")] // both write settings.json of the test data folder
public sealed class LocalizationTests
{
    [Theory]
    [InlineData("de", "Optionen", "Oberfläche und Namen")]
    [InlineData("en", "Settings", "Interface and names")]
    public async Task OptionsRenderWithTheSavedLanguage(string language, string tab, string label)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<OverlayHotkeyService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<BossRushService>();
        services.AddSingleton(_ => new AlertsService(Path.Combine(Path.GetTempPath(), "Soulcrest-map-render-" + Guid.NewGuid().ToString("N") + ".json"), runTimer: false));
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<UiState>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IJSRuntime, UnusedJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = (settings.Current.UiLanguage, settings.Current.NameLanguage, settings.Current.TutorialDone);
        settings.Update(s => { s.UiLanguage = language; s.NameLanguage = language; s.TutorialDone = true; });
        Assert.Equal(language, new SettingsService().Current.UiLanguage);
        provider.GetRequiredService<UiState>().Navigate("options");
        try
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var markup = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.Main>()).ToHtmlString());
            var html = System.Net.WebUtility.HtmlDecode(markup);
            Assert.Contains(tab, html);
            Assert.DoesNotContain(">Pet Scan</button>", html);
            Assert.Contains(label, html);
            Assert.Contains("id=\"ui-language\"", html);
            Assert.Contains("id=\"overlay-language\"", html);
            Assert.Contains("id=\"map-layers-legend-hint\"", html);
            Assert.Contains(language == "de" ? "getrennt für interaktive Karte, Minimap und Weltkarte" : "separately for the interactive map, minimap and world map", html);
            Assert.DoesNotContain(language == "de" ? "Pet-Symbole mit Stand" : "Pet symbols with progress", html);
            Assert.Contains(language == "de" ? "Overlay-Sprache" : "Overlay language", html);
            Assert.Contains(language == "de" ? "Automatisch (Spielsprache)" : "Automatic (game language)", html);
            Assert.Contains(language == "de" ? "Schloss im Overlay anklicken" : "Click the overlay lock", html);
            Assert.DoesNotContain("Auf der Weltkarte im Spiel: Alt", html);
            Assert.Contains(language == "de" ? "Overlay-Hotkeys" : "Overlay hotkeys", html);
            Assert.Contains("hotkey-record", html);
            Assert.Contains("hotkey-label-leveling.unlock", html);
            Assert.Contains("hotkey-label-notifications.unlock", html);
            Assert.Contains(language == "de" ? "Alert-Benachrichtigungen" : "Alert notifications", html);
            Assert.DoesNotContain("hotkey-label-exploration-scan.toggle", html);
            Assert.DoesNotContain("hotkey-label-pet-scan.toggle", html);
            Assert.Contains(language == "de" ? "Nicht belegt" : "Unassigned", html);
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "options-" + language + ".html"),
                    "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + markup + "</body></html>");
            }
        }
        finally { settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.NameLanguage = previous.NameLanguage; s.TutorialDone = previous.TutorialDone; }); }
    }

    private sealed class UnusedJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException(identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException(identifier);
    }

    [Theory]
    [InlineData("de", "Netzwerkfortschritt", "Quest-Baseline")]
    [InlineData("en", "Network progress", "Quest baseline")]
    public async Task ExplorationExplainsPassiveNetworkProgressInBothLanguages(string language, string percentage, string check)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<OverlayHotkeyService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<CharacterDetectionService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<UiState>();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SettingsService>();
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var markup = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.ExplorationView>()).ToHtmlString());
            var html = System.Net.WebUtility.HtmlDecode(markup);
            Assert.Contains(percentage, html);
            Assert.Contains(check, html);
            Assert.Contains("data-progress-source=\"network\"", html);
            Assert.DoesNotContain(language == "de" ? "Scan starten" : "Start scan", html);
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "exploration-" + language + ".html"),
                    "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + markup + "</body></html>");
            }
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void LanguageChangesStaticAndDynamicTextWithoutChangingNames()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "en";
            Assert.Equal("Settings", UiText.T("Optionen"));
            Assert.Equal("Test frame: 9 lines read, including 2 soul lines.", UiText.T("Testbild: 9 Zeilen gelesen, davon 2 Soul-Zeilen."));
            Assert.Equal("Applied 4 pets.", UiText.F("{0} Pets übernommen.", 4));
            Assert.Equal("Magic Gravi", UiText.T("Magic Gravi"));
            UiText.Language = "de";
            Assert.Equal("Optionen", UiText.T("Optionen"));
            Assert.Equal("4 Pets übernommen.", UiText.F("{0} Pets übernommen.", 4));
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void InterfaceSelectionPreservesTheLegacyGameLanguagePreference()
    {
        var settings = new AppSettings { UiLanguage = "de", NameLanguage = "de" };
        Assert.Equal("auto", settings.EffectiveOcrLanguage);
        settings.UiLanguage = settings.NameLanguage = "en";
        Assert.Equal("auto", settings.EffectiveOcrLanguage);
    }
}
