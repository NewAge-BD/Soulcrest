using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;
using Soulcrest.App.Network;

namespace Soulcrest.App.Tests;

public sealed class NetworkTrackerSettingsTests
{
    [Fact]
    public void ExistingOcrSettingsMigrateToEnabledNetworkTracking()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"LootSource\":\"ocr\"}")!;
        Assert.True(old.LootTrackingEnabled);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{\"LootTrackingEnabled\":false}")!.LootTrackingEnabled);
    }

    [Fact]
    public void StopPersistsAndShutdownPreservesThePreference()
    {
        var settings = new SettingsService();
        var before = settings.Current.LootTrackingEnabled;
        try
        {
            var source = new FakeSource();
            using var tracker = new TrackerService(settings, source);
            settings.Update(s => s.LootTrackingEnabled = true);
            tracker.StartIfEnabled();
            Assert.Equal(1, source.Starts);
            tracker.StartIfEnabled();
            Assert.Equal(1, source.Starts);
            source.Emit();
            Assert.Equal(2, tracker.SessionTotals["swarm"]);
            Assert.True(new SettingsService().Current.LootTrackingEnabled);
            tracker.Dispose();
            Assert.True(new SettingsService().Current.LootTrackingEnabled);
            tracker.Stop();
            tracker.StartIfEnabled();
            Assert.False(new SettingsService().Current.LootTrackingEnabled);
            Assert.False(tracker.Running);
            Assert.Equal(1, source.Starts);
            tracker.Start();
            Assert.Equal(2, source.Starts);
            Assert.Empty(tracker.SessionTotals);
        }
        finally { settings.Update(s => s.LootTrackingEnabled = before); }
    }

    private sealed class FakeSource : INetworkLootSource
    {
        public int Starts { get; private set; }
        public bool Running { get; private set; }
        public string Status => "test";
        public bool StatusIsError => false;
        public string? Connection => null;
        public long Packets => 0;
        public long Messages => 0;
        public IReadOnlyList<int> UnknownPets => [];
        public event Action? Changed;
        public event Action<NetworkSoul>? SoulGained;
        public void Start() { Starts++; Running = true; Changed?.Invoke(); }
        public void Stop() { Running = false; Changed?.Invoke(); }
        public void Emit() => SoulGained?.Invoke(new NetworkSoul(DateTimeOffset.Now, 1, "swarm", "Swarm", 2));
    }
}
