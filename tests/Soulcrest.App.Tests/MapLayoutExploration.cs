using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Writes the rendered map page with the app stylesheet to a file for a look (SOULCREST_MAP_LAYOUT=path).</summary>
[Collection("Settings file")]
public sealed class MapLayoutExploration
{
    [Fact]
    public async Task WriteMapPage()
    {
        var target = Environment.GetEnvironmentVariable("SOULCREST_MAP_LAYOUT");
        if (string.IsNullOrEmpty(target))
            return;
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
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Soulcrest.App.Components.Main>()).ToHtmlString());
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Soulcrest.App", "wwwroot", "css", "app.css"));
        File.WriteAllText(target, $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{{height:100%;margin:0}} #app{{height:100%}} {css} #map{{background:#3b4a3a;height:100%}}</style></head><body><div id=\"app\">{html}</div></body></html>");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
