using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LiveBossTimersTests
{
    [Theory]
    [InlineData("de", "Warte auf Boss-Timer", "Boss-Erinnerungen global pausiert")]
    [InlineData("en", "Waiting for boss timers", "Boss reminders are globally paused")]
    public async Task SelectedUncapturedBossHasCardAndPausedReminderWithoutStartingCapture(string language, string waiting, string paused)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        var html = await fixture.Render();
        Assert.Contains("data-boss=\"1110:111001\"", html);
        Assert.DoesNotContain("data-boss=\"1110:111002\"", html);
        Assert.Contains(waiting, html);
        Assert.Contains(paused, html);
        Assert.Contains("event-quick-reminders", html);
        Assert.DoesNotContain("00:00:00", html);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TurningOneReminderOffKeepsItsTimerAndOtherBossSettingsVisible(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossAlertLeadSeconds = 60;
        fixture.Capture(false, 610_000);
        await fixture.Render();
        var enabled = await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = true, LeadSeconds = 300 });
        Assert.Contains(language == "de" ? "Boss-Erinnerung gespeichert." : "Boss reminder saved.", enabled);
        Assert.Equal(300, fixture.Bosses.GetReminder("1110:111001").LeadSeconds);
        Assert.Equal(60, fixture.Bosses.GetReminder("1110:111002").LeadSeconds);
        var disabled = await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        Assert.Contains("data-boss=\"1110:111001\"", disabled);
        Assert.Contains("00:10:10", disabled);
        Assert.False(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.True(fixture.Bosses.GetReminder("1110:111002").Enabled);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(60, fixture.Settings.Current.BossAlertLeadSeconds);
        Assert.Equal(new[] { "1110:111001", "1110:111002" }, fixture.Settings.Current.BossAlertIds);
        Assert.Empty(fixture.Alerts.Items);
        Assert.Empty(fixture.Alerts.NotificationRows);
        Assert.False(fixture.Tracker.Running);
    }

    [Theory]
    [InlineData("de", "Gespeichert · unbestätigt", "Unbestätigt")]
    [InlineData("en", "Saved · unconfirmed", "Unconfirmed")]
    public async Task ExpiredAndCachedDataDoNotBecomeConfirmedSpawns(string language, string provisional, string unconfirmed)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Capture(false, 1_000);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(17);
        var expired = await fixture.Render();
        Assert.Contains(provisional, expired);
        Assert.Contains(language == "de" ? "Warte auf Bestätigung" : "Waiting for confirmation", expired);
        Assert.DoesNotContain(">" + (language == "de" ? "Gespawnt" : "Spawned") + "</strong>", expired);
        fixture.Capture(true, -1_000);
        fixture.Network.Stop();
        var cached = await fixture.Render();
        Assert.Contains(provisional, cached);
        Assert.Contains(">" + unconfirmed + "</strong>", cached);
        Assert.DoesNotContain(">" + (language == "de" ? "Gespawnt" : "Spawned") + "</strong>", cached);
        Assert.Empty(fixture.Alerts.NotificationRows);
    }

    [Fact]
    public async Task BusyAndDeselectedCardsRejectQueuedMutationsAndPreview()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossAlertsEnabled = true;
        await fixture.Render(busy: true);
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        await fixture.Invoke("SavePresentation", "1110:111001", new BossAlertPresentation { ShowOverlay = false });
        await fixture.Invoke("Preview", "1110:111001");
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.True(fixture.Alerts.GetBossPresentation("1110:111001").ShowOverlay);
        Assert.Empty(fixture.Bosses.Alerts);
        await fixture.View.SetBusy(false);
        fixture.Settings.Current.BossAlertIds = [];
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false, LeadSeconds = 300 });
        await fixture.Invoke("Preview", "1110:111001");
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled);
        Assert.Empty(fixture.Bosses.Alerts);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BossRushVariantKeepsUnselectedCapturedTimersAndSharesExplicitSelection(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Capture(false, 610_000);
        var html = await fixture.Render(includeAll: true);
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"false\"", html);
        Assert.Contains("00:10:10", html);
        Assert.Contains(language == "de" ? "Für Erinnerungen auswählen" : "Select for reminders", html);
        await fixture.Invoke("SetReminder", "1110:111001", new BossReminderPreference { Enabled = false });
        Assert.True(fixture.Bosses.GetReminder("1110:111001").Enabled); // An old click cannot implicitly select a boss.
        var selected = await fixture.Invoke("SelectChoice", "1110:111001", true);
        Assert.Equal("1110:111001", Assert.Single(fixture.Settings.Current.BossAlertIds));
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"true\"", selected);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        var removed = await fixture.Invoke("SelectChoice", "1110:111001", false);
        Assert.Empty(fixture.Settings.Current.BossAlertIds);
        Assert.Contains("data-boss=\"1110:111001\" data-selected=\"false\"", removed);
        Assert.Contains("00:10:10", removed);
        Assert.False(fixture.Tracker.Running);
    }

    [Fact]
    public async Task CardPreviewTargetsItsOwnBossEvenWhenItsReminderIsOff()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossAlertsEnabled = true;
        Assert.True(fixture.Bosses.SetReminder("1110:111002", new() { Enabled = false, LeadSeconds = 300 }));
        await fixture.Render();
        await fixture.Invoke("Preview", "1110:111002");
        Assert.Equal("1110:111002", Assert.Single(fixture.Bosses.Alerts).Key);
        Assert.False(fixture.Bosses.GetReminder("1110:111002").Enabled);
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Tracker.Running);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-timer-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        public CaptureLiveBossTimers View { get; private set; } = null!;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public AlertsService Alerts => _provider.GetRequiredService<AlertsService>();
        public TrackerService Tracker => _provider.GetRequiredService<TrackerService>();
        public NetworkLootService Network => _provider.GetRequiredService<NetworkLootService>();
        public BossRushService Bosses => _provider.GetRequiredService<BossRushService>();
        public ManualTime Clock { get; } = new();

        public Fixture(string language)
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[
                {"spawnId":111001,"npcId":2400017,"en":"Synthetic boss one","de":null,"x":100,"y":200},
                {"spawnId":111002,"npcId":2400074,"en":"Synthetic boss two","de":null,"x":300,"y":400}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(sp => new BossRushService(sp.GetRequiredService<ProgressService>(), sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<MapTargetsService>(), sp.GetRequiredService<NetworkLootService>(), Clock, Path.Combine(_folder, "boss-timers.json")));
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), Clock, runTimer: false, bosses: sp.GetRequiredService<BossRushService>(), settings: sp.GetRequiredService<SettingsService>()));
            _provider = services.BuildServiceProvider();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.BossRushEnabled = Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossReminderOverrides = new(StringComparer.Ordinal);
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.BossAlertSound = false;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public void Capture(bool spawned, long delta)
        {
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            byte[] message = spawned
                ? [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                    1, 0x99, 0xe3, 6, .. new byte[12], 1, .. BitConverter.GetBytes(now + delta), 0, 0, 0]
                : [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                    0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + delta), 0, 0, 0];
            var parsed = Soulcrest.Core.Network.BossSpawnMessage.TryParse(message);
            Assert.NotNull(parsed);
            Assert.Equal(spawned, Assert.Single(parsed.Bosses).Spawned);
            Bosses.OnMessage("fixture", message);
            Assert.Equal(spawned, Assert.Single(Bosses.Schedule).Spawned);
        }

        public async Task<string> Render(bool busy = false, bool includeAll = false)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureLiveBossTimers>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaptureLiveBossTimers.Captured)] = (Action<CaptureLiveBossTimers>)(view => View = view),
                [nameof(LiveBossTimers.Busy)] = busy, [nameof(LiveBossTimers.IncludeAllCaptured)] = includeAll
            })));
            return await Markup();
        }
        public async Task<string> Invoke(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(LiveBossTimers).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, args);
                if (result is Task task) await task;
                await View.RefreshNow();
            });
            return await Markup();
        }
        private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => UtcNow.UtcTicks;
    }
}

public sealed class CaptureLiveBossTimers : LiveBossTimers
{
    [Parameter] public Action<CaptureLiveBossTimers>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
    public Task SetBusy(bool busy) => InvokeAsync(() => { Busy = busy; StateHasChanged(); });
}
