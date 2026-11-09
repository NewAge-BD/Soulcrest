using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossRushFilterTests
{
    [Theory]
    [InlineData("41", "unique", true, true)]
    [InlineData("41", "unique", false, false)]
    [InlineData("42", "unique", true, false)]
    [InlineData("43", "legend", true, false)]
    [InlineData("43", "unique", true, false)]
    [InlineData("41", "legend", true, false)]
    [InlineData("999", "unique", true, false)]
    [InlineData("unknown", "unique", true, false)]
    public void UniqueGearRequiresTheBossOwnPoolAndMatchingExplicitItemFacts(string itemId, string rarity, bool ownPool, bool expected)
    {
        var catalog = MonsterLootItemCatalog.FromJson("""
            {"schemaVersion":1,"items":[
                {"id":41,"nameEn":"Synthetic unique equipment","gear":true,"rarity":4},
                {"id":42,"nameEn":"Synthetic unique material","gear":false,"rarity":4},
                {"id":43,"nameEn":"Synthetic legendary equipment","gear":true,"rarity":3}
            ]}
            """);
        var boss = new BossPlace(111001, 2400017, "Synthetic boss", null, 100, 200,
            Loot: [new(itemId, "Synthetic drop", null, null, rarity, OwnPool: ownPool)]);

        Assert.Equal(expected, BossRushService.DropsOwnUniqueGear(boss, catalog));
    }

    [Theory]
    [InlineData(false, false, "111001,111002,111003,111004")]
    [InlineData(true, false, "111001,111003")]
    [InlineData(false, true, "111001,111002")]
    [InlineData(true, true, "111001")]
    public async Task EnabledFiltersIntersectBeforeChoosingNavigationAndOverlayTargets(bool uniqueOnly, bool alertOnly, string expectedIds)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyUniqueGearBosses = uniqueOnly;
        fixture.Settings.Current.BossRushOnlyAlertBosses = alertOnly;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Settings.Current.BossNavigationPreviewCount = 23;
        fixture.Observe(new(111001, false, fixture.Now + 60000), new(111002, false, fixture.Now + 120000),
            new(111003, false, fixture.Now + 180000), new(111004, false, fixture.Now + 240000));
        var expected = expectedIds.Split(',').Select(int.Parse).ToArray();

        Assert.Equal(expected, fixture.Bosses.NavigationSchedule.Select(row => row.Place.Boss.SpawnId));
        Assert.Equal(expected, fixture.Bosses.OverlaySpawns.Select(row => row.Place.Boss.SpawnId));
        Assert.Equal(expected.Select(id => (int?)id), fixture.Targets.BossNavigationTargets.Select(target => target.BossSpawnId));
        Assert.Equal(4, fixture.Bosses.Schedule.Count);
        Assert.Equal(4, fixture.Bosses.Catalogue.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlertFilterUsesSelectionAndEnabledPreferenceIndependentlyOfTheGlobalMaster(bool masterEnabled)
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Settings.Current.BossAlertsEnabled = masterEnabled;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111003"];
        fixture.Settings.Current.BossReminderOverrides = new()
        {
            ["1110:111002"] = new() { Enabled = true }, // An override alone is not a selection.
            ["1110:111003"] = new() { Enabled = false },
        };
        fixture.Settings.Current.TimerOverlayTimerIds = ["boss:1110:111004"];
        fixture.Observe(new(111004, false, fixture.Now + 240000), new(111003, false, fixture.Now + 120000),
            new(111002, false, fixture.Now + 60000), new(111001, false, fixture.Now + 180000));

        Assert.Equal(111001, Assert.Single(fixture.Bosses.NavigationSchedule).Place.Boss.SpawnId);
        Assert.Equal(111001, Assert.Single(fixture.Bosses.OverlaySpawns).Place.Boss.SpawnId);
        Assert.Equal(111001, Assert.Single(fixture.Targets.BossNavigationTargets).BossSpawnId);
        Assert.Equal(111001, fixture.Bosses.View.Boss!.SpawnId);
        Assert.Equal(4, fixture.Bosses.Schedule.Count);
        Assert.Equal(4, fixture.Bosses.Catalogue.Count);
        Assert.Equal(new[] { "1110:111001", "1110:111003" }, fixture.Settings.Current.BossAlertIds);
        Assert.Equal(masterEnabled, fixture.Settings.Current.BossAlertsEnabled);
    }

    [Fact]
    public async Task SpawnOnlyReminderWithNoAdvanceWarningsStillMatchesTheAlertFilter()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Settings.Current.BossReminderOverrides = new()
        {
            ["1110:111001"] = new() { Enabled = true, LeadSecondsList = [] },
        };
        fixture.Observe(new(111001, false, fixture.Now + 60000), new(111002, false, fixture.Now + 30000));

        Assert.Empty(fixture.Bosses.GetReminder("1110:111001").WarningSeconds);
        Assert.Equal(111001, Assert.Single(fixture.Bosses.NavigationSchedule).Place.Boss.SpawnId);
        Assert.Equal(111001, fixture.Bosses.View.Boss!.SpawnId);
    }

    [Fact]
    public async Task FilteringBeforeThePreviewLimitsBuildsAChronologicalChainOfEligibleBosses()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111002", "1110:111004"];
        fixture.Settings.Current.BossNavigationPreviewCount = 1;
        fixture.Settings.Current.BossOverlayCount = 1;
        fixture.Observe(new(111001, false, fixture.Now + 60000), new(111002, false, fixture.Now + 120000),
            new(111003, false, fixture.Now + 180000), new(111004, false, fixture.Now + 240000));

        Assert.Equal(new[] { 111002, 111004 }, fixture.Bosses.NavigationSchedule.Select(row => row.Place.Boss.SpawnId));
        Assert.Equal(111002, Assert.Single(fixture.Bosses.OverlaySpawns).Place.Boss.SpawnId);
        Assert.Equal(new int?[] { 111002, 111004 }, fixture.Targets.BossNavigationTargets.Select(target => target.BossSpawnId));
        Assert.Equal("next", fixture.Targets.BossNavigationTargets[0].BossRole);
        Assert.Null(fixture.Targets.BossNavigationTargets[0].After);
        Assert.Equal("preview", fixture.Targets.BossNavigationTargets[1].BossRole);
        Assert.Equal("boss:1110:111002", fixture.Targets.BossNavigationTargets[1].After);
    }

    [Fact]
    public async Task PerBossPreferenceChangesImmediatelyRefreshNavigationWithoutClearingTheSelection()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001", "1110:111002"];
        fixture.Observe(new(111001, false, fixture.Now + 120000), new(111002, false, fixture.Now + 180000));
        var changes = 0;
        fixture.Targets.Changed += () => changes++;

        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = false }));

        Assert.Equal(1, changes);
        Assert.Equal(111002, Assert.Single(fixture.Bosses.NavigationSchedule).Place.Boss.SpawnId);
        Assert.Equal(111002, fixture.Bosses.View.Boss!.SpawnId);
        Assert.Equal(2, fixture.Settings.Current.BossAlertIds.Length);
        fixture.Settings.Update(settings => settings.BossAlertsEnabled = true);
        Assert.Equal(1, changes);
        fixture.Settings.Update(settings => settings.BossAlertsEnabled = false);
        Assert.Equal(1, changes);
        Assert.True(fixture.Bosses.SetReminder("1110:111001", new() { Enabled = true }));
        Assert.Equal(2, changes);
        Assert.Equal(2, fixture.Bosses.NavigationSchedule.Count);
    }

    [Fact]
    public async Task EmptyAlertSelectionHidesNavigationWhileKeepingCapturedTimersAndTheCatalogue()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Observe(new(111001, true, fixture.Now - 20000), new(111002, false, fixture.Now + 120000));

        Assert.Empty(fixture.Bosses.NavigationSchedule);
        Assert.Empty(fixture.Bosses.OverlaySpawns);
        Assert.Empty(fixture.Targets.BossNavigationTargets);
        Assert.Null(fixture.Bosses.View.Boss);
        Assert.True(fixture.Bosses.View.FilteredOut);
        Assert.Equal("altgard", fixture.Bosses.View.MapId);
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.Equal(4, fixture.Bosses.Catalogue.Count);
        fixture.Settings.Update(settings => settings.BossRushOnlyAlertBosses = false);
        Assert.Equal(2, fixture.Bosses.NavigationSchedule.Count);
        Assert.Equal(2, fixture.Targets.BossNavigationTargets.Count);
    }

    [Fact]
    public async Task DisplayFilterChangesDoNotRetriggerOrRemoveAnAuthorizedAlert()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111001"];
        fixture.Observe(new(111001, false, fixture.Now + 30000), new(111002, false, fixture.Now + 120000));
        var notice = Assert.Single(fixture.Bosses.Alerts);

        fixture.Settings.Update(settings => settings.BossRushOnlyAlertBosses = true);

        Assert.Equal(notice.Id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        fixture.Settings.Update(settings => settings.BossRushOnlyAlertBosses = false);
        Assert.Equal(notice.Id, Assert.Single(fixture.Bosses.Alerts).Id);
    }

    [Fact]
    public async Task UniqueDisplayFilterDoesNotSuppressAnAlertForABossWithOnlyBasicGear()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossAlertsEnabled = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111002"];
        fixture.Observe(new(111002, false, fixture.Now + 30000), new(111001, false, fixture.Now + 120000));
        var notice = Assert.Single(fixture.Bosses.Alerts);
        Assert.Equal("1110:111002", notice.Key);

        fixture.Settings.Update(settings => settings.BossRushOnlyUniqueGearBosses = true);

        Assert.Equal(111001, Assert.Single(fixture.Bosses.NavigationSchedule).Place.Boss.SpawnId);
        Assert.Equal(notice.Id, Assert.Single(fixture.Bosses.Alerts).Id);
        Assert.Equal(2, fixture.Bosses.Schedule.Count);
        Assert.Equal(new[] { "1110:111002" }, fixture.Settings.Current.BossAlertIds);
    }

    [Fact]
    public async Task CachedTimersUseTheSameAlertSelectionWithoutConfirmingNewSpawns()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BossRushOnlyAlertBosses = true;
        fixture.Settings.Current.BossAlertIds = ["1110:111002"];
        fixture.Observe(new(111001, false, fixture.Now + 60000), new(111002, false, fixture.Now + 120000));

        fixture.Network.Stop();

        Assert.True(Assert.Single(fixture.Bosses.NavigationSchedule).Cached);
        var target = Assert.Single(fixture.Targets.BossNavigationTargets);
        Assert.Equal(111002, target.BossSpawnId);
        Assert.Equal("next", target.BossRole);
        Assert.True(target.BossProvisional);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-filters-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        public long Now { get; } = 1791547200000;
        public SettingsService Settings { get; }
        public NetworkLootService Network { get; }
        public MapTargetsService Targets { get; }
        public BossRushService Bosses { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodier"}]}""");
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"), """
                {"mapId":1110,"bosses":[
                {"spawnId":111001,"npcId":2400017,"en":"Synthetic boss one","x":100,"y":200,
                 "loot":[{"id":"210230006","en":"Synthetic own equipment","rarity":"unique","ownPool":true}]},
                {"spawnId":111002,"npcId":2400074,"en":"Synthetic boss two","x":300,"y":400,
                 "loot":[{"id":"210230006","en":"Synthetic basic equipment","rarity":"unique","ownPool":false}]},
                {"spawnId":111003,"npcId":2400140,"en":"Synthetic boss three","x":500,"y":600,
                 "loot":[{"id":"210230006","en":"Synthetic own equipment","rarity":"unique","ownPool":true}]},
                {"spawnId":111004,"npcId":2400141,"en":"Synthetic boss four","x":700,"y":800,
                 "loot":[{"id":"610660025","en":"Synthetic own material","rarity":"unique","ownPool":true}]}]}
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
            Settings.Current.BossRushEnabled = true;
            Settings.Current.BossRushShowOlderSpawns = false;
            Settings.Current.BossRushOnlyAlertBosses = Settings.Current.BossRushOnlyUniqueGearBosses = false;
            Settings.Current.BossNavigationPreviewCount = 1;
            Settings.Current.BossOverlayCount = 12;
            Settings.Current.BossAlertsEnabled = false;
            Settings.Current.BossAlertIds = [];
            Settings.Current.BossReminderOverrides = [];
            Settings.Current.BossAlertLeadSeconds = 60;
            Settings.Current.BossAlertLeadSecondsList = null;
            Settings.Current.BossAlertDurationSeconds = 15;
            Settings.Current.BossAlertUntilSpawn = false;
            Settings.Current.TimerOverlayTimerIds = [];
            Network = _provider.GetRequiredService<NetworkLootService>();
            Targets = _provider.GetRequiredService<MapTargetsService>();
            Bosses = new(_provider.GetRequiredService<ProgressService>(), Settings, Targets, Network,
                new FixedTime(Now), Path.Combine(_folder, "boss-timers.json"));
        }

        public void Observe(params BossSpawn[] bosses)
        {
            Bosses.OnMessage("fixture", [0, 0x36, .. BitConverter.GetBytes(Now)]);
            List<byte> message = [1, 0x91, 0, 0, .. BitConverter.GetBytes(1110)];
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
            Bosses.OnMessage("fixture", message.ToArray());

            void Varint(int value)
            {
                var rest = (uint)value;
                while (rest >= 128) { message.Add((byte)(rest | 128)); rest >>= 7; }
                message.Add((byte)rest);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Bosses.Dispose();
            await _provider.DisposeAsync();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FixedTime(long unixNow) : TimeProvider
    {
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixNow);
    }
}
