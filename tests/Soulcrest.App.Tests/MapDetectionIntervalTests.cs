using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Standing still the map detection slows down (up to 2 s); anything unsure keeps 250 ms, moving too except
/// on the world map (1 s, the flow carries the overlay there).
/// </summary>
public sealed class MapDetectionIntervalTests
{
    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(9, 2000)]
    public void QuietDetectionsDoubleTheGap(int stillRuns, int expected) =>
        Assert.Equal(expected, MapTrackingService.DetectionIntervalMs(found: true, worldMap: false, misses: 0, motionPixels: 0.4, flowLost: false, stillRuns));

    [Fact]
    public void WorldMapDetectsLessOftenWhileTheFlowHoldsIt()
    {
        Assert.Equal(1000, MapTrackingService.DetectionIntervalMs(true, worldMap: true, 0, motionPixels: 3, flowLost: false, stillRuns: 0));
        Assert.Equal(2000, MapTrackingService.DetectionIntervalMs(true, worldMap: true, 0, motionPixels: 0.4, flowLost: false, stillRuns: 5));
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(true, worldMap: true, 0, motionPixels: 3, flowLost: true, stillRuns: 5)); // closed
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(true, worldMap: true, misses: 1, 3, false, 5));
    }

    [Fact]
    public void MovementLostFlowOrUnsurePositionKeep250()
    {
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(true, false, 0, motionPixels: 3, flowLost: false, stillRuns: 5));
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(true, false, 0, 0, flowLost: true, stillRuns: 5));
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(found: false, false, 0, 0, false, 5));
        Assert.Equal(250, MapTrackingService.DetectionIntervalMs(true, false, misses: 1, 0, false, 5));
    }
}
