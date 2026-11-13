using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task BoundAndUnboundAbyssIncomeKeepIndependentWatermarksAndAtomicStoredFacts()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        var original = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Null(original.KinaBound);
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        await f.Ready();
        Assert.Equal(0, Assert.Single(f.Service.Snapshot.Sessions).KinaBound);
        var before = f.Journal.Count;

        f.Source.BoundKina(75, 1); f.Source.BoundKina(75, 1); await f.Ready();
        Assert.Equal(before + 1, f.Journal.Count);
        f.Source.Kina(25, 1); f.Source.Kina(25, 1); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(original.Id, current.Id); Assert.Equal(75, current.KinaBound); Assert.Equal(25, current.KinaUnbound);
        Assert.Equal(75, Assert.Single(current.MinuteIncome).KinaBound);
        Assert.Equal(25, Assert.Single(current.MinuteIncome).KinaUnbound);
        var bound = Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
        Assert.Equal(75, bound.Amount); Assert.Equal(LootTrackerActivityOrigin.Abyss, bound.Origin);
        Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaUnbound);
        Assert.Empty(f.Accepted);

        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(75, restored.KinaBound); Assert.Equal(25, restored.KinaUnbound);
        var restoredFact = Assert.Single(restored.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
        Assert.Equal(bound.Id, restoredFact.Id); Assert.Equal(bound.Amount, restoredFact.Amount);
    }

    [Theory]
    [InlineData("unqualified")] [InlineData("missing-time")] [InlineData("default-time")]
    [InlineData("future-time")] [InlineData("old-own")] [InlineData("stopped")]
    public async Task BoundAbyssIncomeRequiresItsOwnQualifiedCurrentSourceFact(string variant)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        var original = f.Source.OwnCharacter;
        if (variant == "old-own") { f.Source.Switch(1); await f.Ready(); }
        var fact = variant == "missing-time" ? (DateTimeOffset?)null
            : variant == "default-time" ? default(DateTimeOffset)
            : f.Clock.GetUtcNow().AddSeconds(variant == "future-time" ? 1 : 0);
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        {
            Character = variant == "old-own" ? original : f.Source.OwnCharacter,
            BoundKinaGainsQualified = variant != "unqualified", BoundKinaGainsObservedAt = fact
        });
        if (variant == "stopped") { f.Source.Running = false; f.Source.Notify(); }
        await f.Ready(); f.Source.BoundKina(90, 1); await f.Ready();
        Assert.All(f.Service.Snapshot.Sessions, session =>
        {
            Assert.True(session.KinaBound is null or 0);
            Assert.DoesNotContain(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
        });
    }

    [Fact]
    public async Task BoundQualificationAndItsRevocationDoNotReplaceTheUnboundChannelOrPastGains()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { KinaGainsQualified = false, BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        await f.Ready();
        f.Source.Kina(100, 1); f.Source.BoundKina(40, 1); await f.Ready();
        var first = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(40, first.KinaBound); Assert.Equal(0, first.KinaUnbound);
        Assert.DoesNotContain(first.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaUnbound);
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with { BoundKinaGainsQualified = false, KinaGainsQualified = true });
        await f.Ready(); f.Source.BoundKina(60, 2); f.Source.Kina(10, 1); await f.Ready();
        var after = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(40, after.KinaBound); Assert.Equal(10, after.KinaUnbound);
        Assert.False(after.TimelineEvents.Last(row => row.Kind == LootTrackerTimelineEventKind.Availability
            && row.Metric == LootTrackerTimelineMetric.KinaBound).Available);
    }

    [Fact]
    public async Task BoundIncomeCannotOpenAnOutsideSessionAndDisabledEchoesCannotReturnAfterReenable()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        f.Source.BoundKina(90, 1); await f.Ready(); Assert.Empty(f.Service.Snapshot.Sessions);
        f.SetWorld(true); await f.Ready();
        f.Settings.Current.AbyssTrackingEnabled = false;
        f.Source.BoundKina(90, 1); await f.Ready();
        f.Settings.Current.AbyssTrackingEnabled = true;
        f.Source.BoundKina(90, 1); f.Source.BoundKina(15, 2); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(15, current.KinaBound);
        Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
    }

    [Fact]
    public async Task NewBoundIncomeKeepsOlderUnboundSessionsAndUnknownBoundHistoryUnchanged()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.Kina(35, 1); await f.Ready();
        var oldId = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.SetWorld(false); await f.Ready(); f.SetWorld(true); await f.Ready();
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        await f.Ready(); f.Source.BoundKina(55, 1); await f.Ready();
        var restored = new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions;
        var old = Assert.Single(restored, session => session.Id == oldId);
        Assert.Null(old.KinaBound); Assert.Equal(35, old.KinaUnbound);
        Assert.DoesNotContain(old.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
        Assert.Equal(55, Assert.Single(restored, session => session.Id != oldId).KinaBound);
    }

    [Fact]
    public async Task FailedBoundIncomeAppendCannotPersistOnlyItsSumMinuteOrTimelineFact()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.SetAbsolute(f.Source.AbyssSnapshot with
        { BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = f.Clock.GetUtcNow() });
        await f.Ready(); f.FailAppend = true;
        f.Source.BoundKina(90, 1); await f.Ready();
        Assert.True(f.Service.StatusIsError);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(0, restored.KinaBound); Assert.Empty(restored.MinuteIncome);
        Assert.DoesNotContain(restored.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.KinaBound);
    }
}
