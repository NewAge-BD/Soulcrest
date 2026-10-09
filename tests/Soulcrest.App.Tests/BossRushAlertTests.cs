using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossRushAlertTests
{
    [Fact]
    public async Task AlertsContinueWhileNavigationAndSpawnOverlayAreOff()
    {
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-independent-alerts-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(folder, "altgard"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
        File.WriteAllText(Path.Combine(folder, "altgard", "bosses.json"), """
            {"mapId":1110,"bosses":[{"spawnId":111001,"npcId":2400017,"en":"Melted Danar","de":"Geschmolzener Danar","x":100,"y":200}]}
            """);
        var previousData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        var settingsPath = AppPaths.SettingsFile;
        var backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", folder);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<MapTargetsService>();
            await using var provider = services.BuildServiceProvider();
            var settings = provider.GetRequiredService<SettingsService>();
            settings.Current.BossRushEnabled = false;
            settings.Current.BossAlertsEnabled = true;
            settings.Current.BossAlertLeadSeconds = 60;
            settings.Current.BossAlertDurationSeconds = 15;
            settings.Current.BossAlertIds = ["1110:111001"];
            var targets = provider.GetRequiredService<MapTargetsService>();
            var clock = new ManualTime();
            using var rush = new BossRushService(provider.GetRequiredService<ProgressService>(), settings, targets,
                provider.GetRequiredService<NetworkLootService>(), clock, Path.Combine(folder, "boss-timers.json"));
            const long now = 1791257500000;
            rush.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                0, 0x99, 0xe3, 6, 0, .. BitConverter.GetBytes(now + 10000), 0, 0, 0]);
            var warning = Assert.Single(rush.Alerts);
            Assert.False(warning.Spawned);
            Assert.Null(targets.BossRush);
            Assert.Null(targets.BossPreview);

            settings.Update(s => s.BossRushEnabled = true);
            Assert.NotNull(targets.BossPreview);
            Assert.Equal(warning.Id, Assert.Single(rush.Alerts).Id);
            settings.Update(s => s.BossRushEnabled = false);
            Assert.Null(targets.BossPreview);
            Assert.Equal(warning.Id, Assert.Single(rush.Alerts).Id);

            clock.Advance(10000);
            rush.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now + 10000)]);
            rush.OnMessage("fixture", [1, 0x91, 0, 0, 0x56, 4, 0, 0, 1,
                1, 0x99, 0xe3, 6, .. new byte[12], 1, .. BitConverter.GetBytes(now + 10000), 0, 0, 0]);
            Assert.True(Assert.Single(rush.Alerts).Spawned);
            Assert.Null(targets.BossRush);
            Assert.Null(targets.BossPreview);
            settings.Update(s => s.BossAlertsEnabled = false);
            Assert.Empty(rush.Alerts);
            rush.PreviewAlert();
            Assert.Empty(rush.Alerts);
            settings.Update(s => s.BossAlertsEnabled = true);
            Assert.Empty(rush.Alerts); // toggling visibility never repeats an already notified spawn
            rush.PreviewAlert();
            Assert.False(Assert.Single(rush.Alerts).Spawned); // preview works without navigation
            clock.Advance(16000);
            Assert.Empty(rush.Alerts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", previousData);
            if (backup is null) File.Delete(settingsPath); else File.WriteAllText(settingsPath, backup);
            _ = new SettingsService();
            Directory.Delete(folder, true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1791257500000 + _stamp);
        public void Advance(long ms) => _stamp += ms;
    }
}
