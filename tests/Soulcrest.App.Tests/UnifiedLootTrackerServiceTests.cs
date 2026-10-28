using System.Collections.Immutable;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task PveAndHarvestShareOneSessionClockCanonicalItemAndAtomicOriginFacts()
    {
        await using var f = new Fixture(); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Drop(7, 2, 1); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        f.Source.Harvest(1, new NetworkGatheringItem(7, 3), new NetworkGatheringItem(8, 4)); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        f.Source.Kina(25, 1); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(LootTrackerSessionKind.Mixed, session.Kind);
        Assert.Equal(1, session.UnifiedVersion); Assert.Equal(TimeSpan.FromMinutes(1), session.Elapsed);
        Assert.Equal(9, session.ItemCount); Assert.Equal(1, session.HarvestCount); Assert.Equal(25, session.KinaUnbound);
        Assert.Equal(5, Assert.Single(session.Loot, row => row.Id == "Item:7").Quantity);
        Assert.Equal(4, Assert.Single(session.Loot, row => row.Id == "Item:8").Quantity);
        var harvest = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
        Assert.Equal(LootTrackerActivityOrigin.Lifeskill, harvest.Origin);
        Assert.Equal(2, harvest.Loot.Length);
        Assert.Equal(LootTrackerActivityOrigin.Pve, Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.Drop).Origin);
        Assert.Equal(3, f.Accepted.Count); Assert.Single(f.Starts);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(9, recovered.ItemCount); Assert.Equal(25, recovered.KinaUnbound);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, recovered.Status);
        Assert.Equal(9, recovered.TimelineEvents.SelectMany(row => row.Loot).Sum(row => row.Quantity));
    }

    [Fact]
    public async Task FirstHarvestIsDurableBeforeAutoShowAndItsFirstRareNoticeIsNotLost()
    {
        await using var f = new Fixture(); await f.Ready();
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No audio in regression tests"), f.Clock,
            unifiedSource: f.Service);
        var order = new List<string>();
        f.Service.SessionStarted += id =>
        {
            var stored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
            Assert.Equal(id, stored.Id); Assert.Equal(1, stored.ItemCount);
            var current = Assert.Single(f.Service.Snapshot.Sessions);
            Assert.Equal(id, current.Id); Assert.Equal(LootTrackerSessionStatus.Live, current.Status);
            Assert.Null(current.EndedAt); Assert.Equal(1, current.ItemCount);
            var overlay = Assert.IsType<LootTrackerSession>(f.Service.OverlayState.Session);
            Assert.Equal(id, overlay.Id); Assert.Equal(1, overlay.ItemCount);
            Assert.Contains(overlay.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
            order.Add("started"); alerts.SetPresentationVisible(true);
        };
        f.Service.DropAccepted += _ => order.Add("drop");
        f.Source.Harvest(1, new NetworkGatheringItem(210230006, 1)); await f.Ready();
        Assert.Equal(new[] { "started", "drop" }, order);
        Assert.Single(alerts.CurrentNotices);
        Assert.Single(f.Accepted);
        _ = new UnifiedLootSessionStore(f.SessionRoot).Read(); await f.Ready();
        Assert.Single(alerts.CurrentNotices); Assert.Single(f.Accepted); Assert.Single(f.Starts);
    }

    [Fact]
    public async Task AFailedAttemptStartsAndExtendsTheCommonOutsideTimeoutButWalkingDoesNot()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Failed(1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        Assert.Empty(f.Accepted);
        f.Clock.Advance(TimeSpan.FromSeconds(100)); f.Source.Failed(2); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(100)); await f.Ready();
        Assert.Equal(id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(f.Service.Snapshot.Sessions).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        var finished = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, finished.Status);
        Assert.Equal(2, finished.FailedHarvestCount); Assert.Equal(TimeSpan.FromSeconds(100), finished.Elapsed);
    }

    [Fact]
    public async Task ChannelWatermarksAreIndependentAndDisabledEchoesCannotReturnAfterReenable()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 2, 1); f.Source.Drop(7, 2, 1);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 3)); f.Source.Harvest(1, new NetworkGatheringItem(7, 3));
        f.Source.Kina(10, 1); f.Source.Kina(10, 1); await f.Ready();
        Assert.Equal(5, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
        Assert.Equal(10, Assert.Single(f.Service.Snapshot.Sessions).KinaUnbound);
        f.Settings.Current.LifeskillTrackingEnabled = false;
        f.Source.Harvest(2, new NetworkGatheringItem(7, 100));
        f.Settings.Current.LifeskillTrackingEnabled = true;
        await f.Ready(); f.Source.Harvest(2, new NetworkGatheringItem(7, 100)); await f.Ready();
        Assert.Equal(5, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
    }

    [Theory]
    [InlineData(1_000L, 1L)]
    [InlineData(1L, 1_000L)]
    public async Task MonsterAndAbyssKinaKeepIndependentWatermarksAcrossEntryAndExit(long monsterSequence, long abyssSequence)
    {
        await using var f = new Fixture(); await f.Ready();
        var own = f.Source.OwnCharacter;
        f.Source.Kina(10, monsterSequence); f.Source.Kina(10, monsterSequence); await f.Ready();
        var first = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(10, first.KinaUnbound);

        f.SetWorld(abyss: true); await f.Ready();
        f.Source.Kina(20, abyssSequence); f.Source.Kina(20, abyssSequence); await f.Ready();
        var abyss = Assert.Single(f.Service.Snapshot.Sessions, row => row.Kind == LootTrackerSessionKind.Abyss);
        Assert.Equal(20, abyss.KinaUnbound);
        Assert.Single(abyss.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaUnbound);

        f.SetWorld(abyss: false); await f.Ready();
        f.Source.Kina(10, monsterSequence); await f.Ready();
        Assert.DoesNotContain(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        f.Source.Kina(30, monsterSequence + 1); f.Source.Kina(30, monsterSequence + 1); await f.Ready();
        var after = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.Equal(LootTrackerSessionKind.Mixed, after.Kind); Assert.Equal(30, after.KinaUnbound);
        Assert.NotEqual(first.Id, after.Id); Assert.Equal(3, f.Service.Snapshot.Sessions.Length);
        Assert.Same(own, f.Source.OwnCharacter);
        Assert.Equal(60, f.Service.Snapshot.Sessions.Sum(row => row.KinaUnbound ?? 0));
        Assert.All(f.Service.Snapshot.Sessions, row =>
            Assert.Single(row.TimelineEvents, fact => fact.Kind == LootTrackerTimelineEventKind.KinaUnbound));
        Assert.Equal(60, new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions.Sum(row => row.KinaUnbound ?? 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutsideKinaAvailabilityUsesTheLiveDailyChannelInsteadOfTheWallet(bool dailyReady)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.WalletBalance = dailyReady ? null : 1_000;
        // The live counter is already applied when its gain callback runs; Changed may follow later.
        if (dailyReady) f.Source.Daily(0, notify: false);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        if (dailyReady) Assert.Equal(0, session.KinaUnbound);
        else Assert.Null(session.KinaUnbound);
        var availability = Assert.Single(session.TimelineEvents,
            row => row.Kind == LootTrackerTimelineEventKind.Availability && row.Metric == LootTrackerTimelineMetric.KinaUnbound);
        Assert.Equal(dailyReady, availability.Available);
        Assert.DoesNotContain(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaUnbound);
        Assert.Equal(2, session.ItemCount);
    }

    [Fact]
    public async Task AnUnwritableDailyCacheKeepsTheConfirmedTotalAndLaterLootIsStillRecorded()
    {
        await using var f = new Fixture(); await f.Ready();
        File.WriteAllText(Path.Combine(f.Root, "daily.json"), "{ damaged");
        f.Source.Daily(400_000); await f.Ready();
        Assert.True(f.Service.OverlayState.DailyConfirmed);
        Assert.Equal(400_000, f.Service.OverlayState.CachedDaily!.Total);
        f.Source.Drop(7, 1, 1); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Live, session.Status);
        Assert.Single(f.Accepted);
    }

    [Fact]
    public async Task KinaCapMarksTheSameMixedSessionWithoutClosingIt()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready(); var first = Assert.Single(f.Service.Snapshot.Sessions);
        f.Source.Daily(1_000_001); f.Source.Kina(9, 1); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, current.Id); Assert.Equal(first.StartedAt, current.StartedAt);
        Assert.Equal(LootTrackerSessionMode.LimitedKina, current.Mode); Assert.Equal(9, current.KinaUnbound);
        Assert.Equal(LootTrackerSessionStatus.Live, current.Status);
    }

    [Fact]
    public async Task AnOpenWorldSessionFreezesTheNearestAreaOfItsFirstLootPosition()
    {
        var resolved = 0;
        await using var f = new Fixture(locationName: position =>
        {
            resolved++;
            return new(position.Position!.X < 50 ? "Immortal Isle" : "Other area", true);
        });
        await f.Ready();
        var x = 10d;
        f.Position = () => new(new("test-map", x, 20, f.Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        f.Source.Drop(7, 1, 1); await f.Ready();
        x = 90; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Drop(7, 1, 2); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal("Immortal Isle", session.Location);
        Assert.True(session.LocationApproximate);
        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task AQualifiedDailyCapWithoutIncomeOnlyMarksTheDurableSessionAndDoesNotCommitWalkingOrExtendTimeout()
    {
        await using var f = new Fixture(); await f.Ready();
        var x = 10d;
        f.Position = () => new(new("test-map", x, 20, f.Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 3)); await f.Ready();
        var original = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Single(original.RouteSegments.SelectMany(row => row.Points));
        x = 20; f.Clock.Advance(TimeSpan.FromSeconds(100)); await f.Ready();
        Assert.Equal(2, Assert.Single(f.Service.Snapshot.Sessions).RouteSegments.Sum(row => row.Points.Length));
        var appends = f.Journal.Count;

        f.Source.Daily(1_000_000); await f.Ready();
        var marked = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(original.Id, marked.Id); Assert.Equal(LootTrackerSessionMode.LimitedKina, marked.Mode);
        Assert.Equal(original.LastLootAt, marked.LastLootAt); Assert.Equal(3, marked.ItemCount);
        Assert.Equal(original.TimelineEvents, marked.TimelineEvents); Assert.Equal(appends + 1, f.Journal.Count);
        Assert.Equal(2, marked.RouteSegments.Sum(row => row.Points.Length));
        var durable = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(LootTrackerSessionMode.LimitedKina, durable.Mode); Assert.Equal(original.Elapsed, durable.Elapsed);
        Assert.Equal(original.LastLootAt, durable.LastLootAt);
        Assert.Single(durable.RouteSegments.SelectMany(row => row.Points));
        Assert.Equal(3, durable.ItemCount); Assert.Null(durable.KinaUnbound);
        Assert.Single(f.Starts); Assert.Single(f.Accepted);
        f.Source.Notify(); f.Source.Notify(); await f.Ready();
        Assert.Equal(appends + 1, f.Journal.Count); // Repeated absolute facts do not append another mode change.

        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        var completed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(original.Elapsed, completed.Elapsed); Assert.Equal(original.LastLootAt, completed.EndedAt);
        Assert.Equal(LootTrackerSessionMode.LimitedKina, completed.Mode);
        Assert.Single(completed.RouteSegments.SelectMany(row => row.Points));
    }

    [Fact]
    public async Task SevenUtcResetEndsOutsideButNeverSplitsTheAbyssResidence()
    {
        await using (var outside = new Fixture(start: new(2026, 10, 27, 6, 59, 30, TimeSpan.Zero)))
        {
            await outside.Ready(); outside.Source.Drop(7, 1, 1); await outside.Ready();
            outside.Clock.Advance(TimeSpan.FromSeconds(31)); await outside.Ready();
            Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(outside.Service.Snapshot.Sessions).Status);
            outside.Source.Drop(7, 1, 2); await outside.Ready(); Assert.Equal(2, outside.Service.Snapshot.Sessions.Length);
        }
        await using var abyss = new Fixture(abyss: true, start: new(2026, 10, 27, 6, 59, 30, TimeSpan.Zero));
        await abyss.Ready(); var first = Assert.Single(abyss.Service.Snapshot.Sessions);
        abyss.Clock.Advance(TimeSpan.FromDays(2)); await abyss.Ready();
        var after = Assert.Single(abyss.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, after.Id); Assert.Equal(first.FarmDay, after.FarmDay);
        Assert.Equal(TimeSpan.FromDays(2), after.Elapsed); Assert.Equal(LootTrackerSessionMode.Normal, after.Mode);
    }

    [Fact]
    public async Task AbyssEntryAndExitSeparateContextsAndAbyssHarvestDoesNotRequireTheOutsideLifeToggle()
    {
        await using var f = new Fixture(); await f.Ready(); f.Source.Drop(7, 1, 1); await f.Ready();
        var outside = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.SetWorld(abyss: true); await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        var residence = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.Equal(LootTrackerSessionKind.Abyss, residence.Kind); Assert.NotEqual(outside, residence.Id);
        f.Settings.Current.LifeskillTrackingEnabled = false;
        f.Source.Harvest(1, new NetworkGatheringItem(610610013, 3)); f.Source.Points(12, 1); f.Source.Rank(-20, 1); f.Source.Kina(99, 1);
        await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Id == residence.Id);
        Assert.Equal(3, current.ItemCount); Assert.Equal(12, current.AbyssPoints);
        Assert.Equal(20, current.RankPointsLost); Assert.Equal(99, current.KinaUnbound);
        Assert.Equal(LootTrackerActivityOrigin.Lifeskill, Assert.Single(current.TimelineEvents,
            row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).Origin);
        Assert.All(current.TimelineEvents.Where(row => row.Kind != LootTrackerTimelineEventKind.HarvestSuccess),
            row => Assert.Equal(LootTrackerActivityOrigin.Abyss, row.Origin));
        f.SetWorld(abyss: false); await f.Ready();
        Assert.DoesNotContain(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        f.Source.Drop(7, 2, 2); await f.Ready();
        Assert.Equal(3, f.Service.Snapshot.Sessions.Length);
    }

    [Fact]
    public async Task TransitionSameCharacterRebindKeepsAbyssButStaleInterruptsAndHidesTheOverlay()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.WorldStatus = WorldContextStatus.Transitioning; f.Source.LoseOwn(); await f.Ready();
        Assert.Null(f.Service.OverlayState.Session);
        f.Clock.Advance(TimeSpan.FromSeconds(30)); await f.Ready();
        f.Source.Switch(1); f.WorldStatus = WorldContextStatus.Confirmed; f.WorldGeneration++; f.Source.Notify(); await f.Ready();
        Assert.Equal(id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.NotNull(f.Service.OverlayState.Session);
        f.WorldStatus = WorldContextStatus.Stale; f.Source.Notify(); await f.Ready();
        Assert.Equal(LootTrackerSessionStatus.Interrupted, Assert.Single(f.Service.Snapshot.Sessions).Status);
        Assert.Null(f.Service.OverlayState.Session); Assert.False(f.Service.OverlayState.TrackingAvailable);
    }

    [Fact]
    public async Task DeletingTheCurrentResidenceDoesNotImmediatelyCreateAnEmptyReplacementAndDoesNotDeleteAbsoluteCaches()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.Daily(100); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        await f.Service.DeleteSessionAsync(id); f.Clock.Advance(TimeSpan.FromMinutes(5)); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2)); f.Source.Points(20, 1); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Accepted);
        Assert.NotNull(f.Service.CachedDailyMonsterKina);
        f.SetWorld(false); await f.Ready(); f.SetWorld(true); await f.Ready();
        Assert.NotEqual(id, Assert.Single(f.Service.Snapshot.Sessions).Id);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HarvestInstanceFlagMustMatchTheConfirmedOutsideOrAbyssParent(bool abyss)
    {
        await using var f = new Fixture(abyss: abyss); await f.Ready();
        f.Source.InInstance = !abyss;
        f.Source.Harvest(1, new NetworkGatheringItem(7, 100)); await f.Ready();
        Assert.All(f.Service.Snapshot.Sessions, row => Assert.Empty(row.Loot)); Assert.Empty(f.Accepted);
        f.Source.InInstance = abyss;
        f.Source.Harvest(1, new NetworkGatheringItem(7, 100)); await f.Ready();
        Assert.All(f.Service.Snapshot.Sessions, row => Assert.Empty(row.Loot)); // A rejected receipt is not replayed.
        f.Source.Harvest(2, new NetworkGatheringItem(7, 2)); await f.Ready();
        Assert.Equal(2, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
    }

    [Theory]
    [InlineData("unbound")] [InlineData("stopped")] [InlineData("future-world")] [InlineData("wrong-binding")]
    [InlineData("instance")]
    public async Task UnqualifiedCurrentContextCannotCreateOutsideSessions(string variant)
    {
        await using var f = new Fixture(); await f.Ready();
        switch (variant)
        {
            case "unbound": f.Bound = false; break;
            case "stopped": f.Source.Running = false; break;
            case "future-world": f.WorldOffset = TimeSpan.FromMinutes(1); break;
            case "wrong-binding": f.BindingOffset = 1; break;
            case "instance": f.WorldKind = WorldContextKind.Instance; f.Category = "Party"; break;
        }
        f.Source.Notify(); f.Source.Drop(7, 1, 1); f.Source.Harvest(1, new NetworkGatheringItem(8, 1)); f.Source.Kina(10, 1); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Accepted); Assert.Null(f.Service.OverlayState.Session);
    }

    [Fact]
    public async Task AQueuedHarvestCannotRollANewerWorldBackAndNoNativeTargetOrOpponentIdentityEntersTheJournal()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.BeforeBind = () => { f.WorldStatus = WorldContextStatus.Stale; f.WorldGeneration++; f.Source.Notify(); };
        f.Source.Harvest(1, new NetworkGatheringItem(7, 1)); await f.Ready();
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).Loot);
        Assert.DoesNotContain(f.Journal, line => line.Contains("TargetKey", StringComparison.Ordinal)
            || line.Contains("OriginSpawnKey", StringComparison.Ordinal) || line.Contains("CharacterId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QualifiedCombatIsAnonymousAndNeverDerivesPointIncome()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.Combat(NetworkAbyssCombatKind.Kill, 1); f.Source.Combat(NetworkAbyssCombatKind.Kill, 1);
        f.Source.Combat(NetworkAbyssCombatKind.Death, 2); f.Source.Combat(NetworkAbyssCombatKind.Assist, 3); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, session.CombatEvents.Length);
        Assert.All(session.CombatEvents, row => Assert.Equal(LootTrackerActivityOrigin.Abyss, row.Origin));
        Assert.Equal(0, session.AbyssPoints); Assert.Equal(0, session.RankPointsGained); Assert.Equal(0, session.RankPointsLost);
        Assert.DoesNotContain(f.Journal, line => line.Contains("Synthetic opponent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageFailureDoesNotPublishPartialHarvestRewardsOrAcceptedDrops()
    {
        await using var f = new Fixture(abyss: true); await f.Ready(); f.FailAppend = true;
        f.Source.Harvest(1, new NetworkGatheringItem(7, 2), new NetworkGatheringItem(8, 3)); await f.Ready();
        Assert.True(f.Service.StatusIsError); Assert.Empty(f.Accepted);
        var session = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Empty(session.Loot); Assert.Equal(0, session.ItemCount);
        Assert.DoesNotContain(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
    }

    [Fact]
    public async Task DailyCacheIsPerProfileAndAStoredValueNeverBecomesAConfirmedCurrentBaseline()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(700_000); await f.Ready(); Assert.True(f.Service.OverlayState.DailyConfirmed);
        Assert.Equal(700_000, f.Service.OverlayState.CachedDaily!.Total);
        f.Source.Switch(2); await f.Ready();
        Assert.Null(f.Service.OverlayState.CachedDaily); Assert.False(f.Service.OverlayState.DailyConfirmed);
        f.Source.Daily(10); await f.Ready(); Assert.Equal(10, f.Service.OverlayState.CachedDaily!.Total);
        f.Source.Switch(1); await f.Ready();
        Assert.Equal(700_000, f.Service.OverlayState.CachedDaily!.Total); Assert.False(f.Service.OverlayState.DailyConfirmed);
        Assert.Null(f.Service.OverlayState.Daily.Total);
    }

    [Fact]
    public async Task ADelayedOutsideActivityAfterAnEarlierQueuedTickIsImmediatelyCompletedWhenItsTimeoutHasElapsed()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Position = () =>
        {
            f.Position = () => null;
            // QueueActivity has already frozen its receipt stamp. This timer is enqueued
            // before the delayed activity, and finds no session to expire yet.
            f.Clock.Advance(TimeSpan.FromMinutes(3));
            return null;
        };
        f.Source.Drop(7, 2, 1); await f.Ready();
        var saved = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, saved.ItemCount); Assert.Equal(LootTrackerSessionStatus.Completed, saved.Status);
        Assert.Equal(TimeSpan.Zero, saved.Elapsed); Assert.Null(f.Service.OverlayState.Session);
        Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LosingTheProfileInAConfirmedWorldInterruptsAndStopsRoutingUntilANewSession(bool abyss)
    {
        await using var f = new Fixture(abyss: abyss); await f.Ready();
        f.Source.Drop(7, 2, 1); await f.Ready();
        var original = Assert.Single(f.Service.Snapshot.Sessions).Id;
        var positionReads = 0;
        f.Position = () => { positionReads++; return null; };
        f.Bound = false; f.Source.Notify(); await f.Ready();
        Assert.Equal(0, positionReads); Assert.Null(f.Service.OverlayState.Session);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, Assert.Single(f.Service.Snapshot.Sessions).Status);
        f.Bound = true; f.Source.Notify(); await f.Ready(); f.Source.Drop(7, 3, 2); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(original, current.Id); Assert.Equal(3, current.ItemCount);
        Assert.Equal(2, Assert.Single(f.Service.Snapshot.Sessions, row => row.Id == original).ItemCount);
    }

    [Fact]
    public async Task ExistingJournalsAreOnlyHistoryAndExplicitDeletionUsesTheLegacyDelegate()
    {
        var legacy = new LootTrackerSession { Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind,
            StartedAt = DateTimeOffset.UnixEpoch, LastLootAt = DateTimeOffset.UnixEpoch, Status = LootTrackerSessionStatus.Completed };
        var deletes = 0;
        await using var f = new Fixture(legacy: [legacy], deleteLegacy: session => { Assert.Equal(legacy, session); deletes++; return false; });
        await f.Ready(); Assert.Single(f.Service.Snapshot.Sessions); Assert.Empty(f.Journal);
        await f.Service.DeleteSessionAsync(legacy.Id); await f.Ready();
        Assert.Equal(1, deletes); Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Journal);
    }

    [Fact]
    public async Task AGatheringSourceWarningBreaksTheRouteAndIsReportedUntilItClears()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        Assert.False(f.Service.StatusIsError);
        f.Source.GatheringWarning = "Sammelbeute wird nicht mehr sicher erkannt.";
        f.Source.GatheringWarningVersion++; f.Source.Notify(); await f.Ready();
        Assert.True(f.Service.StatusIsError);
        Assert.Equal("Sammelbeute wird nicht mehr sicher erkannt.", f.Service.Status);
        f.Source.GatheringWarning = null; f.Source.GatheringWarningVersion++; f.Source.Notify(); await f.Ready();
        Assert.False(f.Service.StatusIsError);
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(f.Service.Snapshot.Sessions).Status);
    }

    [Fact]
    public async Task AFailingLegacyDeleteCompletesWithItsErrorAndRecordingContinues()
    {
        var legacy = new LootTrackerSession { Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind,
            StartedAt = DateTimeOffset.UnixEpoch, LastLootAt = DateTimeOffset.UnixEpoch, Status = LootTrackerSessionStatus.Completed };
        await using var f = new Fixture(legacy: [legacy],
            deleteLegacy: _ => throw new KeyNotFoundException("The original session store is unavailable."));
        await f.Ready();
        var delete = f.Service.DeleteSessionAsync(legacy.Id);
        Assert.Same(delete, await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => delete);
        f.Source.Drop(7, 1, 1); await f.Ready();
        Assert.Single(f.Accepted);
        Assert.Contains(f.Service.Snapshot.Sessions, session => session.Status == LootTrackerSessionStatus.Live);
    }

    [Fact]
    public async Task LootCapturedBeforeTheFarmDayResetIsCountedEvenIfItIsProcessedAfterIt()
    {
        var legacy = new LootTrackerSession { Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind,
            StartedAt = DateTimeOffset.UnixEpoch, LastLootAt = DateTimeOffset.UnixEpoch, Status = LootTrackerSessionStatus.Completed };
        using var worker = new ManualResetEventSlim();
        await using var f = new Fixture(start: new(2026, 10, 27, 6, 59, 59, TimeSpan.Zero), legacy: [legacy],
            deleteLegacy: _ => { worker.Wait(TimeSpan.FromSeconds(10)); return false; });
        await f.Ready();
        // Hold the single worker so the capture-time fact is queued before the reset and processed after it.
        var delete = f.Service.DeleteSessionAsync(legacy.Id);
        f.Clock.Advance(TimeSpan.FromMilliseconds(900));
        f.Source.Drop(7, 1, 1);
        f.Clock.Advance(TimeSpan.FromMilliseconds(200));
        worker.Set(); await delete; await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(new DateOnly(2026, 10, 26), session.FarmDay);
        Assert.Single(f.Accepted);
        Assert.Equal(1, session.ItemCount);
    }

    [Fact]
    public async Task NewOwnGenerationRejectsOldEventsEvenIfTheyShareTheSameCharacterAndSequence()
    {
        await using var f = new Fixture(); await f.Ready(); var old = f.Source.OwnCharacter!;
        f.Source.Switch(1); await f.Ready();
        f.Source.Drop(7, 100, 1, old); f.Source.Drop(7, 2, 1); await f.Ready();
        Assert.Equal(2, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
    }

    [Fact]
    public async Task HighFrequencySidecarDoesNotAddLootCommandsAndIsSealedAtTheOriginalSessionCutoff()
    {
        await using var f = new Fixture(replayPositions: true); await f.Ready();
        f.Source.Drop(7, 2, 1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var stamp = f.Clock.GetTimestamp();
        f.PositionRecorder!.Observe(Enumerable.Range(1, 60).Select(frame => new ReplayMapPositionSample("sample-map", frame, 123.123456789,
            f.Clock.GetUtcNow(), stamp + frame, frame, 1, 1, f.WorldGeneration,
            f.Source.OwnCharacter!.Generation, ReplayPositionProvenance.Flow, 1)).ToArray());
        Assert.Single(f.Journal); // Sixty frames never enter the canonical loot command/journal path.
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Drop(7, 3, 2); await f.Ready();
        await f.Service.DisposeAsync(); await ReplayPositionStore.FlushAsync(f.SessionRoot, id);
        var result = ReplayPositionStore.Read(f.SessionRoot, id, TimeSpan.FromSeconds(2));
        Assert.Equal(60, result.Samples.Length); Assert.False(result.Truncated);
        Assert.All(result.Samples, row => Assert.Equal(123.123456789, row.Y));
        var session = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(5, session.ItemCount); Assert.Equal(TimeSpan.FromSeconds(2), session.Elapsed);
        Assert.Equal(3, f.Journal.Count); // Two rewards and one terminal snapshot.
    }

    [Fact]
    public async Task DeletingAnActiveSessionStopsItsSidecarBeforeTheDirectoryCanBeRemoved()
    {
        await using var f = new Fixture(replayPositions: true); await f.Ready();
        f.Source.Drop(7, 2, 1); await f.Ready(); var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        await f.Service.DeleteSessionAsync(id); await f.Ready();
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, id)));
        f.PositionRecorder!.Observe([new("sample-map", 1, 2, f.Clock.GetUtcNow(), f.Clock.GetTimestamp() + 1,
            1, 1, 1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, ReplayPositionProvenance.Matched, 1)]);
        await f.PositionRecorder.SealAsync(TimeSpan.FromSeconds(1));
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, id))); Assert.Empty(f.Service.Snapshot.Sessions);
    }

    [Theory]
    [InlineData("fresh", true)] [InlineData("missing", false)] [InlineData("stale", false)]
    [InlineData("future", false)] [InlineData("binding", false)] [InlineData("world", false)]
    [InlineData("map-generation", false)] [InlineData("detection", false)] [InlineData("nonfinite", false)]
    public async Task NewDropPlacementUsesOnlyItsFreshQualifiedSnapshotAndIsAtomicWithDeduplicatedLoot(string variant, bool placed)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Position = () => variant == "missing" ? null : new RecorderPositionSnapshot(
            new("test-map", variant == "nonfinite" ? double.NaN : 12.123456789, 34.987654321,
                f.Clock.GetUtcNow().AddSeconds(variant == "stale" ? -3 : variant == "future" ? 1 : 0))
                { ContextGeneration = variant == "map-generation" ? 2 : 1 },
            1, f.WorldGeneration + (variant == "world" ? 1 : 0),
            f.Source.OwnCharacter!.Generation + (variant == "binding" ? 1 : 0),
            f.Clock.GetUtcNow().AddSeconds(variant == "detection" ? -6 : 0), 30, 1);
        f.Source.Drop(7, 2, 1); await f.Ready();
        var first = Assert.Single(f.Service.Snapshot.Sessions);
        var fact = Assert.Single(first.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.Drop);
        if (placed)
        {
            Assert.NotNull(fact.Position); Assert.Equal(12.123456789, fact.Position.X); Assert.Equal(34.987654321, fact.Position.Y);
            Assert.Equal(f.Clock.GetUtcNow(), fact.Position.ObservedAt);
        }
        else Assert.Null(fact.Position);
        f.Position = () => null; f.Source.Drop(7, 2, 1); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, current.ItemCount); Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.Drop);
        var reopened = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        var restored = Assert.Single(reopened.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.Drop);
        Assert.Equal(fact.Id, restored.Id); Assert.Equal(fact.Position, restored.Position);
        Assert.Equal(2, Assert.Single(restored.Loot).Quantity); Assert.Equal(2, reopened.ItemCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-unified-" + Guid.NewGuid().ToString("N"));
        internal string SessionRoot => Path.Combine(Root, "sessions");
        internal SettingsService Settings { get; } = new();
        internal ManualTime Clock { get; }
        internal FakeSource Source { get; }
        internal UnifiedLootTrackerService Service { get; }
        internal ReplayPositionRecorder? PositionRecorder { get; }
        internal List<AcceptedFarmLootDrop> Accepted { get; } = [];
        internal List<string> Starts { get; } = [];
        internal List<string> Journal { get; } = [];
        internal bool Bound = true, FailAppend;
        internal Action? BeforeBind;
        internal Func<RecorderPositionSnapshot?> Position = () => null;
        internal WorldContextKind WorldKind = WorldContextKind.OpenWorld;
        internal WorldContextStatus WorldStatus = WorldContextStatus.Confirmed;
        internal string? Category;
        internal long WorldGeneration = 1, BindingOffset;
        internal TimeSpan WorldOffset;
        internal Fixture(bool abyss = false, DateTimeOffset? start = null, IEnumerable<LootTrackerSession>? legacy = null,
            Func<LootTrackerSession, bool>? deleteLegacy = null, bool replayPositions = false,
            Func<RecorderPositionSnapshot, FarmLocationMatch?>? locationName = null)
        {
            Directory.CreateDirectory(Root); Clock = new(start); Source = new(Clock);
            Settings.Current.LootTrackingEnabled = Settings.Current.MonsterLootTrackingEnabled = true;
            Settings.Current.LifeskillTrackingEnabled = Settings.Current.AbyssTrackingEnabled = true;
            if (abyss) { WorldKind = WorldContextKind.Instance; Category = "Abyss"; Source.InInstance = true; }
            var store = new UnifiedLootSessionStore(SessionRoot, (path, line) =>
            {
                if (FailAppend) throw new IOException("Synthetic append failure.");
                File.AppendAllText(path, line, new UTF8Encoding(false)); Journal.Add(line);
            }, (path, value) => JsonFile.Save(path, value));
            if (replayPositions) PositionRecorder = new(null, root: SessionRoot, time: Clock);
            Service = new(Settings, Source, Source, Source, Source, own =>
            {
                Interlocked.Exchange(ref BeforeBind, null)?.Invoke();
                return Bound ? new(own.Identity.CharacterId == 1 ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "Synthetic character") : null;
            }, World, store, Clock, position: () => Position(), itemName: id => "Synthetic item " + id,
                legacyHistory: legacy, combat: Source, dailyStore: new(Path.Combine(Root, "daily.json")), deleteLegacy: deleteLegacy,
                replayPositionRecorder: PositionRecorder, locationName: locationName);
            Service.DropAccepted += Accepted.Add; Service.SessionStarted += Starts.Add;
        }
        internal WorldContextSnapshot World() => new(WorldStatus, WorldKind, 1, null, Category, null, "Synthetic map", "Synthetic map",
            WorldGeneration, Clock.GetUtcNow() + WorldOffset, "synthetic") { BindingGeneration = Source.OwnCharacter?.Generation + BindingOffset };
        internal void SetWorld(bool abyss)
        { WorldKind = abyss ? WorldContextKind.Instance : WorldContextKind.OpenWorld; Category = abyss ? "Abyss" : null; WorldGeneration++; Source.InInstance = abyss; Source.Notify(); }
        internal Task Ready() => Service.FlushAsync();
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeSource : INetworkFarmSource, INetworkKinaSource, INetworkGatheringSource, INetworkAbyssSource, INetworkAbyssCombatSource
    {
        private readonly ManualTime _clock;
        private long _generation = 1, _absoluteSequence = 1, _dailySequence;
        internal FakeSource(ManualTime clock)
        {
            _clock = clock; OwnCharacter = Own(1);
            AbyssSnapshot = new(OwnCharacter, 100, 1_000, 1_000, clock.GetUtcNow(), _absoluteSequence)
            { AbyssPointGainsQualified = true, RankPointChangesQualified = true, KinaGainsQualified = true };
        }
        private OwnCharacterContext Own(int character) => new(new(character, 1, character, "Synthetic character"), _generation)
        { ObservedAt = _clock.GetUtcNow() };
        public bool Running { get; set; } = true;
        internal bool InInstance;
        public OwnCharacterContext? OwnCharacter { get; private set; }
        public bool GatheringVerified { get; set; } = true;
        public string? GatheringWarning { get; set; }
        public long GatheringWarningVersion { get; set; }
        public string GatheringStatus => "Synthetic";
        public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot { get; private set; } = new(null, null, null, 0);
        internal long? WalletBalance = 1_000;
        public NetworkKinaSnapshot KinaSnapshot => new(WalletBalance, null, _clock.GetUtcNow(), false, "Synthetic");
        public NetworkAbyssSnapshot AbyssSnapshot { get; private set; }
        public NetworkAbyssCombatCapabilities CombatCapabilities => new(true, true, false);
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkKinaGain>? KinaGained;
        public event Action<NetworkKinaGain>? MonsterKinaGained;
        public event Action<NetworkFarmDrop>? FarmDropGained;
        public event Action<NetworkGathering>? GatheringCompleted;
        public event Action<NetworkAbyssPointGain>? AbyssPointsGained;
        public event Action<NetworkAbyssRankChange>? RankPointsChanged;
        public event Action<NetworkAbyssCombatEvent>? CombatEvent;
        internal void Notify() => Changed?.Invoke();
        internal void Drop(int item, long amount, long sequence, OwnCharacterContext? own = null) =>
            FarmDropGained?.Invoke(new(_clock.GetUtcNow(), sequence, own ?? OwnCharacter!, item, amount, NetworkFarmDropKind.Item, InInstance));
        internal void Harvest(long sequence, params NetworkGatheringItem[] rewards) => GatheringCompleted?.Invoke(
            new(_clock.GetUtcNow(), sequence, OwnCharacter!, 777_777, rewards.ToImmutableArray())
            { InInstance = InInstance, Object = new(42, 888_888, 999_999) });
        internal void Failed(long sequence) => GatheringCompleted?.Invoke(new(_clock.GetUtcNow(), sequence, OwnCharacter!, 777_777, [])
        { Outcome = NetworkGatheringOutcome.Failed, InInstance = InInstance });
        internal void Kina(long amount, long sequence)
        { var gain = new NetworkKinaGain(_clock.GetUtcNow(), sequence, OwnCharacter!, amount) { InInstance = InInstance };
            KinaGained?.Invoke(gain); MonsterKinaGained?.Invoke(gain); }
        internal void Points(long amount, long sequence, DateTimeOffset? at = null, OwnCharacterContext? own = null) =>
            AbyssPointsGained?.Invoke(new(at ?? _clock.GetUtcNow(), sequence, own ?? OwnCharacter!, amount));
        internal void Rank(long amount, long sequence, DateTimeOffset? at = null, OwnCharacterContext? own = null) =>
            RankPointsChanged?.Invoke(new(at ?? _clock.GetUtcNow(), sequence, own ?? OwnCharacter!, amount));
        internal void Combat(NetworkAbyssCombatKind kind, long sequence) => CombatEvent?.Invoke(new(_clock.GetUtcNow(), sequence, OwnCharacter!, kind, "Synthetic opponent")
        { OpponentClassId = "Ranger", OpponentAbyssRankGrade = 19 });
        internal void Daily(long total, bool notify = true)
        { DailyMonsterKinaSnapshot = new(OwnCharacter, total, _clock.GetUtcNow(), ++_dailySequence); if (notify) Notify(); }
        internal void Absolute(long rank, bool qualified = true)
        {
            AbyssSnapshot = new(OwnCharacter, 100, 1_000, rank, _clock.GetUtcNow(), ++_absoluteSequence)
            { AbyssPointGainsQualified = qualified, RankPointChangesQualified = qualified, KinaGainsQualified = qualified };
            Notify();
        }
        internal void SetAbsolute(NetworkAbyssSnapshot snapshot, bool notify = true)
        { AbyssSnapshot = snapshot; if (notify) Notify(); }
        internal void ClearAbsolute() { AbyssSnapshot = NetworkAbyssSnapshot.Empty; Notify(); }
        internal void LoseOwn() { OwnCharacter = null; AbyssSnapshot = NetworkAbyssSnapshot.Empty; OwnCharacterChanged?.Invoke(); }
        internal void Switch(int character)
        {
            _generation++; OwnCharacter = Own(character); DailyMonsterKinaSnapshot = new(null, null, null, 0);
            AbyssSnapshot = new(OwnCharacter, 100, 1_000, 1_000, _clock.GetUtcNow(), ++_absoluteSequence)
            { AbyssPointGainsQualified = true, RankPointChangesQualified = true, KinaGainsQualified = true };
            OwnCharacterChanged?.Invoke();
        }
    }

    private sealed class ManualTime(DateTimeOffset? start) : TimeProvider
    {
        private readonly DateTimeOffset _start = start ?? new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        private long _stamp, _wallAdjustment;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public override DateTimeOffset GetUtcNow() => _start.AddTicks(GetTimestamp() + Interlocked.Read(ref _wallAdjustment));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(() => callback(state)); _timers.Add(timer); return timer; }
        internal void Advance(TimeSpan duration, bool fireTimers = true) { Interlocked.Add(ref _stamp, duration.Ticks); if (fireTimers) foreach (var timer in _timers.ToArray()) timer.Fire(); }
        internal void AdjustWallClock(TimeSpan duration) => Interlocked.Add(ref _wallAdjustment, duration.Ticks);
        private sealed class ManualTimer(Action callback) : ITimer
        {
            private bool _disposed;
            internal void Fire() { if (!_disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
