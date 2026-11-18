using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task CanonicalPveBoundIncomeIgnoresWalletEchoesAndCommitsOneBoundSessionBinAndFact()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(500); await f.Ready();
        f.Source.WalletKina(40, 1); f.Source.WalletKina(40, 2, bound: true); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        var before = f.Journal.Count;
        f.Source.Daily(540, notify: false);
        f.Source.Kina(40, 1); f.Source.Kina(40, 1); f.Source.Notify(); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, current.Kind); Assert.Equal(40, current.KinaBound); Assert.Null(current.KinaUnbound);
        var bin = Assert.Single(current.MinuteIncome); Assert.Equal(40, bin.KinaBound); Assert.Null(bin.KinaUnbound);
        var fact = Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
        Assert.Equal(40, fact.Amount); Assert.Equal(LootTrackerActivityOrigin.Pve, fact.Origin);
        Assert.DoesNotContain(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaUnbound);
        Assert.False(current.TimelineEvents.Last(row => row.Kind == LootTrackerTimelineEventKind.Availability
            && row.Metric == LootTrackerTimelineMetric.KinaUnbound).Available);
        Assert.True(current.TimelineEvents.Last(row => row.Kind == LootTrackerTimelineEventKind.Availability
            && row.Metric == LootTrackerTimelineMetric.KinaBound).Available);
        Assert.Equal(before + 1, f.Journal.Count); Assert.Single(f.Starts); Assert.Empty(f.Accepted);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(40, recovered.KinaBound); Assert.Null(recovered.KinaUnbound);
        Assert.Equal(fact.Id, Assert.Single(recovered.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound).Id);
        Assert.Equal(40, Assert.Single(recovered.MinuteIncome).KinaBound);
    }

    [Fact]
    public async Task PveBoundIncomeResumesTheSameSessionAndExcludesTheInactiveInterval()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Daily(0); f.Source.Kina(40, 1); await f.Ready();
        var original = Assert.Single(f.Service.Snapshot.Sessions);
        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        f.Source.Kina(10, 2); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(original.Id, resumed.Id); Assert.False(resumed.IsPaused);
        Assert.Equal(50, resumed.KinaBound); Assert.Null(resumed.KinaUnbound); Assert.Equal(TimeSpan.Zero, resumed.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Source.Kina(5, 3); await f.Ready();
        var after = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(55, after.KinaBound); Assert.Equal(TimeSpan.FromSeconds(10), after.Elapsed);
    }

    [Theory]
    [InlineData(1_000L, 1L)] [InlineData(1L, 1_000L)]
    public async Task PveAndAbyssBoundIncomeKeepIndependentWatermarksAcrossTheirWorldContexts(long pveSequence, long abyssSequence)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Kina(10, pveSequence); await f.Ready();
        f.SetWorld(true); await f.Ready();
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        await f.Ready(); f.Source.BoundKina(20, abyssSequence); f.Source.BoundKina(20, abyssSequence); await f.Ready();
        f.SetWorld(false); await f.Ready();
        f.Source.Kina(10, pveSequence); await f.Ready();
        Assert.DoesNotContain(f.Service.Snapshot.Sessions, session => session.Status == LootTrackerSessionStatus.Live);
        f.Source.Kina(30, pveSequence + 1); f.Source.Kina(30, pveSequence + 1); await f.Ready();
        var sessions = f.Service.Snapshot.Sessions;
        Assert.Equal(3, sessions.Length); Assert.Equal(60, sessions.Sum(session => session.KinaBound ?? 0));
        Assert.All(sessions, session =>
            Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound));
        Assert.Equal(20, Assert.Single(sessions, session => session.Kind == LootTrackerSessionKind.Abyss).KinaBound);
    }

    [Fact]
    public async Task NewPveBoundSessionsLeaveStoredUnboundHistoryAndItsJournalBytesUntouched()
    {
        var at = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        var fact = new LootTrackerTimelineEvent(Guid.NewGuid().ToString("N"), LootTrackerTimelineEventKind.KinaUnbound,
            at.AddSeconds(10), TimeSpan.FromSeconds(10)) { Origin = LootTrackerActivityOrigin.Pve, Amount = 50 };
        var old = new LootTrackerSession
        {
            Id = Guid.NewGuid().ToString("N"), UnifiedVersion = 1, TimelineVersion = 1,
            Kind = LootTrackerSessionKind.Mixed, Status = LootTrackerSessionStatus.Completed,
            Character = new(new string('a', 32), "Synthetic character"), StartedAt = at,
            LastLootAt = at.AddSeconds(10), EndedAt = at.AddSeconds(10), Elapsed = TimeSpan.FromSeconds(10),
            KinaUnbound = 50, KinaBound = null, ItemCount = 0, SoulCount = 0,
            TimelineEvents = [fact], MinuteIncome = [new(0, null, 50)]
        };
        await using var f = new Fixture(storedHistory: [old]); await f.Ready();
        var journal = Path.Combine(f.SessionRoot, old.Id, "events-0001.jsonl"); var original = File.ReadAllBytes(journal);
        f.Source.Daily(0); f.Source.Kina(25, 1); await f.Ready();
        var historical = Assert.Single(f.Service.Snapshot.Sessions, session => session.Id == old.Id);
        Assert.Equal(50, historical.KinaUnbound); Assert.Null(historical.KinaBound);
        Assert.Equal(original, File.ReadAllBytes(journal));
        var restored = new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions;
        var restoredOld = Assert.Single(restored, session => session.Id == old.Id);
        Assert.Equal(50, restoredOld.KinaUnbound); Assert.Null(restoredOld.KinaBound);
        Assert.Equal(fact.Id, Assert.Single(restoredOld.TimelineEvents).Id);
        var current = Assert.Single(restored, session => session.Id != old.Id);
        Assert.Equal(25, current.KinaBound); Assert.Null(current.KinaUnbound);
    }

    [Fact]
    public async Task FailedPveBoundIncomeAppendCannotPersistAPartialCurrencySumOrFact()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready(); f.Source.Daily(0); await f.Ready();
        f.FailAppend = true; f.Source.Kina(40, 1); await f.Ready();
        Assert.True(f.Service.StatusIsError);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(1, restored.ItemCount); Assert.Null(restored.KinaBound); Assert.Null(restored.KinaUnbound);
        Assert.Empty(restored.MinuteIncome);
        Assert.DoesNotContain(restored.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
    }
}
