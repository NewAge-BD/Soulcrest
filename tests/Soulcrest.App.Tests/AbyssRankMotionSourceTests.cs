using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssRankMotionSourceTests
{
    [Fact]
    public async Task AcceptedRankMetadataReachesTheOverlayAndInitialConfirmationIsSilent()
    {
        await using var f = new Fixture(); await f.Ready();
        var snapshot = f.Snapshot();
        Assert.Equal(Fixture.Profile, snapshot.ProfileId);
        Assert.Equal(f.Source.OwnCharacter!.Generation, snapshot.BindingGeneration);
        Assert.Equal(f.Source.AbyssSnapshot.Sequence, snapshot.RankObservationSequence);
        Assert.Equal(f.Clock.GetUtcNow(), snapshot.RankPointsObservedAt);
        Assert.True(snapshot.RankTrackingActive);
        Assert.True(snapshot.WorldContextVisible);
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(snapshot), 0);
        Assert.Null(controller.Sample(0));
    }

    [Fact]
    public async Task WalletEnvelopesWorldTicksAndRepeatedChangedEventsDoNotAdvanceTheRankFactSequence()
    {
        await using var f = new Fixture(); await f.Ready();
        var before = f.Snapshot();
        f.Clock.Advance(); f.Source.Wallet(101); await f.Ready();
        Assert.True(f.Source.AbyssSnapshot.Sequence > before.RankObservationSequence);
        f.Source.Notify(); f.Source.Notify(); await f.Ready();
        var after = f.Snapshot();
        Assert.Equal(before.RankObservationSequence, after.RankObservationSequence);
        Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.Equal(before.RankContinuityRevision, after.RankContinuityRevision);
    }

    [Fact]
    public async Task OnlyAFreshAcceptedStageCrossingStartsMotion()
    {
        await using var f = new Fixture(); await f.Ready();
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(f.Snapshot()), 0);
        f.Clock.Advance(); f.Source.Rank(11_999); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 100);
        Assert.Null(controller.Sample(100));
        var sameStageSequence = f.Snapshot().RankObservationSequence;
        f.Clock.Advance(); f.Source.Rank(12_040); await f.Ready();
        var crossed = f.Snapshot();
        Assert.True(crossed.RankObservationSequence > sameStageSequence);
        controller.Observe(Input(crossed), 200);
        Assert.True(controller.IsAnimating);
        Assert.Null(controller.Sample(10_000));
        Assert.Equal(2, controller.CurrentStageIndex);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("rank")]
    [InlineData("tracking")]
    [InlineData("world")]
    [InlineData("world-time")]
    [InlineData("outside")]
    public async Task CoalescedInvalidationAndRecoveryCannotReplayAMissedStageCrossing(string boundary)
    {
        await using var f = new Fixture(); await f.Ready();
        var baseline = f.Snapshot();
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(baseline), 0);
        // The consumer deliberately reads no intermediate snapshot, just like one queued UI refresh.
        switch (boundary)
        {
            case "transport":
                f.Source.SetRunning(false); f.Source.Rank(75_040); f.Source.SetRunning(true); break;
            case "rank":
                f.Source.InvalidateRank(); f.Source.Rank(75_040); break;
            case "tracking":
                f.Settings.SetAbyssTrackingEnabled(false); f.Source.Rank(75_040);
                f.Settings.SetAbyssTrackingEnabled(true); break;
            case "world":
                f.WorldStatus = WorldContextStatus.Transitioning; f.Source.Notify();
                f.Source.Rank(75_040); f.WorldStatus = WorldContextStatus.Confirmed; f.Source.Notify(); break;
            case "world-time":
                f.UnknownWorldTime = true; f.Source.Notify();
                f.Source.Rank(75_040); f.UnknownWorldTime = false; f.Source.Notify(); break;
            case "outside":
                f.Category = "Party"; f.Source.Notify();
                f.Source.Rank(75_040); f.Category = "Abyss"; f.Source.Notify(); break;
        }
        await f.Ready();
        var recovered = f.Snapshot();
        Assert.True(recovered.Confirmed);
        Assert.True(recovered.RankTrackingActive);
        Assert.Equal(baseline.ProfileId, recovered.ProfileId);
        Assert.Equal(baseline.BindingGeneration, recovered.BindingGeneration);
        Assert.True(recovered.RankContinuityRevision > baseline.RankContinuityRevision);
        controller.Observe(Input(recovered), 100);
        Assert.Null(controller.Sample(100));
        f.Clock.Advance(); f.Source.Rank(180_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 200);
        Assert.True(controller.IsAnimating);
    }

    [Fact]
    public async Task PausedTrackingIsExplicitlyInactiveEvenWhenTheAbsoluteRankRemainsConfirmed()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.SetAbyssTrackingEnabled(false); await f.Ready();
        var paused = f.Snapshot();
        Assert.True(paused.Confirmed);
        Assert.False(paused.RankTrackingActive);
        Assert.False(paused.WorldContextVisible);
    }

    [Fact]
    public async Task ANewBindingOfTheSameProfileAndSavedValuesBothEstablishSilentBaselines()
    {
        await using var f = new Fixture(); await f.Ready();
        var controller = new AbyssRankTransitionController();
        var first = f.Snapshot();
        controller.Observe(Input(first), 0);
        f.Source.ChangeBinding(); f.Source.Rank(75_040); await f.Ready();
        var rebound = f.Snapshot();
        Assert.Equal(first.ProfileId, rebound.ProfileId);
        Assert.NotEqual(first.BindingGeneration, rebound.BindingGeneration);
        controller.Observe(Input(rebound), 100);
        Assert.Null(controller.Sample(100));
        await f.Service.DisposeAsync(); f.Source.InvalidateRank();
        f.Restart(); await f.Ready();
        var saved = f.Snapshot();
        Assert.Equal(75_040, saved.RankPoints);
        Assert.False(saved.Confirmed);
        Assert.Equal(0, saved.RankObservationSequence);
        controller.Observe(Input(saved), 200);
        f.Clock.Advance(); f.Source.Rank(180_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 300);
        Assert.Null(controller.Sample(300));
    }

    [Fact]
    public async Task OpenWorldFirstGainAndANewSessionEstablishSilentBaselinesAndSessionEndHidesTheTracker()
    {
        await using var f = new Fixture(openWorld: true); await f.Ready();
        var controller = new AbyssRankTransitionController();
        var baseline = f.Snapshot();
        Assert.True(baseline.Confirmed); Assert.False(baseline.WorldContextVisible);
        controller.Observe(Input(baseline), 0);
        f.OpenWorldPointGain = true;
        var visible = f.Snapshot(); Assert.True(visible.WorldContextVisible);
        controller.Observe(Input(visible), 100); Assert.Null(controller.Sample(100));
        f.Clock.Advance(); f.Source.Rank(12_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 200); Assert.True(controller.IsAnimating);
        f.SessionEnded = true;
        var ended = f.Snapshot(); Assert.False(ended.WorldContextVisible);
        controller.Observe(Input(ended), 300); Assert.Null(controller.Sample(300));
        f.Clock.Advance(); f.Source.Rank(75_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 400); Assert.Null(controller.Sample(400));
        f.SessionEnded = false;
        controller.Observe(Input(f.Snapshot()), 500); Assert.Null(controller.Sample(500));
        f.Clock.Advance(); f.Source.Rank(180_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 600); Assert.True(controller.IsAnimating);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("rank")]
    [InlineData("world")]
    [InlineData("monster-setting")]
    public async Task OpenWorldCoalescedAuthorityLossCannotAnimateAnUnobservedCrossing(string boundary)
    {
        await using var f = new Fixture(openWorld: true); f.OpenWorldPointGain = true; await f.Ready();
        var baseline = f.Snapshot(); Assert.True(baseline.WorldContextVisible);
        var controller = new AbyssRankTransitionController(); controller.Observe(Input(baseline), 0);
        switch (boundary)
        {
            case "transport": f.Source.SetRunning(false); f.Source.Rank(75_040); f.Source.SetRunning(true); break;
            case "rank": f.Source.InvalidateRank(); f.Source.Rank(75_040); break;
            case "world": f.WorldStatus = WorldContextStatus.Transitioning; f.Source.Notify();
                f.Source.Rank(75_040); f.WorldStatus = WorldContextStatus.Confirmed; f.Source.Notify(); break;
            case "monster-setting": f.Settings.SetMonsterLootTrackingEnabled(false); f.Source.Rank(75_040);
                f.Settings.SetMonsterLootTrackingEnabled(true); break;
        }
        await f.Ready();
        var recovered = f.Snapshot(); Assert.True(recovered.WorldContextVisible);
        Assert.True(recovered.RankContinuityRevision > baseline.RankContinuityRevision);
        controller.Observe(Input(recovered), 100); Assert.Null(controller.Sample(100));
        f.Clock.Advance(); f.Source.Rank(180_040); await f.Ready();
        controller.Observe(Input(f.Snapshot()), 200); Assert.True(controller.IsAnimating);
    }

    private static AbyssRankMotionInput Input(AbyssRankOverlaySnapshot snapshot) => new(snapshot.ProfileId,
        snapshot.BindingGeneration, snapshot.RankObservationSequence, snapshot.RankPointsObservedAt, snapshot.RankPoints,
        snapshot.Confirmed, snapshot.CharacterBound, snapshot.RankTrackingActive, snapshot.WorldContextVisible,
        snapshot.RankContinuityRevision);

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-rank-motion-source-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        internal SettingsService Settings { get; } = new();
        internal Clock Clock { get; } = new();
        internal Source Source { get; }
        internal AbyssProgressService Service { get; private set; }
        internal WorldContextStatus WorldStatus { get; set; } = WorldContextStatus.Confirmed;
        internal bool UnknownWorldTime { get; set; }
        internal string Category { get; set; } = "Abyss";
        private readonly bool _openWorld;
        private readonly DateTimeOffset _sessionStartedAt;
        internal bool OpenWorldPointGain { get; set; }
        internal bool SessionEnded { get; set; }

        internal Fixture(bool openWorld = false)
        {
            _openWorld = openWorld; _sessionStartedAt = Clock.GetUtcNow();
            if (openWorld) Category = "field";
            Settings.Current.AbyssTrackingEnabled = Settings.Current.LootTrackingEnabled = Settings.Current.MonsterLootTrackingEnabled = true;
            Source = new(Clock);
            Source.Rank(11_990);
            Service = CreateService();
        }
        private AbyssProgressService CreateService() => new(Settings, Source,
            _ => new(Profile, "Synthetic rank motion"), World, new(Path.Combine(_root, "progress.json")), Clock);
        internal void Restart() => Service = CreateService();
        internal Task Ready() => Service.DrainAsync();
        private WorldContextSnapshot World() => new(WorldStatus, _openWorld ? WorldContextKind.OpenWorld : WorldContextKind.Instance, 20, null, Category, null,
            "Abyss", "Abyss", 1, UnknownWorldTime ? default : Clock.GetUtcNow(), "synthetic")
            { BindingGeneration = Source.OwnCharacter?.Generation };
        private LootTrackerSession? OpenWorldSession() => !_openWorld ? null : new()
        {
            Id = "synthetic-open-world-motion", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1,
            Character = new(Profile, "Synthetic rank motion"), Status = SessionEnded ? LootTrackerSessionStatus.Completed : LootTrackerSessionStatus.Live,
            StartedAt = _sessionStartedAt, LastLootAt = Clock.GetUtcNow(), EndedAt = SessionEnded ? Clock.GetUtcNow() : null,
            Elapsed = Clock.GetUtcNow() - _sessionStartedAt, AbyssPoints = OpenWorldPointGain ? 1 : null,
            TimelineEvents = OpenWorldPointGain ? [LootTrackerTimelineFacts.Create(LootTrackerTimelineEventKind.AbyssPoints,
                _sessionStartedAt, TimeSpan.Zero) with { Origin = LootTrackerActivityOrigin.Pve, Amount = 1 }] : []
        };
        internal AbyssRankOverlaySnapshot Snapshot() => AbyssRankOverlaySource.ProjectRank(Service.ProgressSnapshot, [], false,
            session: OpenWorldSession(), world: World(), trackingAvailable: Service.SourceAvailable);
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (_settings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Source(Clock clock) : INetworkAbyssSource
    {
        private long _sequence;
        public bool Running { get; private set; } = true;
        public OwnCharacterContext? OwnCharacter { get; private set; } =
            new(new(1, 1, 1, "Synthetic rank motion"), 1) { ObservedAt = clock.GetUtcNow() };
        public NetworkAbyssSnapshot AbyssSnapshot { get; private set; } = NetworkAbyssSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkAbyssPointGain>? AbyssPointsGained { add { } remove { } }
        public event Action<NetworkAbyssRankChange>? RankPointsChanged { add { } remove { } }
        internal void Notify() => Changed?.Invoke();
        internal void SetRunning(bool running) { Running = running; Notify(); }
        internal void Rank(long points)
        {
            AbyssSnapshot = new(OwnCharacter, 100, 1_000, points, clock.GetUtcNow(), ++_sequence)
            { RankPointsObservedAt = clock.GetUtcNow(), RankPointChangesQualified = true };
            Notify();
        }
        internal void Wallet(long balance)
        {
            AbyssSnapshot = AbyssSnapshot with { AbyssPointBalance = balance, At = clock.GetUtcNow(), Sequence = ++_sequence,
                AbyssPointBalanceObservedAt = clock.GetUtcNow() };
            Notify();
        }
        internal void InvalidateRank()
        {
            AbyssSnapshot = AbyssSnapshot with { RankPoints = null, RankPointsObservedAt = null, Sequence = ++_sequence };
            Notify();
        }
        internal void ChangeBinding()
        {
            OwnCharacter = new(OwnCharacter!.Identity, OwnCharacter.Generation + 1) { ObservedAt = clock.GetUtcNow() };
            AbyssSnapshot = NetworkAbyssSnapshot.Empty;
            OwnCharacterChanged?.Invoke(); Notify();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow.AddHours(-1);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _epoch.AddTicks(_ticks);
        internal void Advance() => _ticks += TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();
        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
