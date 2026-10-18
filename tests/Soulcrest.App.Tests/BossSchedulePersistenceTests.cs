using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossSchedulePersistenceTests
{
    [Fact]
    public async Task SwitchingMapsRetainsOtherCountdownsWithoutRoutesOrNewAlertsForCachedMaps()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1010:101001"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 300000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 900000));
        var first = fixture.Bosses.Schedule.ToDictionary(row => row.Place.Key);
        Assert.True(first["1110:111001"].Cached);
        Assert.False(first["1010:101001"].Cached);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        Assert.Equal("verteron", fixture.Targets.BossPreview!.MapId);
        Assert.Equal("verteron", Assert.Single(fixture.Bosses.OverlaySpawns).Place.Map);
        using var alerts = fixture.CreateAlerts();
        fixture.Clock.Advance(TimeSpan.FromSeconds(250));
        fixture.Refresh();
        var rows = alerts.TimerOverlayRows.ToDictionary(row => row.Key);
        Assert.True(rows["boss:1110:111001"].Provisional);
        Assert.False(rows["boss:1010:101001"].Provisional);
        Assert.Equal(TimeSpan.FromSeconds(50), rows["boss:1110:111001"].At - fixture.Clock.GetUtcNow());
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.DoesNotContain(alerts.NotificationRows, row => row.Key.StartsWith("boss:", StringComparison.Ordinal));
        fixture.Clock.Advance(TimeSpan.FromSeconds(700));
        fixture.Refresh();
        Assert.All(fixture.Bosses.Schedule, row => { Assert.False(row.Spawned); Assert.Equal(TimeSpan.Zero, row.Remaining); });
        Assert.Empty(fixture.Bosses.Alerts);
    }

    [Fact]
    public async Task RestartAfterSeveralDaysKeepsAllCapturedMapsProvisionalAndDoesNotConfirmElapsedSpawns()
    {
        await using var fixture = new Fixture();
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 60000));
        fixture.Observe(1010, new BossSpawn(101001, true, fixture.UnixNow - 1000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Clock.Advance(TimeSpan.FromDays(3));
        fixture.Restart();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1010:101001"];
        fixture.Refresh();
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.All(fixture.Bosses.Schedule, row => { Assert.True(row.Cached); Assert.True(row.Age >= TimeSpan.FromDays(3)); });
        var altgard = Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "altgard");
        Assert.False(altgard.Spawned);
        Assert.Equal(TimeSpan.Zero, altgard.Remaining);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        Assert.True(fixture.Bosses.View.Cached);
        Assert.Equal("verteron", fixture.Targets.BossRush!.MapId); // Preserve existing latest-map navigation.
        Assert.Null(fixture.Targets.BossPreview);
        Assert.Empty(fixture.Bosses.Alerts);
        using var alerts = fixture.CreateAlerts();
        Assert.All(alerts.TimerOverlayRows, row => { Assert.True(row.Provisional); Assert.Equal("?", TimerOverlayPresentation.Countdown(row, fixture.Clock.GetUtcNow())); });
        Assert.Equal(2, alerts.TimerOverlayRows.Count);
        Assert.DoesNotContain(alerts.NotificationRows, row => row.Key.StartsWith("boss:", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath));
    }

    [Fact]
    public async Task RetainedMapsUseMonotonicTimeAndAConfirmedListReplacesOnlyItsOwnMap()
    {
        await using var fixture = new Fixture();
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Clock.ShiftWall(TimeSpan.FromDays(-2));
        Assert.Equal(TimeSpan.FromSeconds(590), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "altgard").Remaining);
        Assert.Equal(TimeSpan.FromSeconds(1190), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Map == "verteron").Remaining);
        fixture.Clock.ShiftWall(TimeSpan.FromDays(2));
        fixture.Observe(1110, new BossSpawn(111002, false, fixture.UnixNow + 1800000));
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.DoesNotContain(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111001");
        Assert.False(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111002").Cached);
        Assert.True(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1010:101001").Cached);
        fixture.Network.Stop();
        Assert.All(fixture.Bosses.Schedule, row => Assert.True(row.Cached));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1795), Assert.Single(fixture.Bosses.Schedule, row => row.Place.Key == "1110:111002").Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownFullListCannotExposeKnownPartsOrReviveItsOldMapCache(bool unknownTime)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1010:101001"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        var bytes = File.ReadAllBytes(fixture.CachePath);
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow),
            new BossSpawn(999999, !unknownTime, unknownTime ? null : fixture.UnixNow));
        var only = Assert.Single(fixture.Bosses.Schedule);
        Assert.Equal("verteron", only.Place.Map);
        Assert.True(only.Cached);
        Assert.Null(fixture.Bosses.View.Boss);
        Assert.Empty(fixture.Bosses.OverlaySpawns);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.CachePath));
    }

    [Fact]
    public async Task MixedMissingTimesSurviveRestartAndCannotChooseUnknownTargetsOrAlerts()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111001", "boss:1110:111002"];
        fixture.Observe(1110, new BossSpawn(111001, false, fixture.UnixNow + 600000), new(111002, false, null));
        Assert.Equal("1110:111001", Assert.Single(fixture.Bosses.Schedule).Place.Key);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Restart();
        var restored = Assert.Single(fixture.Bosses.Schedule);
        Assert.True(restored.Cached);
        Assert.Equal("1110:111001", restored.Place.Key);
        Assert.Equal(TimeSpan.FromSeconds(590), restored.Remaining);
        fixture.Refresh();
        Assert.Null(fixture.Targets.BossRush);
        Assert.Equal("Synthetic boss one", fixture.Targets.BossPreview!.Name);
        Assert.Empty(fixture.Bosses.Alerts);
        using var alerts = fixture.CreateAlerts();
        Assert.Equal("boss:1110:111001", Assert.Single(alerts.TimerOverlayRows).Key);
        var fullList = new BossTimerCache(fixture.CachePath, fixture.Clock).Load();
        Assert.NotNull(fullList);
        Assert.Equal(2, fullList.Bosses.Count);
        Assert.Null(fullList.Bosses.Single(b => b.SpawnId == 111002).SpawnUnixMs);
        fixture.Observe(1110, new BossSpawn(111001, false, null), new(111002, false, null));
        Assert.Empty(fixture.Bosses.Schedule);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Empty(alerts.TimerOverlayRows);
    }

    [Fact]
    public async Task UnknownSavedListsAreRejectedWholeAndRestoredMapsNeedTheirOwnFreshClockToBecomeLive()
    {
        await using var fixture = new Fixture();
        var cache = new BossTimerCache(fixture.CachePath, fixture.Clock);
        Assert.True(cache.Save(new(1110, [new BossSpawn(111001, false, fixture.UnixNow + 600000), new BossSpawn(999999, false, fixture.UnixNow + 600000)], fixture.UnixNow, TimeSpan.Zero)));
        Assert.True(cache.Save(new(1010, [new BossSpawn(101001, false, fixture.UnixNow + 1200000)], fixture.UnixNow, TimeSpan.Zero)));
        fixture.Restart();
        Assert.Equal("verteron", Assert.Single(fixture.Bosses.Schedule).Place.Map);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        var list = ListMessage(1010, new BossSpawn(101001, false, fixture.UnixNow + 1200000));
        fixture.Bosses.OnMessage("fresh", list);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Bosses.OnMessage("other", [0, 0x36, .. BitConverter.GetBytes(fixture.UnixNow)]);
        fixture.Bosses.OnMessage("fresh", list);
        Assert.True(Assert.Single(fixture.Bosses.Schedule).Cached);
        fixture.Bosses.OnMessage("fresh", [0, 0x36, .. BitConverter.GetBytes(fixture.UnixNow)]);
        fixture.Bosses.OnMessage("fresh", list);
        Assert.False(Assert.Single(fixture.Bosses.Schedule).Cached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BossWithoutASingleVerifiedCoordinateRetainsItsTimerButNeverCreatesATarget(bool spawned)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111002"];
        fixture.Observe(1110, new BossSpawn(111002, spawned, fixture.UnixNow + 600000));
        Assert.Equal("1110:111002", Assert.Single(fixture.Bosses.Schedule).Place.Key);
        Assert.NotNull(fixture.Bosses.View.Boss);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Null(fixture.Targets.BossPreview);
        using var alerts = fixture.CreateAlerts();
        Assert.Equal("boss:1110:111002", Assert.Single(alerts.TimerOverlayRows).Key);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BossRushTargetStatisticsRemainOnTheCurrentMapWhileTheListRetainsOtherMaps(string language)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.UiLanguage = fixture.Settings.Current.NameLanguage = UiText.Language = language;
        fixture.Settings.Current.BossRushEnabled = true;
        fixture.Observe(1110, new BossSpawn(111001, true, fixture.UnixNow - 1000));
        fixture.Observe(1010, new BossSpawn(101001, false, fixture.UnixNow + 600000));
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.Equal("verteron", fixture.Bosses.View.MapId);
        var html = await fixture.RenderBossRush();
        Assert.Matches("boss-dashboard-stat spawned[\\s\\S]*?<dd>0</dd>", html);
        Assert.Matches("boss-dashboard-stat upcoming[\\s\\S]*?<dd>1</dd>", html);
        Assert.Contains("Synthetic boss one", html); // Historic map remains available in the shared timer list.
        Assert.Contains("Synthetic boss two", html);
        Assert.Null(fixture.Targets.BossRush);
        Assert.Equal("verteron", fixture.Targets.BossPreview!.MapId);
    }

    private static byte[] ListMessage(int mapId, params BossSpawn[] bosses)
    {
        List<byte> message = [1, 0x91, 0, 0, .. BitConverter.GetBytes(mapId)];
        Varint(bosses.Length);
        for (var index = 0; index < bosses.Length; index++)
        {
            var boss = bosses[index];
            message.Add((byte)(boss.Spawned ? 1 : 0));
            Varint(boss.SpawnId);
            if (boss.Spawned) message.AddRange(new byte[12]);
            if (index % 8 == 0)
            {
                byte mask = 0;
                for (var bit = 0; bit < 8 && index + bit < bosses.Length; bit++)
                    if (bosses[index + bit].Spawned) mask |= (byte)(1 << bit);
                message.Add(mask);
            }
            message.AddRange(BitConverter.GetBytes(boss.SpawnUnixMs ?? 0));
        }
        message.AddRange([0, 0, 0]);
        return message.ToArray();

        void Varint(int value)
        {
            var remaining = (uint)value;
            while (remaining >= 128) { message.Add((byte)(remaining | 128)); remaining >>= 7; }
            message.Add((byte)remaining);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-persistence-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        public SettingsService Settings { get; }
        public NetworkLootService Network { get; }
        public MapTargetsService Targets { get; }
        public BossRushService Bosses { get; private set; }
        public ManualTime Clock { get; } = new();
        public long UnixNow => Clock.GetUtcNow().ToUnixTimeMilliseconds();
        public string CachePath => Path.Combine(_folder, "boss-timers.json");

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            Directory.CreateDirectory(Path.Combine(_folder, "verteron"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[
                {"spawnId":111001,"npcId":2400017,"en":"Synthetic boss one","x":100,"y":200},
                {"spawnId":111002,"npcId":2400074,"en":"Synthetic boss without single location","x":null,"y":null}]}
                """);
            File.WriteAllText(Path.Combine(_folder, "verteron", "bosses.json"), """
                {"mapId":1010,"bosses":[{"spawnId":101001,"npcId":2400101,"en":"Synthetic boss two","x":300,"y":400}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<MapTargetsService>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Current.BossRushEnabled = Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossReminderOverrides = [];
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.TimerOverlayTimerIds = [];
            Settings.Current.TimerOverlayLeadMinutes = 0;
            Network = _provider.GetRequiredService<NetworkLootService>();
            Targets = _provider.GetRequiredService<MapTargetsService>();
            Bosses = CreateBosses();
        }

        private BossRushService CreateBosses() => new(_provider.GetRequiredService<ProgressService>(), Settings, Targets, Network, Clock, CachePath);
        public void Restart() { Bosses.Dispose(); Bosses = CreateBosses(); }
        public void Refresh() => Settings.Update(_ => { });
        public AlertsService CreateAlerts() => new(Path.Combine(_folder, Guid.NewGuid().ToString("N") + "-alerts.json"), Clock, runTimer: false, bosses: Bosses, settings: Settings);
        public void Observe(int mapId, params BossSpawn[] bosses)
        {
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(UnixNow)]);
            Bosses.OnMessage("fixture", ListMessage(mapId, bosses));
        }

        public async Task<string> RenderBossRush()
        {
            using var alerts = CreateAlerts();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Bosses);
            services.AddSingleton(Settings);
            services.AddSingleton(Targets);
            services.AddSingleton(_provider.GetRequiredService<ProgressService>());
            services.AddSingleton(alerts);
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString());
        }

        public async ValueTask DisposeAsync()
        {
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1791547200000);
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _stamp += (long)elapsed.TotalMilliseconds; }
        public void ShiftWall(TimeSpan elapsed) => _now += elapsed;
    }
}
