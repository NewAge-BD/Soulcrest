using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerDropPositionReplayTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0d, -.5d)] [InlineData(10d, 9d)]
    public void PositionedDropWithoutARouteProvidesTheConfirmedMapOnlyAfterItsFact(double factSecond, double observedSecond)
    {
        var position = new LootTrackerCombatPosition("sample-map", "Synthetic map", 123.123456789, 456.987654321, At.AddSeconds(observedSecond));
        var session = new LootTrackerSession
        {
            Id = "drop-only-example", Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Completed,
            StartedAt = At, EndedAt = At.AddSeconds(20), Elapsed = TimeSpan.FromSeconds(20), TimelineVersion = 1,
            TimelineEvents = [new("drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(factSecond), TimeSpan.FromSeconds(factSecond))
                { Position = position, Loot = [new("Item:7", "Synthetic item", LootTrackerLootKind.Item, 1)] }]
        };
        var prepared = LootTrackerPreparedReplay.Prepare(session);
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(factSecond), prepared);
        Assert.Equal(position.MapId, replay.MapId); Assert.Equal(position, replay.PlayerPosition);
        Assert.Equal(TimeSpan.FromSeconds(observedSecond), replay.LastPositionElapsed);
        Assert.Empty(replay.PreparedParts); Assert.Empty(replay.EstimatedConnections);
        if (factSecond > 0)
        {
            var earlier = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(factSecond - .1), prepared);
            Assert.Empty(earlier.Session.TimelineEvents); Assert.Null(earlier.MapId); Assert.Null(earlier.PlayerPosition);
        }
    }
}
