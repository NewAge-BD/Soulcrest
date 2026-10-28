using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Real decoder/source to isolated session journal, progress cache and rank presentation.</summary>
public sealed class AbyssRankNetworkIntegrationTests
{
    [Fact]
    public async Task OwnApPairCountsOnlyTheObservedGainWithoutInventingFarmProgressOrRefreshingTheRankFact()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Total(900_012);
        var rankAt = f.Tracker.ProgressSnapshot.RankPointsObservedAt;
        var apEvents = 0;
        f.Network.AbyssPointsGained += _ => apEvents++;
        await f.Wallet(47_702);
        Assert.Null(Assert.Single(f.Loot.Snapshot.Sessions).AbyssPoints);
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(0, apEvents);
        await f.Wallet(47_714);
        await f.Wallet(47_714); // Repeating an absolute total never repeats income.
        var progress = f.Tracker.ProgressSnapshot;
        Assert.Equal(47_714, progress.AbyssPointBalance);
        Assert.True(progress.AbyssPointBalanceConfirmed);
        Assert.Null(progress.FarmedAbyssPoints);
        Assert.Equal(900_012, progress.RankPoints);
        Assert.Equal(rankAt, progress.RankPointsObservedAt);
        Assert.True(progress.RankPointsConfirmed);
        Assert.Equal(47_714, f.Progress.Read(Fixture.Profile)!.AbyssPointBalance!.Value);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Equal(1, apEvents);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(12, session.AbyssPoints);
        Assert.Equal(10_800, LootTrackerPresentation.PerHour(session.AbyssPoints, session.Elapsed));
        Assert.Equal(180, LootTrackerPresentation.PerMinute(session.AbyssPoints, session.Elapsed));
        Assert.Equal(12, Assert.Single(session.MinuteIncome).AbyssPoints);
        Assert.Equal(12, session.RankPointsGained);
        Assert.Equal(0, session.RankPointsLost);
    }

    [Fact]
    public async Task QualifiedEqualApPairConfirmsZeroWithoutInventingIncomeAndTheNextUpdateCounts()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Wallet(100);
        Assert.Null(Assert.Single(f.Loot.Snapshot.Sessions).AbyssPoints);
        await f.Wallet(100);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(0, session.AbyssPoints);
        Assert.Empty(session.MinuteIncome);
        await f.Wallet(105);
        Assert.Equal(5, Assert.Single(f.Loot.Snapshot.Sessions).AbyssPoints);
    }

    [Fact]
    public async Task LaterRankFactCannotSuppressAnApGainWithAnEarlierValidApFactTime()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Wallet(100);
        var apAt = f.Clock.GetUtcNow().AddMilliseconds(500);
        await f.Total(900_012);
        var rankAt = f.Clock.GetUtcNow();
        await f.Wallet(105, apAt);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(12, session.RankPointsGained);
        Assert.Equal(apAt, f.Progress.Read(Fixture.Profile)!.AbyssPointBalance!.ObservedAt);
        Assert.Equal(rankAt, f.Tracker.ProgressSnapshot.RankPointsObservedAt);
    }

    [Fact]
    public async Task ApGapAndNewBaselineCannotRecoverUnobservedIncome()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Wallet(100);
        await f.Wallet(105);
        f.Network.ResetProgressContext("stream-gap", f.Clock.GetUtcNow());
        await f.Drain();
        await f.Wallet(999); // No own authority survives the gap.
        await f.Enter(900_000);
        await f.Wallet(1_000); // A fresh baseline never catches up the missing interval.
        await f.Wallet(1_002);
        var sessions = f.Loot.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal(7, sessions.Sum(session => session.AbyssPoints ?? 0));
        Assert.Null(f.Tracker.ProgressSnapshot.FarmedAbyssPoints);
    }

    [Fact]
    public async Task OwnFarmTotalConfirmsCapWithoutCreatingIncomeOrRefreshingWalletAndRankFacts()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Total(900_012);
        var rankAt = f.Clock.GetUtcNow();
        await f.Wallet(52_576);
        var walletAt = f.Clock.GetUtcNow();
        var apEvents = 0;
        f.Network.AbyssPointsGained += _ => apEvents++;
        await f.FarmTotal(717_422);
        var progress = f.Tracker.ProgressSnapshot;
        Assert.Equal(717_422, progress.FarmedAbyssPoints);
        Assert.True(progress.FarmedAbyssPointsConfirmed);
        Assert.Equal(1_282_578, progress.Remaining);
        Assert.Equal(walletAt, f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.Equal(rankAt, progress.RankPointsObservedAt);
        Assert.Equal(0, apEvents);
        Assert.Null(Assert.Single(f.Loot.Snapshot.Sessions).AbyssPoints);

        await f.Wallet(52_581);
        walletAt = f.Clock.GetUtcNow();
        await f.FarmTotal(717_427);
        await f.FarmTotal(717_427); // Repeated absolute farm totals cannot duplicate AP income.
        var farmAt = f.Clock.GetUtcNow();
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(1, apEvents);
        Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(5, Assert.Single(session.MinuteIncome).AbyssPoints);
        Assert.Equal(12, session.RankPointsGained);
        Assert.Equal(717_427, f.Tracker.ProgressSnapshot.FarmedAbyssPoints);
        Assert.Equal(1_282_573, f.Tracker.ProgressSnapshot.Remaining);
        Assert.Equal(walletAt, f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.Equal(rankAt, f.Tracker.ProgressSnapshot.RankPointsObservedAt);
        Assert.Equal(new SavedAbyssValue(717_427, farmAt), f.Progress.Read(Fixture.Profile)!.FarmedAbyssPoints);
        var overlay = UnifiedLootOverlaySource.Project(f.Loot.OverlayState, f.Tracker.ProgressSnapshot, new AppSettings());
        Assert.Equal(717_427, overlay.FarmedAbyssPoints);
        Assert.True(overlay.AbyssFarmConfirmed);
        Assert.Equal(5, overlay.SessionAbyssPoints);

        await f.Loot.DeleteSessionAsync(session.Id);
        Assert.Empty(f.Loot.Snapshot.Sessions);
        Assert.Equal(new SavedAbyssValue(717_427, farmAt), f.Progress.Read(Fixture.Profile)!.FarmedAbyssPoints);
        Assert.Equal(1_282_573, f.Tracker.ProgressSnapshot.Remaining);
    }

    [Fact]
    public async Task MalformedFarmTotalRevokesItsConfirmationWithoutRevokingWalletOrRank()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Total(900_012);
        await f.Wallet(100);
        await f.Wallet(105);
        await f.FarmTotal(717_427);
        var farmAt = f.Clock.GetUtcNow();
        f.Clock.Advance();
        f.Network.ProcessProgressMessage("synthetic-rank-stream", [0xA9, 0xFF, 1], f.Clock.GetUtcNow());
        await f.Drain();
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.False(f.Tracker.ProgressSnapshot.FarmedAbyssPointsConfirmed);
        Assert.Equal(717_427, f.Tracker.ProgressSnapshot.FarmedAbyssPoints); // Cached, not currently confirmed.
        Assert.Equal(105, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.Equal(900_012, f.Network.AbyssSnapshot.RankPoints);
        await f.Wallet(107);
        await f.Total(900_013);
        await f.FarmTotal(999_999); // A revoked farm channel cannot catch up until fresh Own.
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(7, session.AbyssPoints);
        Assert.Equal(13, session.RankPointsGained);
        Assert.Equal(new SavedAbyssValue(717_427, farmAt), f.Progress.Read(Fixture.Profile)!.FarmedAbyssPoints);
        Assert.False(f.Tracker.ProgressSnapshot.FarmedAbyssPointsConfirmed);
        Assert.True(f.Tracker.ProgressSnapshot.AbyssPointBalanceConfirmed);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
    }

    [Fact]
    public async Task NewerFarmFactDoesNotSuppressEarlierValidWalletAndRankObservations()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Wallet(100);
        var apAt = f.Clock.GetUtcNow().AddMilliseconds(200);
        var rankAt = f.Clock.GetUtcNow().AddMilliseconds(400);
        await f.FarmTotal(717_422);
        var farmAt = f.Clock.GetUtcNow();
        await f.Wallet(105, apAt);
        await f.Total(900_005, rankAt);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(5, session.RankPointsGained);
        Assert.Equal(new SavedAbyssValue(717_422, farmAt), f.Progress.Read(Fixture.Profile)!.FarmedAbyssPoints);
        Assert.Equal(apAt, f.Network.AbyssSnapshot.AbyssPointBalanceObservedAt);
        Assert.Equal(rankAt, f.Tracker.ProgressSnapshot.RankPointsObservedAt);
        Assert.True(f.Tracker.ProgressSnapshot.FarmedAbyssPointsConfirmed);
    }

    [Fact]
    public async Task AbyssKinaPairUsesTheExistingProducerOnceAndKeepsCapAndApSeparate()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        var kinaEvents = 0;
        f.Network.KinaGained += _ => kinaEvents++;
        await f.KinaWallet(488_546);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Null(Assert.Single(f.Loot.Snapshot.Sessions).KinaUnbound);
        await f.KinaWallet(488_645);
        await f.KinaWallet(488_645);
        var kinaAt = f.Clock.GetUtcNow();
        await f.FarmTotal(717_427);
        await f.Wallet(52_576);
        await f.Wallet(52_581);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(1, kinaEvents);
        Assert.Equal(99, session.KinaUnbound);
        Assert.Equal(99, Assert.Single(session.MinuteIncome).KinaUnbound);
        Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(717_427, f.Tracker.ProgressSnapshot.FarmedAbyssPoints);
        Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
        Assert.Equal(kinaAt, f.Network.AbyssSnapshot.KinaGainsObservedAt);
        var overlay = UnifiedLootOverlaySource.Project(f.Loot.OverlayState, f.Tracker.ProgressSnapshot, new AppSettings());
        Assert.True(overlay.SessionKinaKnown);
        Assert.Equal(99, overlay.SessionKina);
        Assert.NotNull(overlay.SessionKinaPerHour);
    }

    [Fact]
    public async Task EqualKinaPairConfirmsZeroWithoutInventingAnIncomeBin()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.KinaWallet(100);
        await f.KinaWallet(100);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(0, session.KinaUnbound);
        Assert.Empty(session.MinuteIncome);
        Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
    }

    [Fact]
    public async Task UnchangedQualifiedTotalConfirmsZeroObservedChangeWithoutCreatingAnIncomeBin()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Total(900_000);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(0, session.RankPointsGained);
        Assert.Equal(0, session.RankPointsLost);
        Assert.Empty(session.MinuteIncome);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
        Assert.Equal(900_000, f.Tracker.ProgressSnapshot.RankPoints);
    }

    [Fact]
    public async Task SuccessiveOwnTotalsCountSignedChangesOnceAndRefreshTheOverlayWithoutQualifyingAp()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        var baseline = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Null(baseline.RankPointsGained);
        Assert.Null(baseline.RankPointsLost);
        Assert.Null(baseline.AbyssPoints);
        Assert.Null(baseline.KinaUnbound);

        await f.Total(900_012);
        await f.Total(900_012); // Repeated absolute total cannot count the same gain again.
        await f.Total(899_427);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(12, session.RankPointsGained);
        Assert.Equal(585, session.RankPointsLost);
        Assert.Equal(-573, LootTrackerPresentation.NetRankPoints(session));
        Assert.True(f.Loot.SourceAvailable);
        Assert.Null(session.AbyssPoints);
        Assert.Null(session.KinaUnbound);
        Assert.True(session.CoverageLimited);
        Assert.Equal(899_427, f.Tracker.ProgressSnapshot.RankPoints);
        Assert.Equal(f.Clock.GetUtcNow(), f.Tracker.ProgressSnapshot.RankPointsObservedAt);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
        Assert.Equal(899_427, AbyssRankPresentation.Project(f.Tracker.ProgressSnapshot.RankPoints).TotalPoints);
        Assert.Equal(899_427, f.Progress.Read(Fixture.Profile)!.RankPoints!.Value);
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.False(f.Network.AbyssSnapshot.KinaGainsQualified);

        await f.Loot.DisposeAsync();
        await f.Tracker.DisposeAsync();
        var saved = Assert.Single(new UnifiedLootSessionStore(f.Sessions).Read().Sessions);
        Assert.Equal(12, saved.RankPointsGained);
        Assert.Equal(585, saved.RankPointsLost);
        Assert.Equal(-573, LootTrackerPresentation.NetRankPoints(saved));
    }

    [Fact]
    public async Task GapAndFreshOwnCannotConvertUnobservedChangesOrBaselineDifferencesIntoSessionIncome()
    {
        await using var f = new Fixture();
        await f.Enter(900_000);
        await f.Total(900_012);
        f.Network.ResetProgressContext("stream-gap", f.Clock.GetUtcNow());
        await f.Drain();
        await f.Total(899_000); // No own authority survives the gap.
        Assert.Equal(900_012, f.Progress.Read(Fixture.Profile)!.RankPoints!.Value);
        Assert.Null(f.Tracker.ProgressSnapshot.RankPoints);
        Assert.False(f.Tracker.ProgressSnapshot.RankPointsConfirmed);

        await f.Enter(899_000); // The fresh baseline difference is never a loss.
        await f.Total(899_002);
        var sessions = f.Loot.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal(14, sessions.Sum(row => row.RankPointsGained ?? 0));
        Assert.Equal(0, sessions.Sum(row => row.RankPointsLost ?? 0));
        Assert.Equal(899_002, f.Tracker.ProgressSnapshot.RankPoints);
    }

    [Fact]
    public async Task MotionSequenceFollowsAcceptedRankFactsRatherThanOtherCurrencyEnvelopes()
    {
        await using var f = new Fixture();
        await f.Enter(11_990);
        var baseline = f.Tracker.ProgressSnapshot;
        Assert.True(baseline.RankTrackingActive);
        Assert.Equal(f.Network.AbyssSnapshot.Sequence, baseline.RankObservationSequence);
        await f.Wallet(100);
        Assert.True(f.Network.AbyssSnapshot.Sequence > baseline.RankObservationSequence);
        Assert.Equal(baseline.RankObservationSequence, f.Tracker.ProgressSnapshot.RankObservationSequence);
        Assert.Equal(baseline.RankContinuityRevision, f.Tracker.ProgressSnapshot.RankContinuityRevision);
        await f.Total(12_040);
        var changed = f.Tracker.ProgressSnapshot;
        Assert.Equal(f.Network.AbyssSnapshot.Sequence, changed.RankObservationSequence);
        Assert.True(changed.RankObservationSequence > baseline.RankObservationSequence);
        await f.FarmTotal(1_000);
        Assert.Equal(changed.RankObservationSequence, f.Tracker.ProgressSnapshot.RankObservationSequence);
        Assert.Equal(changed.RankPointsObservedAt, f.Tracker.ProgressSnapshot.RankPointsObservedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenWorldOwnCurrencyAndRankMessagesBookOneMixedSessionAndRoundTripAsPve(bool rankFirst)
    {
        await using var f = new Fixture(openWorld: true, abyssTracking: false);
        await f.Enter(900_000);
        Assert.Equal(WorldContextKind.OpenWorld, f.Network.WorldContext.Snapshot.Kind);
        Assert.Equal(WorldContextStatus.Confirmed, f.Network.WorldContext.Snapshot.Status);
        Assert.Empty(f.Loot.Snapshot.Sessions); // Full Own is a baseline, never income or a session trigger here.
        await f.Wallet(100);
        await f.Wallet(100); // Two equal Mod1 records qualify the channel without creating income.
        await f.Total(900_000);
        Assert.True(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.True(f.Network.AbyssSnapshot.RankPointChangesQualified);
        Assert.Empty(f.Loot.Snapshot.Sessions);
        if (rankFirst) await f.Total(900_012); else await f.Wallet(105);
        var id = Assert.Single(f.Loot.Snapshot.Sessions).Id;
        if (rankFirst) await f.Wallet(105); else await f.Total(900_012);
        await f.Wallet(105); await f.Total(900_012); // Repeated wire totals cannot repeat a receipt.
        await f.Total(899_997);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(id, session.Id);
        Assert.Equal(LootTrackerSessionKind.Mixed, session.Kind);
        Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(12, session.RankPointsGained);
        Assert.Equal(15, session.RankPointsLost);
        Assert.Equal(-3, LootTrackerPresentation.NetRankPoints(session));
        Assert.Equal(TimeSpan.FromSeconds(4), session.Elapsed);
        Assert.Equal(4_500, LootTrackerPresentation.PerHour(session.AbyssPoints, session.Elapsed));
        Assert.Null(session.LastPresenceAt);
        Assert.Equal(f.Clock.GetUtcNow(), session.LastLootAt);
        Assert.Empty(session.CombatEvents); Assert.Empty(session.Loot);
        Assert.All(session.TimelineEvents, fact => Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin));
        Assert.Equal(5, Assert.Single(session.TimelineEvents, fact => fact.Kind == LootTrackerTimelineEventKind.AbyssPoints).Amount);
        Assert.Equal(2, session.TimelineEvents.Count(fact => fact.Kind == LootTrackerTimelineEventKind.RankPoints));
        Assert.Equal(5, Assert.Single(session.MinuteIncome).AbyssPoints);
        Assert.Equal(899_997, f.Progress.Read(Fixture.Profile)!.RankPoints!.Value);
        Assert.Equal(105, f.Progress.Read(Fixture.Profile)!.AbyssPointBalance!.Value);
        Assert.Null(f.Tracker.ProgressSnapshot.FarmedAbyssPoints);
        // A live Open World session with booked own point income shows the rank HUD, never the Abyss feed.
        Assert.True(f.Tracker.SourceAvailable);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
        var overlay = AbyssRankOverlaySource.ProjectRank(f.Tracker.ProgressSnapshot, [], true,
            session: session, world: f.WorldSnapshot, trackingAvailable: true);
        Assert.Equal(899_997, overlay.RankPoints);
        Assert.True(overlay.Confirmed); Assert.True(overlay.WorldContextVisible);
        Assert.True(overlay.SessionActive); Assert.True(overlay.RankTrackingActive);
        Assert.Equal(-3, overlay.SessionNetRankPoints); Assert.Empty(overlay.KillFeed);

        await f.Loot.DisposeAsync(); await f.Tracker.DisposeAsync();
        var read = new UnifiedLootSessionStore(f.Sessions).Read();
        Assert.Empty(read.Issues);
        var restored = Assert.Single(read.Sessions);
        Assert.Equal(id, restored.Id); Assert.Equal(LootTrackerSessionKind.Mixed, restored.Kind);
        Assert.Equal(5, restored.AbyssPoints); Assert.Equal(12, restored.RankPointsGained); Assert.Equal(15, restored.RankPointsLost);
        Assert.Equal(session.TimelineEvents.Select(fact => fact.Id), restored.TimelineEvents.Select(fact => fact.Id));
        Assert.All(restored.TimelineEvents, fact => Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin));
    }

    [Fact]
    public async Task OpenWorldFirstSignedRankLossStartsMixedWithoutCreatingApIncome()
    {
        await using var f = new Fixture(openWorld: true);
        await f.Enter(900_000);
        Assert.Empty(f.Loot.Snapshot.Sessions);
        await f.Total(899_985);
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, session.Kind);
        Assert.Equal(0, session.RankPointsGained); Assert.Equal(15, session.RankPointsLost);
        Assert.Null(session.AbyssPoints); Assert.Null(session.KinaUnbound);
        var fact = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.RankPoints);
        Assert.Equal(-15, fact.Amount); Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin);
        Assert.Equal(899_985, f.Tracker.ProgressSnapshot.RankPoints);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
    }

    [Fact]
    public async Task OpenWorldGapAndFreshOwnDoNotCatchUpApOrRankIncomeAcrossTheMissingInterval()
    {
        await using var f = new Fixture(openWorld: true);
        await f.Enter(900_000); await f.Wallet(100); await f.Wallet(105); await f.Total(900_012);
        var first = Assert.Single(f.Loot.Snapshot.Sessions);
        f.Network.ResetProgressContext("stream-gap", f.Clock.GetUtcNow()); await f.Drain();
        await f.Wallet(999); await f.Total(899_000);
        Assert.Equal(5, Assert.Single(f.Loot.Snapshot.Sessions).AbyssPoints);
        Assert.Equal(12, f.Progress.Read(Fixture.Profile)!.RankPoints!.Value - 900_000);
        await f.Enter(899_000);
        await f.Wallet(1_000); await f.Total(899_000);
        Assert.Single(f.Loot.Snapshot.Sessions); // Fresh baselines never reopen a farming session.
        await f.Wallet(1_002); await f.Total(899_003);
        var sessions = f.Loot.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal(7, sessions.Sum(row => row.AbyssPoints ?? 0));
        Assert.Equal(15, sessions.Sum(row => row.RankPointsGained ?? 0));
        Assert.Equal(0, sessions.Sum(row => row.RankPointsLost ?? 0));
        Assert.Equal(LootTrackerSessionStatus.Interrupted, sessions.Single(row => row.Id == first.Id).Status);
        Assert.All(sessions, row => Assert.Equal(LootTrackerSessionKind.Mixed, row.Kind));
        Assert.Equal(899_003, f.Tracker.ProgressSnapshot.RankPoints);
    }

    [Fact]
    public async Task OpenWorldForeignStreamsAndApSpendingCannotCreateIncomeOrLendAnApBaseline()
    {
        await using var f = new Fixture(openWorld: true);
        await f.Enter(900_000); await f.Wallet(100);
        var baseline = f.Network.AbyssSnapshot;
        await f.Wallet(99_999, stream: "synthetic-foreign-stream");
        await f.Total(999_999, stream: "synthetic-foreign-stream");
        Assert.Equal(baseline, f.Network.AbyssSnapshot);
        Assert.Empty(f.Loot.Snapshot.Sessions);
        await f.Wallet(105); await f.Total(900_002);
        var id = Assert.Single(f.Loot.Snapshot.Sessions).Id;
        await f.Wallet(100, increase: 0, decrease: 15); // Unsupported spending revokes the AP base, never an acquisition.
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        await f.Wallet(100_000); // A later wallet cannot bridge that unqualified inventory transition.
        await f.Total(900_000); // Independent, still qualified signed rank channel.
        var session = Assert.Single(f.Loot.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(5, session.AbyssPoints);
        Assert.Equal(2, session.RankPointsGained); Assert.Equal(2, session.RankPointsLost);
        Assert.Equal(105, f.Progress.Read(Fixture.Profile)!.AbyssPointBalance!.Value);
        Assert.False(f.Tracker.ProgressSnapshot.AbyssPointBalanceConfirmed);
        Assert.True(f.Tracker.ProgressSnapshot.RankPointsConfirmed);
        Assert.Single(session.TimelineEvents, fact => fact.Kind == LootTrackerTimelineEventKind.AbyssPoints);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-rank-integration-" + Guid.NewGuid().ToString("N"));
        public string Sessions => Path.Combine(_root, "sessions");
        public NetworkLootService Network { get; }
        public AbyssProgressService Tracker { get; }
        public UnifiedLootTrackerService Loot { get; }
        public AbyssProgressStore Progress { get; }
        public ManualClock Clock { get; } = new();
        private DateTimeOffset _worldObservedAt;
        private readonly bool _openWorld;
        public WorldContextSnapshot WorldSnapshot => _openWorld ? Network.WorldContext.Snapshot
            : new(WorldContextStatus.Confirmed, WorldContextKind.Instance, 10, null, "Abyss", null,
                "Abyss", "Abyss", 1, _worldObservedAt, "synthetic")
                { BindingGeneration = Network.OwnCharacter?.Generation, SoulcrestMapId = "synthetic-abyss" };

        public Fixture(bool openWorld = false, bool abyssTracking = true)
        {
            _openWorld = openWorld;
            Directory.CreateDirectory(_root);
            var catalog = InstanceCatalog.FromJson("""
                { "schemaVersion": 1, "maps": [{ "id": 20, "names": {"en":"Synthetic Abyss"},
                  "kind": "instance", "category": "Abyss", "sourceRefs": ["synthetic"] },
                  { "id": 21, "names": {"en":"Synthetic Open World"}, "kind": "openWorld",
                    "category": "field", "soulcrestMapId": "synthetic-open-world", "sourceRefs": ["synthetic"] }], "dungeons": [] }
                """);
            Network = new(new ProgressService(Path.Combine(_root, "pets.json"), new PetCatalog([])), worldContext: new(catalog));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            var settings = new SettingsService();
            settings.Current.AbyssTrackingEnabled = abyssTracking;
            settings.Current.LootTrackingEnabled = settings.Current.MonsterLootTrackingEnabled = true;
            Progress = new(Path.Combine(_root, "abyss-progress.json"));
            LootTrackerCharacterSnapshot? Bind(OwnCharacterContext _) => new(Profile, "Synthetic rank integration");
            WorldContextSnapshot World() => WorldSnapshot;
            Tracker = new(settings, Network, Bind, World, Progress, Clock);
            Loot = new(settings, Network, Network, Network, Network, Bind, World,
                new(Sessions), Clock, dailyStore: new(Path.Combine(_root, "daily.json")));
        }

        public async Task Enter(long total)
        {
            Clock.Advance();
            _worldObservedAt = Clock.GetUtcNow();
            Network.ProcessProgressMessage("synthetic-rank-stream", OwnPacket(total, _openWorld ? 21 : 20), Clock.GetUtcNow());
            await Drain();
        }

        public async Task Total(long total, DateTimeOffset? at = null, string stream = "synthetic-rank-stream")
        {
            Clock.Advance();
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write((ushort)0xE273); writer.Write((byte)0); writer.Write(total);
            Network.ProcessProgressMessage(stream, bytes.ToArray(), at ?? Clock.GetUtcNow());
            await Drain();
        }

        public async Task FarmTotal(long total)
        {
            Clock.Advance();
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write((ushort)0xFFA9); writer.Write(total); writer.Write(123L); // Opaque tail, no reset semantics.
            Network.ProcessProgressMessage("synthetic-rank-stream", bytes.ToArray(), Clock.GetUtcNow());
            await Drain();
        }

        public Task KinaWallet(long total) => Wallet(total, itemId: CurrencyInventoryMessage.UnboundKinaItemId, key: 92);

        public async Task Wallet(long total, DateTimeOffset? at = null, int itemId = 930100003, long key = 91,
            string stream = "synthetic-rank-stream", byte increase = 11, byte decrease = 0)
        {
            Clock.Advance();
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write((ushort)0x561B); V(writer, 1); writer.Write((byte)1);
            writer.Write((byte)0); writer.Write(key); writer.Write(itemId); writer.Write(total);
            writer.Write((byte)1); writer.Write(0); writer.Write((byte)0); // Packed false booleans share this byte.
            V(writer, 0); writer.Write(0L);
            writer.Write(increase); writer.Write(decrease); writer.Write((byte)0);
            Network.ProcessProgressMessage(stream, bytes.ToArray(), at ?? Clock.GetUtcNow());
            await Drain();
        }

        public async Task Drain()
        {
            await Tracker.DrainAsync();
            await Loot.DrainAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Loot.DisposeAsync();
            await Tracker.DisposeAsync();
            Network.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance() => _ticks += TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();
        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static byte[] OwnPacket(long rankPoints, int mapDataId = 20)
    {
        using var bytes = new MemoryStream(); using var w = new BinaryWriter(bytes);
        w.Write((ushort)0x3633); V(w, 300); w.Write(65536);
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes("SyntheticRankIntegration"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(29); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(0L); w.Write(mapDataId); w.Write(101L); V(w, 0); w.Write((byte)1);
        w.Write(rankPoints); w.Write(new byte[32]); V(w, 0); V(w, 0);
        return bytes.ToArray();
    }

    private static void V(BinaryWriter writer, uint value)
    {
        do { var next = (byte)(value & 127); value >>= 7; writer.Write(value == 0 ? next : (byte)(next | 128)); } while (value > 0);
    }
}
