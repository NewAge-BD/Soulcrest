using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class UiResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingDuringEitherResponseWaitAllowsLateAndRepeatedCallbacks(bool timedOut)
    {
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = 0;
        using var watcher = new UiResponsiveness.Watchdog(callback => posted.SetResult(callback), () => "synthetic",
            waitForResponse: timedOut ? (_, _) => Task.FromResult(false) : async (answer, stop) =>
            {
                waiting.SetResult();
                await answer.WaitAsync(stop);
                return true;
            }, foreground: () => { waiting.SetResult(); return "synthetic-game"; },
            record: (_, _) => Interlocked.Increment(ref records));
        var acknowledge = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        watcher.Dispose();
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        acknowledge();
        acknowledge();
        Assert.Equal(0, records);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ResponsiveUiAndCancellationDuringPauseDoNotRecordAStall()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = 0;
        using var watcher = new UiResponsiveness.Watchdog(callback => callback(), () => "synthetic",
            pause: async stop => { paused.SetResult(); await Task.Delay(Timeout.Infinite, stop); },
            record: (_, _) => Interlocked.Increment(ref records));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, records);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ARecoveredStallRecordsItsOriginalForegroundAndContextOnce()
    {
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource<(TimeSpan Duration, string Context)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new UiResponsiveness.Watchdog(callback => posted.SetResult(callback), () => "synthetic-tab",
            waitForResponse: (_, _) => Task.FromResult(false),
            foreground: () => { timedOut.SetResult(); return "synthetic-game"; },
            pause: stop => Task.Delay(Timeout.Infinite, stop),
            record: (duration, context) => recorded.SetResult((duration, context)));
        var acknowledge = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
        acknowledge();
        acknowledge();
        var stall = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stall.Duration >= TimeSpan.Zero);
        Assert.Equal("synthetic-tab, Vordergrund beim Beginn: synthetic-game", stall.Context);
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ADisposedWindowEndsTheWorkerNormally()
    {
        using var watcher = new UiResponsiveness.Watchdog(_ => throw new ObjectDisposedException("synthetic-window"), () => "synthetic");
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.Dispose();
        watcher.Dispose();
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
    }
}
