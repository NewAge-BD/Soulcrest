using Soulcrest.App.Services;

namespace Soulcrest.App.Components.OverlayKit;

/// <summary>One pending UI refresh, followed by a real quiet interval even under continuous source changes.</summary>
internal sealed class OverlayEditorRefreshPump(
    Func<Action, Task> dispatch, Action refresh, Func<Exception, Task> reportError, TimeProvider? time = null,
    Action<Exception>? reportLateError = null) : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Task _worker = Task.CompletedTask;
    private bool _dirty, _running, _disposed, _failed;

    internal Task WhenIdle { get { lock (_gate) return _worker; } }

    public void Request()
    {
        lock (_gate)
        {
            if (_disposed || _failed) return;
            _dirty = true;
            if (_running) return;
            _running = true;
            // Source callbacks may run on the UI thread. Never drain the dispatcher inline.
            _worker = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed || !_dirty) { _running = false; return; }
                    _dirty = false;
                }
                var posted = dispatch(() =>
                {
                    if (!Volatile.Read(ref _disposed)) refresh();
                });
                try { await posted.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A native UI post cannot be retracted. Observe its eventual failure even
                    // when disposal must stop waiting for an unavailable dispatcher now.
                    _ = ObserveAfterDisposalAsync(posted);
                    throw;
                }
                // Start the interval only AFTER the UI callback completes. A delayed UI must
                // not build a queue, nor immediately repost forever when sources remain busy.
                await Task.Delay(Interval, _time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate) _running = false;
        }
        catch (Exception exception)
        {
            lock (_gate) { _failed = true; _running = false; }
            await reportError(exception).ConfigureAwait(false);
        }
    }

    private async Task ObserveAfterDisposalAsync(Task posted)
    {
        try { await posted.ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (reportLateError is not null) reportLateError(exception);
            else LogFile.Error("Overlay editor UI refresh after disposal", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _dirty = false;
            worker = _worker;
        }
        _stop.Cancel();
        await worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
