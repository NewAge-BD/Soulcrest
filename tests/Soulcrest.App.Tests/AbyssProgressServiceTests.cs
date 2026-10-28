using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssProgressServiceTests
{
    [Fact]
    public async Task AnUnwritableProgressCacheKeepsTheConfirmedValuesAndTheTrackerAvailable()
    {
        await using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.ProgressPath)!);
        File.WriteAllText(f.ProgressPath, "{ damaged");
        await f.Ready();
        f.Source.Absolute(200, 2_000, 1_500);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.Ready();
        Assert.True(f.Service.SourceAvailable);
        Assert.Equal(1_500, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(2_000, f.Service.ProgressSnapshot.FarmedAbyssPoints);
        Assert.True(f.Service.StatusIsError);
        Assert.True(f.Service.ProgressSnapshot.RankPointsConfirmed);
    }

    [Fact]
    public async Task AbsoluteCacheHasNoSessionWriterOrIncomeSubscriptions()
    {
        await using var f = new Fixture();
        await f.Ready();
        f.Source.Absolute(200, 2_000, 1_500);
        f.Source.Points(13, 1);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.Ready();
        Assert.False(Directory.Exists(f.SessionRoot));
        Assert.Equal(0, f.Source.GainSubscriptions);
        Assert.Equal(1_500, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(2_000, f.Service.ProgressSnapshot.FarmedAbyssPoints);
        Assert.Equal(1_500, f.Progress.Read(Fixture.ProfileA)?.RankPoints?.Value);
    }

    [Fact]
    public async Task WeeklyResetOnlyIncreasesTheCapAndRetainsTheAuthoritativeFarmTotal()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(500, 1_900_000, 900); await f.Ready();
        f.Clock.Advance(TimeSpan.FromDays(2)); await f.Ready();
        Assert.Equal(2_500_000, f.Service.ProgressSnapshot.Cap);
        Assert.Equal(1_900_000, f.Service.ProgressSnapshot.FarmedAbyssPoints);
        Assert.Equal(600_000, f.Service.ProgressSnapshot.Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheAuthoritativeRankTotalUpdatesTheCacheDespiteImmediateSignedChanges(bool totalArrivesFirst)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(100, 1000, 953_177); await f.Ready();
        var originalRankAt = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        if (totalArrivesFirst)
        {
            f.Source.Absolute(100, 1000, 952_540); await f.Ready();
        }
        f.Source.Rank(13, 1); f.Source.Rank(-650, 2); f.Source.Rank(-650, 2); await f.Ready();
        if (!totalArrivesFirst)
        {
            Assert.Equal(953_177, f.Service.ProgressSnapshot.RankPoints);
            Assert.Equal(originalRankAt, f.Service.ProgressSnapshot.RankPointsObservedAt);
            f.Clock.Advance(TimeSpan.FromMinutes(5));
            f.Source.Absolute(100, 1000, 952_540); await f.Ready();
        }
        Assert.Equal(952_540, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(952_540, AbyssRankPresentation.Project(f.Service.ProgressSnapshot.RankPoints).TotalPoints);
        Assert.True(f.Service.ProgressSnapshot.RankPointsConfirmed);
        Assert.Equal(952_540, f.Progress.Read(Fixture.ProfileA)!.RankPoints!.Value);
    }

    [Fact]
    public async Task ApAndRankObservationsPreserveEachOthersFactTimesAndRejectOlderFacts()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(100, 1000, 953_177); await f.Ready();
        var originalAt = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var apAt = f.Clock.GetUtcNow();
        f.Source.Absolute(110, 1010, 953_177, rankAt: originalAt); await f.Ready();
        Assert.Equal(originalAt, f.Service.ProgressSnapshot.RankPointsObservedAt);
        Assert.Equal(originalAt, f.Progress.Read(Fixture.ProfileA)!.RankPoints!.ObservedAt);
        Assert.Equal(apAt, f.Service.ProgressSnapshot.ObservedAt);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var rankAt = f.Clock.GetUtcNow();
        f.Source.Absolute(110, 1010, 952_540, balanceAt: apAt, farmedAt: apAt); await f.Ready();
        var current = f.Progress.Read(Fixture.ProfileA)!;
        Assert.Equal(apAt, current.AbyssPointBalance!.ObservedAt);
        Assert.Equal(apAt, current.FarmedAbyssPoints!.ObservedAt);
        Assert.Equal(rankAt, current.RankPoints!.ObservedAt);
        Assert.Equal(rankAt, f.Service.ProgressSnapshot.RankPointsObservedAt);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Source.Absolute(999, 9999, 999_999, originalAt, originalAt, originalAt); await f.Ready();
        var retained = f.Progress.Read(Fixture.ProfileA)!;
        Assert.Equal(current, retained);
        Assert.Equal(952_540, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(rankAt, f.Service.ProgressSnapshot.RankPointsObservedAt);
        Assert.False(f.Service.ProgressSnapshot.RankPointsConfirmed);
        Assert.False(f.Service.ProgressSnapshot.AbyssPointBalanceConfirmed);
        Assert.False(f.Service.ProgressSnapshot.FarmedAbyssPointsConfirmed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RankFactAfterItsEnvelopeOrAfterNowCannotReplaceOrConfirmTheCache(bool afterNow)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(100, 1000, 953_177); await f.Ready();
        var originalAt = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var now = f.Clock.GetUtcNow();
        f.Source.SetSnapshot(f.Source.AbyssSnapshot with
        {
            At = afterNow ? now : now.AddSeconds(-1),
            RankPoints = 1, RankPointsObservedAt = afterNow ? now.AddSeconds(1) : now,
            Sequence = f.Source.AbyssSnapshot.Sequence + 1
        });
        await f.Ready();
        Assert.Equal(953_177, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(originalAt, f.Service.ProgressSnapshot.RankPointsObservedAt);
        Assert.False(f.Service.ProgressSnapshot.RankPointsConfirmed);
        Assert.Equal(new SavedAbyssValue(953_177, originalAt), f.Progress.Read(Fixture.ProfileA)!.RankPoints);
    }

    [Fact]
    public async Task AbsoluteWalletRemainsReadableWithoutQualifiedGainSemantics()
    {
        await using var f = new Fixture(qualified: false); await f.Ready();
        Assert.Equal(100, f.Service.ProgressSnapshot.AbyssPointBalance);
        Assert.True(f.Service.ProgressSnapshot.AbyssPointBalanceConfirmed);
        f.Source.Points(100, 1); f.Source.Rank(-50, 1); await f.Ready();
        Assert.Equal(100, f.Service.ProgressSnapshot.AbyssPointBalance);
        Assert.Equal(1000, f.Service.ProgressSnapshot.RankPoints);
        Assert.Equal(0, f.Source.GainSubscriptions);
    }

    [Fact]
    public async Task ForeignAbsoluteSnapshotCannotConfirmTheCurrentCharacterCache()
    {
        await using var f = new Fixture(); await f.Ready();
        var foreign = new OwnCharacterContext(new(2, 1, 2, "Synthetic other character"), 99);
        f.Source.SetSnapshot(f.Source.AbyssSnapshot with { Character = foreign });
        f.Source.Points(100, 1); f.Source.Rank(-50, 1); await f.Ready();
        Assert.Equal(100, f.Service.ProgressSnapshot.AbyssPointBalance);
        Assert.Equal(1000, f.Service.ProgressSnapshot.RankPoints);
        Assert.False(f.Service.ProgressSnapshot.Confirmed);
    }

    [Fact]
    public async Task ReplayedOlderAbsoluteSequenceCannotReplaceOrConfirmTheNewerCache()
    {
        await using var f = new Fixture(); await f.Ready();
        var older = f.Source.AbyssSnapshot;
        f.Source.Absolute(500, 1_800_000, 100); await f.Ready();
        f.Source.SetSnapshot(older); await f.Ready();
        Assert.Equal(1_800_000, f.Service.ProgressSnapshot.FarmedAbyssPoints);
        Assert.False(f.Service.ProgressSnapshot.Confirmed);
        Assert.Equal(1_800_000, f.Progress.Read(Fixture.ProfileA)!.FarmedAbyssPoints!.Value);
    }

    [Fact]
    public async Task SpendingDoesNotRestoreTheAuthoritativeFarmBudget()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(500, 1_900_000, 500); await f.Ready();
        f.Source.Absolute(10, 1_900_000, 400); await f.Ready();
        Assert.Equal(100_000, f.Service.ProgressSnapshot.Remaining);
        Assert.Equal(1_900_000, f.Progress.Read(Fixture.ProfileA)!.FarmedAbyssPoints!.Value);
        Assert.Equal(10, f.Progress.Read(Fixture.ProfileA)!.AbyssPointBalance!.Value);
    }

    [Fact]
    public async Task CachedProgressSurvivesRestartAndUnknownCurrentFactsStayUnconfirmed()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(100, 1_500_000, 300); await f.Ready(); await f.Service.DisposeAsync();
        f.Source.ClearAbsolute(); await using var restarted = f.NewService(); await restarted.DrainAsync();
        Assert.Equal(1_500_000, restarted.ProgressSnapshot.FarmedAbyssPoints);
        Assert.False(restarted.ProgressSnapshot.Confirmed); Assert.False(restarted.ProgressSnapshot.RankPointsConfirmed);
        f.Source.Absolute(5, 50, 20); await restarted.DrainAsync();
        Assert.Equal(50, restarted.ProgressSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public async Task CharacterSwitchSeparatesPerCharacterCapUsage()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Absolute(50, 1_200_000, 500); await f.Ready();
        f.Source.Switch(2); f.Source.Absolute(20, 600_000, 100); await f.Ready();
        Assert.Equal(Fixture.ProfileB, f.Service.ProgressSnapshot.ProfileId);
        Assert.Equal(1_400_000, f.Service.ProgressSnapshot.Remaining);
        Assert.Equal(1_200_000, f.Progress.Read(Fixture.ProfileA)!.FarmedAbyssPoints!.Value);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("unknown")]
    [InlineData("unbound")]
    [InlineData("wrong-generation")]
    [InlineData("future")]
    public async Task UnqualifiedWorldOrProfileCannotAuthorizeRankTracking(string variant)
    {
        await using var f = new Fixture(); await f.Ready();
        switch (variant)
        {
            case "outside": f.Category = "Party"; break;
            case "unknown": f.WorldStatus = WorldContextStatus.Unknown; break;
            case "unbound": f.Bound = false; break;
            case "wrong-generation": f.BindingOffset = -1; break;
            case "future": f.WorldObservationOffset = TimeSpan.FromSeconds(1); break;
        }
        f.Source.Notify(); await f.Ready();
        Assert.False(f.Service.SourceAvailable);
        Assert.False(f.Service.ProgressSnapshot.RankTrackingActive);
        Assert.False(Directory.Exists(f.SessionRoot));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TrackingSettingsRevokeRankAuthorityAndPreserveTheCache(bool network, bool openWorld)
    {
        await using var f = new Fixture(openWorld: openWorld); await f.Ready();
        var previous = f.Service.ProgressSnapshot;
        f.Settings.Update(settings =>
        {
            if (network) settings.LootTrackingEnabled = false;
            else if (openWorld) settings.MonsterLootTrackingEnabled = false;
            else settings.AbyssTrackingEnabled = false;
        });
        await f.Ready();
        Assert.False(f.Service.SourceAvailable);
        Assert.False(f.Service.ProgressSnapshot.RankTrackingActive);
        Assert.Equal(previous.RankPoints, f.Service.ProgressSnapshot.RankPoints);
        Assert.True(f.Service.ProgressSnapshot.RankContinuityRevision > previous.RankContinuityRevision);
        Assert.Equal(!network, f.Service.ProgressSnapshot.RankPointsConfirmed);
        f.Settings.Update(settings =>
        {
            settings.LootTrackingEnabled = settings.AbyssTrackingEnabled = settings.MonsterLootTrackingEnabled = true;
        });
        f.Source.Notify(); await f.Ready();
        Assert.True(f.Service.SourceAvailable);
        Assert.True(f.Service.ProgressSnapshot.RankPointsConfirmed);
        Assert.True(f.Service.ProgressSnapshot.RankTrackingActive);
    }

    [Fact]
    public async Task OpenWorldUsesItsMonsterSettingIndependentlyOfTheAbyssSwitch()
    {
        await using var f = new Fixture(openWorld: true); await f.Ready();
        var baseline = f.Service.ProgressSnapshot;
        f.Settings.SetAbyssTrackingEnabled(false); await f.Ready();
        Assert.True(f.Service.SourceAvailable); Assert.True(f.Service.ProgressSnapshot.RankTrackingActive);
        Assert.Equal(baseline.RankContinuityRevision, f.Service.ProgressSnapshot.RankContinuityRevision);
        f.Settings.SetMonsterLootTrackingEnabled(false); await f.Ready();
        Assert.False(f.Service.SourceAvailable); Assert.False(f.Service.ProgressSnapshot.RankTrackingActive);
        Assert.True(f.Service.ProgressSnapshot.RankPointsConfirmed);
        f.Settings.SetAbyssTrackingEnabled(true); await f.Ready();
        Assert.False(f.Service.SourceAvailable);
        f.Settings.SetMonsterLootTrackingEnabled(true); await f.Ready();
        Assert.True(f.Service.SourceAvailable); Assert.True(f.Service.ProgressSnapshot.RankTrackingActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DungeonAndInstancedOpenWorldCannotAuthorizeTheProgressDisplay(bool instanceLayer)
    {
        await using var f = new Fixture(openWorld: true); await f.Ready();
        if (instanceLayer) f.InstanceLayer = true;
        else { f.WorldKind = WorldContextKind.Instance; f.Category = "Party"; }
        f.Source.Notify(); await f.Ready();
        Assert.False(f.Service.SourceAvailable); Assert.False(f.Service.ProgressSnapshot.RankTrackingActive);
        Assert.True(f.Service.ProgressSnapshot.RankPointsConfirmed);
    }

    [Fact]
    public async Task BoundedQueueOverflowPermanentlyRevokesRankAuthorityWithoutDiscardingTheCache()
    {
        await using var f = new Fixture(); await f.Ready(); await f.Service.DisposeAsync();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var block = false;
        var progress = new AbyssProgressStore(f.ProgressPath, (source, target) =>
        {
            if (block)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Synthetic cache writer timeout");
            }
            File.Move(source, target, true);
        });
        await using var service = f.NewService(progress); await service.DrainAsync();
        var before = service.ProgressSnapshot;
        try
        {
            block = true;
            f.Source.Absolute(200, 2_000, 1_500);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 4_100; i++) f.Source.Notify();
        }
        finally { block = false; release.Set(); }
        await service.DrainAsync();
        Assert.True(service.StatusIsError);
        Assert.False(service.SourceAvailable);
        Assert.False(service.ProgressSnapshot.RankTrackingActive);
        Assert.True(service.ProgressSnapshot.RankContinuityRevision > before.RankContinuityRevision);
        Assert.Equal(1_500, service.ProgressSnapshot.RankPoints);
        Assert.Equal(1_500, progress.Read(Fixture.ProfileA)?.RankPoints?.Value);
        f.Source.Notify(); await service.DrainAsync();
        Assert.False(service.SourceAvailable);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ProfileA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ProfileB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        public string SessionRoot => Path.Combine(_root, "sessions");
        public string ProgressPath => Path.Combine(_root, "absolute.json");
        public SettingsService Settings { get; } = new();
        public FakeSource Source { get; } = new();
        public ManualTime Clock { get; } = new();
        public AbyssProgressStore Progress { get; }
        public AbyssProgressService Service { get; }
        public bool Bound = true;
        public string Category = "Abyss", MapId = "abyss-lower";
        public WorldContextStatus WorldStatus = WorldContextStatus.Confirmed;
        public WorldContextKind WorldKind = WorldContextKind.Instance;
        public bool? InstanceLayer;
        public long BindingOffset;
        public TimeSpan WorldObservationOffset;
        public Fixture(bool enabled = true, bool qualified = true, bool openWorld = false)
        {
            if (openWorld) { WorldKind = WorldContextKind.OpenWorld; Category = "field"; }
            Settings.Current.AbyssTrackingEnabled = enabled;
            Settings.Current.LootTrackingEnabled = Settings.Current.MonsterLootTrackingEnabled = true;
            Source.UtcNow = Clock.GetUtcNow; Source.Qualified = qualified; Source.Absolute(100, 1000, 1000);
            Progress = new(Path.Combine(_root, "absolute.json")); Service = NewService();
        }
        public AbyssProgressService NewService(AbyssProgressStore? progress = null) => new(Settings, Source,
            own => Bound ? new(own.Identity.CharacterId == 1 ? ProfileA : ProfileB, "Synthetic character", 100, 50) : null,
            () => new(WorldStatus, WorldKind, 10, null, Category, null, "Abyss", "Abyss", 1,
                Clock.GetUtcNow() + WorldObservationOffset, "synthetic")
                { BindingGeneration = Source.OwnCharacter?.Generation + BindingOffset, SoulcrestMapId = MapId, InstanceLayer = InstanceLayer },
            progress ?? Progress, Clock);
        public Task Ready() => Service.DrainAsync();
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (_settings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FakeSource : INetworkAbyssSource
    {
        private long _generation = 1, _absoluteSequence;
        private Action<NetworkAbyssPointGain>? _points;
        private Action<NetworkAbyssRankChange>? _rank;
        public int GainSubscriptions { get; private set; }
        public bool Running { get; set; } = true;
        public bool Qualified { get; set; } = true;
        public OwnCharacterContext? OwnCharacter { get; private set; } = new(new(1, 1, 1, "Synthetic character"), 1);
        public NetworkAbyssSnapshot AbyssSnapshot { get; private set; } = NetworkAbyssSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkAbyssPointGain>? AbyssPointsGained
        { add { _points += value; GainSubscriptions++; } remove { _points -= value; GainSubscriptions--; } }
        public event Action<NetworkAbyssRankChange>? RankPointsChanged
        { add { _rank += value; GainSubscriptions++; } remove { _rank -= value; GainSubscriptions--; } }
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
        public void Notify() => Changed?.Invoke();
        public void Absolute(long? balance, long? farmed, long? rank, DateTimeOffset? balanceAt = null,
            DateTimeOffset? farmedAt = null, DateTimeOffset? rankAt = null)
        {
            AbyssSnapshot = new(OwnCharacter, balance, farmed, rank, UtcNow(), ++_absoluteSequence)
            { AbyssPointGainsQualified = Qualified, RankPointChangesQualified = Qualified, KinaGainsQualified = Qualified,
                AbyssPointBalanceObservedAt = balanceAt, FarmedAbyssPointsObservedAt = farmedAt, RankPointsObservedAt = rankAt };
            Notify();
        }
        public void ClearAbsolute() { AbyssSnapshot = NetworkAbyssSnapshot.Empty; Notify(); }
        public void SetSnapshot(NetworkAbyssSnapshot snapshot) { AbyssSnapshot = snapshot; Notify(); }
        public void Points(long amount, long sequence, DateTimeOffset? at = null) => _points?.Invoke(new(at ?? UtcNow(), sequence, OwnCharacter!, amount));
        public void Rank(long delta, long sequence, DateTimeOffset? at = null) => _rank?.Invoke(new(at ?? UtcNow(), sequence, OwnCharacter!, delta));
        public void LoseOwn() { OwnCharacter = null; ClearAbsolute(); OwnCharacterChanged?.Invoke(); }
        public void Switch(int id)
        {
            OwnCharacter = new(new(id, 1, id, "Synthetic character"), ++_generation); ClearAbsolute(); OwnCharacterChanged?.Invoke();
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(() => callback(state)); _timers.Add(timer); return timer; }
        public void Advance(TimeSpan by) { Interlocked.Add(ref _stamp, by.Ticks); foreach (var timer in _timers.ToArray()) timer.Fire(); }
        public void AdvanceWithoutTimer(TimeSpan by) => Interlocked.Add(ref _stamp, by.Ticks);
        private sealed class ManualTimer(Action callback) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
