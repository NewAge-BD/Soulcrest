using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class WorldMapFrameWaitTests
{
    [Theory]
    [InlineData(0, 0, 0, 1000, 1000.0 / 60)]
    [InlineData(10, 0, 0, 1000, 1000.0 / 60 - 10)]
    [InlineData(17, 0, 0, 1000, 0)]
    [InlineData(0, 98, 0, 1000, 2)]
    [InlineData(0, 100, 0, 1000, 0)]
    [InlineData(0, 0, 249, 250, 1)]
    [InlineData(0, 0, 250, 250, 0)]
    [InlineData(0, 0, 1001, 1000, 0)]
    [InlineData(15, 99, 249, 250, 1)]
    public void WaitingCannotPassTheFrameBudgetOrEmblemAndDetectionDeadlines(double spent, double emblem,
        double detection, int interval, double expected)
    {
        Assert.Equal(expected, MapTrackingService.WorldMapFrameWaitMs(spent, emblem, detection, interval), precision: 6);
    }

    [Fact]
    public async Task MissingCaptureDoesNotStartAWindowOrModifyGrabMetadata()
    {
        using var service = new GameCaptureService();
        var previous = new CaptureTiming(true, 0, 0, 0, 0, 1, SourceFrameId: 7, CaptureSessionId: 3);
        GameCaptureService.GrabbedTiming = previous;
        var wait = service.WaitForFrameAfterAsync(previous, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
        Assert.Equal(previous, GameCaptureService.GrabbedTiming);
        Assert.Null(await wait);
        Assert.Equal(0, service.Diagnostics.Grabs);
        Assert.Equal(0, service.Diagnostics.FramesArrived);
        Assert.Equal("GDI", service.Method);
        GameCaptureService.GrabbedTiming = default;
    }

    [Fact]
    public void CancellationIsObservedEvenWithoutAnActiveCapture()
    {
        using var service = new GameCaptureService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.WaitForFrameAfterAsync(default, TimeSpan.FromMilliseconds(10), cancellation.Token));
    }
}
