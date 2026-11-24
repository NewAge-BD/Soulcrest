using System.Threading.Channels;
using Soulcrest.App.Components.OverlayKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorRefreshPumpTests
{
    [Fact]
    public async Task ABlockedDispatcherKeepsOnePostAndTheLatestStateWithAFullPauseAfterCompletion()
    {
        var clock = new Clock();
        var dispatcher = new BlockedDispatcher();
        var current = 0;
        var captured = new List<int>();
        await using var pump = new OverlayEditorRefreshPump(dispatcher.Post, () => captured.Add(current),
            error => Task.FromException(error), clock);
        pump.Request();
        var first = await dispatcher.Next();
        for (current = 1; current <= 1000; current++) pump.Request();
        Assert.Equal(1, dispatcher.Posts);
        Assert.Empty(captured);
        Assert.Equal(0, clock.TimersCreated);
        current = 1000;
        first.Complete();
        await clock.NextTimer();
        Assert.Equal([1000], captured);
        Assert.Equal(1, dispatcher.Posts);
        clock.Advance(OverlayEditorRefreshPump.Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, dispatcher.Posts);
        current = 2000;
        pump.Request();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = await dispatcher.Next();
        Assert.Equal(2, dispatcher.Posts);
        second.Complete();
        await clock.NextTimer();
        Assert.Equal([1000, 2000], captured);
        clock.Advance(OverlayEditorRefreshPump.Interval);
        await pump.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        // Restart after becoming idle: the dirty state is not stranded at the worker boundary.
        current = 3000;
        pump.Request();
        var third = await dispatcher.Next();
        third.Complete();
        await clock.NextTimer();
        Assert.Equal([1000, 2000, 3000], captured);
    }

    [Fact]
    public async Task AnEventDuringCaptureIsRefreshedOnceAfterTheQuietInterval()
    {
        var clock = new Clock();
        var dispatcher = new BlockedDispatcher();
        var captures = 0;
        OverlayEditorRefreshPump? pump = null;
        await using var owned = pump = new(dispatcher.Post, () =>
        {
            captures++;
            if (captures == 1) for (var index = 0; index < 1000; index++) pump!.Request();
        }, error => Task.FromException(error), clock);
        pump.Request();
        (await dispatcher.Next()).Complete();
        await clock.NextTimer();
        Assert.Equal(1, captures);
        Assert.Equal(1, dispatcher.Posts);
        clock.Advance(OverlayEditorRefreshPump.Interval);
        (await dispatcher.Next()).Complete();
        await clock.NextTimer();
        Assert.Equal(2, captures);
        Assert.Equal(2, dispatcher.Posts);
    }

    [Fact]
    public async Task DisposeCancelsTheWaitAndGuardsAnAlreadyPostedCallback()
    {
        var dispatcher = new BlockedDispatcher();
        var captures = 0;
        var pump = new OverlayEditorRefreshPump(dispatcher.Post, () => captures++, error => Task.FromException(error));
        pump.Request();
        var pending = await dispatcher.Next();
        await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        pending.Complete();
        for (var index = 0; index < 1000; index++) pump.Request();
        Assert.Equal(0, captures);
        Assert.Equal(1, dispatcher.Posts);
    }

    [Fact]
    public async Task ADispatchFailureAfterDisposalIsStillObservedAndReported()
    {
        var dispatcher = new BlockedDispatcher();
        var expected = new InvalidOperationException("Synthetic late dispatch failure");
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new OverlayEditorRefreshPump(dispatcher.Post, () => { }, error => Task.FromException(error),
            reportLateError: error => reported.SetResult(error));
        pump.Request();
        var pending = await dispatcher.Next();
        await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        pending.Done.SetException(expected);
        Assert.Same(expected, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RefreshFailureIsReportedOnceAndStopsThePump()
    {
        var expected = new InvalidOperationException("Synthetic refresh failure");
        var reported = new List<Exception>();
        await using var pump = new OverlayEditorRefreshPump(action => { action(); return Task.CompletedTask; },
            () => throw expected, error => { reported.Add(error); return Task.CompletedTask; });
        pump.Request();
        await pump.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(expected, Assert.Single(reported));
        pump.Request();
        await pump.WhenIdle;
        Assert.Single(reported);
    }

    private sealed class BlockedDispatcher
    {
        private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>();
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);
        public Task Post(Action action)
        {
            var pending = new Pending(action);
            Interlocked.Increment(ref _posts);
            Assert.True(_queue.Writer.TryWrite(pending));
            return pending.Done.Task;
        }
        public async Task<Pending> Next() => await _queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public sealed class Pending(Action action)
        {
            public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Complete() { action(); Done.SetResult(); }
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private readonly Channel<bool> _created = Channel.CreateUnbounded<bool>();
        private TimeSpan _elapsed;
        public int TimersCreated { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new Timer(this, callback, state, _elapsed + dueTime);
                _timers.Add(timer);
                TimersCreated++;
                Assert.True(_created.Writer.TryWrite(true));
                return timer;
            }
        }
        public async Task NextTimer() => await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Advance(TimeSpan amount)
        {
            Timer[] due;
            lock (_gate)
            {
                _elapsed += amount;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _elapsed).ToArray();
                foreach (var timer in due) timer.Disposed = true;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal TimeSpan Due { get; } = due;
            internal bool Disposed { get; set; }
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { lock (clock._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
