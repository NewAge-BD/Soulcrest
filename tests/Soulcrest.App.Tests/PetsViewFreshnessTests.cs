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
public sealed class PetsViewFreshnessTests
{
    private const string PetId = "gatekeeper-pinopi";
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "Unbestätigt",
        "In dieser Sitzung noch nicht durch eine vollständige Pet-Liste bestätigt. Gespeicherte Werte bleiben erhalten.",
        "Stufe und verfügbare Souls sind noch nicht frisch bestätigt. Neue Soul-Gewinne allein bestätigen den gespeicherten Ausgangsstand nicht.",
        "Manuelle Korrektur aktiv.")]
    [InlineData("en", "Unconfirmed",
        "Not confirmed by a complete pet list in this session yet. Saved values are retained.",
        "Level and available souls have not been freshly confirmed. New soul gains alone do not confirm the saved starting balance.",
        "Manual correction active.")]
    public async Task StoredBalancesRemainVisibleAndUnconfirmedUntilAFullBaselineWhileManualValuesStayDistinct(
        string language, string badge, string tooltip, string note, string manualNotice)
    {
        await using var fixture = new Fixture(language);
        var context = new NetworkProgressContext(new(101, 501, 1001, "Synthetic Alpha"), 1, 1, At);
        var html = await fixture.Render();
        AssertUnconfirmed(html, badge, tooltip, note);
        Assert.Contains("class=\"pets-count\">3/25</span>", html);

        Assert.True(fixture.Progress.BindNetworkContext(context));
        Assert.True(fixture.Progress.ApplyNetworkGain([new(1146, 1)], context with { Sequence = 2 }));
        Assert.Equal(ProgressFreshness.Unknown, fixture.Progress.EntrySnapshot(PetId).Freshness);
        html = await fixture.Render();
        AssertUnconfirmed(html, badge, tooltip, note);
        Assert.Contains("class=\"pets-count\">4/25</span>", html);

        Assert.True(fixture.Progress.ApplyNetworkSnapshot(new(
            new Dictionary<int, int> { [1146] = 1 }, new Dictionary<int, int> { [1146] = 4 }),
            context with { Sequence = 3 }));
        html = await fixture.Render();
        AssertConfirmedOrManual(html);
        Assert.Contains("class=\"pets-count\">4/25</span>", html);
        Assert.Contains("class=\"pets-badge\">1</span>", html);

        fixture.Progress.MarkNetworkStale();
        html = await fixture.Render();
        AssertUnconfirmed(html, badge, tooltip, note);
        Assert.Contains("class=\"pets-count\">4/25</span>", html);

        fixture.Progress.SetSouls(PetId, 10);
        html = await fixture.Render();
        AssertConfirmedOrManual(html);
        Assert.Contains(manualNotice, html);
        Assert.Contains("class=\"pets-count\">5/25</span>", html);
    }

    private static void AssertUnconfirmed(string html, string badge, string tooltip, string note)
    {
        Assert.Contains("data-pet-progress-unconfirmed=\"true\"", html);
        Assert.Contains("data-pet-progress-note=\"unconfirmed\"", html);
        Assert.Contains(badge, html);
        Assert.Contains(tooltip, html);
        Assert.Contains(note, html);
    }

    private static void AssertConfirmedOrManual(string html)
    {
        Assert.DoesNotContain("data-pet-progress-unconfirmed", html);
        Assert.DoesNotContain("data-pet-progress-note", html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-freshness-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _previousSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly Dictionary<string, byte[]?> _learnedBackups = [];
        private readonly ServiceProvider _provider;
        public ProgressService Progress { get; }
        private string ProfilePath => Path.Combine(_folder, "progress.json");

        public Fixture(string language)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            // This fixture asserts the complete rendered pet list. Other network tests may have
            // learned additional pets; retain those files while qualifying this one-pet catalogue.
            foreach (var path in new[] { AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile })
                _learnedBackups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            JsonFile.Save(AppPaths.LearnedPetsFile, Array.Empty<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            Directory.CreateDirectory(_folder);
            JsonFile.Save(ProfilePath, new ProgressProfile { SchemaVersion = 2,
                Pets = new() { [PetId] = new() { Souls = 8, Freshness = ProgressFreshness.Unknown } } });
            Progress = new(ProfilePath, new PetCatalog([new PetDefinition { Id = PetId, En = "Gatekeeper Pinopi",
                Genus = "varian", NumericId = "1146" }]));
            var settings = new SettingsService();
            settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = language;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Progress);
            services.AddSingleton(settings);
            services.AddSingleton(new NetworkLootService(Progress));
            services.AddSingleton<UiState>();
            _provider = services.BuildServiceProvider();
        }

        public async Task<string> Render()
        {
            var before = File.ReadAllBytes(ProfilePath);
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<PetsView>(ParameterView.Empty)).ToHtmlString()));
            Assert.Equal(before, File.ReadAllBytes(ProfilePath));
            return html;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            UiText.Language = _previousLanguage;
            if (_previousSettings is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _previousSettings);
            foreach (var (path, bytes) in _learnedBackups)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_folder, recursive: true);
        }
    }
}
