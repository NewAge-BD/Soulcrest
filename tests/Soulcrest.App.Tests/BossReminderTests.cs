using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossReminderTests
{
    [Fact]
    public async Task PerBossChoicePersistsWithoutChangingMasterSelectionOrGlobalLead()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossAlertsEnabled = false;
        Assert.Equal(60, fixture.Bosses.GetReminder("1110:111001").LeadSeconds);
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { LeadSeconds = 900 }));
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(60, fixture.Settings.Current.BossAlertLeadSeconds);
        Assert.Equal(new[] { "1110:111001", "1110:111002" }, fixture.Settings.Current.BossAlertIds);
        Assert.Equal(60, fixture.Bosses.GetReminder("1110:111002").LeadSeconds);
        Assert.Equal(new BossReminderPreference { LeadSeconds = 900 }, new SettingsService().Current.BossReminderOverrides["1110:111001"]);
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = false, LeadSeconds = 900 }));
        Assert.False(new SettingsService().Current.BossReminderOverrides["1110:111001"].Enabled);
        Assert.True(fixture.Bosses.GetReminder("1110:111002").Enabled);
    }

    [Theory]
    [InlineData("1110:111001", -1)]
    [InlineData("1110:111001", 3601)]
    [InlineData("1110:999999", 300)]
    public async Task InvalidChoicesDoNotChangeSettings(string key, int seconds)
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { LeadSeconds = 120 }));
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        Assert.False(fixture.Bosses.SetReminder(key, new() { LeadSeconds = seconds }));
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Equal(120, fixture.Bosses.GetReminder("1110:111001").LeadSeconds);
    }

    [Fact]
    public async Task DistinctLeadsAndOffKeepSelectedTimersButRemoveQueuedNotices()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { LeadSeconds = 120 }));
        Assert.True(fixture.Bosses.SetReminder("1110:111002", new() { LeadSeconds = 900 }));
        fixture.Observe(300);
        Assert.Equal("1110:111002", Assert.Single(fixture.Bosses.Alerts).Key);
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.True(fixture.Bosses.SetReminder("1110:111002", new() { Enabled = false, LeadSeconds = 900 }));
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.Equal(2, fixture.Settings.Current.BossAlertIds.Length);
        fixture.Observe(300, spawned: true);
        var confirmed = Assert.Single(fixture.Bosses.Alerts);
        Assert.True(confirmed.Spawned);
        Assert.Equal("1110:111001", confirmed.Key);
        Assert.True(fixture.Settings.Current.BossAlertsEnabled);
        Assert.Equal(60, fixture.Settings.Current.BossAlertLeadSeconds);
    }

    [Fact]
    public async Task PreviewTargetsChosenBossAndNeverEnablesTheGlobalMaster()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Bosses.SetReminder("1110:111002", new() { Enabled = false, LeadSeconds = 600 }));
        fixture.Bosses.PreviewAlert("1110:111002");
        var preview = Assert.Single(fixture.Bosses.Alerts);
        Assert.True(preview.Preview);
        Assert.Equal("1110:111002", preview.Key);
        Assert.Equal(TimeSpan.FromMinutes(10), preview.Remaining);
        fixture.Settings.Update(settings => settings.BossAlertsEnabled = false);
        fixture.Bosses.PreviewAlert("1110:111001");
        Assert.Empty(fixture.Bosses.Alerts);
        Assert.False(fixture.Settings.Current.BossAlertsEnabled);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-reminders-" + Guid.NewGuid());
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly ManualTime _time = new();
        public SettingsService Settings { get; }
        public BossRushService Bosses { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[
                    {"spawnId":111001,"npcId":2400017,"en":"Fixture boss one","x":100,"y":200},
                    {"spawnId":111002,"npcId":2400074,"en":"Fixture boss two","x":300,"y":400}]}
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
            Settings.Current.BossRushEnabled = false;
            Settings.Current.BossAlertsEnabled = true;
            Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.BossAlertDurationSeconds = 15;
            Settings.Current.BossReminderOverrides = [];
            Bosses = new(_provider.GetRequiredService<ProgressService>(), Settings,
                _provider.GetRequiredService<MapTargetsService>(), _provider.GetRequiredService<NetworkLootService>(),
                _time, Path.Combine(_folder, "boss-cache.json"));
        }

        public void Observe(int seconds, bool spawned = false)
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(now)]);
            List<byte> message = [1, 0x91, 0, 0, 0x56, 4, 0, 0, 2];
            for (var index = 0; index < 2; index++)
            {
                message.Add((byte)(spawned ? 1 : 0));
                message.AddRange([(byte)(0x99 + index), 0xe3, 6]);
                if (spawned) message.AddRange(new byte[12]);
                if (index == 0) message.Add((byte)(spawned ? 3 : 0));
                message.AddRange(BitConverter.GetBytes(now + seconds * 1000L));
            }
            message.AddRange([0, 0, 0]);
            Bosses.OnMessage("fixture", message.ToArray());
            Assert.Equal(2, Bosses.Schedule.Count);
        }

        public async ValueTask DisposeAsync()
        {
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            _ = new SettingsService();
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1791257500000);
    }
}
