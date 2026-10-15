using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure managed notifications: no capture, native window or app instance is started.</summary>
public sealed class CaptureFrameSignalTests
{
    [Fact]
    public async Task PublicationBeforeRegistrationIsImmediateAndNotConsumed()
    {
        var signal = new CaptureFrameSignal();
        signal.Publish(3);
        signal.Publish(7);

        var first = signal.WaitAfterAsync(2, TimeSpan.Zero);
        var second = signal.WaitAfterAsync(2, TimeSpan.Zero);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(7L, await first);
        Assert.Equal(7L, await second);
        Assert.Null(await signal.WaitAfterAsync(7, TimeSpan.Zero));
    }

    [Fact]
    public async Task EveryPendingReaderReceivesThePublicationIndependently()
    {
        var signal = new CaptureFrameSignal();
        var readers = Enumerable.Range(0, 12)
            .Select(_ => signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan).AsTask()).ToArray();

        Assert.All(readers, reader => Assert.False(reader.IsCompleted));
        signal.Publish(1);

        Assert.All(await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.Equal(1L, result));
        Assert.Equal(1L, await signal.WaitAfterAsync(0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RepeatedOrOlderIdsDoNotWakeAReaderWaitingForTheNextFrame()
    {
        var signal = new CaptureFrameSignal();
        signal.Publish(8);
        var next = signal.WaitAfterAsync(8, Timeout.InfiniteTimeSpan).AsTask();

        signal.Publish(8);
        signal.Publish(4);
        signal.Publish(0);

        Assert.False(next.IsCompleted);
        Assert.Equal(8L, await signal.WaitAfterAsync(0, TimeSpan.Zero));
        signal.Publish(9);
        Assert.Equal(9L, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellingOneReaderDoesNotCancelOtherReadersOrTheNextPublication()
    {
        var signal = new CaptureFrameSignal();
        using var cancel = new CancellationTokenSource();
        var cancelled = signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan, cancel.Token).AsTask();
        var remaining = signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan).AsTask();

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        Assert.False(remaining.IsCompleted);
        signal.Publish(2);

        Assert.Equal(2L, await remaining.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2L, await signal.WaitAfterAsync(0, TimeSpan.Zero));
    }

    [Fact]
    public async Task OneReadersTimeoutDoesNotExpireTheSharedNotification()
    {
        var signal = new CaptureFrameSignal();
        var remaining = signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan).AsTask();

        Assert.Null(await signal.WaitAfterAsync(0, TimeSpan.FromMilliseconds(20)));
        Assert.False(remaining.IsCompleted);
        signal.Publish(1);

        Assert.Equal(1L, await remaining.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PublicationAfterAllReadersTimedOutStillWakesTheNextReader()
    {
        var signal = new CaptureFrameSignal();
        Assert.Null(await signal.WaitAfterAsync(0, TimeSpan.FromMilliseconds(20)));

        signal.Publish(1);
        var next = signal.WaitAfterAsync(1, Timeout.InfiniteTimeSpan).AsTask();
        signal.Publish(2);

        Assert.Equal(2L, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FutureCursorIgnoresIntermediatePublications()
    {
        var signal = new CaptureFrameSignal();
        var next = signal.WaitAfterAsync(10, Timeout.InfiniteTimeSpan).AsTask();
        signal.Publish(2);
        signal.Publish(10);

        Assert.False(next.IsCompleted);
        signal.Publish(11);

        Assert.Equal(11L, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopWakesAllPendingReadersAndDisablesFuturePublications()
    {
        var signal = new CaptureFrameSignal();
        signal.Publish(1);
        var current = signal.WaitAfterAsync(1, Timeout.InfiniteTimeSpan).AsTask();
        var future = signal.WaitAfterAsync(99, Timeout.InfiniteTimeSpan).AsTask();

        signal.Stop();
        signal.Stop();
        signal.Publish(100);

        Assert.Null(await current.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await future.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task PublishDoesNotInvokeReaderContinuationsInline()
    {
        var signal = new CaptureFrameSignal();
        var next = signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan).AsTask();
        var publishingThread = Environment.CurrentManagedThreadId;
        var publishing = 0;
        var inline = false;
        var continuation = next.ContinueWith(_ =>
        {
            inline = Environment.CurrentManagedThreadId == publishingThread && Volatile.Read(ref publishing) != 0;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        Volatile.Write(ref publishing, 1);
        signal.Publish(1);
        Volatile.Write(ref publishing, 0);

        await continuation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inline);
    }

    [Fact]
    public async Task RegistrationRacingPublicationCannotLoseTheWake()
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var signal = new CaptureFrameSignal();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                await start.Task;
                return await signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan);
            });
            var publisher = Task.Run(async () => { await start.Task; signal.Publish(1); });

            start.SetResult();
            await publisher.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1L, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task RegistrationRacingStopCannotRemainAsleep()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var signal = new CaptureFrameSignal();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                await start.Task;
                return await signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan);
            });
            var stopper = Task.Run(async () => { await start.Task; signal.Stop(); });

            start.SetResult();
            await stopper.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void PublishingWithoutReadersDoesNotAllocatePerFrame()
    {
        var signal = new CaptureFrameSignal();
        for (var id = 1; id <= 32; id++) signal.Publish(id);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var id = 33; id <= 1032; id++) signal.Publish(id);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task AlreadyCancelledReaderDoesNotPreventLaterWaiting()
    {
        var signal = new CaptureFrameSignal();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await signal.WaitAfterAsync(0, Timeout.InfiniteTimeSpan, cancel.Token));
        signal.Publish(1);

        Assert.Equal(1L, await signal.WaitAfterAsync(0, TimeSpan.Zero));
    }

    [Fact]
    public async Task InvalidCursorAndTimeoutAreRejectedBeforeRegistration()
    {
        var signal = new CaptureFrameSignal();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await signal.WaitAfterAsync(-1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await signal.WaitAfterAsync(0, TimeSpan.FromMilliseconds(-2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await signal.WaitAfterAsync(0, TimeSpan.FromMilliseconds(uint.MaxValue)));
    }
}
