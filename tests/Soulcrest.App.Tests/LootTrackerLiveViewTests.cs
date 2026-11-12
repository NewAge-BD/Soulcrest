using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerLiveViewTests
{
    private const string DetectedProfile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherProfile = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ParentBindsDetectedProfileAndBothMarketStatusesAsValues(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Ready();
        Assert.Equal(DetectedProfile, fixture.Automatic.Snapshot.ProfileId);
        Assert.Equal("Marktserver automatisch zugeordnet.", fixture.Automatic.Snapshot.Status);
        Assert.NotEqual(DetectedProfile, fixture.Exploration.ActiveId);
        var settingsBefore = await File.ReadAllBytesAsync(AppPaths.SettingsFile);

        var rendered = await fixture.Render();
        var html = WebUtility.HtmlDecode(rendered);

        // Render the real live parent: directly rendering ExchangeMarketSettings cannot
        // detect a Razor string parameter that accidentally contains a C# expression.
        var autoStatus = Regex.Match(html, "data-exchange-auto-status=\"true\"[^>]*>([^<]*)<").Groups[1].Value;
        Assert.Equal(UiText.T(fixture.Automatic.Snapshot.Status), autoStatus);
        Assert.Contains(UiText.T(fixture.Market.Snapshot.Status), html);
        Assert.Matches("<option value=\"" + DetectedProfile + "\" selected(?:=\"selected\")?>Synthetic profile A</option>", html);
        Assert.Contains("Synthetic market", html);
        Assert.DoesNotContain("AutomaticMarket.Snapshot.", html);
        Assert.DoesNotContain("Market.Snapshot.", html);
        Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(AppPaths.SettingsFile));
        Assert.Empty(fixture.Unified.Snapshot.Sessions);
        Assert.False(fixture.Source.Running);
        Assert.Equal(0, fixture.Client.Requests);
        await Export(rendered, language);
    }

    private static async Task Export(string html, string language)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_AUTO_MARKET_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        var cssRoot = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css");
        var css = string.Join("\n", await Task.WhenAll(new[] { "app.css", "overlay.css", "loot-tracker.css" }
            .Select(name => File.ReadAllTextAsync(Path.Combine(cssRoot, name)))));
        await File.WriteAllTextAsync(Path.Combine(output, "auto-market-parent-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css +
            "</style></head><body>" + html + "</body></html>");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _languageBefore = UiText.Language;
        private readonly byte[]? _settingsBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-loot-live-view-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly AbyssProgressService _abyss;
        private readonly LootTrackerReplayService _replays;
        internal SettingsService Settings { get; } = new();
        internal InertSource Source { get; } = new();
        internal NoNetworkClient Client { get; } = new();
        internal ExplorationService Exploration { get; }
        internal UnifiedLootTrackerService Unified { get; }
        internal ExchangeLootValuationService Market { get; }
        internal ExchangeMarketAutoSelectionService Automatic { get; }

        internal Fixture(string language)
        {
            Directory.CreateDirectory(_root);
            Settings.Current.UiLanguage = language; UiText.Language = language;
            Settings.Current.LootTrackingEnabled = false;
            Settings.Current.OnlineMarketPricesEnabled = true;
            Settings.Current.ExchangeMarketProfiles = [];
            var explorationPath = Path.Combine(_root, "exploration.json");
            JsonFile.Save(explorationPath, new ExplorationProfiles
            {
                SchemaVersion = 2, Active = OtherProfile,
                Characters = [new() { Id = DetectedProfile, Name = "Synthetic profile A" },
                    new() { Id = OtherProfile, Name = "Synthetic profile B" }],
            });
            Exploration = new(explorationPath, []);
            var clock = new FixedClock();
            WorldContextSnapshot World() => new(WorldContextStatus.Unknown, WorldContextKind.Unknown,
                null, null, null, null, null, null, 0, null, "synthetic");
            Unified = new(Settings, Source, Source, Source, Source, _ => null, World,
                new(Path.Combine(_root, "sessions")), clock,
                acquireRecorder: () => throw new InvalidOperationException("Rendering cannot acquire tracking."),
                dailyStore: new(Path.Combine(_root, "daily.json")));
            _abyss = new(Settings, Source, _ => null, World, new(Path.Combine(_root, "abyss.json")), clock);
            var cache = new ExchangeMarketCache(clock);
            cache.SaveServers([new(7, "Synthetic market", "EU", "Europe", "Elyos", true) { GameServerKey = 11301 }]);
            Market = new(() => Settings.Current.OnlineMarketPricesEnabled, Unified, Client, cache,
                new(Path.Combine(_root, "valuations")), clock);
            Automatic = new(Settings, () => new(DetectedProfile, 11301, 1, 1), Client, cache, clock);
            _replays = new(Path.Combine(_root, "replays"));
            _provider = new ServiceCollection().AddLogging().AddSingleton(Settings).AddSingleton<UiState>()
                .AddSingleton(Unified).AddSingleton(_replays).AddSingleton(_abyss).AddSingleton(Market)
                .AddSingleton(Automatic).AddSingleton(Exploration).BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        internal async Task Ready()
        {
            await Automatic.FlushAsync();
            await Unified.FlushAsync();
            await _abyss.DrainAsync();
            await Market.FlushAsync();
        }

        internal Task<string> Render() => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<LootTrackerLiveView>(ParameterView.Empty)).ToHtmlString());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            await Automatic.DisposeAsync();
            await Market.DisposeAsync();
            await _abyss.DisposeAsync();
            await Unified.DisposeAsync();
            _replays.Dispose();
            if (_settingsBefore is null) File.Delete(AppPaths.SettingsFile);
            else await File.WriteAllBytesAsync(AppPaths.SettingsFile, _settingsBefore);
            UiText.Language = _languageBefore;
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoNetworkClient : IExchangeMarketClient
    {
        internal int Requests;
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("UI tests must use the fresh synthetic cache."); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("Rendering cannot request prices."); }
    }

    private sealed class InertSource : INetworkFarmSource, INetworkKinaSource, INetworkGatheringSource, INetworkAbyssSource
    {
        public bool Running => false;
        public OwnCharacterContext? OwnCharacter => null;
        public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot => new(null, null, null, 0);
        public NetworkKinaSnapshot KinaSnapshot => new(null, null, null, false, "Synthetic inactive source");
        public NetworkAbyssSnapshot AbyssSnapshot => NetworkAbyssSnapshot.Empty;
        public bool GatheringVerified => false;
        public string GatheringStatus => "Synthetic inactive source";
        public event Action? Changed { add { } remove { } }
        public event Action? OwnCharacterChanged { add { } remove { } }
        public event Action<NetworkKinaGain>? KinaGained { add { } remove { } }
        public event Action<NetworkKinaGain>? MonsterKinaGained { add { } remove { } }
        public event Action<NetworkFarmDrop>? FarmDropGained { add { } remove { } }
        public event Action<NetworkGathering>? GatheringCompleted { add { } remove { } }
        public event Action<NetworkAbyssPointGain>? AbyssPointsGained { add { } remove { } }
        public event Action<NetworkAbyssRankChange>? RankPointsChanged { add { } remove { } }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
