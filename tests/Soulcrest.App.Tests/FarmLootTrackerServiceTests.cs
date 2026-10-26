using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class FarmLootTrackerServiceTests
{
    [Fact]
    public async Task UniversalCrystalKeepsItsOwnTypedLootEntryAndPersistsWithoutFiveGenusOrItemCopies()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(930500001, 1000, NetworkFarmDropKind.UniversalSoulCrystal, 1);
        f.Source.Drop(930500001, 1000, NetworkFarmDropKind.UniversalSoulCrystal, 1); // Receipt echo.
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 2);
        await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(1001, session.SoulCount); Assert.Equal(0, session.ItemCount);
        Assert.Equal(2, session.Loot.Length);
        var universal = Assert.Single(session.Loot, row => row.Id == "UniversalSoulCrystal:930500001");
        Assert.Equal(LootTrackerLootKind.Soul, universal.Kind); Assert.Equal(1000, universal.Quantity);
        Assert.Equal(MonsterLootItemCatalog.Default.Name(930500001, "en"), universal.Name);
        Assert.NotNull(MonsterLootIconCatalog.Default.DropAsset(universal.Id));
        await f.Service.DisposeAsync();
        var recovered = Assert.Single(new FarmLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(1001, recovered.SoulCount); Assert.Equal(0, recovered.ItemCount);
        Assert.Equal(session.Loot.ToArray(), recovered.Loot.ToArray());
    }

    [Fact]
    public async Task UniversalCrystalKindCannotPersistADifferentCurrencyOrTypedCrystalNumber()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(2, 1000, NetworkFarmDropKind.UniversalSoulCrystal, 1);
        f.Source.Drop(930100031, 1000, NetworkFarmDropKind.UniversalSoulCrystal, 2);
        await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
    }

    [Fact]
    public async Task DailyAbsoluteValuesPersistPerBoundProfileEvenWithSessionRecordingOff()
    {
        await using var f = new Fixture(enabled: false);
        await f.Ready();
        f.Source.Daily(58_164); await f.Ready();
        Assert.Equal(58_164, f.Service.Snapshot.DailyMonsterKina);
        Assert.True(f.Service.Snapshot.DailyConfirmed);
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
        f.Source.Switch(2); f.Source.Daily(901_000); await f.Ready();
        Assert.Equal(901_000, f.Service.Snapshot.DailyMonsterKina);
        Assert.Equal(58_164, f.DailyStore.Read(Fixture.ProfileA)!.Total);
        Assert.Equal(901_000, f.DailyStore.Read(Fixture.ProfileB)!.Total);
        f.Source.Switch(1); await f.Ready();
        Assert.Equal(58_164, f.Service.Snapshot.DailyMonsterKina);
        Assert.False(f.Service.Snapshot.DailyConfirmed);
        Assert.Equal($"{Fixture.ProfileA}:{LootFarmDay.Get(f.Clock.GetUtcNow()):yyyy-MM-dd}", f.Service.Snapshot.CompletionScopeKey);
    }

    [Fact]
    public async Task CachedDailySurvivesRestartAndLowerServerTotalReplacesItWithoutInventedDayReset()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(1_000_000); await f.Ready();
        await f.Service.DisposeAsync();
        f.Source.ClearDaily();
        await using var restarted = f.NewService();
        await restarted.DrainAsync();
        Assert.Equal(1_000_000, restarted.Snapshot.DailyMonsterKina);
        Assert.False(restarted.Snapshot.DailyConfirmed);
        f.Clock.Advance(TimeSpan.FromDays(2)); await restarted.DrainAsync();
        Assert.Equal(1_000_000, restarted.Snapshot.DailyMonsterKina);
        f.Source.Daily(120); await restarted.DrainAsync();
        Assert.Equal(120, restarted.Snapshot.DailyMonsterKina);
        Assert.Equal(120, f.DailyStore.Read(Fixture.ProfileA)!.Total);
        Assert.Empty(restarted.SessionSnapshot.Sessions);
    }

    [Fact]
    public async Task DailyAndSessionCountersStaySeparateAndFirstIncomeCountsExactlyOnce()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(58_164); f.Source.Daily(58_992);
        f.Source.Kina(828, 1); f.Source.Kina(828, 1);
        await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(828, session.KinaUnbound);
        Assert.Null(session.KinaBound);
        Assert.Equal(58_992, f.Service.Snapshot.DailyMonsterKina);
        Assert.Equal(58_992, f.DailyStore.Read(Fixture.ProfileA)!.Total);
        Assert.Equal(828, f.Service.Snapshot.SessionKina);
        Assert.Equal(1234, session.Character!.CombatScore);
        Assert.Equal(456, session.Character.GearScore);
        Assert.Null(session.MonsterCount);
    }

    [Fact]
    public async Task CommonGearGroupsOnlyTheOverlayWhileSessionKeepsExactItemDrops()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(110160001, 2, NetworkFarmDropKind.Item, 1);
        f.Source.Drop(110160008, 1, NetworkFarmDropKind.Item, 2);
        f.Source.Drop(610530001, 4, NetworkFarmDropKind.Item, 3); await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(3, session.Loot.Length); Assert.Equal(7, session.ItemCount);
        Assert.Equal(2, f.Service.Snapshot.Drops.Count);
        Assert.Contains(f.Service.Snapshot.Drops, row => row.Name == "Common Gear" && row.Count == 3);
        Assert.Contains(f.Service.Snapshot.Drops, row => row.Key == "Item:610530001" && row.Count == 4);
    }

    [Fact]
    public async Task ItemsAndCrystalsAggregateWithoutPretendingAnUnknownMonsterJoin()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 2);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 3);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 3);
        await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(1, session.ItemCount); Assert.Equal(2, session.SoulCount);
        Assert.Equal(2, session.Loot.Length);
        Assert.All(session.Loot, item => Assert.Null(item.MobId));
        Assert.Contains(session.Loot, item => item.Name == "Chaos Helm" && item.Quantity == 1);
        Assert.Contains(f.Service.Snapshot.Drops, item => item.Name.Contains("Cogni") && item.Count == 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CapCrossingIncomeStaysNormalRegardlessOfObservationOrder(bool observationFirst)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(999_800); f.Source.Kina(50, 1); await f.Ready();
        var normalId = Assert.Single(f.Service.SessionSnapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Source.Daily(1_000_000, observationFirst);
        f.Source.Kina(200, 2);
        if (!observationFirst) f.Source.Notify();
        await f.Ready();
        var completed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(normalId, completed.Id); Assert.Equal(250, completed.KinaUnbound);
        Assert.Equal(LootTrackerSessionMode.Normal, completed.Mode);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(f.Clock.GetUtcNow(), completed.EndedAt);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 1); await f.Ready();
        var limited = f.Service.SessionSnapshot.Sessions[0];
        Assert.NotEqual(normalId, limited.Id); Assert.Equal(LootTrackerSessionMode.LimitedKina, limited.Mode);
        Assert.Equal(1, limited.SoulCount); Assert.Equal(0, limited.KinaUnbound);
        Assert.Equal(2, f.Service.SessionSnapshot.Sessions.Length);
    }

    [Fact]
    public async Task FreshCappedBaselineWithoutGainClosesNormalAtItsLastDrop()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(999_000);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 1); await f.Ready();
        var before = Assert.Single(f.Service.SessionSnapshot.Sessions);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Source.ClearDaily(); f.Source.Notify(); await f.Ready();
        f.Source.Daily(1_000_000); await f.Ready();
        // No continuous baseline means no gain callback. The next timer settles the absolute cap.
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var completed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(before.Id, completed.Id);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(before.LastLootAt, completed.EndedAt);
        Assert.Equal(TimeSpan.Zero, completed.Elapsed); Assert.Equal(0, completed.KinaUnbound);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 2); await f.Ready();
        Assert.Equal(LootTrackerSessionMode.LimitedKina, f.Service.SessionSnapshot.Sessions[0].Mode);
        Assert.Equal(2, f.Service.SessionSnapshot.Sessions.Length);
    }

    [Fact]
    public async Task AlreadyCappedBaselineStartsLimitedAndCachedCapNeverDoes()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(1_000_000); await f.Ready();
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        Assert.Equal(LootTrackerSessionMode.LimitedKina, Assert.Single(f.Service.SessionSnapshot.Sessions).Mode);
        await f.Service.DisposeAsync(); f.Source.ClearDaily();
        await using var restarted = f.NewService(); await restarted.DrainAsync();
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 2); await restarted.DrainAsync();
        Assert.Equal(LootTrackerSessionMode.Normal, restarted.SessionSnapshot.Sessions[0].Mode);
        Assert.False(restarted.Snapshot.DailyConfirmed);
        Assert.Equal(1_000_000, restarted.Snapshot.DailyMonsterKina);
    }

    [Fact]
    public async Task FarmDayResetClosesSessionAtLastDropAndStaleCapRemainsUnconfirmed()
    {
        await using var f = new Fixture();
        f.Clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(59)); await f.Ready();
        f.Source.Daily(1_000_000); f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 1); await f.Ready();
        var before = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionMode.LimitedKina, before.Mode);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Ready();
        var closed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, closed.Status);
        Assert.Equal(before.LastLootAt, closed.EndedAt); Assert.Equal(TimeSpan.Zero, closed.Elapsed);
        Assert.Equal(1_000_000, f.Service.Snapshot.DailyMonsterKina); Assert.False(f.Service.Snapshot.DailyConfirmed);
        f.Source.Notify(); await f.Ready(); Assert.False(f.Service.Snapshot.DailyConfirmed);
        f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 2); await f.Ready();
        var next = f.Service.SessionSnapshot.Sessions[0];
        Assert.Equal(LootTrackerSessionMode.Normal, next.Mode); Assert.NotEqual(before.FarmDay, next.FarmDay);
        f.Source.Daily(20); await f.Ready(); Assert.True(f.Service.Snapshot.DailyConfirmed);
        Assert.Equal(20, f.Service.Snapshot.DailyMonsterKina);
    }

    [Fact]
    public async Task ItemAndSoulDropsExtendIdleAndCommittedEventsDoNotRepeatOnRecovery()
    {
        await using var f = new Fixture(); await f.Ready();
        var accepted = new List<AcceptedFarmLootDrop>(); f.Service.DropAccepted += accepted.Add;
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        // Equal timestamps and loot are real repeated messages when their transport sequence differs.
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 2);
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 2); await f.Ready();
        Assert.Equal(2, accepted.Count); Assert.All(accepted, drop => Assert.Equal(Fixture.ProfileA, drop.ProfileId));
        f.Clock.Advance(TimeSpan.FromSeconds(119)); f.Source.Drop(2, 1, NetworkFarmDropKind.SoulCrystal, 3); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(119)); await f.Ready();
        Assert.Equal(LootTrackerSessionStatus.Live, Assert.Single(f.Service.SessionSnapshot.Sessions).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var completed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(TimeSpan.FromSeconds(119), completed.Elapsed); Assert.Equal(3, accepted.Count);
        await f.Service.DisposeAsync(); await using var restarted = f.NewService();
        var recoveryAlerts = 0; restarted.DropAccepted += _ => recoveryAlerts++;
        await restarted.DrainAsync(); Assert.Single(restarted.SessionSnapshot.Sessions);
        Assert.Equal(3, accepted.Count); Assert.Equal(0, recoveryAlerts);
    }

    [Fact]
    public async Task ConfirmedInstanceWorldAlsoRejectsMissingInstanceBitAndFailedSaveNeverAlerts()
    {
        await using var f = new Fixture(); await f.Ready();
        f.WorldKind = WorldContextKind.Instance;
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); f.Source.Kina(500, 1); await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
        await f.Service.DisposeAsync(); f.WorldKind = WorldContextKind.OpenWorld;
        var store = new FarmLootSessionStore(f.SessionRoot, (_, _) => throw new IOException("Synthetic disk failure."), JsonFile.Save);
        await using var failed = f.NewService(store); var alerts = 0; failed.DropAccepted += _ => alerts++;
        await failed.DrainAsync(); f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 2); await failed.DrainAsync();
        Assert.Equal(0, alerts); Assert.True(failed.StatusIsError); Assert.False(failed.SourceAvailable);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("transition")]
    [InlineData("stale")]
    [InlineData("kind")]
    [InlineData("generation")]
    [InlineData("future")]
    public async Task PositiveCurrentOpenWorldEvidenceIsRequired(string variant)
    {
        await using var f = new Fixture(); await f.Ready();
        switch (variant)
        {
            case "unknown": f.WorldStatus = WorldContextStatus.Unknown; break;
            case "transition": f.WorldStatus = WorldContextStatus.Transitioning; break;
            case "stale": f.WorldStatus = WorldContextStatus.Stale; break;
            case "kind": f.WorldKind = WorldContextKind.Unknown; break;
            case "generation": f.WorldGenerationOffset = -1; break;
            case "future": f.WorldObservationDelay = TimeSpan.FromSeconds(1); break;
        }
        f.Source.Notify(); f.Source.Kina(100, 1); f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions); Assert.False(f.Service.SourceAvailable);
        Assert.Equal("Warte auf bestätigten Open-World-Aufenthalt.", f.Service.Status);
    }

    [Fact]
    public async Task GlobalNetworkOptOutAlsoPreventsAStillRunningSourcesEvents()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.LootTrackingEnabled = false;
        f.Source.Kina(100, 1); f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
    }

    [Fact]
    public async Task IdleTimeoutEndsAtLastLootAndNextIncomeStartsANewSession()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Kina(100, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(30)); f.Source.Kina(50, 2); await f.Ready();
        var last = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromSeconds(119)); await f.Ready();
        var live = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Live, live.Status);
        Assert.Equal(TimeSpan.FromSeconds(149), live.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var completed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status);
        Assert.Equal(last, completed.EndedAt); Assert.Equal(TimeSpan.FromSeconds(30), completed.Elapsed);
        f.Source.Kina(25, 3); await f.Ready();
        Assert.Equal(2, f.Service.SessionSnapshot.Sessions.Length);
        Assert.Equal(25, f.Service.Snapshot.SessionKina);
    }

    [Fact]
    public async Task InstanceDropsCannotStartOrExtendOpenWorldSession()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Kina(999, 1, instance: true); f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1, instance: true);
        await f.Ready(); Assert.Empty(f.Service.SessionSnapshot.Sessions);
        f.Source.Kina(10, 2); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(119)); f.Source.Kina(999, 3, instance: true);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(10, session.KinaUnbound); Assert.Equal(LootTrackerSessionStatus.Completed, session.Status);
    }

    [Fact]
    public async Task CharacterSwitchEndsOldSessionAndFrozenScoresRemainIndependent()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Kina(100, 1); await f.Ready();
        f.Source.Switch(2); f.Source.Kina(200, 1); await f.Ready();
        var sessions = f.Service.SessionSnapshot.Sessions;
        Assert.Equal(2, sessions.Length);
        Assert.Equal(Fixture.ProfileB, sessions[0].Character!.ProfileId);
        Assert.Equal(Fixture.ProfileA, sessions[1].Character!.ProfileId);
        Assert.Equal(1234, sessions[1].Character!.CombatScore);
        Assert.Equal(LootTrackerSessionStatus.Completed, sessions[1].Status);
        Assert.Equal(200, f.Service.Snapshot.SessionKina);
    }

    [Fact]
    public async Task MissingBindingNeverUsesAManuallySelectedProfileAndGapHidesTheDailyStand()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Bound = false;
        f.Source.Daily(500); f.Source.Kina(25, 1); await f.Ready();
        Assert.Null(f.Service.Snapshot.DailyMonsterKina);
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
        f.Bound = true; f.Source.Daily(600); await f.Ready();
        Assert.Equal(600, f.Service.Snapshot.DailyMonsterKina);
        f.Source.LoseOwn(); await f.Ready();
        Assert.Null(f.Service.Snapshot.DailyMonsterKina); Assert.Null(f.Service.Snapshot.CompletionScopeKey);
        Assert.Equal(600, f.DailyStore.Read(Fixture.ProfileA)!.Total);
    }

    [Fact]
    public async Task ReturningToTheSameQualifiedObservationReconfirmsTheSavedDailyValue()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(800); await f.Ready();
        f.Bound = false; f.Source.Notify(); await f.Ready();
        Assert.False(f.Service.Snapshot.DailyConfirmed);
        f.Bound = true; f.Source.Notify(); await f.Ready();
        Assert.Equal(800, f.Service.Snapshot.DailyMonsterKina);
        Assert.True(f.Service.Snapshot.DailyConfirmed);
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
    }

    [Fact]
    public async Task RestartRecoversInterruptedSessionWithoutContinuingUnobservedTimeOrRepeatingLoot()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Kina(100, 1); f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Kina(50, 2);
        await f.Ready(); await f.Service.DisposeAsync();
        f.Clock.Advance(TimeSpan.FromHours(1));
        await using var restarted = f.NewService(); await restarted.DrainAsync();
        var recovered = Assert.Single(restarted.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, recovered.Status);
        Assert.Equal(150, recovered.KinaUnbound); Assert.Equal(TimeSpan.FromSeconds(10), recovered.Elapsed);
        Assert.False(restarted.Snapshot.SessionActive);
    }

    [Fact]
    public async Task DeleteActiveSessionKeepsDailyStandAndNextNewGainStartsAgain()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(900); f.Source.Kina(100, 1); await f.Ready();
        var id = Assert.Single(f.Service.SessionSnapshot.Sessions).Id;
        await f.Service.DeleteSessionAsync(id);
        f.Source.Kina(100, 1); await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(900, f.Service.Snapshot.DailyMonsterKina);
        f.Source.Kina(20, 2); await f.Ready();
        Assert.Equal(20, Assert.Single(f.Service.SessionSnapshot.Sessions).KinaUnbound);
        Assert.DoesNotContain(new FarmLootSessionStore(f.SessionRoot).Read().Sessions, session => session.Id == id);
    }

    [Fact]
    public async Task ActivePveRecordsTheTravelledPathAndPersistsOnlyThroughTheLastLoot()
    {
        await using var f = new Fixture(); f.PositionEnabled = true; await f.Ready();
        Assert.Equal(0, f.LeasesAcquired);
        f.Source.Kina(100, 1); await f.Ready(); Assert.Equal(1, f.LeasesAcquired);
        f.X = 20; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        f.X = 30; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        // Loot is an additional sample between periodic observations.
        f.X = 35; f.Clock.Advance(TimeSpan.FromMilliseconds(250)); f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        var live = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal([10d, 20d, 30d, 35d], Assert.Single(live.RouteSegments).Points.Select(point => point.X).ToArray());
        var journal = Path.Combine(f.SessionRoot, live.Id, "events-0001.jsonl");
        Assert.Equal(2, File.ReadAllLines(journal).Length); // No full session rewrite per movement tick.
        f.X = 45; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        Assert.Equal(5, Assert.Single(Assert.Single(f.Service.SessionSnapshot.Sessions).RouteSegments).Points.Length);
        f.Clock.Advance(TimeSpan.FromMinutes(2)); await f.Ready();
        var completed = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, completed.Status); Assert.Equal(1, f.LeasesReleased);
        Assert.Equal(TimeSpan.FromSeconds(2.25), completed.Elapsed);
        Assert.Equal([10d, 20d, 30d, 35d], Assert.Single(completed.RouteSegments).Points.Select(point => point.X).ToArray());
        var recovered = Assert.Single(new FarmLootSessionStore(f.SessionRoot).Read().Sessions);
        var exported = Assert.Single(LootTrackerRouteSaveService.Preview(recovered));
        Assert.Equal([10d, 20d, 30d, 35d], exported.Stops.Select(point => point.X).ToArray());
        Assert.Equal(completed.RouteSegments[0].Points.ToArray(), recovered.RouteSegments[0].Points.ToArray());
    }

    [Fact]
    public async Task RouteSamplingIsAtMostOnceASecondPlusLootAndNeverConnectsAcrossGapsOrContexts()
    {
        await using var f = new Fixture(); f.PositionEnabled = true; await f.Ready();
        f.Source.Kina(100, 1); await f.Ready();
        f.X = 11; f.Clock.Advance(TimeSpan.FromMilliseconds(200)); await f.Ready();
        Assert.Single(Assert.Single(Assert.Single(f.Service.SessionSnapshot.Sessions).RouteSegments).Points);
        f.X = 12; f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready();
        Assert.Equal(2, Assert.Single(Assert.Single(f.Service.SessionSnapshot.Sessions).RouteSegments).Points.Length);
        f.PositionEnabled = false; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        f.PositionEnabled = true; f.X = 20; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        f.MapGeneration++; f.X = 30; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready(); // Teleport/reacquisition boundary.
        f.WorldGeneration++; f.X = 40; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        f.MapId = "other-map"; f.X = 50; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Kina(10, 2); await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(5, session.RouteSegments.Length);
        Assert.Equal([10d, 12d], session.RouteSegments[0].Points.Select(point => point.X).ToArray());
        Assert.Equal("other-map", session.RouteSegments[^1].MapId);
        Assert.All(session.RouteSegments.Skip(1), segment => Assert.Single(segment.Points));
        await f.Service.DisposeAsync();
        var recovered = Assert.Single(new FarmLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(5, recovered.RouteSegments.Length);
        Assert.Single(LootTrackerRouteSaveService.Preview(recovered)); // Singleton sections cannot invent a connecting route.
    }

    [Theory]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("binding")]
    [InlineData("world")]
    [InlineData("map")]
    [InlineData("nan")]
    public async Task InvalidVisualPositionsNeverCreateARoutePointOrLoseLoot(string variant)
    {
        await using var f = new Fixture(); f.PositionEnabled = true;
        switch (variant)
        {
            case "old": f.PositionAge = TimeSpan.FromSeconds(-3); break;
            case "future": f.PositionAge = TimeSpan.FromSeconds(1); break;
            case "binding": f.PositionBindingOffset = -1; break;
            case "world": f.PositionWorldOffset = -1; break;
            case "map": f.PositionMapOffset = -1; break;
            case "nan": f.X = double.NaN; break;
        }
        await f.Ready(); f.Source.Kina(100, 1); await f.Ready();
        var session = Assert.Single(f.Service.SessionSnapshot.Sessions);
        Assert.Equal(100, session.KinaUnbound); Assert.Empty(session.RouteSegments);
    }

    [Fact]
    public async Task PositionLeaseStopsAtBindingGapDeleteAndShutdownAndNeverStartsAnIdleSession()
    {
        await using var f = new Fixture(); f.PositionEnabled = true; await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        Assert.Empty(f.Service.SessionSnapshot.Sessions); Assert.Equal(0, f.LeasesAcquired);
        f.Source.Kina(10, 1); await f.Ready(); Assert.Equal(1, f.LeasesAcquired);
        f.Source.LoseOwn(); await f.Ready(); Assert.Equal(1, f.LeasesReleased);
        f.Source.Switch(1); await f.Ready(); Assert.Equal(2, f.LeasesAcquired);
        f.X = 20; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Kina(5, 2); await f.Ready();
        Assert.Equal(2, Assert.Single(f.Service.SessionSnapshot.Sessions).RouteSegments.Length);
        var id = Assert.Single(f.Service.SessionSnapshot.Sessions).Id;
        await f.Service.DeleteSessionAsync(id); Assert.Equal(2, f.LeasesReleased);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready(); Assert.Empty(f.Service.SessionSnapshot.Sessions);
        f.Source.Kina(20, 3); await f.Ready(); Assert.Equal(3, f.LeasesAcquired);
        await f.Service.DisposeAsync(); Assert.Equal(3, f.LeasesReleased);
    }

    [Fact]
    public async Task QualifiedSubareaNameReplacesFallbackOnlyFromLootAndKeepsItsApproximationFlag()
    {
        await using var f = new Fixture(); f.PositionEnabled = true; await f.Ready();
        f.Source.Kina(10, 1); await f.Ready();
        Assert.Equal("Example isle", Assert.Single(f.Service.SessionSnapshot.Sessions).Location);
        f.SubareaName = "First region"; f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        Assert.Equal("Example isle", Assert.Single(f.Service.SessionSnapshot.Sessions).Location);
        f.Source.Kina(5, 2); await f.Ready();
        Assert.Equal("First region", Assert.Single(f.Service.SessionSnapshot.Sessions).Location);
        Assert.True(Assert.Single(f.Service.SessionSnapshot.Sessions).LocationApproximate);
        f.SubareaName = "Later region"; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Kina(5, 3); await f.Ready();
        Assert.Equal("First region", Assert.Single(f.Service.SessionSnapshot.Sessions).Location);
    }

    [Fact]
    public async Task SessionStartedIsEmittedOnlyAfterCommittedFirstLootAndRatesUseLiveElapsedTime()
    {
        await using var f = new Fixture(); await f.Ready();
        var started = new List<string>(); f.Service.SessionStarted += started.Add;
        f.Source.Kina(100, 1); f.Source.Kina(100, 1); await f.Ready();
        Assert.Single(started); Assert.Null(f.Service.Snapshot.SessionKinaPerHour);
        f.Clock.Advance(TimeSpan.FromSeconds(30)); await f.Ready();
        Assert.Equal(TimeSpan.FromSeconds(30), f.Service.Snapshot.SessionElapsed);
        Assert.Equal(12000, f.Service.Snapshot.SessionKinaPerHour);
        f.Source.Drop(210350009, 1, NetworkFarmDropKind.Item, 1); await f.Ready(); Assert.Single(started);
        await f.Service.DisposeAsync(); await using var recovered = f.NewService();
        var recoveredStarts = 0; recovered.SessionStarted += _ => recoveredStarts++;
        await recovered.DrainAsync(); Assert.Equal(0, recoveredStarts);
        Assert.Equal(TimeSpan.Zero, recovered.Snapshot.SessionElapsed); Assert.Null(recovered.Snapshot.SessionKinaPerHour);
    }

    [Fact]
    public async Task AFirstGainReachingTheCapStillEmitsOneSessionStartAndFailedStorageNeverDoes()
    {
        await using var f = new Fixture(); await f.Ready();
        var started = 0; f.Service.SessionStarted += _ => started++;
        f.Source.Daily(999_900); f.Source.Daily(1_000_000); f.Source.Kina(100, 2); await f.Ready();
        Assert.Equal(1, started); Assert.Equal(LootTrackerSessionStatus.Completed, Assert.Single(f.Service.SessionSnapshot.Sessions).Status);
        await f.Service.DisposeAsync();
        var store = new FarmLootSessionStore(f.SessionRoot, (_, _) => throw new IOException("Synthetic start failure."), JsonFile.Save);
        await using var failed = f.NewService(store); var failedStarts = 0; failed.SessionStarted += _ => failedStarts++;
        await failed.DrainAsync(); f.Source.Kina(100, 3); await failed.DrainAsync(); Assert.Equal(0, failedStarts);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ProfileA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ProfileB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-farm-service-" + Guid.NewGuid().ToString("N"));
        public string SessionRoot => Path.Combine(_root, "sessions");
        public SettingsService Settings { get; } = new();
        public FakeSource Source { get; } = new();
        public ManualTime Clock { get; } = new();
        public DailyMonsterKinaStore DailyStore { get; }
        public FarmLootTrackerService Service { get; }
        public bool Bound = true;
        public WorldContextKind WorldKind = WorldContextKind.OpenWorld;
        public WorldContextStatus WorldStatus = WorldContextStatus.Confirmed;
        public long WorldGenerationOffset;
        public TimeSpan WorldObservationDelay;
        public bool PositionEnabled;
        public double X = 10;
        public string MapId = "sample-map";
        public long MapGeneration = 1, WorldGeneration = 1;
        public long PositionBindingOffset, PositionWorldOffset, PositionMapOffset;
        public TimeSpan PositionAge;
        public int LeasesAcquired, LeasesReleased;
        public string? SubareaName;
        public Fixture(bool enabled = true)
        {
            Settings.Current.MonsterLootTrackingEnabled = enabled;
            Settings.Current.LootTrackingEnabled = true;
            Settings.Current.NameLanguage = "en"; Settings.Current.OverlayLanguage = "en";
            Source.UtcNow = Clock.GetUtcNow;
            DailyStore = new(Path.Combine(_root, "daily.json")); Service = NewService();
        }
        public FarmLootTrackerService NewService(FarmLootSessionStore? store = null) => new(Settings, Source,
            own => Bound ? new(own.Identity.CharacterId == 1 ? ProfileA : ProfileB, "Example character", own.CombatScore, own.GearScore) : null,
            () => new(WorldStatus, WorldKind, 1, null, null, null,
                "Beispielinsel", "Example isle", WorldGeneration, Clock.GetUtcNow().Add(WorldObservationDelay), "synthetic")
                { BindingGeneration = Source.OwnCharacter?.Generation + WorldGenerationOffset },
            store ?? new(SessionRoot), DailyStore, Clock,
            () => PositionEnabled ? new(new(MapId, X, 20, Clock.GetUtcNow() + PositionAge)
                { ContextGeneration = MapGeneration + PositionMapOffset }, MapGeneration,
                WorldGeneration + PositionWorldOffset, Source.OwnCharacter?.Generation + PositionBindingOffset,
                Clock.GetUtcNow(), 10, .1) : null,
            () => { LeasesAcquired++; return new Lease(() => LeasesReleased++); }, id => id,
            _ => SubareaName is null ? null : new FarmLocationMatch(SubareaName, true));
        public Task Ready() => Service.DrainAsync();
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class FakeSource : INetworkFarmSource
    {
        private long _generation = 1, _dailySeq;
        public bool Running { get; set; } = true;
        public OwnCharacterContext? OwnCharacter { get; private set; } = new(new(1, 1, 1, "Example character"), 1) { CombatScore = 1234, GearScore = 456 };
        public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot { get; private set; } = new(null, null, null, 0);
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkKinaGain>? MonsterKinaGained;
        public event Action<NetworkFarmDrop>? FarmDropGained;
        public void Daily(long total, bool notify = true) { DailyMonsterKinaSnapshot = new(OwnCharacter, total, TestTime, ++_dailySeq); if (notify) Changed?.Invoke(); }
        public void Notify() => Changed?.Invoke();
        public void ClearDaily() => DailyMonsterKinaSnapshot = new(null, null, null, 0);
        public void Switch(int character) { OwnCharacter = new(new(character, 1, character, "Example character"), ++_generation) { CombatScore = 9999, GearScore = 888 }; ClearDaily(); OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
        public void LoseOwn() { OwnCharacter = null; ClearDaily(); OwnCharacterChanged?.Invoke(); }
        public void Kina(long value, long sequence, bool instance = false) => MonsterKinaGained?.Invoke(new(TestTime, sequence, OwnCharacter!, value) { InInstance = instance });
        public void Drop(int id, long count, NetworkFarmDropKind kind, long sequence, bool instance = false) => FarmDropGained?.Invoke(new(TestTime, sequence, OwnCharacter!, id, count, kind, instance));
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UnixEpoch.AddDays(1);
        public DateTimeOffset TestTime => UtcNow();
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
