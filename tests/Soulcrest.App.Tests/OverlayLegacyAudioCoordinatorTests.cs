using System.Collections.Concurrent;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Every audio target is faked. No native window, tracker or sound device is created.</summary>
public sealed class OverlayLegacyAudioCoordinatorTests
{
    [Fact]
    public void InitialTotalsAndRepeatedModuleOrSettingsRefreshesNeverReplayARankCue()
    {
        using var fixture = new Fixture();
        fixture.Coordinator.SetContextAllowed(true);
        Assert.Empty(fixture.Ranks);
        fixture.Source.Publish(Snapshot(4, 2));
        for (var index = 0; index < 12; index++)
        {
            fixture.Source.Raise();
            fixture.Coordinator.ApplySettings();
            fixture.Coordinator.SetContextAllowed(true);
        }
        var cue = Assert.Single(fixture.Ranks);
        Assert.Equal(Points(3), cue.From);
        Assert.Equal(Points(4), cue.To);
        Assert.False(cue.Reduced);
    }

    [Fact]
    public void HiddenObservationsBecomeSilentBaselinesAndOnlyTheNextFreshRisePlays()
    {
        using var fixture = new Fixture();
        fixture.Source.Publish(Snapshot(5, 2));
        fixture.Coordinator.SetContextAllowed(true);
        Assert.Empty(fixture.Ranks);
        fixture.Source.Publish(Snapshot(6, 3));
        Assert.Single(fixture.Ranks);
        fixture.Coordinator.SetContextAllowed(false);
        Assert.Equal(1, fixture.Stops);
        fixture.Source.Publish(Snapshot(9, 4));
        fixture.Coordinator.SetContextAllowed(true);
        Assert.Single(fixture.Ranks);
        fixture.Source.Publish(Snapshot(10, 5));
        Assert.Equal(2, fixture.Ranks.Count);
        Assert.Equal(Points(9), fixture.Ranks.Last().From);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("profile")]
    [InlineData("binding")]
    [InlineData("continuity")]
    [InlineData("tracking")]
    [InlineData("confirmation")]
    [InlineData("time")]
    public void QualificationAndContinuityBreaksCancelWithoutReplayingOnReturn(string reason)
    {
        using var fixture = new Fixture();
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Source.Publish(Snapshot(4, 2));
        var changed = Snapshot(6, 3);
        fixture.Source.Publish(reason switch
        {
            "world" => changed with { WorldContextVisible = false },
            "profile" => changed with { ProfileId = "synthetic-other-profile" },
            "binding" => changed with { BindingGeneration = 2 },
            "continuity" => changed with { RankContinuityRevision = 1 },
            "tracking" => changed with { RankTrackingActive = false },
            "confirmation" => changed with { Confirmed = false },
            _ => changed with { RankPointsObservedAt = At },
        });
        Assert.Equal(1, fixture.Stops);
        fixture.Source.Publish(Snapshot(7, 4));
        Assert.Single(fixture.Ranks);
    }

    [Fact]
    public void RetargetAndManagedTimelineStopOnlyTheOwnedCue()
    {
        using var fixture = new Fixture();
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Source.Publish(Snapshot(4, 2));
        fixture.Source.Publish(Snapshot(3, 3));
        Assert.Single(fixture.Ranks);
        Assert.Equal(1, fixture.Stops);
        fixture.Clock.Advance(20_000);
        fixture.Source.Publish(Snapshot(5, 4));
        Assert.Equal(2, fixture.Ranks.Count);
        fixture.Clock.Advance(20_000);
        Assert.Equal(2, fixture.Stops);
        fixture.Coordinator.ApplySettings();
        Assert.Equal(2, fixture.Stops);
    }

    [Fact]
    public void ReducedMotionUsesTheExistingShortCueAndChangingItDoesNotReplay()
    {
        using var fixture = new Fixture();
        fixture.Reduced = true;
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Source.Publish(Snapshot(4, 2));
        Assert.True(Assert.Single(fixture.Ranks).Reduced);
        fixture.Reduced = false;
        fixture.Coordinator.ApplySettings();
        Assert.Equal(1, fixture.Stops);
        Assert.Single(fixture.Ranks);
        fixture.Source.Raise();
        Assert.Single(fixture.Ranks);
    }

    [Fact]
    public async Task DropContextRevocationCancelsTheOwnChannelAndShowingCannotReplayPastReceipts()
    {
        using var fixture = new Fixture();
        fixture.Drops.Accept(Drop());
        Assert.NotNull(fixture.Drops.LatestVisualNotice);
        Assert.Empty(fixture.DropAudio);
        fixture.Coordinator.SetContextAllowed(true);
        Assert.Empty(fixture.DropAudio);
        fixture.Drops.Accept(Drop());
        await WaitFor(() => fixture.DropAudio.Count == 1 && fixture.DropAudio.Single().Started);
        fixture.Coordinator.SetContextAllowed(false);
        Assert.True(fixture.DropAudio.Single().Disposed);
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Coordinator.ApplySettings();
        Assert.Single(fixture.DropAudio);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("sound")]
    [InlineData("volume")]
    [InlineData("rarity")]
    [InlineData("rarity-sound")]
    public void ExistingDropMasterMuteVolumeAndExactRarityOptionsStillBlockAudio(string option)
    {
        using var fixture = new Fixture();
        fixture.Options = option switch
        {
            "master" => fixture.Options with { Enabled = false },
            "sound" => fixture.Options with { SoundEnabled = false },
            "volume" => fixture.Options with { Volume = 0 },
            _ => fixture.Options with { RarityAlerts = new Dictionary<LootItemRarity, DropRarityAlertSettings>
            {
                [LootItemRarity.Unique] = new(Enabled: option != "rarity", SoundEnabled: option != "rarity-sound"),
            } },
        };
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Drops.Accept(Drop());
        fixture.Coordinator.ApplySettings();
        Assert.Empty(fixture.DropAudio);
    }

