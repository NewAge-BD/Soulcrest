using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LifeskillTrackerServiceTests
{
    [Fact]
    public async Task InstanceOnlyAttemptsStayAvailableToOtherConsumersButDoNotStartLifeskill()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        var observed = 0;
        fixture.Source.GatheringCompleted += _ => observed++;
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 1, fixture.Source.OwnCharacter!, 123,
            [new NetworkGatheringItem(101, 9)]) { InInstance = true });
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 2, fixture.Source.OwnCharacter!, 123, [])
            { Outcome = NetworkGatheringOutcome.Failed, InInstance = true });
        await fixture.Ready();
        Assert.Equal(2, observed);
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        fixture.Failed(3);
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(1, session.FailedHarvestCount);
        Assert.Equal(0, session.HarvestCount);
        Assert.Empty(session.Loot);
    }

    [Fact]
    public async Task InstanceRewardsAndFailuresCannotChangeAnOpenWorldSessionOrItsIdleDeadline()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        var first = Assert.Single(fixture.Service.Snapshot.Sessions);
        fixture.Time.Advance(TimeSpan.FromSeconds(90), fireTimer: false);
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 2, fixture.Source.OwnCharacter!, 123,
            [new NetworkGatheringItem(101, 99)]) { InInstance = true });
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 3, fixture.Source.OwnCharacter!, 123, [])
            { Outcome = NetworkGatheringOutcome.Failed, InInstance = true });
        await fixture.Ready();
        var unchanged = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(1, unchanged.ItemCount);
        Assert.Equal(1, unchanged.HarvestCount);
        Assert.Equal(0, unchanged.FailedHarvestCount);
        Assert.Single(unchanged.GatherVisits);
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Ready();
        var completed = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(first.LastLootAt, completed.EndedAt);
        Assert.Equal(TimeSpan.Zero, completed.Elapsed);
        fixture.Gather(4, new NetworkGatheringItem(202, 2));
        await fixture.Ready();
        Assert.Equal(2, fixture.Service.Snapshot.Sessions.Length);
        Assert.NotEqual(first.Id, fixture.Service.Snapshot.Sessions[0].Id);
        Assert.Equal(2, fixture.Service.Snapshot.Sessions[0].ItemCount);
    }

    [Fact]
    public async Task FirstConfirmedFailureStartsWithoutLootAndSharesTheRewardSequenceDeduplication()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        var first = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Lifeskill, first.Kind);
        Assert.Equal(0, first.HarvestCount);
        Assert.Equal(1, first.FailedHarvestCount);
        Assert.Equal(0, first.ItemCount);
        Assert.Null(first.LastLootAt);
        Assert.Empty(first.Loot);
        Assert.Empty(first.ResourceNodes);
        Assert.Single(first.GatherVisits);
        fixture.Failed(1);
        fixture.Gather(1, new NetworkGatheringItem(101, 99));
        fixture.Failed(2);
        fixture.Gather(3, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, session.Id);
        Assert.Equal(2, session.FailedHarvestCount);
        Assert.Equal(1, session.HarvestCount);
        Assert.Equal(2, session.ItemCount);
        Assert.Equal(2, Assert.Single(session.Loot).Quantity);
    }

    [Fact]
    public async Task FailureOnlySessionExpiresFromItsStartAndTrimsToItsLastAttempt()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(30), fireTimer: false);
        fixture.X = 20;
        fixture.Failed(2);
        fixture.Time.Advance(TimeSpan.FromSeconds(60), fireTimer: false);
        fixture.X = 30;
        fixture.Failed(3);
        await fixture.Ready();
        var lastAttempt = fixture.Time.GetUtcNow();
        fixture.X = 999;
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        await fixture.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(fixture.Service.Snapshot.Sessions).Status);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Ready();
        var completed = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(3, completed.FailedHarvestCount);
        Assert.Equal(0, completed.HarvestCount);
        Assert.Equal(0, completed.ItemCount);
        Assert.Null(completed.LastLootAt);
        Assert.Equal(lastAttempt, completed.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(90), completed.Elapsed);
        Assert.All(completed.RouteSegments.SelectMany(segment => segment.Points),
            point => Assert.True(point.Elapsed <= TimeSpan.FromSeconds(90)));
        var saved = Assert.Single(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(3, saved.FailedHarvestCount);
        Assert.Equal(completed.EndedAt, saved.EndedAt);
        Assert.Equal(LootTrackerSessionStatus.Completed, saved.Status);
        fixture.Failed(4);
        await fixture.Ready();
        Assert.Equal(2, fixture.Service.Snapshot.Sessions.Length);
        Assert.NotEqual(completed.Id, fixture.Service.Snapshot.Sessions[0].Id);
        Assert.Equal(1, fixture.Service.Snapshot.Sessions[0].FailedHarvestCount);
    }

    [Fact]
    public async Task FirstRewardAfterFailuresMovesOnlyTheLootDeadlineAndTheLastAttemptCutsFinalTime()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        fixture.Time.Advance(TimeSpan.FromSeconds(90), fireTimer: false);
        fixture.Failed(2);
        fixture.Time.Advance(TimeSpan.FromSeconds(20), fireTimer: false);
        fixture.Gather(3, new NetworkGatheringItem(101, 5));
        await fixture.Ready();
        var lastLoot = fixture.Time.GetUtcNow();
        fixture.Time.Advance(TimeSpan.FromSeconds(110), fireTimer: false);
        fixture.Failed(4);
        await fixture.Ready();
        var lastAttempt = fixture.Time.GetUtcNow();
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        await fixture.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(fixture.Service.Snapshot.Sessions).Status);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, session.Status);
        Assert.Equal(3, session.FailedHarvestCount);
        Assert.Equal(1, session.HarvestCount);
        Assert.Equal(5, session.ItemCount);
        Assert.Equal(lastLoot, session.LastLootAt);
        Assert.Equal(lastAttempt, session.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(220), session.Elapsed);
    }

    [Fact]
    public async Task CharacterSwitchRetainsAZeroLootFailureSessionWithItsOriginalProfile()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        fixture.Source.ChangeOwn(new(new(2, 1, 20, "Example character B"), 2) { CombatScore = 2000, GearScore = 300 });
        fixture.Failed(1);
        await fixture.Ready();
        var sessions = fixture.Service.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal("Example character A", sessions[1].Character?.Name);
        Assert.Equal(1000, sessions[1].Character?.CombatScore);
        Assert.Equal(LootTrackerSessionStatus.Completed, sessions[1].Status);
        Assert.Equal("Example character B", sessions[0].Character?.Name);
        Assert.All(sessions, session => { Assert.Equal(1, session.FailedHarvestCount); Assert.Empty(session.Loot); });
    }

    [Fact]
    public async Task DisposalRecoversFailureOnlyHistoryAndDoesNotPublishMalformedFailures()
    {
        var fixture = new Fixture();
        try
        {
            await fixture.Ready();
            fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 1, fixture.Source.OwnCharacter!, 123,
                [new NetworkGatheringItem(101, 2)]) { Outcome = NetworkGatheringOutcome.Failed });
            fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 2, fixture.Source.OwnCharacter!, 123, [])
                { Outcome = (NetworkGatheringOutcome)99 });
            await fixture.Ready();
            Assert.Empty(fixture.Service.Snapshot.Sessions);
            fixture.Failed(3);
            fixture.Time.Advance(TimeSpan.FromSeconds(10), fireTimer: false);
            fixture.Failed(4);
            await fixture.Service.DisposeAsync();
            var restored = new LifeskillSessionStore(fixture.Root).Read();
            Assert.Empty(restored.Issues);
            var session = Assert.Single(restored.Sessions);
            Assert.Equal(2, session.FailedHarvestCount);
            Assert.Equal(0, session.HarvestCount);
            Assert.Equal(0, session.ItemCount);
            Assert.Empty(session.Loot);
            Assert.Null(session.LastLootAt);
            Assert.Equal(TimeSpan.FromSeconds(10), session.Elapsed);
            Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task FirstConfirmedHarvestStartsAndCountsAllItsItemsExactlyOnce()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        fixture.Gather(1, new NetworkGatheringItem(101, 2), new NetworkGatheringItem(101, 3), new NetworkGatheringItem(202, 4));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Lifeskill, session.Kind);
        Assert.Equal(1, session.HarvestCount);
        Assert.Equal(9, session.ItemCount);
        Assert.Equal(5, session.Loot.Single(item => item.Id == "item:101").Quantity);
        Assert.Equal(4, session.Loot.Single(item => item.Id == "item:202").Quantity);
        Assert.Equal("Sample place", session.Location);
        Assert.Null(session.KinaBound);
        Assert.Null(session.SoulCount);
        Assert.Null(session.MonsterCount);
        Assert.Empty(session.ResourceNodes);
        fixture.Gather(1, new NetworkGatheringItem(101, 5));
        await fixture.Ready();
        Assert.Equal(9, Assert.Single(fixture.Service.Snapshot.Sessions).ItemCount);
    }

    [Fact]
    public async Task CommittedGatheringReceiptsCountItemAmountsButNeverFailuresDuplicatesOrRecovery()
    {
        await using var fixture = new Fixture(); await fixture.Ready();
        var receipts = new List<AcceptedFarmLootDrop>(); fixture.Service.DropAccepted += receipts.Add;
        fixture.Gather(1, new NetworkGatheringItem(101, 2), new NetworkGatheringItem(101, 3), new NetworkGatheringItem(202, 4));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt =>
        {
            Assert.Equal(session.Id, receipt.SessionId); Assert.Equal(session.Character!.ProfileId, receipt.ProfileId);
            Assert.Equal(fixture.Time.GetUtcNow(), receipt.At);
        });
        Assert.Equal(5, receipts.Single(receipt => receipt.LootKey == "item:101").Quantity);
        Assert.Equal(4, receipts.Single(receipt => receipt.LootKey == "item:202").Quantity);
        fixture.Gather(1, new NetworkGatheringItem(101, 99)); fixture.Failed(2); await fixture.Ready();
        Assert.Equal(2, receipts.Count);
        fixture.Gather(3, new NetworkGatheringItem(101, 1)); await fixture.Ready();
        Assert.Equal(3, receipts.Count); Assert.Equal(1, receipts[2].Quantity);
        await fixture.Service.DisposeAsync();
        await using var restarted = new LifeskillTrackerService(fixture.Settings, fixture.Source,
            _ => session.Character, () => null, () => new Lease(() => { }), id => id, id => $"Example item {id}",
            new(fixture.Root), fixture.Time);
        var recoveredReceipts = 0; restarted.DropAccepted += _ => recoveredReceipts++;
        await restarted.DrainAsync();
        Assert.Single(restarted.Snapshot.Sessions); Assert.Equal(0, recoveredReceipts); Assert.Equal(3, receipts.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedGatheringPersistenceDoesNotPublishDropReceipts(bool checkpointFailure)
    {
        await using var fixture = new Fixture(checkpointFailure: checkpointFailure,
            append: checkpointFailure ? null : (_, _) => throw new IOException("Synthetic receipt append failure."));
        await fixture.Ready(); var receipts = 0; fixture.Service.DropAccepted += _ => receipts++;
        fixture.Gather(1, new NetworkGatheringItem(210230006, 1)); await fixture.Ready();
        Assert.Equal(0, receipts); Assert.True(fixture.Service.StatusIsError);
    }

    [Fact]
    public async Task RareDropAlertsSubscribeToTheExistingGatheringServiceAndUnsubscribeOnDispose()
    {
        await using var fixture = new Fixture(); await fixture.Ready();
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No playback"), fixture.Time, lifeskillSource: fixture.Service);
        alerts.SetPresentationVisible(true); var notices = 0; alerts.Changed += () => notices++;
        fixture.Gather(1, new NetworkGatheringItem(210230006, 2)); await fixture.Ready();
        var notice = Assert.Single(alerts.CurrentNotices);
        Assert.Equal(2, notice.Quantity); Assert.Equal(LootItemRarity.Unique, notice.Rarity);
        Assert.NotNull(notice.IconPath); Assert.Equal(1, notices);
        fixture.Gather(1, new NetworkGatheringItem(210230006, 2)); fixture.Failed(2);
        fixture.Gather(3, new NetworkGatheringItem(101, 1)); await fixture.Ready();
        Assert.Single(alerts.CurrentNotices); Assert.Equal(1, notices);
        fixture.Gather(4, new NetworkGatheringItem(210230006, 1)); await fixture.Ready();
        Assert.Equal(2, alerts.CurrentNotices.Count); Assert.Equal(2, notices);
        alerts.Dispose(); fixture.Gather(5, new NetworkGatheringItem(210230006, 1)); await fixture.Ready();
        Assert.Empty(alerts.CurrentNotices); Assert.Equal(2, notices);
        Assert.Equal(1, fixture.LeasesAcquired);
    }

    [Fact]
    public async Task SameTargetWithDistinctConfirmedSequencesCountsAsTwoHarvests()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        fixture.Gather(2, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, session.HarvestCount);
        Assert.Equal(4, session.ItemCount);
        Assert.Empty(session.ResourceNodes); // A repeated target key is not a catalogue-node association.
    }

    [Fact]
    public async Task IdleDeadlineClosesAndTheNextHarvestStartsANewSessionWithNoTrailingTimeOrRoute()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Ready();
        fixture.Gather(2, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        var last = fixture.Time.GetUtcNow();
        fixture.X = 40;
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(90));
        await fixture.Ready();
        var completed = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), completed.Elapsed);
        Assert.Equal(last, completed.EndedAt);
        Assert.All(completed.RouteSegments.SelectMany(segment => segment.Points), point =>
            Assert.True(point.Elapsed <= TimeSpan.FromSeconds(10)));
        fixture.Gather(3, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        Assert.Equal(2, fixture.Service.Snapshot.Sessions.Length);
        Assert.NotEqual(completed.Id, fixture.Service.Snapshot.Sessions[0].Id);
        Assert.Equal(1, fixture.Service.Snapshot.Sessions[0].HarvestCount);
        Assert.Equal(2, fixture.Service.Snapshot.Sessions[0].ItemCount);
    }

    [Fact]
    public async Task HarvestChecksTheDeadlineEvenWithoutATimerCallback()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromMinutes(2), fireTimer: false);
        fixture.Gather(2, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        Assert.Equal(2, fixture.Service.Snapshot.Sessions.Length);
        Assert.Equal(LootTrackerSessionStatus.Completed, fixture.Service.Snapshot.Sessions[1].Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FarmDayResetSeparatesAttemptsAtSevenUtcWithoutAddingIdleTail(bool fireTimer)
    {
        await using var fixture = new Fixture();
        fixture.Time.Advance(TimeSpan.FromHours(18) + TimeSpan.FromMinutes(59)); await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2)); await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(30)); fixture.Failed(2); await fixture.Ready();
        var previous = Assert.Single(fixture.Service.Snapshot.Sessions);
        fixture.Time.Advance(TimeSpan.FromSeconds(30), fireTimer); await fixture.Ready();
        if (fireTimer)
        {
            var closed = Assert.Single(fixture.Service.Snapshot.Sessions);
            Assert.Equal(LootTrackerSessionStatus.Completed, closed.Status);
            Assert.Equal(previous.StartedAt.AddSeconds(30), closed.EndedAt);
            Assert.Equal(TimeSpan.FromSeconds(30), closed.Elapsed);
            Assert.Equal(1, closed.FailedHarvestCount); Assert.Equal(2, closed.ItemCount);
        }
        fixture.Failed(3); await fixture.Ready();
        var sessions = fixture.Service.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length); Assert.NotEqual(sessions[0].FarmDay, sessions[1].FarmDay);
        Assert.Equal(new DateOnly(2026, 10, 26), sessions[0].FarmDay);
        Assert.Equal(new DateOnly(2026, 10, 25), sessions[1].FarmDay);
        Assert.Equal(LootTrackerSessionStatus.Live, sessions[0].Status);
        Assert.Equal(0, sessions[0].ItemCount); Assert.Equal(1, sessions[0].FailedHarvestCount);
        Assert.Equal(LootTrackerSessionStatus.Completed, sessions[1].Status);
        await fixture.Service.DisposeAsync();
        var recovered = new LifeskillSessionStore(fixture.Root).Read();
        Assert.Empty(recovered.Issues); Assert.Equal(2, recovered.Sessions.Count);
        Assert.Equal(sessions[0].FarmDay, recovered.Sessions[0].FarmDay);
    }

    [Fact]
    public async Task MonotoneTimeoutDoesNotFollowAWallClockAdjustment()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Time.AdjustWallClock(TimeSpan.FromHours(-2));
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, session.Status);
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Null(LootTrackerPresentation.PerHour(session.ItemCount, session.Elapsed));
    }

    [Fact]
    public async Task ChangedIdleSettingIsAppliedWithoutAnotherHarvest()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Settings.SetLootTrackerIdleTimeoutMinutes(1);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Ready();
        Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(fixture.Service.Snapshot.Sessions).Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrUnverifiedSourcesCannotStartSessionsOrTheMapRecorder(bool enabled, bool verified)
    {
        await using var fixture = new Fixture(enabled, verified);
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        Assert.False(fixture.Service.SourceAvailable);
        Assert.Equal(0, fixture.LeasesAcquired);
    }

    [Fact]
    public async Task UnboundCharacterAndForeignOwnContextDoNotStartSessions()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Bound = false;
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        fixture.Bound = true;
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 2, fixture.Source.OwnCharacter! with { Generation = 99 }, 123, [new NetworkGatheringItem(101, 1)]));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
    }

    [Fact]
    public async Task CharacterSwitchClosesOldSessionAndStartScoresRemainFrozen()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Source.ChangeOwn(new(new(2, 1, 20, "Example character B"), 2) { CombatScore = 2000, GearScore = 300 });
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(202, 2));
        await fixture.Ready();
        var sessions = fixture.Service.Snapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal("Example character B", sessions[0].Character?.Name);
        Assert.Equal(2000, sessions[0].Character?.CombatScore);
        Assert.Equal(300, sessions[0].Character?.GearScore);
        Assert.Equal("Example character A", sessions[1].Character?.Name);
        Assert.Equal(1000, sessions[1].Character?.CombatScore);
        Assert.Equal(200, sessions[1].Character?.GearScore);
        Assert.Equal(LootTrackerSessionStatus.Completed, sessions[1].Status);
    }

    [Fact]
    public async Task SameCharacterLoadKeepsSessionAndScoresButBreaksItsRoute()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Source.ChangeOwn(null);
        fixture.MapGeneration++;
        fixture.Time.Advance(TimeSpan.FromSeconds(5), fireTimer: false);
        fixture.Source.ChangeOwn(new(new(1, 1, 11, "Example character A"), 2) { CombatScore = 9000, GearScore = 900 });
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, session.HarvestCount);
        Assert.Equal(1000, session.Character?.CombatScore);
        Assert.Equal(200, session.Character?.GearScore);
        Assert.Equal(2, session.GatherVisits.Select(visit => visit.Section).Distinct().Count());
        Assert.True(session.CoverageLimited);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(1)]
    public async Task StaleOrFutureHarvestPositionsDoNotInventNodeVisitsOrRoutes(int seconds)
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.PositionAge = TimeSpan.FromSeconds(seconds);
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, session.ItemCount);
        Assert.Empty(session.RouteSegments);
        Assert.Empty(session.ResourceNodes);
        Assert.True(session.CoverageLimited);
    }

    [Fact]
    public async Task RecorderStoresOnlyCompletedAttemptPositionsAndNoPeriodicWalkingSamples()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        fixture.Gather(2, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, session.GatherVisits.Length);
        Assert.Single(session.FarmRoutePoints);
        Assert.Empty(session.RouteSegments);
        var records = File.ReadAllLines(Path.Combine(fixture.Root, session.Id, "events-0001.jsonl"));
        Assert.Equal(3, records.Length); // start plus two atomic attempts; timer records no position.
        Assert.All(records, record => Assert.DoesNotContain("\"Kind\":2", record));
    }

    [Fact]
    public async Task NewSourceWarningsLimitCoverageButKeepValidLootAndDoNotExtendTheIdleDeadline()
    {
        const string warning = "Synthetic gathering coverage warning.";
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        var id = Assert.Single(fixture.Service.Snapshot.Sessions).Id;

        fixture.Time.Advance(TimeSpan.FromSeconds(30), fireTimer: false);
        fixture.Source.Warn(warning);
        await fixture.Ready();
        Assert.True(Assert.Single(fixture.Service.Snapshot.Sessions).CoverageLimited);
        Assert.True(fixture.Service.StatusIsError);
        Assert.Equal(warning, fixture.Service.Status);
        Assert.True(fixture.Service.SourceAvailable);
        Assert.Equal(1, fixture.LeasesAcquired);
        Assert.Equal(0, fixture.LeasesReleased);

        fixture.Time.Advance(TimeSpan.FromSeconds(10), fireTimer: false);
        fixture.Gather(2, new NetworkGatheringItem(101, 3));
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(10), fireTimer: false);
        fixture.Gather(3, new NetworkGatheringItem(101, 4));
        await fixture.Ready();
        var continued = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(id, continued.Id);
        Assert.Equal(LootTrackerSessionStatus.Live, continued.Status);
        Assert.Equal(3, continued.HarvestCount);
        Assert.Equal(9, continued.ItemCount);
        Assert.Equal(2, continued.GatherVisits.Select(visit => visit.Section).Distinct().Count()); // The same warning revision cannot break every later harvest.
        Assert.Equal(warning, fixture.Service.Status);
        Assert.True(fixture.Service.StatusIsError);
        var lastLoot = continued.LastLootAt;

        fixture.Time.Advance(TimeSpan.FromSeconds(110), fireTimer: false);
        fixture.Source.Warn(warning);
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        await fixture.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(fixture.Service.Snapshot.Sessions).Status);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Ready();
        var completed = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(lastLoot, completed.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(50), completed.Elapsed);
        Assert.Equal(9, completed.ItemCount);
        Assert.True(completed.CoverageLimited);

        var restored = new LifeskillSessionStore(fixture.Root).Read();
        Assert.Empty(restored.Issues);
        var saved = Assert.Single(restored.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, saved.Status);
        Assert.True(saved.CoverageLimited);
        Assert.Equal(completed.ItemCount, saved.ItemCount);
        Assert.Equal(completed.HarvestCount, saved.HarvestCount);
    }

    [Fact]
    public async Task ANewSessionCanCountAfterAnOlderWarningWithoutInheritingItsCoverageGap()
    {
        const string warning = "Synthetic earlier gathering warning.";
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        var oldId = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        fixture.Time.Advance(TimeSpan.FromSeconds(10), fireTimer: false);
        fixture.Source.Warn(warning);
        await fixture.Ready();
        fixture.Time.Advance(TimeSpan.FromSeconds(110));
        await fixture.Ready();
        Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(fixture.Service.Snapshot.Sessions).Status);

        fixture.Time.Advance(TimeSpan.FromSeconds(1), fireTimer: false);
        fixture.Gather(2, new NetworkGatheringItem(202, 4));
        await fixture.Ready();
        var session = fixture.Service.Snapshot.Sessions[0];
        Assert.NotEqual(oldId, session.Id);
        Assert.Equal(2, fixture.Service.Snapshot.Sessions.Length);
        Assert.Equal(LootTrackerSessionStatus.Live, session.Status);
        Assert.False(session.CoverageLimited);
        Assert.Equal(4, session.ItemCount);
        Assert.Equal(warning, fixture.Service.Status);
        Assert.True(fixture.Service.StatusIsError);
        Assert.True(fixture.Service.SourceAvailable);

        fixture.Time.Advance(TimeSpan.FromSeconds(10), fireTimer: false);
        fixture.Gather(3, new NetworkGatheringItem(202, 2));
        await fixture.Ready();
        var continued = fixture.Service.Snapshot.Sessions[0];
        Assert.Equal(session.Id, continued.Id);
        Assert.Equal(2, continued.HarvestCount);
        Assert.Equal(6, continued.ItemCount);
        Assert.False(continued.CoverageLimited);
        Assert.Single(continued.GatherVisits.Select(visit => visit.Section).Distinct());
        Assert.Equal(warning, fixture.Service.Status);

        fixture.Source.ClearWarning();
        await fixture.Ready();
        Assert.False(fixture.Service.StatusIsError);
        Assert.NotEqual(warning, fixture.Service.Status);
        Assert.True(fixture.Service.SourceAvailable);
        Assert.Equal(6, fixture.Service.Snapshot.Sessions[0].ItemCount);
        Assert.False(fixture.Service.Snapshot.Sessions[0].CoverageLimited);
    }

    [Fact]
    public async Task DisablingFinishesAtLastHarvestAndReleasesTheLease()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.Settings.SetLifeskillTrackingEnabled(false);
        await fixture.Ready();
        Assert.Equal(1, fixture.LeasesAcquired);
        Assert.Equal(1, fixture.LeasesReleased);
        Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(fixture.Service.Snapshot.Sessions).Status);
    }

    [Fact]
    public async Task DisposalDrainsQueuedHarvestsAndRecoveryNeverResumesThemAsLive()
    {
        var fixture = new Fixture();
        try
        {
            await fixture.Ready();
            fixture.Gather(1, new NetworkGatheringItem(101, 2));
            fixture.Gather(2, new NetworkGatheringItem(101, 3));
            await fixture.Service.DisposeAsync();
            var restored = new LifeskillSessionStore(fixture.Root).Read();
            var session = Assert.Single(restored.Sessions);
            Assert.Equal(5, session.ItemCount);
            Assert.Equal(2, session.HarvestCount);
            Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
            Assert.Equal(1, fixture.LeasesReleased);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task CheckpointFailureIsVisibleAndStopsAcceptingFurtherLoot()
    {
        await using var fixture = new Fixture(checkpointFailure: true);
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        Assert.True(fixture.Service.StatusIsError);
        Assert.False(fixture.Service.SourceAvailable);
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, session.ItemCount);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
        fixture.Gather(2, new NetworkGatheringItem(101, 3));
        await fixture.Ready();
        Assert.Equal(2, Assert.Single(fixture.Service.Snapshot.Sessions).ItemCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHarvestAppendCannotPublishMoreLootThanTheRecoverableJournal(bool invalidDataFailure)
    {
        var writes = 0;
        await using var fixture = new Fixture(append: (path, text) =>
        {
            if (++writes == 3)
            {
                if (invalidDataFailure) throw new InvalidDataException("Synthetic journal limit failure.");
                throw new IOException("Synthetic append failure.");
            }
            File.AppendAllText(path, text);
        });
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        fixture.Gather(2, new NetworkGatheringItem(101, 7));
        await fixture.Ready();
        Assert.True(fixture.Service.StatusIsError);
        var live = Assert.Single(fixture.Service.Snapshot.Sessions);
        var saved = Assert.Single(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(2, live.ItemCount);
        Assert.Equal(live.ItemCount, saved.ItemCount);
        Assert.Equal(live.HarvestCount, saved.HarvestCount);
    }

    [Fact]
    public async Task HarvestPositionIsCopiedBeforeDelayedJournalWorkCanObserveANewerFrame()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        await using var fixture = new Fixture(append: (path, text) =>
        {
            if (++writes == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Synthetic append barrier timed out.");
            }
            File.AppendAllText(path, text);
        });
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        fixture.X = 999;
        fixture.Time.Advance(TimeSpan.FromSeconds(1), fireTimer: false);
        release.Set();
        await fixture.Ready();
        var first = Assert.Single(Assert.Single(fixture.Service.Snapshot.Sessions).GatherVisits);
        Assert.Equal(10, first.OriginalX);
        Assert.Equal(first.OriginalX, first.X);
    }

    [Fact]
    public async Task InvalidOrOverflowingAmountsDoNotBecomeHarvests()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 0));
        fixture.Gather(2, new NetworkGatheringItem(-1, 2));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        fixture.Gather(3, new NetworkGatheringItem(101, long.MaxValue), new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        Assert.True(fixture.Service.StatusIsError);
    }

    [Fact]
    public async Task SuccessAndFailureFreezeNearbyNodeAndOriginalCoordinatesInTheSameJournalRecord()
    {
        var catalog = new ResourceNodeCatalog([new("node-a", "sample-map", 7001, "resource-ore",
            "Example node", "Beispielnode", 15, 20, 15, 20)]);
        await using var fixture = new Fixture(resolver: new(catalog, _ => 1, _ => 8192));
        await fixture.Ready();
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 1, fixture.Source.OwnCharacter!, 123,
            [new NetworkGatheringItem(101, 2)]) { Object = new(7001, 555, 777) });
        fixture.X = 17;
        fixture.Source.Emit(new(fixture.Time.GetUtcNow(), 2, fixture.Source.OwnCharacter!, 123, [])
            { Outcome = NetworkGatheringOutcome.Failed, Object = new(7001, 666, 888) });
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(1, session.FarmRouteVersion);
        Assert.Equal(2, session.GatherVisits.Length);
        Assert.Equal(10, session.GatherVisits[0].OriginalX);
        Assert.Equal(17, session.GatherVisits[1].OriginalX);
        Assert.All(session.GatherVisits, visit => { Assert.Equal("node-a", visit.CatalogNodeId); Assert.Equal(15, visit.X); });
        var stop = Assert.Single(session.FarmRoutePoints);
        Assert.Equal(2, stop.VisitCount);
        Assert.Equal(1, stop.SuccessCount);
        Assert.Equal(1, stop.FailureCount);
        Assert.Empty(session.RouteSegments);
        var journal = File.ReadAllLines(Path.Combine(fixture.Root, session.Id, "events-0001.jsonl"));
        Assert.Equal(3, journal.Length);
        foreach (var forbidden in new[] { "TargetKey", "OriginSpawnKey", "SpawnUniqueKey", "555", "777" })
            Assert.DoesNotContain(forbidden, string.Join("\n", journal));
        var restored = Assert.Single(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(session.GatherVisits.ToArray(), restored.GatherVisits.ToArray());
        Assert.Equal(session.FarmRoutePoints.ToArray(), restored.FarmRoutePoints.ToArray());
    }

    [Fact]
    public async Task MissingPositionBreaksFarmSectionsWhileKeepingAttemptAndLootCounts()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.PositionAge = TimeSpan.FromSeconds(-3);
        fixture.Failed(2);
        await fixture.Ready();
        fixture.PositionAge = TimeSpan.Zero;
        fixture.X = 100;
        fixture.Failed(3);
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(1, session.HarvestCount);
        Assert.Equal(2, session.FailedHarvestCount);
        Assert.Equal(2, session.GatherVisits.Length);
        Assert.Equal(new[] { 0, 1 }, session.GatherVisits.Select(visit => visit.Section));
        Assert.True(session.CoverageLimited);
        var restored = Assert.Single(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(session.GatherVisits.ToArray(), restored.GatherVisits.ToArray());
    }

    [Fact]
    public async Task MapAndBindingAuthorityChangesBreakSectionsWithoutCreatingSessions()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        fixture.MapId = "other-map";
        fixture.Failed(2);
        await fixture.Ready();
        fixture.BindingOverride = 99;
        fixture.Gather(3, new NetworkGatheringItem(101, 1));
        await fixture.Ready();
        fixture.BindingOverride = null;
        fixture.Failed(4);
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(new[] { 0, 1, 2 }, session.GatherVisits.Select(visit => visit.Section));
        Assert.Equal(1, session.ItemCount);
        Assert.Equal(3, session.FailedHarvestCount);
    }

    [Fact]
    public async Task OutOfMapVisualPositionDropsOnlyRouteEvidenceAndRecordingContinues()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.X = 9000;
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        fixture.Failed(2);
        await fixture.Ready();
        var first = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(2, first.ItemCount);
        Assert.Equal(1, first.FailedHarvestCount);
        Assert.Empty(first.GatherVisits);
        Assert.True(first.CoverageLimited);
        Assert.True(fixture.Service.SourceAvailable);
        Assert.False(fixture.Service.StatusIsError);
        fixture.X = 20;
        fixture.Gather(3, new NetworkGatheringItem(101, 3));
        await fixture.Ready();
        var continued = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(5, continued.ItemCount);
        Assert.Single(continued.GatherVisits);
        Assert.Equal(first.Id, continued.Id);
    }

    [Fact]
    public async Task DeleteActiveSessionRetainsDuplicateGuardAndNextNewAttemptStartsFresh()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        var id = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        await fixture.Service.DeleteSessionAsync(id);
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, id)));
        Assert.Empty(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.True(fixture.Service.SourceAvailable);
        fixture.Gather(1, new NetworkGatheringItem(101, 2));
        await fixture.Ready();
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        fixture.Failed(2);
        await fixture.Ready();
        var next = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.NotEqual(id, next.Id);
        Assert.Equal(1, next.FailedHarvestCount);
        Assert.Equal(0, next.ItemCount);
    }

    [Fact]
    public async Task DeleteCompletedHistoryDoesNotRemoveAnActiveSession()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        var old = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        fixture.Gather(2, new NetworkGatheringItem(101, 3));
        await fixture.Ready();
        var active = fixture.Service.Snapshot.Sessions[0].Id;
        await fixture.Service.DeleteSessionAsync(old);
        Assert.Equal(active, Assert.Single(fixture.Service.Snapshot.Sessions).Id);
        Assert.Equal(active, Assert.Single(new LifeskillSessionStore(fixture.Root).Read().Sessions).Id);
    }

    [Fact]
    public async Task FailedRenameLeavesActiveSessionVisibleAndContinuesRecording()
    {
        await using var fixture = new Fixture(move: (_, _) => throw new IOException("Synthetic rename failure."));
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        var id = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.DeleteSessionAsync(id));
        Assert.Equal(id, Assert.Single(fixture.Service.Snapshot.Sessions).Id);
        fixture.Gather(2, new NetworkGatheringItem(101, 5));
        await fixture.Ready();
        var session = Assert.Single(fixture.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id);
        Assert.Equal(5, session.ItemCount);
        Assert.Equal(1, session.FailedHarvestCount);
        Assert.True(fixture.Service.SourceAvailable);
    }

    [Fact]
    public async Task CommittedDeleteReportsDeferredCleanupWithoutResurrectingSession()
    {
        await using var fixture = new Fixture(delete: _ => throw new IOException("Synthetic cleanup failure."));
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        var id = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        await fixture.Service.DeleteSessionAsync(id);
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        Assert.Equal("Session gelöscht; Dateibereinigung ausstehend.", fixture.Service.Status);
        Assert.False(fixture.Service.StatusIsError);
        Assert.True(fixture.Service.SourceAvailable);
        Assert.Single(Directory.GetDirectories(fixture.Root, ".deleted-*"));
        Assert.Empty(new LifeskillSessionStore(fixture.Root).Read().Sessions);
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".deleted-*"));
    }

    [Fact]
    public async Task DeleteSerializesWithQueuedHarvestsAndRejectsInvalidOrDisposedRequests()
    {
        await using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Failed(1);
        await fixture.Ready();
        var id = Assert.Single(fixture.Service.Snapshot.Sessions).Id;
        fixture.Gather(2, new NetworkGatheringItem(101, 2));
        var deleting = fixture.Service.DeleteSessionAsync(id);
        fixture.Gather(3, new NetworkGatheringItem(101, 3));
        await deleting;
        await fixture.Ready();
        Assert.Equal(3, Assert.Single(fixture.Service.Snapshot.Sessions).ItemCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DeleteSessionAsync("../other"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.DeleteSessionAsync(Guid.NewGuid().ToString("N")));
        await fixture.Service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.DeleteSessionAsync(id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-lifeskill-" + Guid.NewGuid().ToString("N"));
        public SettingsService Settings { get; } = new();
        public FakeGatheringSource Source { get; } = new();
        public ManualTime Time { get; } = new();
        public LifeskillTrackerService Service { get; }
        public int LeasesAcquired, LeasesReleased;
        public long MapGeneration = 1;
        public double X = 10;
        public bool Bound = true;
        public TimeSpan PositionAge;
        public string MapId = "sample-map";
        public long? BindingOverride;

        public Fixture(bool enabled = true, bool verified = true, bool checkpointFailure = false,
            Action<string, string>? append = null, Action<string, string>? move = null, Action<string>? delete = null,
            LifeskillGatherPointResolver? resolver = null)
        {
            Settings.Current.LifeskillTrackingEnabled = enabled;
            Settings.Current.LootTrackerIdleTimeoutMinutes = 2;
            Source.GatheringVerified = verified;
            var store = checkpointFailure ? new LifeskillSessionStore(Root,
                (path, text) => File.AppendAllText(path, text), (_, _) => throw new IOException("Synthetic checkpoint failure."))
                : new(Root, append ?? ((path, text) => File.AppendAllText(path, text)),
                    (path, value) => JsonFile.Save(path, value), move, delete);
            Service = new(Settings, Source,
                own => Bound ? new(own.Identity.CharacterId == 1 ? "11111111111111111111111111111111" : "22222222222222222222222222222222",
                    own.Identity.CharacterId == 1 ? "Example character A" : "Example character B", own.CombatScore, own.GearScore) : null,
                () => new RecorderPositionSnapshot(new(MapId, X, 20, Time.GetUtcNow() + PositionAge)
                    { ContextGeneration = MapGeneration }, MapGeneration, 1, BindingOverride ?? Source.OwnCharacter?.Generation, Time.GetUtcNow(), 10, .1),
                () => { LeasesAcquired++; return new Lease(() => LeasesReleased++); },
                _ => "Sample place", id => $"Example item {id}", store, Time, resolver);
        }

        public void Failed(long sequence) => Source.Emit(new(Time.GetUtcNow(), sequence, Source.OwnCharacter!, 123, [])
            { Outcome = NetworkGatheringOutcome.Failed });
        public Task Ready() => Service.DrainAsync();
        public void Gather(long sequence, params NetworkGatheringItem[] items) => Source.Emit(new(Time.GetUtcNow(),
            sequence, Source.OwnCharacter!, 123, items.ToImmutableArray()));
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        }
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class FakeGatheringSource : INetworkGatheringSource
    {
        public bool Running { get; set; } = true;
        public bool GatheringVerified { get; set; } = true;
        public string GatheringStatus => "Synthetic source";
        public string? GatheringWarning { get; private set; }
        public long GatheringWarningVersion { get; private set; }
        public OwnCharacterContext? OwnCharacter { get; private set; } = new(new(1, 1, 10, "Example character A"), 1)
            { CombatScore = 1000, GearScore = 200 };
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkGathering>? GatheringCompleted;
        public void Warn(string warning) { GatheringWarning = warning; GatheringWarningVersion++; Changed?.Invoke(); }
        public void ClearWarning() { GatheringWarning = null; Changed?.Invoke(); }
        public void Emit(NetworkGathering gathering) => GatheringCompleted?.Invoke(gathering);
        public void ChangeOwn(OwnCharacterContext? own) { OwnCharacter = own; OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _stamp, _wallAdjustment;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)
            .AddTicks(GetTimestamp() + Interlocked.Read(ref _wallAdjustment));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration, bool fireTimer = true)
        {
            Interlocked.Add(ref _stamp, duration.Ticks);
            if (fireTimer) foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        public void AdjustWallClock(TimeSpan by) => Interlocked.Add(ref _wallAdjustment, by.Ticks);
        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTime _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _due, _period;
            private bool _disposed;
            public ManualTimer(ManualTime clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            { _clock = clock; _callback = callback; _state = state; Change(due, period); }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock.GetTimestamp() + dueTime.Ticks;
                _period = period.Ticks;
                return !_disposed;
            }
            public void Fire()
            {
                if (_disposed || _clock.GetTimestamp() < _due) return;
                _due = _period < 0 ? long.MaxValue : _clock.GetTimestamp() + _period;
                _callback(_state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
