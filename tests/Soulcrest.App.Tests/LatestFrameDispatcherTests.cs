using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LatestFrameDispatcherTests
{
    [Fact]
    public void RequestsBeforeWorkerStartsComposeOnlyTheLatestState()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        var state = 0;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, state), ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);

        for (state = 1; state <= 100; state++) dispatcher.Request();
        state = 100;
        Assert.Equal(1, worker.Count);
        worker.RunNext();
        Assert.Equal(1, ui.Count);
        Assert.Single(frames);
        ui.RunNext();

        Assert.Equal(new int?[] { 100 }, shown);
        Assert.Equal(1, frames[0].DisposeCount);
    }

    [Fact]
    public void BusyUiHasOneCallbackAndOnlyTheLatestOwnedFrame()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);

        for (var i = 0; i < 100; i++)
        {
            dispatcher.Request();
            worker.RunNext();
            Assert.Equal(1, ui.Count);
            Assert.Equal(i, frames.Sum(frame => frame.DisposeCount));
        }
        ui.RunNext();

        Assert.Equal(new int?[] { 100 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void MovementDuringComposeDoesNotStarvePresentation()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        using var owned = dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            var frame = AddFrame(frames, frames.Count + 1);
            if (frame.Id <= 3)
            {
                for (var i = 0; i < 60; i++) dispatcher!.Request();
            }
            // A UI turn between slow compositions must show each completed frame even though
            // movement already requested the next picture during this composition.
            if (ui.Count > 0) ui.RunNext();
            return frame;
        }, ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);

        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();

        Assert.Equal(new int?[] { 1, 2, 3, 4 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void NullReplacesAndDisposesAQueuedPictureThenHides()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frame = new TestFrame(1);
        var shown = new List<int?>();
        var hide = false;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => hide ? null : frame, ui.Post, picture => shown.Add(picture?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        hide = true;
        dispatcher.Request();
        worker.RunNext();

        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(1, ui.Count);
        ui.RunNext();
        Assert.Equal(new int?[] { null }, shown);
    }

    [Fact]
    public void PictureMayReplaceAnUnpresentedHideWithoutExtraCallback()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frame = new TestFrame(1);
        var shown = new List<int?>();
        var hide = true;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => hide ? null : frame, ui.Post, picture => shown.Add(picture?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        hide = false;
        dispatcher.Request();
        worker.RunNext();
        Assert.Equal(1, ui.Count);
        ui.RunNext();
        Assert.Equal(new int?[] { 1 }, shown);
        Assert.Equal(1, frame.DisposeCount);
    }

    [Fact]
    public void SemanticInvalidationDiscardsInFlightFrameButKeepsLatestRequest()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        using var owned = dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            var frame = AddFrame(frames, frames.Count + 1);
            if (frame.Id == 1) dispatcher!.Request(invalidatePending: true);
            return frame;
        }, ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();

        Assert.Equal(1, frames[0].DisposeCount);
        Assert.Equal(1, ui.Count);
        ui.RunNext();
        Assert.Equal(new int?[] { 2 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void InvalidatingWaitingFrameRetainsCallbackForItsReplacement()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        dispatcher.Request(invalidatePending: true);
        Assert.Equal(1, frames[0].DisposeCount);
        worker.RunNext();
        Assert.Equal(1, ui.Count);
        ui.RunNext();
        Assert.Equal(new int?[] { 2 }, shown);
    }

    [Fact]
    public void PresenterDoesNotBlockCompositionOrAccumulateUiCallbacks()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        using var owned = dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), ui.Post, frame =>
            {
                shown.Add(frame?.Id);
                if (frame?.Id != 1) return;
                for (var i = 0; i < 10; i++)
                {
                    dispatcher!.Request();
                    worker.RunNext();
                    Assert.Equal(0, ui.Count);
                }
                Assert.Equal(0, frame.DisposeCount); // Presenter may still use the first frame.
            }, runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal(1, ui.Count);
        ui.RunNext();
        Assert.Equal(new int?[] { 1, 11 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void DisposeReleasesWaitingFrameAndMakesQueuedCallbackHarmless()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frame = new TestFrame(1);
        var shown = new List<int?>();
        var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => frame, ui.Post, picture => shown.Add(picture?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        dispatcher.Dispose();
        dispatcher.Dispose();
        dispatcher.Request();
        ui.RunNext();
        Assert.Empty(shown);
        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(0, worker.Count);
    }

    [Fact]
    public void DisposeDuringComposeDisposesItsResultWithoutPosting()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frame = new TestFrame(1);
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            dispatcher!.Dispose();
            return frame;
        }, ui.Post, _ => Assert.Fail("Disposed dispatcher must not present."), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        Assert.Equal(1, frame.DisposeCount);
        Assert.Equal(0, ui.Count);
    }

    [Fact]
    public void ComposeFailureDoesNotStrandSubsequentRequests()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frame = new TestFrame(1);
        var errors = new List<Exception>();
        var attempts = 0;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("compose");
            return frame;
        }, ui.Post, _ => { }, errors.Add, worker.Post);
        dispatcher.Request();
        worker.RunNext();
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal("compose", Assert.Single(errors).Message);
        Assert.Equal(1, frame.DisposeCount);
    }

    [Fact]
    public void PostFailureDisposesTheFrameAndNextRequestRecovers()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var errors = new List<Exception>();
        var posts = 0;
        var shown = new List<int?>();
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), action =>
            {
                if (++posts == 1) throw new InvalidOperationException("post");
                ui.Post(action);
            }, frame => shown.Add(frame?.Id), errors.Add, worker.Post);
        dispatcher.Request();
        worker.RunNext();
        Assert.Equal(1, frames[0].DisposeCount);
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal("post", Assert.Single(errors).Message);
        Assert.Equal(new int?[] { 2 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void PresenterFailureDisposesTheFrameAndNextRequestRecovers()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var errors = new List<Exception>();
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), ui.Post, frame =>
            {
                if (frame?.Id == 1) throw new InvalidOperationException("present");
            }, errors.Add, worker.Post);
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal("present", Assert.Single(errors).Message);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void WorkerSchedulerFailureAllowsRecoveryOnNextRequest()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var errors = new List<Exception>();
        var frame = new TestFrame(1);
        var schedules = 0;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(() => frame, ui.Post, _ => { }, errors.Add, action =>
        {
            if (++schedules == 1) throw new InvalidOperationException("worker");
            worker.Post(action);
        });
        dispatcher.Request();
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal("worker", Assert.Single(errors).Message);
        Assert.Equal(1, frame.DisposeCount);
    }

    [Fact]
    public void CallbackQueuedBeforePostThrowsCannotStealARecoveredFrame()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        var errors = new List<Exception>();
        var posts = 0;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), action =>
            {
                ui.Post(action);
                if (++posts == 1) throw new InvalidOperationException("post queued then failed");
            }, frame => shown.Add(frame?.Id), errors.Add, worker.Post);
        dispatcher.Request();
        worker.RunNext();
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Empty(shown);
        ui.RunNext();
        Assert.Equal(new int?[] { 2 }, shown);
        Assert.Single(errors);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void WorkerQueuedBeforeSchedulerThrowsCannotComposeAlongsideItsReplacement()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var errors = new List<Exception>();
        var schedules = 0;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), ui.Post, _ => { }, errors.Add, action =>
            {
                worker.Post(action);
                if (++schedules == 1) throw new InvalidOperationException("worker queued then failed");
            });
        dispatcher.Request();
        dispatcher.Request();
        worker.RunNext();
        Assert.Empty(frames);
        worker.RunNext();
        ui.RunNext();
        Assert.Single(errors);
        Assert.Equal(1, Assert.Single(frames).DisposeCount);
    }

    [Fact]
    public void CancellingDuringComposeDropsOldResultAndAllowsFreshHandleRequest()
    {
        var worker = new ManualQueue();
        var ui = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        using var owned = dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            var frame = AddFrame(frames, frames.Count + 1);
            if (frame.Id == 1)
            {
                dispatcher!.CancelPending();
                dispatcher.Request();
            }
            return frame;
        }, ui.Post, frame => shown.Add(frame?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        ui.RunNext();
        Assert.Equal(new int?[] { 2 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void SynchronousSchedulersSupportReentrantRequestsAndDispose()
    {
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        LatestFrameDispatcher<TestFrame>? dispatcher = null;
        using var owned = dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), action => action(), frame =>
            {
                shown.Add(frame?.Id);
                if (frame?.Id == 1) dispatcher!.Request();
                else dispatcher!.Dispose();
            }, runWorker: action => action());
        dispatcher.Request();
        Assert.Equal(new int?[] { 1, 2 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void LostHandleCanCancelAnUndeliveredCallbackAndUseANewUiQueue()
    {
        var worker = new ManualQueue();
        var oldUi = new ManualQueue();
        var newUi = new ManualQueue();
        var frames = new List<TestFrame>();
        var shown = new List<int?>();
        var ui = oldUi;
        using var dispatcher = new LatestFrameDispatcher<TestFrame>(
            () => AddFrame(frames, frames.Count + 1), action => ui.Post(action),
            frame => shown.Add(frame?.Id), runWorker: worker.Post);
        dispatcher.Request();
        worker.RunNext();
        dispatcher.CancelPending();
        Assert.Equal(1, frames[0].DisposeCount);
        ui = newUi;
        dispatcher.Request();
        worker.RunNext();
        oldUi.RunNext(); // A late old-handle callback cannot take the newer callback's frame.
        Assert.Empty(shown);
        newUi.RunNext();
        Assert.Equal(new int?[] { 2 }, shown);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public async Task ConcurrentRequestsCannotStartAnotherComposerAndDisposeOwnsInFlightResult()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var frame = new TestFrame(1);
        var workers = new List<Task>();
        var composeCalls = 0;
        var posts = 0;
        var dispatcher = new LatestFrameDispatcher<TestFrame>(() =>
        {
            Interlocked.Increment(ref composeCalls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return frame;
        }, _ => Interlocked.Increment(ref posts), _ => Assert.Fail("Disposed dispatcher must not present."),
            runWorker: action => workers.Add(Task.Run(action)));
        try
        {
            dispatcher.Request();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Parallel.For(0, 1_000, _ => dispatcher.Request());
            _ = Assert.Single(workers); // The returned task is awaited with all workers in finally.
            dispatcher.Dispose();
        }
        finally
        {
            dispatcher.Dispose();
            release.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, composeCalls);
        Assert.Equal(0, posts);
        Assert.Equal(1, frame.DisposeCount);
    }

    private static TestFrame AddFrame(List<TestFrame> frames, int id)
    {
        var frame = new TestFrame(id);
        frames.Add(frame);
        return frame;
    }

    private sealed class TestFrame(int id) : IDisposable
    {
        private int _disposeCount;
        public int Id { get; } = id;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ManualQueue
    {
        private readonly Queue<Action> _actions = new();
        public int Count => _actions.Count;
        public void Post(Action action) => _actions.Enqueue(action);
        public void RunNext() => _actions.Dequeue()();
    }
}
