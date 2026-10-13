using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkProgressViewTests
{
    [Theory]
    [InlineData("de", "In dieser Sitzung noch nicht bestätigt", "Wartet auf Bestätigung")]
    [InlineData("en", "Not confirmed in this session yet", "Awaiting confirmation")]
    public async Task MissingBaselinesAreExplicitAndNeverOfferAScan(string language, string missing, string waiting)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render<NetworkProgressStatus>();
        Assert.Contains(missing, html);
        Assert.Contains(waiting, html);
        Assert.Contains("data-progress-live=\"false\"", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("data-scan-state", html);
        Assert.False(fixture.Network.Running);
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task PetStatusKeepsExplorationBaselinesOutOfThePetPanel()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render<NetworkProgressStatus>(new() { [nameof(NetworkProgressStatus.Scope)] = "pet" });
        Assert.Contains("Pet baseline", html);
        Assert.DoesNotContain("Quest baseline", html);
        Assert.DoesNotContain("Kibelisk baseline", html);
        Assert.Contains("one shared collection", html);
    }

    [Fact]
    public async Task RetainedTimestampsAloneNeverClaimALiveIdentity()
    {
        await using var fixture = new Fixture("en");
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.PetBaselineAt))!
            .SetValue(fixture.Network, new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        var html = await fixture.Render<NetworkProgressStatus>(new() { [nameof(NetworkProgressStatus.Scope)] = "pet" });
        Assert.DoesNotContain("Not confirmed in this session yet", html);
        Assert.Contains("data-progress-live=\"false\"", html);
        Assert.Contains("Awaiting confirmation", html);
    }

    [Theory]
    [InlineData("de", "Charakterprofil", "Netzwerkfortschritt")]
    [InlineData("en", "Character profile", "Network progress")]
    public async Task SetupRetainsTheProfileAndDoesNotStartPixelOrOcrProgressWork(string language, string profile, string status)
    {
        await using var fixture = new Fixture(language);
        var before = File.ReadAllBytes(fixture.ProfilePath);
        var character = fixture.Exploration.ActiveId;
        var html = await fixture.Render<TutorialView>();
        Assert.Contains(profile, html);
        Assert.Contains(status, html);
        Assert.Contains("Synthetic retained profile", html);
        Assert.DoesNotContain("Pet Scan", html);
        Assert.DoesNotContain("data-scan-state", html);
        Assert.DoesNotContain("Scan starten", html);
        Assert.Equal(character, fixture.Exploration.ActiveId);
        Assert.Equal(before, File.ReadAllBytes(fixture.ProfilePath));
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
        Assert.False(fixture.Network.Running);
    }

    [Fact]
    public void LegacyPetScanNavigationOpensPets()
    {
        var ui = new UiState();
        ui.Navigate("petscan");
        Assert.Equal("pets", ui.Tab);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly byte[]? _savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _oldLanguage = UiText.Language;
        public string ProfilePath { get; } = Path.Combine(AppPaths.DataDirectory, "network-view-" + Guid.NewGuid().ToString("N") + ".json");
        public ExplorationService Exploration { get; }
        public NetworkLootService Network => _provider.GetRequiredService<NetworkLootService>();
        public GameCaptureService Capture => _provider.GetRequiredService<GameCaptureService>();

        public Fixture(string language)
        {
            Exploration = new ExplorationService(ProfilePath, [new("synthetic", "altgard", "dungeon", 0, "Synthetic place", null, 100, 100)]);
            Exploration.Add("Synthetic retained profile");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton(Exploration);
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime, NoJs>();
            _provider = services.BuildServiceProvider();
            _provider.GetRequiredService<SettingsService>().Current.UiLanguage = UiText.Language = language;
        }

        public async Task<string> Render<T>(Dictionary<string, object?>? parameters = null) where T : IComponent
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString()));
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (_savedSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _savedSettings);
            UiText.Language = _oldLanguage;
            File.Delete(ProfilePath);
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
