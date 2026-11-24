using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionAnimationTimingTests
{
    [Fact]
    public void DrawingTimeIsSubtractedInsteadOfBeingAddedToEachFrameInterval()
    {
        var pacing = new DaevanionAnimationPacing(30000);
        long started = 30000;
        var initial = started;
        for (var frame = 0; frame < 120; frame++)
        {
            var finished = started + 240; // Eight milliseconds raster/transfer work.
            var delay = pacing.AfterFrame(started, finished);
            Assert.InRange(delay, 24, 26);
            started = finished + delay * 30;
        }
        Assert.InRange((started - initial) / 30d, 4000, 4001);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(120)]
    [InlineData(1500)]
    public void DelayedUiFramesSkipMissedSlotsWithoutCatchUpBursts(int drawingMilliseconds)
    {
        var pacing = new DaevanionAnimationPacing(30000);
        var started = 30000L;
        var finished = started + drawingMilliseconds * 30;
        var delay = pacing.AfterFrame(started, finished);
        Assert.InRange(delay, 4, 37);
        var nextStarted = finished + delay * 30;
        var nextDelay = pacing.AfterFrame(nextStarted, nextStarted + 180);
        Assert.InRange(nextDelay, 26, 28);
    }

    [Fact]
    public void AHiddenAnimationRestartsItsDeadlineInsteadOfReplayingAnOldSchedule()
    {
        var pacing = new DaevanionAnimationPacing(30000);
        _ = pacing.AfterFrame(30000, 30240);
        pacing.Reset();
        Assert.Equal(26, pacing.AfterFrame(900000, 900240));
    }

    [Fact]
    public void PresentationTelemetryIsPassiveAndExcludesTimeSpentSuppressed()
    {
        var telemetry = new DaevanionAnimationTelemetry();
        Assert.Equal(0, telemetry.Snapshot.PresentedFrames);
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        telemetry.Presented(at, 2, 3, 6, 1);
        telemetry.Presented(at + System.Diagnostics.Stopwatch.Frequency / 20, 1, 2, 4, 1);
        telemetry.NextDelay(28);
        Assert.Equal(2, telemetry.Snapshot.PresentedFrames);
        Assert.InRange(telemetry.Snapshot.LastFrameIntervalMilliseconds, 49.999, 50.001);
        Assert.Equal(2, telemetry.Snapshot.MaximumRasterMilliseconds);
        Assert.Equal(3, telemetry.Snapshot.MaximumTransferMilliseconds);
        Assert.Equal(1, telemetry.Snapshot.NativeBufferAllocations);
        Assert.Equal(28, telemetry.Snapshot.NextFrameDelayMilliseconds);
        telemetry.Suppressed();
        telemetry.Presented(at + System.Diagnostics.Stopwatch.Frequency * 2, 1, 2, 4, 2);
        Assert.Equal(0, telemetry.Snapshot.LastFrameIntervalMilliseconds);
        Assert.Equal(1, telemetry.Snapshot.Suppressions);
        Assert.Equal(3, telemetry.Snapshot.PresentedFrames);
    }

    [Fact]
    public void CompleteTickMeasurementsRemainIndependentOfSuccessfulPresentations()
    {
        var telemetry = new DaevanionAnimationTelemetry();
        Assert.Equal(0, telemetry.Snapshot.AnimationTicks);
        Assert.Equal(0, telemetry.Snapshot.LastTickMilliseconds);
        telemetry.Presented(System.Diagnostics.Stopwatch.GetTimestamp(), 2, 3, 6, 1);
        // The initial authority read and work after the native transfer are outside LastTotal.
        telemetry.TickCompleted(24, 11);
        Assert.Equal(24, telemetry.Snapshot.LastTickMilliseconds);
        Assert.Equal(11, telemetry.Snapshot.LastInitialSourceValidationMilliseconds);
        Assert.Equal(6, telemetry.Snapshot.LastTotalMilliseconds);
        telemetry.TickCompleted(15, 2);
        Assert.Equal(24, telemetry.Snapshot.MaximumTickMilliseconds);
        Assert.Equal(11, telemetry.Snapshot.MaximumInitialSourceValidationMilliseconds);
        Assert.Equal(15, telemetry.Snapshot.LastTickMilliseconds);
        Assert.Equal(2, telemetry.Snapshot.LastInitialSourceValidationMilliseconds);

        // A source rejection must report its validation cost even though nothing was drawn.
        telemetry.Suppressed();
        telemetry.TickCompleted(12, 12);
        Assert.Equal(3, telemetry.Snapshot.AnimationTicks);
        Assert.Equal(1, telemetry.Snapshot.PresentedFrames);
        Assert.Equal(1, telemetry.Snapshot.Suppressions);
        Assert.Equal(12, telemetry.Snapshot.LastTickMilliseconds);
        Assert.Equal(12, telemetry.Snapshot.LastInitialSourceValidationMilliseconds);
        Assert.Equal(12, telemetry.Snapshot.MaximumInitialSourceValidationMilliseconds);
        Assert.Equal(0, telemetry.Snapshot.LastFrameIntervalMilliseconds);
    }
}
