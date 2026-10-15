using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Timing metadata only: no native capture, desktop window or app instance is started.</summary>
public sealed class CaptureTimingTests
{
    [Fact]
    public void StopwatchTicksUseTheirOwnFrequencyAndOuterWaitIsAddedOnlyOnce()
    {
        var second = Stopwatch.Frequency;
        var inner = CaptureTiming.FromTicks(second, 2 * second, 3 * second, 4 * second, 12 * second);
        var complete = inner.WithOuterTiming(5 * second, 20 * second);

        Assert.True(complete.Valid);
        Assert.Equal(6000, complete.GateWaitMs, precision: 6);
        Assert.Equal(2000, complete.CopyMs, precision: 6);
        Assert.Equal(3000, complete.MapWaitMs, precision: 6);
        Assert.Equal(4000, complete.ConversionMs, precision: 6);
        Assert.Equal(20000, complete.TotalMs, precision: 6);
        Assert.Equal(1000, inner.GateWaitMs, precision: 6);
        Assert.Equal(12000, inner.TotalMs, precision: 6);
    }

    [Fact]
    public void MissingWgcPictureCannotBecomeValidFromOuterTimingAlone()
    {
        var missing = default(CaptureTiming).WithOuterTiming(Stopwatch.Frequency, 2 * Stopwatch.Frequency);

        Assert.Equal(default, missing);
        Assert.True(CaptureTiming.FromTicks(0, 0, 0, 0, 0).Valid);
    }

    [Fact]
    public void SelectedFrameAgeAndIdentitySurviveOuterTimingAndNewerPublishedFrames()
    {
        var second = Stopwatch.Frequency;
        var selected = CaptureTiming.FromTicks(0, 0, 0, 0, second)
            .WithSourceFrame(10 * second, 12 * second, frameId: 7, arrived: 11, published: 9, frameGateDrops: 2, sessionId: 3);
        var complete = selected.WithOuterTiming(second, 4 * second);

        Assert.Equal(2000, complete.SourceAgeAtCopyMs, precision: 6);
        Assert.Equal(7, complete.SourceFrameId); // The read can precede later publications while GPU conversion runs.
        Assert.Equal(11, complete.ArrivedCount);
        Assert.Equal(9, complete.PublishedCount);
        Assert.Equal(2, complete.FrameGateDropCount);
        Assert.Equal(3, complete.CaptureSessionId);
        Assert.Equal(selected.SourceAgeAtCopyMs, complete.SourceAgeAtCopyMs);
        Assert.Equal(4000, complete.TotalMs, precision: 6);
    }

    [Fact]
    public void SourceMetadataCannotMakeMissingPictureValidAndSourceAgeCannotBeNegative()
    {
        var missing = default(CaptureTiming)
            .WithSourceFrame(1, 2, frameId: 3, arrived: 4, published: 3, frameGateDrops: 1, sessionId: 1);
        var future = CaptureTiming.FromTicks(0, 0, 0, 0, 0)
            .WithSourceFrame(2, 1, frameId: 3, arrived: 4, published: 3, frameGateDrops: 1, sessionId: 1);

        Assert.Equal(default, missing);
        Assert.Equal(0, future.SourceAgeAtCopyMs);
        Assert.True(future.Valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedGrabClearsPreviousSuccessfulTimingBeforeCheckingDisposal(bool windowOnly)
    {
        using var capture = new GameCaptureService();
        capture.Dispose();
        GameCaptureService.GrabbedTiming = SuccessfulTiming;
        try
        {
            if (windowOnly)
                Assert.Throws<ObjectDisposedException>(() => capture.TryGrabGameWindowOnly(Rectangle.Empty));
            else
                Assert.Throws<ObjectDisposedException>(() => capture.Grab(Rectangle.Empty));

            Assert.Equal(default, GameCaptureService.GrabbedTiming);
        }
        finally { GameCaptureService.GrabbedTiming = default; }
    }

    [Fact]
    public void MissingStrictWindowPictureClearsPreviousTimingWithoutStartingCapture()
    {
        using var capture = new GameCaptureService();
        GameCaptureService.GrabbedTiming = SuccessfulTiming;
        try
        {
            Assert.Null(capture.TryGrabGameWindowOnly(Rectangle.Empty));
            Assert.Equal(default, GameCaptureService.GrabbedTiming);
            Assert.Equal("GDI", capture.Method);
            Assert.Equal(0, capture.Diagnostics.FramesArrived);
        }
        finally { GameCaptureService.GrabbedTiming = default; }
    }

    [Fact]
    public void AnotherCaptureThreadCannotOverwriteTheCallersSnapshot()
    {
        GameCaptureService.GrabbedTiming = SuccessfulTiming;
        CaptureTiming initial = SuccessfulTiming;
        var other = new Thread(() =>
        {
            initial = GameCaptureService.GrabbedTiming;
            GameCaptureService.GrabbedTiming = new(true, 9, 8, 7, 6, 30);
        }) { IsBackground = true };
        try
        {
            other.Start();
            Assert.True(other.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(default, initial);
            Assert.Equal(SuccessfulTiming, GameCaptureService.GrabbedTiming);
        }
        finally { GameCaptureService.GrabbedTiming = default; }
    }

    private static CaptureTiming SuccessfulTiming => new(true, 1, 2, 3, 4, 12, 5, 7, 11, 9, 2, 3);
}
