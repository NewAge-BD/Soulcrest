using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Soulcrest.App.Components;
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
    public async Task DedicatedTabTargetsEverySpawnAndRetainsTimersOnDisconnect(string language, string spawned, string name)
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
        var routeProgressPath = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
        var routeProgressBackup = File.Exists(routeProgressPath) ? File.ReadAllBytes(routeProgressPath) : null;
        Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", folder);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var clock = new ManualTime();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<SettingsService>();
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(sp => new BossRushService(sp.GetRequiredService<ProgressService>(),
                sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<MapTargetsService>(),
                sp.GetRequiredService<NetworkLootService>(), clock, Path.Combine(folder, "boss-timers.json")));
            services.AddSingleton(sp => new AlertsService(Path.Combine(folder, "alerts.json"), clock, runTimer: false, bosses: sp.GetRequiredService<BossRushService>(), settings: sp.GetRequiredService<SettingsService>()));
            services.AddSingleton(new ExplorationService(Path.Combine(folder, "exploration.json"), []));
            services.AddSingleton<UiState>();
            var js = new NoJs();
            services.AddSingleton<IJSRuntime>(js);
            await using var provider = services.BuildServiceProvider();
            var settings = provider.GetRequiredService<SettingsService>();
            settings.Current.UiLanguage = settings.Current.NameLanguage = UiText.Language = language;
            settings.Current.LastMap = "altgard";
            settings.Current.BossRushEnabled = true;
            settings.Current.BossRushShowOlderSpawns = false;
            settings.Current.BossNavigationPreviewCount = 1;
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
            var firstSpawn = Assert.Single(targets.BossNavigationTargets, target => target.BossSpawnId == 111001);
            Assert.Equal(name, firstSpawn.Name);
            Assert.Equal("#4ade80", firstSpawn.Color);
            Assert.Equal(2, targets.BossNavigationTargets.Count(target => target.BossRole == "spawned"));
            Assert.All(targets.BossNavigationTargets, target => Assert.Null(target.After));
            Assert.DoesNotContain(targets.Targets, target => target.IsBossNavigation); // not a saved/arrival target
            var bossRoot = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>());
            var markup = await renderer.Dispatcher.InvokeAsync(bossRoot.ToHtmlString);
            var html = System.Net.WebUtility.HtmlDecode(markup);
            Assert.Contains("Boss Rush Mode", html);
            Assert.Contains(language == "de" ? "Weitere Spawns auf der Karte" : "Additional spawns on the map", html);
            var navigationCount = Element(html, "input", "boss-navigation-preview-count");
            Assert.Contains("min=\"0\"", navigationCount);
            Assert.Contains("max=\"23\"", navigationCount);
            Assert.Contains("value=\"1\"", navigationCount);
            Assert.Contains("boss-dashboard-columns", html);
            Assert.Contains("<dl class=\"boss-dashboard-summary\">", html);
            Assert.Matches("boss-dashboard-stat spawned[\\s\\S]*?<dd>2</dd>", html);
            Assert.Matches("boss-dashboard-stat upcoming[\\s\\S]*?<dd>1</dd>", html);
            Assert.DoesNotContain("boss-loot-", html);
            Assert.Contains("data-overlay-link=\"boss\"", html);
            Assert.DoesNotContain("boss-quick-stats", html);
            Assert.Contains("boss-timer-card", html);
            Assert.Contains("event-boss-live boss-timers-compact", html);
            Assert.Equal(4, Regex.Matches(html, "class=\"boss-timer-overview\"").Count);
            var compactOptions = Regex.Matches(html, "<details class=\"boss-timer-options\"[^>]*>");
            Assert.Equal(4, compactOptions.Count);
            Assert.All(compactOptions.Cast<Match>(), option => Assert.DoesNotContain("open", option.Value));
            Assert.Contains("data-boss=\"1110:111003\" data-selected=\"false\"", html);
            Assert.Contains("data-boss=\"1110:111004\" data-selected=\"false\"", html); // Uncaptured catalogue bosses remain selectable after the separate card is removed.
            Assert.Contains("boss-timer-search", html);
            Assert.DoesNotContain("boss-alert-card", html);
            Assert.Single(Regex.Matches(html, "data-boss-alerts-master"));
            Assert.True(html.IndexOf("data-boss-alerts-master", StringComparison.Ordinal) > html.IndexOf("data-boss-list", StringComparison.Ordinal));
            Assert.Contains(language == "de" ? "Für Erinnerungen auswählen" : "Select for reminders", html);
            Assert.DoesNotContain("boss-upcoming-schedule", html);
            Assert.Contains(spawned, html);
            Assert.Contains(name, html);
            Assert.Contains(language == "de" ? "Spawn-Overlay" : "Spawn overlay", html);
            Assert.DoesNotContain("boss-scale-field", html);
            Assert.Contains("data-overlay-link=\"boss\"", html);
            Assert.DoesNotContain(language == "de" ? "Overlay sperren" : "Lock overlay", html);
            Assert.DoesNotContain("<kbd>L</kbd>", html);
            Assert.DoesNotContain("boss-scale-field", html);
            MapView? mapView = null;
            var mapRoot = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<MapHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(MapHost.Capture)] = (Action<MapView>)(view => mapView = view),
                })));
            Assert.NotNull(mapView);
            await ShowMap(renderer, mapView);
            AssertBossTimerPayload(js, "altgard", rush.Schedule);
            typeof(MapView).GetField("_mapId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mapView, "verteron");
            await ShowMap(renderer, mapView);
            AssertBossTimerPayload(js, "verteron", []);
            typeof(MapView).GetField("_mapId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mapView, "altgard");
            await ShowMap(renderer, mapView);
            var mapMarkup = await renderer.Dispatcher.InvokeAsync(mapRoot.ToHtmlString);
            Assert.Contains("boss-rush-tile", mapMarkup);
            Assert.Contains("checked", Element(mapMarkup, "input", "boss-rush-toggle"));
            Assert.DoesNotContain("checked", Element(mapMarkup, "input", "boss-rush-show-older-spawns"));
            var mapHtml = System.Net.WebUtility.HtmlDecode(mapMarkup);
            Assert.Contains(language == "de" ? "Gespawnte Bosse älter als 1 Min. anzeigen" : "Show Bosses Spawned older than 1 min", mapHtml);
            Assert.Contains(language == "de" ? "Boss-Rush-Tab öffnen" : "Open Boss Rush tab", mapHtml);
            Assert.Contains(language == "de" ? "Aktivierte Boss-Alerts bleiben unabhängig davon eingeschaltet." : "Enabled boss alerts remain independent.", mapHtml);
            await renderer.Dispatcher.InvokeAsync(() => settings.Update(s => s.BossRushShowOlderSpawns = true));
            Assert.Contains("checked", Element(await renderer.Dispatcher.InvokeAsync(mapRoot.ToHtmlString), "input", "boss-rush-show-older-spawns"));
            await renderer.Dispatcher.InvokeAsync(() => settings.Update(s => s.BossRushShowOlderSpawns = false));
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
            Assert.Contains("checked", Regex.Match(pausedMarkup, "<input[^>]*data-boss-alerts-master[^>]*>").Value);
            Assert.Contains(language == "de" ? "Boss Rush pausiert" : "Boss Rush paused", pausedHtml);
            Assert.Contains(language == "de" ? "Aktivierte Boss-Alerts bleiben eingeschaltet." : "Enabled boss alerts stay on.", pausedHtml);
            Assert.True(settings.Current.BossOverlayEnabled);
            await ShowMap(renderer, mapView);
            AssertBossTimerPayload(js, "altgard", rush.Schedule); // Guidance off retains the known schedule on the map.
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
            // The first waiting boss starts independently at the player, even with a live boss.
            settings.Current.BossAlertsEnabled = true;
            settings.Current.BossAlertIds = ["1110:111002"];
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 2,
                0, 0x9a, 0xe3, 6, 2, .. BitConverter.GetBytes(now + 10000),
                1, 0x99, 0xe3, 6, .. new byte[12], .. BitConverter.GetBytes(now - 2000), 0, 0, 0]);
            Assert.Null(Assert.Single(targets.BossNavigationTargets, target => target.BossRole == "next").After);
            Assert.Equal(111002, Assert.Single(rush.Schedule, r => !r.Spawned).Place.Boss.SpawnId);
            Assert.Single(rush.Alerts);
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 2,
                0, 0x9a, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 10000),
                0, 0x99, 0xe3, 6, .. BitConverter.GetBytes(now + 20000), 0, 0, 0]);
            Assert.DoesNotContain(targets.BossNavigationTargets, target => target.BossRole == "spawned");
            Assert.Null(Assert.Single(targets.BossNavigationTargets, target => target.BossRole == "next").After);
            Assert.Equal("boss:1110:111002", Assert.Single(targets.BossNavigationTargets, target => target.BossRole == "preview").After);
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
            Assert.NotEmpty(targets.BossNavigationTargets);
            Assert.All(targets.BossNavigationTargets, target => { Assert.True(target.BossProvisional); Assert.True(target.BossFaded); Assert.Equal("#4ade80", target.Color); });
            Assert.Empty(rush.Alerts);
            var cachedMarkup = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<Soulcrest.App.Components.BossRushView>()).ToHtmlString()));
            Assert.Contains(language == "de" ? "Gespeicherter Stand" : "Saved schedule", cachedMarkup);
            await ShowMap(renderer, mapView);
            AssertBossTimerPayload(js, "altgard", rush.Schedule);
            Assert.All(rush.Schedule, row => { Assert.True(row.Cached); Assert.True(row.Age.TotalSeconds >= 16); });
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
            if (routeProgressBackup is null) File.Delete(routeProgressPath);
            else File.WriteAllBytes(routeProgressPath, routeProgressBackup);
            Directory.Delete(folder, true);
        }
    }

    private static Task ShowMap(HtmlRenderer renderer, MapView view) => renderer.Dispatcher.InvokeAsync(async () =>
        await (Task)typeof(MapView).GetMethod("ShowMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [null])!);

    private static void AssertBossTimerPayload(NoJs js, string map, IReadOnlyList<BossRushEntry> expected)
    {
        var call = js.Calls.Last(call => call.Name == "soulcrestMap.setBossTimers");
        Assert.Equal(map, call.Arguments[0]);
        using var document = JsonDocument.Parse(Assert.IsType<string>(call.Arguments[1]));
        var rows = document.RootElement.EnumerateArray().OrderBy(row => row.GetProperty("spawnId").GetInt32()).ToArray();
        Assert.Equal(expected.Count, rows.Length);
        foreach (var (actual, entry) in rows.Zip(expected.OrderBy(row => row.Place.Boss.SpawnId)))
        {
            Assert.Equal(entry.Place.Boss.SpawnId, actual.GetProperty("spawnId").GetInt32());
            Assert.Equal(entry.Spawned, actual.GetProperty("spawned").GetBoolean());
            Assert.Equal(entry.Remaining.TotalSeconds, actual.GetProperty("remaining").GetDouble());
            Assert.Equal(entry.Age.TotalSeconds, actual.GetProperty("age").GetDouble());
            Assert.Equal(entry.Cached, actual.GetProperty("cached").GetBoolean());
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
            "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/lib/leaflet/leaflet.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/boss-timers.css\"><link rel=\"stylesheet\" href=\"/css/boss-rush.css\"></head><body>" + markup + "</body></html>");
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1791257500000 + _stamp);
        public void Advance(long ms) => _stamp += ms;
    }

    private sealed class MapHost : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            if (typeof(T) == typeof(string))
                return ValueTask.FromResult((T)(object)(identifier == "soulcrestMap.show" ? """{"groups":[],"regions":0,"markers":0}""" : "[]"));
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}
