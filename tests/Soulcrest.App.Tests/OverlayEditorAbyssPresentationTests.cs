using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorAbyssPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void DefaultAndLegacyPresentationSourcesHaveNoAbyssData()
    {
        Assert.Same(AbyssRankOverlaySnapshot.Empty, new OverlaySnapshot().Abyss);
        using var source = Create();
        Assert.Same(AbyssRankOverlaySnapshot.Empty, source.Capture().Abyss);
    }

    [Fact]
    public void OneLiveCaptureReadsOneExistingRankSnapshotWithoutRequiringALootSession()
    {
        var reads = 0;
        var rank = Eligible();
        using var source = Create(() => { reads++; return rank; });
        var snapshot = source.Capture(language: "en");
        Assert.Equal(1, reads);
        Assert.False(snapshot.SessionActive);
        Assert.True(snapshot.Abyss.WorldContextVisible);
        Assert.Equal(rank.RankPoints, snapshot.Abyss.RankPoints);
        Assert.Equal(rank.RankPointsObservedAt, snapshot.Abyss.RankPointsObservedAt);
        Assert.Equal(rank.RankObservationSequence, snapshot.Abyss.RankObservationSequence);
        Assert.Equal(rank.RankContinuityRevision, snapshot.Abyss.RankContinuityRevision);
        Assert.Equal(rank.KillFeed, snapshot.Abyss.KillFeed);
        Assert.Equal(-40, snapshot.Abyss.SessionNetRankPoints);
        Assert.Equal(-80, snapshot.Abyss.SessionRankPointsPerHour);
    }

    [Fact]
    public void HiddenOrUnboundNativeSnapshotsCannotLeakCachedPointsOrFeedIntoTheEditor()
    {
        var rank = Eligible();
        var invalid = new[]
        {
            rank with { WorldContextVisible = false },
            rank with { CharacterBound = false },
            rank with { ProfileId = null },
            rank with { ProfileId = "" },
            rank with { ProfileId = "not-a-profile" },
            rank with { ProfileId = Guid.Empty.ToString("N") },
            rank with { BindingGeneration = null },
            rank with { BindingGeneration = 0 },
            rank with { BindingGeneration = -1 },
        };
        foreach (var current in invalid)
        {
            using var source = Create(() => current);
            Assert.Same(AbyssRankOverlaySnapshot.Empty, source.Capture().Abyss);
        }
    }

    [Fact]
    public void CachedRankInAQualifiedNativeContextKeepsItsUnconfirmedMarker()
    {
        var rank = Eligible() with
        {
            Confirmed = false, RankTrackingActive = false, SessionActive = false,
            SessionId = null, SessionNetRankPoints = null, SessionRankPointsPerHour = null,
        };
        using var source = Create(() => rank);
        var snapshot = source.Capture().Abyss;
        Assert.Equal(999_572, snapshot.RankPoints);
        Assert.False(snapshot.Confirmed);
        Assert.False(snapshot.RankTrackingActive);
        Assert.False(snapshot.SessionActive);
        Assert.Equal(rank.KillFeed.Count, snapshot.KillFeed.Count);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void ExistingFeedEnableAndCategoryPreferencesRemainAuthoritative(bool enabled, bool kills, bool deaths, bool assists)
    {
        var rank = Eligible() with
        {
            KillFeedEnabled = enabled, ShowKills = kills, ShowDeaths = deaths, ShowAssists = assists,
        };
        var before = JsonSerializer.Serialize(rank);
        using var source = Create(() => rank);
        var projected = source.Capture().Abyss;
        Assert.Equal(enabled, projected.KillFeedEnabled);
        Assert.Equal(kills, projected.ShowKills);
        Assert.Equal(deaths, projected.ShowDeaths);
        Assert.Equal(assists, projected.ShowAssists);
        Assert.Equal(AbyssRankOverlayRenderer.KillFeedEntries(rank), projected.KillFeed);
        Assert.Equal(before, JsonSerializer.Serialize(rank));
    }

    [Fact]
    public void CurrentWorldAndBindingInvalidationUseTheExistingNativeQualification()
    {
        var world = World();
        var progress = Progress();
        var feed = new AbyssKillFeedDisplaySnapshot(7, [.. Eligible().KillFeed]);
        var qualified = Native(world);
        Assert.True(qualified.WorldContextVisible);
        Assert.False(qualified.SessionActive);
        Assert.NotEmpty(OverlayEditorPresentation.ProjectAbyss(qualified).KillFeed);
        var invalid = new WorldContextSnapshot?[]
        {
            null,
            world with { Status = WorldContextStatus.Unknown },
            world with { Status = WorldContextStatus.Stale },
            world with { Status = WorldContextStatus.Transitioning },
            world with { BindingGeneration = 8 },
            world with { ObservedAt = null },
            world with { ObservedAt = default(DateTimeOffset) },
            world with { ObservedAt = DateTimeOffset.MaxValue },
            world with { Category = "Other" },
        };
        foreach (var current in invalid)
            Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(current)));
        Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(world, false)));
        var wrongFeedBinding = AbyssRankOverlaySource.ProjectRank(progress,
            feed with { BindingGeneration = 8 }, true, true, true, true, world: world, trackingAvailable: true);
        Assert.Empty(OverlayEditorPresentation.ProjectAbyss(wrongFeedBinding).KillFeed);

        AbyssRankOverlaySnapshot Native(WorldContextSnapshot? current, bool tracking = true) =>
            AbyssRankOverlaySource.ProjectRank(progress, feed, true, true, true, true,
                world: current, trackingAvailable: tracking);
    }

    [Fact]
    public void OpenWorldRankVisibilityStillNeedsTheNativeCurrentOwnGainReceipt()
    {
        var world = World() with { Kind = WorldContextKind.OpenWorld, Category = null, InstanceLayer = false };
        var session = new LootTrackerSession
        {
            Id = "synthetic-open-world", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1,
            Status = LootTrackerSessionStatus.Live, StartedAt = Now.AddMinutes(-30), LastLootAt = Now,
            Elapsed = TimeSpan.FromMinutes(30), Character = new(Profile, "Example"),
            AbyssPoints = 10, RankPointsGained = 0, RankPointsLost = 40,
            TimelineEvents = [new("ap-receipt", LootTrackerTimelineEventKind.AbyssPoints, Now, TimeSpan.FromMinutes(30))
                { Origin = LootTrackerActivityOrigin.Pve, Amount = 10 }],
        };
        var visible = OverlayEditorPresentation.ProjectAbyss(Native(session));
        Assert.True(visible.WorldContextVisible);
        Assert.Equal(-40, visible.SessionNetRankPoints);
        Assert.Empty(visible.KillFeed);
        var lossOnly = session with
        {
            AbyssPoints = null,
            TimelineEvents = [new("loss", LootTrackerTimelineEventKind.RankPoints, Now, TimeSpan.FromMinutes(30))
                { Origin = LootTrackerActivityOrigin.Pve, Amount = -40 }],
        };
        Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(lossOnly)));
        Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(session with { TimelineEvents = [] })));
        Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(session with { Status = LootTrackerSessionStatus.Completed })));
        Assert.Same(AbyssRankOverlaySnapshot.Empty, OverlayEditorPresentation.ProjectAbyss(Native(session with
            { Character = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Example") })));

        AbyssRankOverlaySnapshot Native(LootTrackerSession current) => AbyssRankOverlaySource.ProjectRank(Progress(),
            new AbyssKillFeedDisplaySnapshot(7, [.. Eligible().KillFeed]), true, true, true, true,
            session: current, world: world, trackingAvailable: true);
    }

    [Fact]
    public void AuthorizedHistoricalRowsKeepTheirOriginalGenerationAndCapturedFrame()
    {
        var rows = Eligible().KillFeed.ToList();
        var rank = Eligible() with { BindingGeneration = 7, KillFeed = rows };
        using var source = Create(() => rank);
        var first = source.Capture().Abyss;
        Assert.All(first.KillFeed, row => Assert.Equal(1, row.Generation));
        rows.Clear();
        Assert.Equal(3, first.KillFeed.Count);
        Assert.Empty(source.Capture().Abyss.KillFeed);
    }

    [Fact]
    public void NativeSourceChangesAreForwardedOnlyToConsumersAndDetachedOnDispose()
    {
        var native = new RankSource();
        using var source = new OverlayEditorPresentationSource(() => UnifiedLootOverlayState.Empty,
            () => null, (_, _) => throw new InvalidOperationException("No market projection"),
            () => throw new InvalidOperationException("No market lease"), native, new Clock());
        var changes = 0;
        source.Changed += () => changes++;
        Assert.Equal(1, native.Subscribers);
        native.Publish(Eligible());
        Assert.Equal(0, changes);
        using var consumer = source.Acquire();
        native.Publish(Eligible() with { RankPoints = 1_000_000 });
        Assert.Equal(1, changes);
        Assert.Equal(1_000_000, source.Capture().Abyss.RankPoints);
        Assert.Equal(1, native.Reads);
        source.Dispose();
        Assert.Equal(0, native.Subscribers);
        native.Publish(Eligible());
        Assert.Equal(1, changes);
        Assert.Same(AbyssRankOverlaySnapshot.Empty, source.Capture().Abyss);
        Assert.Equal(1, native.Reads);
    }

    [Fact]
    public void PureExamplesProvideDeterministicEligibleRanksAndEveryFeedResultWithoutLiveReads()
    {
        using var source = new OverlayEditorPresentationSource(() => throw new InvalidOperationException("No session read"),
            () => throw new InvalidOperationException("No notice read"), (_, _) => throw new InvalidOperationException("No price read"),
            () => throw new InvalidOperationException("No market lease"), new Clock(),
            abyss: () => throw new InvalidOperationException("No rank source read"));
        var sample = source.Capture(examples: true, language: "en");
        Assert.True(sample.Abyss.CharacterBound);
        Assert.True(sample.Abyss.Confirmed);
        Assert.True(sample.Abyss.WorldContextVisible);
        Assert.True(sample.Abyss.RankTrackingActive);
        Assert.True(sample.Abyss.BindingGeneration > 0);
        Assert.Equal(6, sample.Abyss.KillFeed.Count);
        Assert.Equal(6, sample.Abyss.KillFeed.Select(row => row.OpponentAbyssRankGrade).Distinct().Count());
        Assert.Contains(sample.Abyss.KillFeed, row => row.Kind == AbyssKillFeedKind.Kill && row.AbyssPointGain > 0 && row.RankPointDelta > 0);
        Assert.Contains(sample.Abyss.KillFeed, row => row.Kind == AbyssKillFeedKind.Death && row.RankPointDelta < 0);
        Assert.Contains(sample.Abyss.KillFeed, row => row.Kind == AbyssKillFeedKind.Assist && row.IsEstimated);
        Assert.All(sample.Abyss.KillFeed, row => Assert.StartsWith("Example opponent ", row.OpponentName));
        foreach (var language in new[] { "de", "en" })
        {
            var demo = OverlaySnapshot.DemoFor(language);
            Assert.Equal(JsonSerializer.Serialize(sample.Abyss), JsonSerializer.Serialize(demo.Abyss));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RankNameLanguageRemainsIndependentFromUiLanguageInBothModes(bool examples)
    {
        var names = "de";
        using var source = Create(Eligible, () => names);
        var germanNames = source.Capture(examples, "en");
        Assert.Equal("en", germanNames.UiLanguage);
        Assert.Equal("de", germanNames.NameLanguage);
        names = "en";
        var englishNames = source.Capture(examples, "de");
        Assert.Equal("de", englishNames.UiLanguage);
        Assert.Equal("en", englishNames.NameLanguage);
        Assert.Equal("en", new OverlaySnapshot().NameLanguage);
    }

    private static OverlayEditorPresentationSource Create(Func<AbyssRankOverlaySnapshot>? abyss = null,
        Func<string>? names = null) => new(() => UnifiedLootOverlayState.Empty, () => null,
            (_, _) => throw new InvalidOperationException("No market projection"),
            () => throw new InvalidOperationException("No market lease"), new Clock(), nameLanguage: names, abyss: abyss);

    private static AbyssRankOverlaySnapshot Eligible() => new(999_572, true, true)
    {
        WorldContextVisible = true, ProfileId = Profile, BindingGeneration = 7,
        RankPointsObservedAt = Now, RankObservationSequence = 11, RankTrackingActive = true,
        RankContinuityRevision = 3, SessionId = "synthetic-abyss-session", SessionActive = true,
        SessionNetRankPoints = -40, SessionRankPointsPerHour = -80,
        KillFeedEnabled = true, ShowKills = true, ShowDeaths = true, ShowAssists = true,
        KillFeed =
        [
            new(Now, AbyssKillFeedKind.Kill, "Example opponent 1", 100)
                { Generation = 1, Sequence = 3, AbyssPointGain = 50, OpponentClassId = "Gladiator", OpponentAbyssRankGrade = 1 },
            new(Now.AddSeconds(-1), AbyssKillFeedKind.Death, "Example opponent 2", -40)
                { Generation = 1, Sequence = 2, OpponentClassId = "Sorcerer", OpponentAbyssRankGrade = 13 },
            new(Now.AddSeconds(-2), AbyssKillFeedKind.Assist, "Example opponent 3", 20)
                { Generation = 1, Sequence = 1, AbyssPointGain = 10, IsEstimated = true, OpponentClassId = "Cleric", OpponentAbyssRankGrade = 19 },
        ],
    };

    private static AbyssProgressSnapshot Progress() => new(Profile, null, null, 999_572, null, Now, true)
    {
        BindingGeneration = 7, RankPointsConfirmed = true, RankTrackingActive = true,
    };
    private static WorldContextSnapshot World() => new(WorldContextStatus.Confirmed, WorldContextKind.Instance,
        20, null, "Abyss", null, "Abyss", "Abyss", 1, DateTimeOffset.UtcNow.AddMinutes(-1), "synthetic") { BindingGeneration = 7 };

    private sealed class RankSource : IAbyssRankOverlaySource
    {
        private Action? _changed;
        private AbyssRankOverlaySnapshot _snapshot = AbyssRankOverlaySnapshot.Empty;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public int Reads { get; private set; }
        public AbyssRankOverlaySnapshot Snapshot { get { Reads++; return _snapshot; } }
        public event Action? Changed { add => _changed += value; remove => _changed -= value; }
        internal void Publish(AbyssRankOverlaySnapshot snapshot) { _snapshot = snapshot; _changed?.Invoke(); }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
    }
    private sealed class Timer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
