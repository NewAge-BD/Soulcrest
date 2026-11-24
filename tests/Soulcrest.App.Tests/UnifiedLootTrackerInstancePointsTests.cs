using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData(WorldContextKind.Instance)]
    [InlineData(WorldContextKind.Unknown)]
    public async Task ConfirmedOwnMapAllowsApOnlyWithoutAuthorizingOtherLootChannels(WorldContextKind kind)
    {
        await using var f = new Fixture();
        f.WorldKind = kind;
        f.Category = kind == WorldContextKind.Instance ? "Party" : null;
        f.MapDataId = 27;
        f.Source.InInstance = true;
        f.Source.Daily(1_000_000);
        await f.Ready();
        Assert.True(f.Service.SourceAvailable);
        Assert.Empty(f.Service.Snapshot.Sessions);

        void OtherChannels(long sequence)
        {
            f.Source.Drop(7, 2, sequence);
            f.Source.Harvest(sequence, new NetworkGatheringItem(7, 3));
            f.Source.Kina(5, sequence);
            f.Source.Rank(10, sequence);
            f.Source.Combat(NetworkAbyssCombatKind.Kill, sequence);
        }
        OtherChannels(1);
        await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        Assert.Empty(f.Journal);

        f.Source.Points(14, 1); f.Source.Points(14, 1);
        await f.Ready();
        var first = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, first.Kind);
        Assert.Equal(kind, f.World().Kind);
        Assert.Equal(LootTrackerSessionMode.Normal, first.Mode);
        Assert.Equal(14, first.AbyssPoints);
        Assert.Null(first.KinaBound); Assert.Null(first.KinaUnbound);
        Assert.Null(first.HarvestCount); Assert.Null(first.FailedHarvestCount);
        Assert.Null(first.RankPointsGained); Assert.Null(first.RankPointsLost);
        Assert.Empty(first.Loot); Assert.Empty(first.CombatEvents); Assert.Empty(first.RouteSegments);
        Assert.Empty(f.Accepted);

        OtherChannels(2);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Source.Points(3, 2); f.Source.Notify();
        await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, current.Id);
        Assert.Equal(LootTrackerSessionStatus.Live, current.Status);
        Assert.Equal(17, current.AbyssPoints);
        Assert.Equal(TimeSpan.FromSeconds(10), current.Elapsed);
        Assert.Equal(new long?[] { 14, 3 }, current.TimelineEvents
            .Where(fact => fact.Kind == LootTrackerTimelineEventKind.AbyssPoints).Select(fact => fact.Amount));
        Assert.All(current.TimelineEvents.Where(fact => fact.Kind != LootTrackerTimelineEventKind.Availability),
            fact => Assert.Equal(LootTrackerTimelineEventKind.AbyssPoints, fact.Kind));
        Assert.DoesNotContain(current.TimelineEvents, fact => fact.Metric == LootTrackerTimelineMetric.RankPoints);
        var native = UnifiedLootOverlaySource.Project(f.Service.OverlayState, AbyssProgressSnapshot.Empty, new AppSettings());
        Assert.True(native.SessionActive); Assert.True(native.HasAbyssPointActivity);
        Assert.Equal(17, native.SessionAbyssPoints); Assert.Equal(6_120d, native.SessionAbyssPointsRate);
        Assert.Null(native.HarvestCount); Assert.False(native.SessionKinaKnown);
        Assert.Empty(f.Accepted); Assert.Single(f.Starts);

        f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
        Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        f.Source.Points(5, 3); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(first.Id, resumed.Id); Assert.False(resumed.IsPaused); Assert.Equal(22, resumed.AbyssPoints);
        Assert.Equal(TimeSpan.FromSeconds(10), resumed.Elapsed);
        f.Clock.Advance(TimeSpan.FromSeconds(120)); await f.Ready();
        var ended = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, ended.Status);
        Assert.Equal(22, ended.AbyssPoints); Assert.Equal(TimeSpan.FromSeconds(10), ended.Elapsed);
        Assert.Equal(LootTrackerSessionMode.Normal, ended.Mode);
        Assert.Equal(22, Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).AbyssPoints);
    }

    [Theory]
    [InlineData(WorldContextKind.Instance)]
    [InlineData(WorldContextKind.Unknown)]
    public async Task ApOnlyWorldCannotStartAPositionSidecarOrCommitMatchingVisualSamples(WorldContextKind kind)
    {
        await using var f = new Fixture(replayPositions: true);
        f.WorldKind = kind; f.MapDataId = 27;
        f.Category = kind == WorldContextKind.Instance ? "Party" : null;
        f.Source.InInstance = true;
        f.Source.Notify(); await f.Ready();
        f.Source.Points(14, 1); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Ready();
        f.PositionRecorder!.Observe([new("sample-map", 1, 2, f.Clock.GetUtcNow(), f.Clock.GetTimestamp(),
            1, 1, 1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, ReplayPositionProvenance.Matched, 1)]);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Points(3, 2); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(17, session.AbyssPoints);
        Assert.Single(f.Starts); Assert.Empty(session.RouteSegments);
        Assert.Equal(0, f.PositionRecorder.PendingSampleCount);
        await ReplayPositionStore.FlushAsync(f.SessionRoot, id);
        var replay = ReplayPositionStore.Read(f.SessionRoot, id, session.Elapsed);
        Assert.False(replay.Exists); Assert.Empty(replay.Samples); Assert.Empty(replay.Gaps);
    }

    [Fact]
    public async Task ApRequiresAnOwnConfirmedPositiveMapIdAndCannotRecoverEarlierUnboundIncome()
    {
        await using var f = new Fixture();
        f.WorldKind = WorldContextKind.Unknown; f.MapDataId = 0;
        f.Source.Notify(); await f.Ready();
        f.Source.Points(10, 1); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Empty(f.Journal);
        f.MapDataId = 27; f.WorldGeneration++;
        f.Source.Notify(); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Points(3, 2); await f.Ready();
        Assert.Equal(3, Assert.Single(f.Service.Snapshot.Sessions).AbyssPoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedApReceiptKeepsItsOriginalTimesWhenTheNewWorldIsObservedLater(bool abyss)
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        var old = Assert.Single(f.Service.Snapshot.Sessions);
        f.WorldKind = WorldContextKind.Instance; f.Category = abyss ? "Abyss" : "Party";
        f.Source.InInstance = true; f.WorldGeneration++;
        var receiptAt = f.Clock.GetUtcNow();
        f.BeforeBind = () =>
        {
            // Delay profile binding/queue delivery without inserting a separate timer command.
            f.Clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
            f.Source.SetAbsolute(f.Source.AbyssSnapshot with
            {
                At = f.Clock.GetUtcNow(), RankPointsObservedAt = f.Clock.GetUtcNow(),
                AbyssPointBalanceObservedAt = receiptAt, FarmedAbyssPointsObservedAt = receiptAt
            }, notify: false);
        };
        f.Source.Points(4, 1); await f.Ready();
        var current = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(old.Id, current.Id);
        Assert.Equal(abyss ? LootTrackerSessionKind.Abyss : LootTrackerSessionKind.Mixed, current.Kind);
        Assert.Equal(receiptAt, current.StartedAt);
        Assert.Equal(4, current.AbyssPoints);
        var fact = Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.AbyssPoints);
        Assert.Equal(receiptAt, fact.At); Assert.Equal(TimeSpan.Zero, fact.Elapsed);
        var previous = Assert.Single(f.Service.Snapshot.Sessions, row => row.Id == old.Id);
        Assert.Null(previous.AbyssPoints);
        Assert.Equal(LootTrackerSessionStatus.Completed, previous.Status);
        if (abyss)
        {
            var rankAvailable = Assert.Single(current.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.Availability
                && row.Metric == LootTrackerTimelineMetric.RankPoints && row.Available == true);
            Assert.Equal(f.Clock.GetUtcNow(), rankAvailable.At);
            Assert.True(rankAvailable.At > fact.At);
        }
        Assert.Null(f.Service.RecordingFault);
    }

    [Fact]
    public async Task EnteringAnOrdinaryInstanceOnTheSameMapEndsThePreviousOpenWorldSession()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        var openWorld = Assert.Single(f.Service.Snapshot.Sessions);
        f.WorldKind = WorldContextKind.Instance; f.Category = "Party";
        f.Source.InInstance = true; f.WorldGeneration++;
        // The real 0x6100 path changes the world before Network.Changed or the next timer tick.
        f.Source.Points(4, 1); await f.Ready();
        var previous = Assert.Single(f.Service.Snapshot.Sessions, session => session.Id == openWorld.Id);
        Assert.Equal(LootTrackerSessionStatus.Completed, previous.Status);
        Assert.Null(previous.AbyssPoints);
        Assert.DoesNotContain(previous.TimelineEvents, fact => fact.Kind == LootTrackerTimelineEventKind.AbyssPoints);
        var instance = Assert.Single(f.Service.Snapshot.Sessions, session => session.Status == LootTrackerSessionStatus.Live);
        Assert.NotEqual(openWorld.Id, instance.Id);
        Assert.Equal(4, instance.AbyssPoints); Assert.Empty(instance.Loot); Assert.Null(instance.KinaBound);
        f.Source.Notify(); await f.Ready();
        Assert.Equal(instance.Id, Assert.Single(f.Service.Snapshot.Sessions,
            session => session.Status == LootTrackerSessionStatus.Live).Id);
    }
}
