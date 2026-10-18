using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkCaptureCoordinatorTests
{
    [Fact]
    public void LootCanStopWhileCombatKeepsTheOnlySourceAndProgressIsDisabled()
    {
        var source = new FakeSource();
        using var capture = new NetworkCaptureCoordinator(source);
        capture.SetDemand(NetworkCaptureConsumer.Loot, true);
        capture.SetDemand(NetworkCaptureConsumer.Combat, true);
        capture.SetDemand(NetworkCaptureConsumer.Loot, false);
        Assert.True(source.Running);
        Assert.False(source.ProgressEnabled);
        Assert.Equal(1, source.Starts);
        Assert.Equal(0, source.Stops);
        capture.SetDemand(NetworkCaptureConsumer.Combat, false);
        Assert.False(source.Running);
        Assert.Equal(1, source.Stops);
    }

    [Fact]
    public void StoppingCombatPreservesLootAndFailedStartsCanRetry()
    {
        var source = new FakeSource { FailNextStart = true };
        using var capture = new NetworkCaptureCoordinator(source);
        capture.SetDemand(NetworkCaptureConsumer.Combat, true);
        Assert.False(source.Running);
        capture.SetDemand(NetworkCaptureConsumer.Combat, true);
        Assert.True(source.Running);
        Assert.Equal(2, source.Starts);
        capture.SetDemand(NetworkCaptureConsumer.Loot, true);
        capture.SetDemand(NetworkCaptureConsumer.Combat, false);
        Assert.True(source.Running);
        Assert.True(source.ProgressEnabled);
        Assert.Equal(0, source.Stops);
    }

    [Fact]
    public void TrackerPreferenceAndSessionDoNotFollowAnotherConsumersRunningState()
    {
        var settings = new SettingsService();
        var before = settings.Current.LootTrackingEnabled;
        try
        {
            var source = new FakeSource();
            using var capture = new NetworkCaptureCoordinator(source);
            using var tracker = new TrackerService(settings, source, capture);
            capture.SetDemand(NetworkCaptureConsumer.Combat, true);
            tracker.Start();
            source.Emit();
            Assert.Equal(2, tracker.SessionTotals["pet-test"]);
            tracker.Stop();
            source.Emit();
            Assert.False(tracker.Running);
            Assert.True(source.Running);
            Assert.Equal(2, tracker.SessionTotals["pet-test"]);
            Assert.False(new SettingsService().Current.LootTrackingEnabled);
            tracker.Start();
            Assert.True(tracker.Running);
            Assert.Empty(tracker.SessionTotals);
            capture.SetDemand(NetworkCaptureConsumer.Combat, false);
            Assert.True(source.Running);
            tracker.Dispose();
            Assert.False(source.Running);
            Assert.True(new SettingsService().Current.LootTrackingEnabled);
        }
        finally { settings.Update(s => s.LootTrackingEnabled = before); }
    }

    [Fact]
    public void ShutdownAllowsRepeatedConsumerReleaseButCannotRestartCapture()
    {
        var source = new FakeSource();
        var capture = new NetworkCaptureCoordinator(source);
        capture.SetDemand(NetworkCaptureConsumer.Combat, true);
        capture.Dispose();
        capture.Dispose();
        capture.SetDemand(NetworkCaptureConsumer.Combat, false);
        Assert.Equal(1, source.Stops);
        Assert.Throws<ObjectDisposedException>(() => capture.SetDemand(NetworkCaptureConsumer.Combat, true));
    }

    [Fact]
    public void ReentrantDemandDoesNotApplyAnObsoleteStopDecision()
    {
        var source = new FakeSource();
        using var capture = new NetworkCaptureCoordinator(source);
        capture.SetDemand(NetworkCaptureConsumer.Loot, true);
        source.ProgressChanged = enabled => { if (!enabled) capture.SetDemand(NetworkCaptureConsumer.Combat, true); };
        capture.SetDemand(NetworkCaptureConsumer.Loot, false);
        Assert.True(source.Running);
        Assert.True(capture.IsRequested(NetworkCaptureConsumer.Combat));
        Assert.Equal(1, source.Starts);
        Assert.Equal(0, source.Stops);
        source.ProgressChanged = null;
    }

    [Fact]
    public async Task AnotherCaptureCallbackCanChangeDemandWhileTheSourceIsBusy()
    {
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var source = new FakeSource { ProgressChanged = _ => { entered.Set(); released.Wait(TimeSpan.FromSeconds(5)); } };
        using var capture = new NetworkCaptureCoordinator(source);
        var first = Task.Run(() => capture.SetDemand(NetworkCaptureConsumer.Loot, true));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Task.Run(() => capture.SetDemand(NetworkCaptureConsumer.Combat, true)).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { released.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(source.Running);
        Assert.Equal(1, source.Starts);
    }

    private sealed class FakeSource : INetworkLootSource, INetworkProgressControl
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool FailNextStart { get; set; }
        public Action<bool>? ProgressChanged { get; set; }
        public bool ProgressEnabled { get; private set; } = true;
        public bool Running { get; private set; }
        public string Status => "test";
        public bool StatusIsError => false;
        public string? Connection => null;
        public long Packets => 0;
        public long Messages => 0;
        public IReadOnlyList<int> UnknownPets => [];
        public event Action? Changed;
        public event Action<NetworkSoul>? SoulGained;
        public void Start()
        {
            Starts++;
            Running = !FailNextStart;
            FailNextStart = false;
            Changed?.Invoke();
        }
        public void Stop() { Stops++; Running = false; Changed?.Invoke(); }
        public void SetProgressProcessingEnabled(bool enabled) { ProgressEnabled = enabled; ProgressChanged?.Invoke(enabled); }
        public void Emit() => SoulGained?.Invoke(new( DateTimeOffset.UtcNow, 1, "pet-test", "Pet", 2));
    }
}
