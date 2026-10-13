using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class GlobalPetStatusViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "Bereit zum Empfang", "Gemeinsame", "ohne Charakter- oder Serverauswahl")]
    [InlineData("en", "Ready to receive", "shared", "without selecting a character or server")]
    public async Task PetReadinessGainAndFullBaselineDoNotWaitForOwnIdentity(string language, string ready, string shared, string independent)
    {
        await using var f = new Fixture(language);
        f.Running();
        var html = await f.Render("pet");
        Assert.Contains(ready, html);
        Assert.Contains(independent, html);
        Assert.Contains("data-pet-counting=\"true\"", html);
        Assert.Contains("data-progress-live=\"false\"", html);
        Assert.False(f.Network.ProgressSources.IdentityIsLive);

        f.Receive(Gain());
        Assert.Equal(2, f.Progress.Souls("fossa"));
        html = await f.Render("pet");
        Assert.Contains("data-progress-state=\"partial\"", html);
        Assert.Contains("data-progress-initialized=\"false\"", html);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Contains("data-progress-state=\"waiting\"", await f.Render("exploration"));

        f.Receive(Pets());
        html = await f.Render("pet");
        Assert.Contains(shared, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-progress-state=\"current\"", html);
        Assert.Contains("data-progress-live=\"true\"", html);
        Assert.Contains("data-progress-initialized=\"true\"", html);
        Assert.DoesNotContain("Detected character profile", html);
        Assert.DoesNotContain("Erkanntes Charakterprofil", html);
        Assert.DoesNotContain("per server", html);
        Assert.DoesNotContain("pro Server", html);
        Assert.False(f.Network.ProgressSources.IdentityIsLive);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Contains("data-progress-initialized=\"false\"", await f.Render("all"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExplorationStillRequiresItsOwnContextAndCannotInvalidateThePetPanel(string language)
    {
        await using var f = new Fixture(language);
        f.Running(); f.Receive(Pets());
        f.Receive([0x09, 0x8D, 0]); f.Receive([0x32, 0x92, 0]);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        f.Network.ObserveIdentity("own", new(101, 501, 100, "SyntheticGlobalAlpha"), At.AddSeconds(1));
        f.Receive([0x09, 0x8D, 0]); f.Receive([0x32, 0x92, 0]);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Contains("data-progress-initialized=\"true\"", await f.Render("all"));

        f.Receive([0x33, 0x36, 0]);
        Assert.False(f.Network.ProgressSources.IdentityIsLive);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Contains("data-progress-state=\"current\"", await f.Render("pet"));
        Assert.Contains("data-progress-state=\"waiting\"", await f.Render("exploration"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task PetFormatWarningAndTransportGapDoNotInventAConfirmedStartingBalance(string language)
    {
        await using var f = new Fixture(language);
        f.Running(); f.Receive(Pets());
        f.Receive([.. Pets(), 0]);
        Assert.True(f.Network.ProgressSources.PetFormatRejected);
        Assert.Contains(UiText.T("Pet-Netzwerkformat nicht unterstützt. Neue Soul-Gewinne werden weiter erfasst; der vollständige Pet-Stand muss erneut empfangen werden."), await f.Render("pet"));
        f.Receive(Gain());
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Contains("data-progress-initialized=\"false\"", await f.Render("pet"));
        f.Receive(Pets());
        Assert.Contains("data-progress-state=\"current\"", await f.Render("pet"));
        f.Network.ProcessProgressGap("foreign");
        Assert.Contains("data-progress-state=\"current\"", await f.Render("pet"));
        f.Network.ProcessProgressGap("own");
        Assert.Contains("data-progress-live=\"false\"", await f.Render("pet"));
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot("fossa").Freshness);
        f.Network.Stop();
        var html = await f.Render("pet");
        Assert.Contains("data-pet-counting=\"false\"", html);
        Assert.Contains(UiText.T("Pet-Netzwerkquelle aus. Aktiviere das Loot-Tracking, um neue Souls zu erfassen."), html);
    }

    private static byte[] Gain() => [0x0D, 0x90, 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(2)];
    private static byte[] Pets() => [0, 0x90, .. BitConverter.GetBytes(0), 1, .. BitConverter.GetBytes(999), .. BitConverter.GetBytes(1004),
        .. BitConverter.GetBytes(1), 1, .. BitConverter.GetBytes(1004), .. BitConverter.GetBytes(3), 0, 0, 0, 0];

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-global-pet-view-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldLanguage = UiText.Language;
        private readonly byte[]? _oldSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        public ProgressService Progress { get; }
        public NetworkLootService Network { get; }
        public Fixture(string language)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress, new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            var services = new ServiceCollection();
            services.AddLogging(); services.AddSingleton(Network); services.AddSingleton<SettingsService>();
            _provider = services.BuildServiceProvider();
            _provider.GetRequiredService<SettingsService>().Current.UiLanguage = UiText.Language = language;
        }
        public void Running() => typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        public void Receive(byte[] message) => Network.ProcessProgressMessage("own", message, At);
        public async Task<string> Render(string scope)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<NetworkProgressStatus>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(NetworkProgressStatus.Scope)] = scope }))).ToHtmlString()));
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            if (_oldSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _oldSettings);
            UiText.Language = _oldLanguage; Directory.Delete(_folder, recursive: true);
        }
    }
}
