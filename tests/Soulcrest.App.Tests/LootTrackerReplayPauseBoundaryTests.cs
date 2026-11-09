using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerReplayPauseBoundaryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static LootTrackerSession Session => new()
    {
        Id = "synthetic-paused-replay", Kind = LootTrackerSessionKind.Mixed,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddSeconds(72),
        Elapsed = TimeSpan.FromSeconds(12)
    };
    private static ReplayPositionSample Sample(int second, double x, int wallSecond) => new(
        TimeSpan.FromSeconds(second), At.AddSeconds(wallSecond), "map", "Synthetic map", x, 100,
        ReplayPositionProvenance.Matched, 1, second);

    [Theory]
    [InlineData(10)]
    [InlineData(10.5)]
    public void AnExplicitPauseNeverBecomesASolidOrEstimatedConnection(double pauseAt)
    {
        var positions = new ReplayPositionReadResult([
            Sample(9, 90, 9), Sample(10, 100, 10), Sample(11, 500, 71), Sample(12, 510, 72)
        ], [new(TimeSpan.FromSeconds(pauseAt), ReplayPositionGapReason.SessionPaused)], "synthetic", true, false);
        var prepared = LootTrackerPreparedReplay.Prepare(Session, positions);
        Assert.Equal(2, prepared.Routes.Length);
        Assert.Equal(new double[] { 90, 100 }, prepared.Routes[0].Points.Select(point => point.X));
        Assert.Equal(new double[] { 500, 510 }, prepared.Routes[1].Points.Select(point => point.X));
        Assert.Empty(prepared.Connections);
        Assert.Empty(prepared.DisplayConnections);
    }

    [Fact]
    public void OrdinarySameMapSourceGapsKeepTheirExistingEstimatedConnections()
    {
        var positions = new ReplayPositionReadResult([
            Sample(9, 90, 9), Sample(10, 100, 10), Sample(11, 500, 71), Sample(12, 510, 72)
        ], [new(TimeSpan.FromSeconds(10.5), ReplayPositionGapReason.FlowBroken)], "synthetic", true, false);
        var prepared = LootTrackerPreparedReplay.Prepare(Session, positions);
        Assert.Equal(2, prepared.Routes.Length);
        Assert.Single(prepared.Connections);
    }

    [Fact]
    public void LowFrequencyPauseBoundaryIsRetainedWithoutAPositionSidecarAndChangesItsCacheIdentity()
    {
        var before = new LootTrackerRouteSegment("map", "Synthetic map",
            [new(90, 100, At.AddSeconds(9), TimeSpan.FromSeconds(9))]);
        var after = new LootTrackerRouteSegment("map", "Synthetic map",
            [new(500, 100, At.AddSeconds(71), TimeSpan.FromSeconds(11))]);
        var ordinary = Session with { RouteSegments = [before, after] };
        var paused = ordinary with { RouteSegments = [before, after with { StartsAfterPause = true }] };
        Assert.Single(LootTrackerPreparedReplay.Prepare(ordinary).Connections);
        Assert.Empty(LootTrackerPreparedReplay.Prepare(paused).Connections);
        Assert.NotEqual(LootTrackerPreparedReplay.Fingerprint(ordinary), LootTrackerPreparedReplay.Fingerprint(paused));
        Assert.Equal(LootTrackerPreparedReplay.Fingerprint(ordinary),
            LootTrackerPreparedReplay.Fingerprint(ordinary with { RouteSegments = [before, after with { StartsAfterPause = false }] }));
    }
}
