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
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<ExplorationScanService>();
        services.AddSingleton<UiState>();
        services.AddSingleton<OcrLanguageInstaller>();
        services.AddSingleton<IJSRuntime, UnusedJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = settings.Current.UiLanguage;
        settings.Update(s => { s.UiLanguage = language; s.NameLanguage = language; });
        Assert.Equal(language, new SettingsService().Current.UiLanguage);
        provider.GetRequiredService<UiState>().Navigate("options");
        try
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.Main>()).ToHtmlString());
            html = System.Net.WebUtility.HtmlDecode(html);
            Assert.Contains(tab, html);
            Assert.Contains(label, html);
            Assert.Contains("id=\"ui-language\"", html);
        }
        finally { settings.Update(s => { s.UiLanguage = previous; s.NameLanguage = previous; }); }
    }

    private sealed class UnusedJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException(identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException(identifier);
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
    public void InterfaceSelectionDoesNotSelectTheOcrEngine()
    {
        var settings = new AppSettings { UiLanguage = "de", NameLanguage = "de" };
        Assert.Equal("auto", settings.EffectiveOcrLanguage);
        settings.UiLanguage = settings.NameLanguage = "en";
        Assert.Equal("auto", settings.EffectiveOcrLanguage);
    }
}
