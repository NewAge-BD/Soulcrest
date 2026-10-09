using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossRushViewTests
{
    [Theory]
    [InlineData("de", "Bereits gespawnt", "Geschmolzener Danar")]
    [InlineData("en", "Already spawned", "Melted Danar")]
    public async Task DedicatedTabTargetsOldestSpawnAndRetainsTimersOnDisconnect(string language, string spawned, string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-map-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(folder, "altgard"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
        File.WriteAllText(Path.Combine(folder, "altgard", "bosses.json"), """
            {"mapId":1110,"bosses":[
            {"spawnId":111001,"npcId":2400017,"en":"Melted Danar","de":"Geschmolzener Danar","x":100,"y":200,"icon":"icons/gamingtools/ui/resource/texture/portrait/portrait_256/ut_256_mob_dranavar_02_v01.png"},
            {"spawnId":111002,"npcId":2400074,"en":"Black Warrior Aed","de":null,"x":300,"y":400,"icon":"icons/gamingtools/ui/resource/texture/portrait/portrait_256/ut_256_mob_surawar_05_v02.png"},
            {"spawnId":111003,"npcId":2400140,"en":"Faithful Rajit","de":"Treuer Rajit","x":400,"y":500,"icon":"icons/gamingtools/ui/resource/texture/portrait/portrait_256/ut_256_mob_dracowar_01_v03.png"},
            {"spawnId":111004,"npcId":2400141,"en":"Berserker Vargor","de":"Berserker Vargor","x":600,"y":700}]}
            """);
        var previousData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        var previousLanguage = UiText.Language;
        var settingsPath = AppPaths.SettingsFile;
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", folder);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var clock = new ManualTime();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(sp => new BossRushService(sp.GetRequiredService<ProgressService>(),
                sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<MapTargetsService>(),
                sp.GetRequiredService<NetworkLootService>(), clock, Path.Combine(folder, "boss-timers.json")));
            services.AddSingleton<ExplorationService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime, NoJs>();
            await using var provider = services.BuildServiceProvider();
            var settings = provider.GetRequiredService<SettingsService>();
            settings.Current.UiLanguage = settings.Current.NameLanguage = UiText.Language = language;
            settings.Current.LastMap = "altgard";
            settings.Current.BossRushEnabled = true;
            settings.Current.BossAlertsEnabled = false;
            settings.Current.BossAlertIds = [];
            settings.Current.BossOverlayEnabled = true;
            var rush = provider.GetRequiredService<BossRushService>();
            var targets = provider.GetRequiredService<MapTargetsService>();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var emptyMarkup = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString());
            Assert.Contains(language == "de" ? "Noch keine Bossliste vorhanden" : "No boss list yet", System.Net.WebUtility.HtmlDecode(emptyMarkup));
            ExportPreview("empty", language, emptyMarkup);
            const long now = 1791257500000;
            rush.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            // Two spawned bosses, newest first, and one upcoming spawn; arrival never completes them.
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 3,
                1, 0x9a, 0xe3, 6, .. new byte[12], 3, .. BitConverter.GetBytes(now - 1000),
                1, 0x99, 0xe3, 6, .. new byte[12], .. BitConverter.GetBytes(now - 2000),
                0, 0x9b, 0xe3, 6, .. BitConverter.GetBytes(now + 61000), 0, 0, 0]);
            Assert.Equal(111001, rush.View.Boss!.SpawnId);
            Assert.Equal(name, targets.BossRush!.Name);
            Assert.Equal("#ef4444", targets.BossRush.Color);
            Assert.DoesNotContain(targets.Targets, t => t.Id == "boss-rush"); // not a saved/arrival target
            var bossRoot = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>());
            var markup = await renderer.Dispatcher.InvokeAsync(bossRoot.ToHtmlString);
            var html = System.Net.WebUtility.HtmlDecode(markup);
            Assert.Contains("Boss Rush Mode", html);
            Assert.Contains(spawned, html);
            Assert.Contains(name, html);
            Assert.Contains(language == "de" ? "Spawn-Overlay" : "Spawn overlay", html);
            Assert.Contains(language == "de" ? "Boss-Alerts" : "Boss alerts", html);
            Assert.Contains("min=\"1\" max=\"12\"", html);
            var mapRoot = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Soulcrest.App.Components.MapView>());
            var mapMarkup = await renderer.Dispatcher.InvokeAsync(mapRoot.ToHtmlString);
            Assert.Contains("boss-rush-tile", mapMarkup);
            Assert.Contains("checked", Element(mapMarkup, "input", "boss-rush-toggle"));
            var mapHtml = System.Net.WebUtility.HtmlDecode(mapMarkup);
            Assert.Contains(language == "de" ? "Boss-Rush-Tab öffnen" : "Open Boss Rush tab", mapHtml);
            Assert.Contains(language == "de" ? "Aktivierte Boss-Alerts bleiben unabhängig davon eingeschaltet." : "Enabled boss alerts remain independent.", mapHtml);
            // A settings change from either tab immediately updates both switches without a language change.
            // Turning guidance off keeps the overlay preference and independently enabled alerts intact.
            await renderer.Dispatcher.InvokeAsync(() => settings.Update(s =>
            {
                s.BossRushEnabled = false;
                s.BossAlertsEnabled = true;
            }));
            var pausedMarkup = await renderer.Dispatcher.InvokeAsync(bossRoot.ToHtmlString);
            var pausedMap = await renderer.Dispatcher.InvokeAsync(mapRoot.ToHtmlString);
            var pausedHtml = System.Net.WebUtility.HtmlDecode(pausedMarkup);
            Assert.DoesNotContain("checked", Element(pausedMap, "input", "boss-rush-toggle"));
            Assert.DoesNotContain("checked", Element(pausedMarkup, "input", "boss-rush-mode"));
            Assert.Contains("checked", Element(pausedMarkup, "input", "boss-alerts-toggle"));
            Assert.DoesNotContain("disabled", Element(pausedMarkup, "button", "boss-alert-test"));
            Assert.Contains(language == "de" ? "Boss Rush pausiert" : "Boss Rush paused", pausedHtml);
            Assert.Contains(language == "de" ? "Aktivierte Boss-Alerts bleiben eingeschaltet." : "Enabled boss alerts stay on.", pausedHtml);
            Assert.Contains(language == "de" ? "auch bei ausgeschaltetem Boss Rush Mode" : "also work with Boss Rush Mode off", pausedHtml);
            Assert.True(settings.Current.BossOverlayEnabled);
            Assert.False(provider.GetRequiredService<TrackerService>().Running); // no capture is started by rendering/settings updates
            rush.PreviewAlert();
            Assert.Single(rush.Alerts); // preview remains available while navigation is off
            ExportPreview("paused", language, pausedMarkup);
            await renderer.Dispatcher.InvokeAsync(() => settings.Update(s =>
            {
                s.BossRushEnabled = true;
                s.BossAlertsEnabled = false;
            }));
            Assert.Contains("checked", Element(await renderer.Dispatcher.InvokeAsync(mapRoot.ToHtmlString), "input", "boss-rush-toggle"));
            Assert.Contains("checked", Element(await renderer.Dispatcher.InvokeAsync(bossRoot.ToHtmlString), "input", "boss-rush-mode"));
            Assert.Empty(rush.Alerts);
            // A waiting boss is a faded preview chained to the current live boss, never an arrival target.
            settings.Current.BossAlertsEnabled = true;
            settings.Current.BossAlertIds = ["1110:111002"];
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 2,
                0, 0x9a, 0xe3, 6, 2, .. BitConverter.GetBytes(now + 10000),
                1, 0x99, 0xe3, 6, .. new byte[12], .. BitConverter.GetBytes(now - 2000), 0, 0, 0]);
            Assert.Equal("boss-rush", targets.BossPreview!.After);
            Assert.Equal(111002, Assert.Single(rush.Schedule, r => !r.Spawned).Place.Boss.SpawnId);
            Assert.Single(rush.Alerts);
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 2,
                0, 0x9a, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 10000),
                0, 0x99, 0xe3, 6, .. BitConverter.GetBytes(now + 20000), 0, 0, 0]);
            Assert.Null(targets.BossRush);
            Assert.Null(targets.BossPreview!.After);
            Assert.Single(rush.Alerts); // unchanged timer does not repeat a warning
            settings.Current.BossOverlayCount = 1;
            Assert.Single(rush.OverlaySpawns);
            settings.Current.BossOverlayCount = 12;
            Assert.Equal(2, rush.OverlaySpawns.Count);
            Assert.Equal(111002, rush.OverlaySpawns[0].Place.Boss.SpawnId);
            var upcomingMarkup = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString());
            ExportPreview("upcoming", language, upcomingMarkup);
            clock.Advance(16000);
            Assert.Empty(rush.Alerts);
            // Queue four selected spawns: three appear immediately, the fourth gets its full duration afterwards.
            provider.GetRequiredService<NetworkLootService>().Stop();
            settings.Current.BossAlertIds = ["1110:111001", "1110:111002", "1110:111003", "1110:111004"];
            for (var i = 0; i < 4; i++)
            {
                settings.Current.BossAlertIds = [$"1110:{111001 + i}"];
                rush.PreviewAlert();
            }
            Assert.Equal(3, rush.Alerts.Count);
            clock.Advance(16000);
            Assert.Equal(111004, Assert.Single(rush.Alerts).Boss.SpawnId);
            clock.Advance(16000);
            Assert.Empty(rush.Alerts);
            ExportPreview(null, language, markup);
            ExportPreview("map", language, mapMarkup);
            provider.GetRequiredService<NetworkLootService>().Stop(); // no capture was started
            Assert.NotNull(rush.View.Boss);
            Assert.True(rush.View.Cached);
            Assert.Null(targets.BossRush);
            Assert.NotNull(targets.BossPreview);
            Assert.Empty(rush.Alerts);
            var cachedMarkup = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString()));
            Assert.Contains(language == "de" ? "Gespeicherter Stand" : "Saved schedule", cachedMarkup);
            ExportPreview("cached", language, cachedMarkup);
            using var restarted = new BossRushService(provider.GetRequiredService<ProgressService>(), settings, targets,
                provider.GetRequiredService<NetworkLootService>(), clock, Path.Combine(folder, "boss-timers.json"));
            Assert.True(restarted.View.Cached);
            Assert.Equal(rush.Schedule.Select(r => r.Place.Boss.SpawnId), restarted.Schedule.Select(r => r.Place.Boss.SpawnId));
            Assert.Equal(rush.View.Remaining, restarted.View.Remaining);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", previousData);
            if (settingsBackup is null) File.Delete(settingsPath); else File.WriteAllText(settingsPath, settingsBackup);
            _ = new SettingsService();
            UiText.Language = previousLanguage;
            Directory.Delete(folder, true);
        }
    }

    private static string Element(string markup, string tag, string id)
    {
        var match = Regex.Match(markup, "<" + tag + "\\b[^>]*\\bid=\"" + id + "\"[^>]*>");
        Assert.True(match.Success, $"Missing {tag} #{id}.");
        return match.Value;
    }

    private static void ExportPreview(string? state, string language, string markup)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_BOSS_UI_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        var name = state == "map" ? "boss-map" : state is null ? "boss-rush" : "boss-rush-" + state;
        File.WriteAllText(Path.Combine(output, name + "-" + language + ".html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/lib/leaflet/leaflet.css\"></head><body>" + markup + "</body></html>");
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1791257500000 + _stamp);
        public void Advance(long ms) => _stamp += ms;
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