    [Fact]
    public async Task ContextAndDisposalDoNotCancelAnExplicitDropPreviewWhenNoOwnCueWasStarted()
    {
        using var fixture = new Fixture();
        var preview = fixture.Drops.PreviewAsync(LootItemRarity.Unique);
        await WaitFor(() => fixture.DropAudio.Count == 1 && fixture.DropAudio.Single().Started);
        fixture.Coordinator.SetContextAllowed(false);
        fixture.Coordinator.Dispose();
        Assert.False(fixture.DropAudio.Single().Disposed);
        Assert.Equal(0, fixture.Stops);
        fixture.DropAudio.Single().Finish.TrySetResult();
        Assert.True(await preview);
    }

    [Fact]
    public async Task ExistingRankPlayerMuteAndVolumeAreAppliedWithoutCreatingOrReplayingAudio()
    {
        var clock = new Clock();
        var source = new Source();
        var options = new AbyssRankSoundOptions(false, .35);
        using var drops = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No real audio"), clock);
        var audio = new Audio();
        using var player = new AbyssRankSoundPlayer(() => options, () => audio, (_, _, _) => [1]);
        using var coordinator = new OverlayLegacyAudioCoordinator(drops, source, () => false,
            player.Start, player.Stop, player.ApplySettings, clock);
        coordinator.SetContextAllowed(true);
        source.Publish(Snapshot(4, 2));
        Assert.False(audio.Started);
        clock.Advance(20_000);
        options = options with { Enabled = true };
        coordinator.ApplySettings();
        Assert.False(audio.Started);
        source.Publish(Snapshot(5, 3));
        await WaitFor(() => audio.Started);
        Assert.Equal(.35, audio.Volume);
        options = options with { Enabled = false };
        coordinator.ApplySettings();
        Assert.True(audio.Disposed);
    }

    [Fact]
    public async Task ConcurrentSourceAndContextCallbacksAndLateCallsAfterDisposeAreSafe()
    {
        using var fixture = new Fixture();
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Source.Publish(Snapshot(4, 2));
        await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 50; i++) fixture.Source.Raise(); }),
            Task.Run(() => { for (var i = 0; i < 50; i++) fixture.Coordinator.SetContextAllowed(i % 2 == 0); }),
            Task.Run(fixture.Coordinator.Dispose));
        fixture.Coordinator.Dispose();
        var reads = fixture.Source.Reads;
        fixture.Coordinator.SetContextAllowed(true);
        fixture.Coordinator.ApplySettings();
        fixture.Source.Publish(Snapshot(12, 10));
        fixture.Clock.Advance(20_000);
        Assert.Equal(reads, fixture.Source.Reads);
        Assert.Single(fixture.Ranks);
        Assert.Equal(1, fixture.Stops);
    }

    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static long Points(int stage) => AbyssRankCatalog.Default.Stages[stage].Threshold;
    private static AbyssRankOverlaySnapshot Snapshot(int stage, long sequence) => new(Points(stage), true, true)
    {
        WorldContextVisible = true, ProfileId = "synthetic-rank-profile", BindingGeneration = 1,
        RankObservationSequence = sequence, RankPointsObservedAt = At.AddMilliseconds(sequence), RankTrackingActive = true,
    };
    private static AcceptedFarmLootDrop Drop() => new("synthetic-session", "synthetic-profile", At,
        "Item:110120003", "Example drop", 1);
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly Source Source = new();
        internal readonly ConcurrentQueue<(long From, long To, bool Reduced)> Ranks = new();
        internal readonly ConcurrentQueue<Audio> DropAudio = new();
        internal RareDropAlertOptions Options = new();
        internal bool Reduced;
        internal int Stops;
        internal RareDropAlertService Drops { get; }
        internal OverlayLegacyAudioCoordinator Coordinator { get; }
        internal Fixture()
        {
            Drops = new(() => Options, () => { var audio = new Audio(); DropAudio.Enqueue(audio); return audio; }, Clock);
            Coordinator = new(Drops, Source, () => Reduced,
                (from, to, reduced) => Ranks.Enqueue((from, to, reduced)), () => Interlocked.Increment(ref Stops), () => { }, Clock);
        }
        public void Dispose() { Coordinator.Dispose(); Drops.Dispose(); }
    }

    private sealed class Source : IAbyssRankOverlaySource
    {
        private AbyssRankOverlaySnapshot _snapshot = OverlayLegacyAudioCoordinatorTests.Snapshot(3, 1);
        internal int Reads;
        public AbyssRankOverlaySnapshot Snapshot { get { Interlocked.Increment(ref Reads); return Volatile.Read(ref _snapshot); } }
        public event Action? Changed;
        internal void Publish(AbyssRankOverlaySnapshot snapshot) { Volatile.Write(ref _snapshot, snapshot); Raise(); }
        internal void Raise() => Changed?.Invoke();
    }

    private sealed class Audio : IRareDropAudioSession
    {
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Started, Disposed;
        internal double Volume;
        public async Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation)
        { Volume = volume; Started = true; await Finish.Task.WaitAsync(cancellation); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => At.AddMilliseconds(_milliseconds);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        internal void Advance(long milliseconds)
        { _milliseconds += milliseconds; foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _active, _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (_disposed) return false; _active = dueTime != Timeout.InfiniteTimeSpan; return true; }
            internal void Fire() { if (_active && !_disposed) callback(state); }
            public void Dispose() { _disposed = true; _active = false; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
