using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

#pragma warning disable BL0006
[Collection("Settings file")]
public sealed class MonsterSearchCardTests
{
    private const string PetId = "gatekeeper-pinopi";
    private const int Number = 1146;
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FreshCardShowsMonsterIconActualLevelAndAvailableBalanceWithoutRedundantStageDetails(string language)
    {
        await using var fixture = new Fixture(language, "regular");
        var html = await fixture.Render();
        Assert.Contains("data-pet-known=\"true\"", html);
        Assert.Contains("data-pet-fresh=\"true\"", html);
        Assert.Contains("icons/monster.png", html);
        Assert.Contains(language == "de" ? "Torwächter-Monster" : "Gatekeeper Monster", html);
        Assert.Contains(language == "de" ? "Stufe 1" : "Level 1", html);
        Assert.Contains("<b>3 / 25</b>", html); // spent souls are not reconstructed from earned total
        Assert.DoesNotContain("monster-search-stages", html);
        Assert.DoesNotContain(language == "de" ? "Souls je Stufe" : "Souls per level", html);
        Assert.DoesNotContain("monster-search-remaining", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Equal(15, fixture.Progress.Souls(PetId));
        Assert.Equal(3, fixture.Progress.AvailableSouls(PetId));
        await fixture.Export(html, "regular");
    }

    [Theory]
    [InlineData("de", "max")]
    [InlineData("en", "max")]
    [InlineData("de", "saved")]
    [InlineData("en", "saved")]
    [InlineData("de", "unknown")]
    [InlineData("en", "unknown")]
    [InlineData("de", "manual")]
    [InlineData("en", "manual")]
    public async Task MaxSavedUnknownAndManualStatesRemainDistinctAndReadOnly(string language, string state)
    {
        await using var fixture = new Fixture(language, state);
        var html = await fixture.Render();
        if (state == "max")
        {
            Assert.Contains("monster-search-current is-max", html);
            Assert.Contains("<strong>MAX</strong>", html);
            Assert.DoesNotContain("class=\"monster-search-balance\"", html);
            Assert.DoesNotContain("class=\"monster-search-remaining\"", html);
        }
        else if (state == "saved")
        {
            Assert.Contains(language == "de" ? "Gespeichert · unbestätigt" : "Saved · unconfirmed", html);
            Assert.Contains("data-pet-known=\"true\"", html);
            Assert.Contains("data-pet-fresh=\"false\"", html);
            Assert.Contains("<b>3 / 25</b>", html);
            Assert.DoesNotContain("999", html);
        }
        else if (state == "unknown")
        {
            Assert.Contains(language == "de" ? "Unbekannt" : "Unknown", html);
            Assert.Contains("data-pet-known=\"false\"", html);
            Assert.Contains(language == "de" ? "<strong>Unbekannt</strong>" : "<strong>Unknown</strong>", html);
            Assert.DoesNotContain("class=\"monster-search-balance\"", html);
            Assert.DoesNotContain("0 / 5", html);
            Assert.DoesNotContain("class=\"monster-search-remaining\"", html);
        }
        else
        {
            Assert.Contains(language == "de" ? "Manuell" : "Manual", html);
            Assert.Contains("<b>5 / 25</b>", html);
        }
        await fixture.Export(html, state);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MultipleExplicitPetsAreDisplayedOnceEachAndMissingSoulRelationsStayUnknown(string language)
    {
        await using var fixture = new Fixture(language, "regular");
        var multiple = fixture.Monster with { PetIds = [PetId, "pinopi-legacy", "second-pet", "missing"] };
        var html = await fixture.Render(multiple);
        Assert.Equal(1, Count(html, "data-pet-id=\"" + PetId + "\""));
        Assert.Equal(1, Count(html, "data-pet-id=\"second-pet\""));
        Assert.DoesNotContain("data-pet-id=\"missing\"", html);
        Assert.DoesNotContain("data-pet-id=\"pinopi-legacy\"", html);
        await fixture.Export(html, "multiple-pets");
        html = await fixture.Render(fixture.Monster with { PetIds = [] });
        Assert.Contains(language == "de" ? "Diesem Monster ist kein Pet-Soul sicher zugeordnet." : "No pet soul is reliably linked to this monster.", html);
        Assert.DoesNotContain("data-pet-id", html);
        Assert.DoesNotContain("class=\"monster-search-stages\"", html);
        await fixture.Export(html, "unmapped");
    }

    [Fact]
    public async Task MapButtonsInvokeTheSelectedMapAndBusyStateDisablesNavigationAndClearing()
    {
        await using var fixture = new Fixture("en", "regular");
        var chosen = new List<string>();
        var cleared = 0;
        await fixture.Render(mapChanged: EventCallback.Factory.Create<string>(fixture, (string map) => chosen.Add(map)),
            clear: EventCallback.Factory.Create(fixture, () => cleared++));
        await fixture.Click("Show spawn points on Verteron");
        Assert.Equal(new[] { "verteron" }, chosen);
        await fixture.Click("Clear highlight");
        Assert.Equal(1, cleared);
        var html = await fixture.Render(busy: true);
        Assert.Contains("aria-busy=\"true\"", html);
        Assert.Contains("Loading map", html);
        var buttons = await fixture.Buttons();
        Assert.Equal(4, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.Disabled));
    }

    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private sealed record Button(string? Label, bool Disabled, object? Callback);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-monster-card-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly string _language;
        private MonsterSearchCard? _card;
        private string ProfilePath => Path.Combine(_folder, "progress.json");
        public ProgressService Progress { get; }
        public MonsterSearchMatch Monster { get; } = new("Gatekeeper Monster", "Gatekeeper Monster", "Torwächter-Monster", "icons/monster.png", [PetId], ["altgard", "verteron"], 12);
        private static readonly MapInfo[] Maps = [new("altgard", "Altgard", "Asmodian"), new("verteron", "Verteron", "Elyos"), new("abyss", "Abyss", "Abyss")];

        public Fixture(string language, string state)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            _language = language;
            Directory.CreateDirectory(_folder);
            var profile = new ProgressProfile { SchemaVersion = 3 };
            if (state == "saved")
            {
                profile.Pets[PetId] = new() { Souls = 999, Freshness = ProgressFreshness.Fresh, Source = "network" };
                profile.NetworkPets[Number] = new() { Level = 1, AvailableSouls = 3, EarnedTotal = 999,
                    HasBaseline = true, LevelObserved = true, BalanceObserved = true };
            }
            if (state == "unknown") profile.Pets[PetId] = new() { Souls = 999, Freshness = ProgressFreshness.Stale };
            JsonFile.Save(ProfilePath, profile);
            var catalog = new PetCatalog([new PetDefinition { Id = PetId, En = "Gatekeeper Pinopi", De = "Torwächter-Pinopi", Genus = "varian", NumericId = "1146", Icon = "icons/pet.png" },
                new PetDefinition { Id = "second-pet", En = "Fixture Second Pet", De = "Zweites Beispiel-Pet", Genus = "fera" }],
                new Dictionary<string, string> { ["pinopi-legacy"] = PetId });
            Progress = new ProgressService(ProfilePath, catalog);
            if (state is "regular" or "max")
            {
                Assert.True(Progress.BindPetSession(new(1, 0, At)));
                Assert.True(Progress.ApplyPetSnapshot(new(new Dictionary<int, int> { [Number] = state == "max" ? 3 : 1 },
                    new Dictionary<int, int> { [Number] = state == "max" ? 0 : 10 }), new(1, 1, At)));
                if (state == "regular") Assert.True(Progress.ApplyPetSpent([new(Number, 7)], new(1, 2, At)));
            }
            if (state == "manual") Progress.SetLevel(PetId, 1, 5);
            var settings = new SettingsService();
            settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = language;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Progress);
            services.AddSingleton(settings);
            _provider = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public Task<string> Render(MonsterSearchMatch? monster = null, bool busy = false, EventCallback<string> mapChanged = default, EventCallback clear = default) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var before = File.ReadAllBytes(ProfilePath);
            var parameters = new Dictionary<string, object?> { [nameof(MonsterSearchCard.Monster)] = monster ?? Monster,
                [nameof(MonsterSearchCard.SpawnMaps)] = Maps, [nameof(MonsterSearchCard.MapId)] = "altgard", [nameof(MonsterSearchCard.SpawnNote)] = _language == "de" ? "12 Fundorte auf Altgard hervorgehoben" : "12 locations on Altgard highlighted",
                [nameof(MonsterSearchCard.Busy)] = busy, [nameof(MonsterSearchCard.MapChanged)] = mapChanged, [nameof(MonsterSearchCard.Clear)] = clear };
            var root = await _renderer.RenderComponentAsync<CaptureCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CaptureCard.Parameters)] = parameters, [nameof(CaptureCard.Capture)] = (Action<MonsterSearchCard>)(card => _card = card) }));
            var html = WebUtility.HtmlDecode(root.ToHtmlString());
            Assert.Equal(before, File.ReadAllBytes(ProfilePath));
            return html;
        });
        public Task<List<Button>> Buttons() => _renderer.Dispatcher.InvokeAsync(ReadButtons);
        private List<Button> ReadButtons()
        {
            using var tree = new RenderTreeBuilder();
            typeof(MonsterSearchCard).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_card, [tree]);
            var frames = tree.GetFrames();
            var result = new List<Button>();
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                var end = i + frames.Array[i].ElementSubtreeLength;
                string? label = null;
                var disabled = false;
                object? callback = null;
                for (var j = i + 1; j < end && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var frame = frames.Array[j];
                    if (frame.AttributeName == "aria-label") label = frame.AttributeValue as string;
                    if (frame.AttributeName == "disabled") disabled = frame.AttributeValue is true;
                    if (frame.AttributeName == "onclick") callback = frame.AttributeValue;
                }
                result.Add(new(label, disabled, callback));
            }
            return result;
        }
        public Task Click(string? label) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var callback = Assert.IsAssignableFrom<object>(Assert.Single(ReadButtons(), button => button.Label == label).Callback);
            switch (callback)
            {
                case EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed: await typed.InvokeAsync(new()); break;
                case EventCallback untyped: await untyped.InvokeAsync(); break;
                case MulticastDelegate handler: if (handler.DynamicInvoke() is Task task) await task; break;
                default: throw new InvalidOperationException("Unexpected card callback type: " + callback.GetType().Name);
            }
        });
        public async Task Export(string html, string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_MONSTER_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"monster-card-{_language}-{state}.html"), "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\"><link rel=\"stylesheet\" href=\"/css/monster-search.css\"><style>html,body{margin:0;min-height:100%;background:#111922}.preview{padding:24px}.preview .sidebar{width:360px;min-height:670px;border:1px solid var(--line)}@media(max-width:430px){.preview{padding:8px}.preview .sidebar{width:100%}}</style></head><body><main class=\"preview\"><aside class=\"sidebar mapside mapside-left\"><div class=\"side-top\"><input class=\"search\" value=\"" + WebUtility.HtmlEncode(Monster.DisplayName(_language)) + "\" readonly></div>" + html + "</aside></main></body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previousLanguage;
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class CaptureCard : ComponentBase
    {
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = [];
        [Parameter] public Action<MonsterSearchCard> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MonsterSearchCard>(0);
            builder.AddMultipleAttributes(1, Parameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
            builder.AddComponentReferenceCapture(2, value => Capture((MonsterSearchCard)value));
            builder.CloseComponent();
        }
    }
}
#pragma warning restore BL0006
