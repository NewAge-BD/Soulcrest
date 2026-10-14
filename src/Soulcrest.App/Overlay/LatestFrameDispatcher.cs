namespace Soulcrest.App.Overlay;

/// <summary>
/// Composes at most one frame at a time and keeps only the latest result while the UI is busy.
/// The dispatcher owns every composed frame, including while <paramref name="present"/> uses it.
/// </summary>
internal sealed class LatestFrameDispatcher<TFrame>(
    Func<TFrame?> compose,
    Action<Action> post,
    Action<TFrame?> present,
    Action<Exception>? onError = null,
    Action<Action>? runWorker = null) : IDisposable where TFrame : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _runWorker = runWorker ?? (action => { _ = Task.Run(action); });
    private bool _disposed;
    private bool _requested;
    private bool _workerScheduled;
    private bool _workerRunning;
    private long _workerToken;
    private long _epoch;
    private bool _uiPosted;
    private long _postToken;
    private long _presentingToken;
    private bool _hasFrame;
    private TFrame? _frame;

    /// <summary>
    /// Requests the latest state. Ordinary movement requests coalesce without invalidating an
    /// in-flight frame: continuous movement must not starve a slower composer. Semantic changes
    /// may additionally discard queued frames and in-flight compositions from the old state,
    /// including a queued null/hide. A UI presentation that already claimed its frame may finish.
    /// </summary>
    public void Request(bool invalidatePending = false)
    {
        TFrame? discard = null;
        long workerToken = 0;
        lock (_gate)
        {
            if (_disposed) return;
            if (invalidatePending)
            {
                _epoch++;
                discard = _frame;
                _frame = null;
                _hasFrame = false;
            }
            _requested = true;
            if (!_workerScheduled)
            {
                _workerScheduled = true;
                workerToken = ++_workerToken;
            }
        }
        DisposeFrame(discard);
        if (workerToken != 0)
            StartWorker(workerToken);
    }

    private void StartWorker(long token)
    {
        try { _runWorker(() => ComposeRequested(token)); }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_workerToken == token && !_workerRunning)
                {
                    _workerScheduled = false;
                    _workerToken++; // Also invalidate a callback queued by a scheduler that threw.
                }
            }
            Report(error);
        }
    }

    private void ComposeRequested(long token)
    {
        while (true)
        {
            long epoch;
            lock (_gate)
            {
                if (_workerToken != token) return;
                if (_disposed || !_requested)
                {
                    _workerScheduled = false;
                    _workerRunning = false;
                    return;
                }
                _workerRunning = true;
                _requested = false;
                epoch = _epoch;
            }

            TFrame? frame;
            try { frame = compose(); }
            catch (Exception error)
            {
                Report(error);
                continue;
            }
            Publish(frame, epoch);
        }
    }

    private void Publish(TFrame? frame, long epoch)
    {
        TFrame? discard;
        long postToken = 0;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch)
            {
                discard = frame;
            }
            else
            {
                discard = _frame;
                _frame = frame;
                _hasFrame = true; // null is a real result: hide the overlay.
                if (!_uiPosted)
                {
                    _uiPosted = true;
                    postToken = ++_postToken;
                }
            }
        }
        DisposeFrame(discard);
        if (postToken != 0)
            Post(postToken);
    }

    private void Post(long token)
    {
        try { post(() => PresentLatest(token)); }
        catch (Exception error)
        {
            TFrame? discard = null;
            lock (_gate)
            {
                // A synchronous scheduler may already have executed this callback. Its failure
                // must not clear a newer callback or take ownership from the presenter.
                if (_uiPosted && _postToken == token && _presentingToken != token)
                {
                    _uiPosted = false;
                    discard = _frame;
                    _frame = null;
                    _hasFrame = false;
                }
            }
            DisposeFrame(discard);
            Report(error);
        }
    }

    private void PresentLatest(long token)
    {
        TFrame? frame;
        lock (_gate)
        {
            if (_disposed || !_uiPosted || _postToken != token) return;
            if (!_hasFrame)
            {
                _uiPosted = false;
                return;
            }
            frame = _frame;
            _frame = null;
            _hasFrame = false;
            _presentingToken = token;
            // Keep _uiPosted set while presenting, so concurrent composers only replace the slot.
        }

        try { present(frame); }
        catch (Exception error) { Report(error); }
        finally
        {
            DisposeFrame(frame);
            long postToken = 0;
            lock (_gate)
            {
                if (_presentingToken == token)
                    _presentingToken = 0;
                if (_postToken == token)
                {
                    _uiPosted = false;
                    if (!_disposed && _hasFrame)
                    {
                        _uiPosted = true;
                        postToken = ++_postToken;
                    }
                }
            }
            if (postToken != 0)
                Post(postToken);
        }
    }

    public void Dispose()
    {
        TFrame? discard;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _requested = false;
            discard = _frame;
            _frame = null;
            _hasFrame = false;
        }
        DisposeFrame(discard);
    }

    /// <summary>
    /// Discards work tied to a lost UI handle. Previously posted callbacks become harmless even
    /// if the platform drops them. A later request may use a new handle without waiting for them.
    /// </summary>
    public void CancelPending()
    {
        TFrame? discard;
        lock (_gate)
        {
            if (_disposed) return;
            _epoch++;
            _requested = false;
            _uiPosted = false;
            _postToken++;
            discard = _frame;
            _frame = null;
            _hasFrame = false;
        }
        DisposeFrame(discard);
    }

    private void DisposeFrame(TFrame? frame)
    {
        try { frame?.Dispose(); }
        catch (Exception error) { Report(error); }
    }

    private void Report(Exception error)
    {
        try
        {
            if (onError is not null) onError(error);
            else System.Diagnostics.Trace.TraceWarning("Overlay frame dispatch failed: {0}", error.Message);
        }
        catch (Exception) { /* Diagnostics must not strand frame ownership or the worker. */ }
    }
}
