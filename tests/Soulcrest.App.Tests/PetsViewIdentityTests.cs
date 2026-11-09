using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetsViewIdentityTests
{
    [Fact]
    public async Task TheSourcedGatekeeperPinopiHasItsSoulIdAndNoGenericGatekeeperRow()
    {
        var pinopi = Sourced(1146);
        var gatekeeper = new PetDefinition { Id = "gatekeeper", En = "Gatekeeper", Genus = "unknown" };
        await using var fixture = new Fixture([pinopi], learned: [gatekeeper]);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal("Gatekeeper Pinopi", PetIdTable.Find(1146)!.En);
        Assert.Contains("data-pet-id=\"gatekeeper-pinopi\"", html);
        Assert.DoesNotContain("data-pet-id=\"gatekeeper\"", html);
        Assert.Contains("data-pet-soul-id=\"1146\"", html);
        Assert.Contains("Soul-ID: 1146", html);
        Assert.Contains("Nur Pets mit belegter Soul-ID.", html);
        Assert.Single(fixture.Progress.VerifiedPets);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task UnverifiedLearnedPetsCannotEnterTheListOrTheSelectedDetail()
    {
        var unverified = new PetDefinition
            { Id = "synthetic-unverified-companion", En = "Synthetic Unverified Companion", Genus = "unknown" };
        await using var fixture = new Fixture([Sourced(1146)], learned: [unverified]);
        Assert.NotNull(fixture.Progress.Catalog.Find(unverified.Id));
        Assert.Null(fixture.Progress.SoulId(unverified.Id));
        var before = fixture.ProfileBytes;

        await fixture.Render();
        await fixture.SelectUnverified(unverified.Id);
        var html = await fixture.Html();

        Assert.DoesNotContain(unverified.En, html);
        Assert.DoesNotContain("data-pet-id=\"" + unverified.Id + "\"", html);
        Assert.Contains("Gatekeeper Pinopi", html);
        Assert.Contains("data-pet-soul-id=\"1146\"", html);
        Assert.Equal(1, fixture.Count("_total"));
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task VerifiedSoulIdentitiesAreCountedOnceAndUnverifiedFocusAndMaxValuesStayExcluded()
    {
        var primary = Sourced(1098);
        var legacy = primary with { Id = "kalnif-02", LegacyDuplicate = true };
        var unverified = new PetDefinition
            { Id = "synthetic-unverified-map-pet", En = "Synthetic Unverified Map Pet", Genus = "unknown" };
        var profile = new ProgressProfile();
        profile.Pets[primary.Id] = new() { Souls = 40 };
        profile.Pets[legacy.Id] = new() { Souls = 105, Focus = true };
        profile.Pets[unverified.Id] = new() { Souls = 105, Focus = true };
        await using var fixture = new Fixture([primary, legacy, Sourced(1146), unverified], profile);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal(2, Regex.Matches(html, "class=\"pets-card pets-g-").Count);
        Assert.Contains("data-pet-id=\"modified-karnif\"", html);
        Assert.DoesNotContain("data-pet-id=\"kalnif-02\"", html);
        Assert.DoesNotContain(unverified.En, html);
        Assert.Equal(2, fixture.Count("_total"));
        Assert.Equal(1, fixture.Count("_unlocked"));
        Assert.Equal(1, fixture.Count("_done"));
        Assert.Equal(1, fixture.Count("_focusCount"));
        Assert.Equal((1, 1), fixture.GenusCounts["fera"]);
        Assert.Equal((1, 0), fixture.GenusCounts["varian"]);
        Assert.DoesNotContain("unknown", fixture.GenusCounts.Keys);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(1, 100, 25)]
    [InlineData(2, 75, 75)]
    public async Task AvailableSoulsDoNotPromoteTheAcquiredLevelInThePetView(int level, int balance, int needed)
    {
        var pet = Sourced(1146);
        await using var fixture = new Fixture([pet]);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var context = new NetworkProgressContext(new(101, 501, 1001, "Synthetic Alpha"), 1, 1, at);
        Assert.True(fixture.Progress.BindNetworkContext(context));
        Assert.True(fixture.Progress.ApplyNetworkSnapshot(new PetProgressSnapshot(
            new Dictionary<int, int> { [1146] = level }, new Dictionary<int, int> { [1146] = balance }),
            context with { Sequence = 2, At = at.AddSeconds(1) }));
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal(level == 0 ? 0 : 1, fixture.Count("_unlocked"));
        Assert.Equal(0, fixture.Count("_done"));
        Assert.Contains($"class=\"pets-count\">{balance}/{needed}</span>", html);
        Assert.DoesNotContain("class=\"pets-count is-max\"", html);
        Assert.Contains($"Verfügbare Souls: {balance}", html);
        if (level > 0) Assert.Contains($"class=\"pets-badge\">{level}</span>", html);
        else Assert.DoesNotContain("class=\"pets-badge\"", html);
        Assert.DoesNotContain("width:400%", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Netzwerkstand übernehmen", "Manuelle Korrektur aktiv.")]
    [InlineData("en", "Use network progress", "Manual correction active.")]
    public async Task AManualCorrectionCanReturnToObservedNetworkProgress(string language, string action, string notice)
    {
        var pet = Sourced(1146);
        await using var fixture = new Fixture([pet], language: language);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var context = new NetworkProgressContext(new(101, 501, 1001, "Synthetic Alpha"), 1, 1, at);
        Assert.True(fixture.Progress.BindNetworkContext(context));
        Assert.True(fixture.Progress.ApplyNetworkSnapshot(new PetProgressSnapshot(
            new Dictionary<int, int> { [1146] = 1 }, new Dictionary<int, int> { [1146] = 3 }),
            context with { Sequence = 2, At = at.AddSeconds(1) }));
        fixture.Progress.SetSouls(pet.Id, fixture.Progress.Thresholds.Max);
        var html = await fixture.Render();
        Assert.Contains(action, html);
        Assert.Contains(notice, html);
        Assert.Equal(1, fixture.Count("_done"));

        await fixture.UseNetworkProgress(pet.Id);
        html = await fixture.Html();

        Assert.False(fixture.Progress.EntrySnapshot(pet.Id).ManualOverride);
        Assert.DoesNotContain(action, html);
        Assert.DoesNotContain(notice, html);
        Assert.Contains("class=\"pets-count\">3/25</span>", html);
        Assert.Contains("class=\"pets-badge\">1</span>", html);
        Assert.Equal(0, fixture.Count("_done"));
    }

    [Theory]
    [InlineData("de", "Soul-Quellen", "Insel des Blauatems", "Dropchance nicht bekannt.")]
    [InlineData("en", "Soul sources", "Azure Breath Island", "Drop rate unknown.")]
    public async Task DungeonSoulSourcesAreVisibleWithoutInventingMapPositionsOrChangingProgress(
        string language, string heading, string dungeon, string chanceNote)
    {
        var pet = Sourced(1009);
        await using var fixture = new Fixture([pet], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-acquisition=\"1009\"", html);
        Assert.Contains("data-pet-dungeon-source=\"true\"", html);
        Assert.Contains(heading, html);
        Assert.Contains(dungeon, html);
        Assert.Contains(chanceNote, html);
        Assert.Empty(fixture.Progress.Catalog.Find(pet.Id)!.Maps);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
        Assert.Equal(0, fixture.Progress.Souls(pet.Id));
    }

    [Theory]
    [InlineData("de", 1009, "Zukünftige Quelle", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", 1009, "Future source", "Future variants · not released yet")]
    [InlineData("de", 1043, "Zukünftige Quelle", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", 1080, "Future source", "Future variants · not released yet")]
    public async Task FutureSourcesRemainVisibleWithoutBeingAdvertisedAsCurrentGlobalFarmLocations(
        string language, int number, string badge, string explanation)
    {
        await using var fixture = new Fixture([Sourced(number)], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-global-availability=\"upcoming\"", html);
        Assert.Contains(badge, html);
        Assert.Contains(explanation, html);
        Assert.Contains("data-pet-source-status=\"upcoming\"", html);
        Assert.DoesNotContain("data-pet-source-status=\"available\"", html);
        Assert.DoesNotContain("data-pet-source-status=\"unconfirmed\"", html);
        Assert.Contains("2026-11-05", html);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Auf Global verfügbare Varianten", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", "Variants available on Global", "Future variants · not released yet")]
    public async Task AnOpenDungeonDoesNotPromoteItsFutureVariantToAvailable(
        string language, string currentLabel, string futureLabel)
    {
        await using var fixture = new Fixture([Sourced(1082)], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-global-availability=\"available\"", html);
        Assert.Contains("data-pet-source-status=\"available\"", html);
        Assert.Contains("data-pet-source-status=\"upcoming\"", html);
        Assert.Contains(currentLabel, html);
        Assert.Contains(futureLabel, html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Azure Breath")]
    [InlineData("en", "Insel des Blauatems")]
    public async Task SourceDungeonSearchFindsPetsInEitherOriginalLanguage(string language, string query)
    {
        var kernon = Sourced(1009);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([kernon, pinopi], language: language);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_query", query);
        var html = await fixture.Html();

        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", html);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Keneire", 1009)]
    [InlineData("en", "Keneire", 1009)]
    [InlineData("de", "Air Raiders", 1080)]
    [InlineData("en", "Luftangriffstruppwaffe", 1080)]
    public async Task SourceMonsterSearchUsesTheProvenDropperRatherThanThePetName(
        string language, string query, int petNumber)
    {
        var pet = Sourced(petNumber);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([pet, pinopi], language: language);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_query", query);
        var html = await fixture.Html();

        Assert.DoesNotContain(query, pet.En, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"data-pet-id=\"{pet.Id}\"", html);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task LegacyPetAliasesShowOneAcquisitionPanelAndOldPackagesResolveTheSoulIdentity()
    {
        var primary = Sourced(1009);
        var legacy = primary with { Id = "synthetic-legacy-kernon", LegacyDuplicate = true };
        await using (var fixture = new Fixture([primary, legacy]))
        {
            var before = fixture.ProfileBytes;
            var html = await fixture.Render();
            Assert.Single(fixture.Progress.VerifiedPets);
            Assert.Single(Regex.Matches(html, "data-pet-acquisition=\"1009\"").Cast<Match>());
            Assert.DoesNotContain($"data-pet-id=\"{legacy.Id}\"", html);
            Assert.Equal(before, fixture.ProfileBytes);
        }

        await using (var fixture = new Fixture([primary with { NumericId = null }]))
        {
            var before = fixture.ProfileBytes;
            var html = await fixture.Render();
            Assert.Equal(1009, fixture.Progress.SoulId(primary.Id));
            Assert.Contains("data-pet-acquisition=\"1009\"", html);
            Assert.Equal(before, fixture.ProfileBytes);
        }
    }

    [Fact]
    public async Task DungeonSourceFilterAndResetKeepOrdinaryPetsAndProgressIntact()
    {
        var kernon = Sourced(1009);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([kernon, pinopi]);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_source", "dungeon");
        var filtered = await fixture.Html();
        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", filtered);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", filtered);
        Assert.Single(Regex.Matches(filtered, "data-pet-id=\"").Cast<Match>());

        await fixture.Invoke("ResetFilters");
        var reset = await fixture.Html();
        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", reset);
        Assert.Contains($"data-pet-id=\"{pinopi.Id}\"", reset);
        Assert.Equal("", fixture.State("_source"));
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public async Task MapActionRequiresPositivePositionsOnAnAvailableMap(
        int positions, bool mapAvailable, bool expectedEnabled)
    {
        var pet = Sourced(1009) with { Spawns = new() { ["synthetic-map"] = positions } };
        IReadOnlyList<MapInfo> maps = mapAvailable ? [new("synthetic-map", "Synthetic map", "Synthetic")] : [];
        await using var fixture = new Fixture([pet], maps: maps);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        AssertMapAction(html, expectedEnabled);
        Assert.Equal(positions, fixture.Progress.Catalog.Find(pet.Id)!.Spawns["synthetic-map"]);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task UnlistedPetsDoNotReceiveAnotherPetsDungeonSource()
    {
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([pinopi]);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.DoesNotContain("data-pet-acquisition=", html);
        Assert.DoesNotContain("data-pet-dungeon-source=", html);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    private static void AssertMapAction(string html, bool enabled)
    {
        var action = Regex.Match(html, "<button\\b[^>]*data-pet-map-action=\"true\"[^>]*>");
        Assert.True(action.Success, "The map action needs an explicit testable button.");
        Assert.Equal(!enabled, Regex.IsMatch(action.Value, "\\bdisabled(?:\\s|=|>)"));
    }

    private static PetDefinition Sourced(int soulId)
    {
        var source = PetIdTable.Find(soulId);
        Assert.NotNull(source);
        Assert.NotNull(source.CatalogId);
        return new PetDefinition { Id = source.CatalogId, En = source.En,
            Genus = source.Genus.ToLowerInvariant(), NumericId = soulId.ToString(CultureInfo.InvariantCulture) };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pets-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private PetsView _view = default!;
        public ProgressService Progress { get; }
        public byte[] ProfileBytes => File.ReadAllBytes(AppPaths.ProgressFile);
        public int Count(string field) => (int)typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!;
        public Dictionary<string, (int Total, int Unlocked)> GenusCounts =>
            (Dictionary<string, (int Total, int Unlocked)>)typeof(PetsView)
                .GetField("_genusCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!;

        public Fixture(IReadOnlyList<PetDefinition> pets, ProgressProfile? profile = null, IReadOnlyList<PetDefinition>? learned = null, string language = "de", IReadOnlyList<MapInfo>? maps = null)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), JsonSerializer.Serialize(new
            {
                maps = (maps ?? []).Select(map => new { id = map.Id, label = map.Label, group = map.Group }).ToArray(),
            }));
            File.WriteAllText(Path.Combine(_folder, "pets.json"), JsonSerializer.Serialize(pets));
            _backups = new[] { AppPaths.SettingsFile, AppPaths.ProgressFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile }
                .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
            JsonFile.Save(AppPaths.ProgressFile, profile ?? new ProgressProfile());
            JsonFile.Save(AppPaths.LearnedPetsFile, learned ?? []);
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<Soulcrest.App.Network.NetworkLootService>();
            services.AddSingleton<UiState>();
            _provider = services.BuildServiceProvider();
            var settings = _provider.GetRequiredService<SettingsService>();
            settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = language;
            Progress = _provider.GetRequiredService<ProgressService>();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () =>
                await _renderer.RenderComponentAsync<CaptureView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(CaptureView.Capture)] = (Action<PetsView>)(view => _view = view) })));
            return await Html();
        }

        public async Task<string> Html() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task UseNetworkProgress(string id) => await _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetMethod("UseNetworkProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [id]);
        });

        public async Task SelectUnverified(string id) => await _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetField("_selectedId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, id);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public object? State(string field) =>
            typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view);

        public Task SetState(string field, object value) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public Task Invoke(string method) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            UiText.Language = _previousLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class CaptureView : ComponentBase
    {
        [Parameter] public Action<PetsView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PetsView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((PetsView)value));
            builder.CloseComponent();
        }
    }
}
