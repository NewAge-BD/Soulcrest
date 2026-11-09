using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>Original frame time and a local source epoch; no character identity, text or pixels are retained.</summary>
public sealed record GenusMenuSnapshot(bool? MenuVisible, string? Genus, long FrameAt, long SourceGeneration, byte? ViewedPreset = null);

internal sealed record GenusMenuCapturedFrame(Mat Image, Rectangle Bounds, long FrameAt, long SessionId, long FrameId);

/// <summary>Lease-driven observer of the existing singleton WGC session, independent of map tracking and its pause policy.</summary>
public sealed class GenusMenuObservationService : IDisposable
{
    internal static readonly TimeSpan ObservationLifetime = TimeSpan.FromMilliseconds(900);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Func<GenusMenuCapturedFrame?> _capture;
    private readonly Func<Rectangle?> _bounds;
    private readonly Func<long> _clock;
    private readonly bool _runWorker;
    private readonly GenusMenuDetector _detector = new();
    private readonly Action? _detach;
    private CancellationTokenSource? _run;
    private GenusMenuSnapshot _snapshot = new(null, null, 0, 0);
    private Rectangle? _sourceBounds;
    private long _session, _frame, _lastFrameAt, _revision, _generation, _candidateAt;
    private string? _candidate;
    private byte? _candidatePreset;
    private int _confirmations, _consumers;
    private bool _disposed;

    public GenusMenuObservationService(GameCaptureService capture, SettingsService settings)
    {
        _bounds = () => settings.Current.OverlayShowMode == "always" ? MonitorCapture.GameWindowBounds() : MonitorCapture.ForegroundGameWindowBounds();
        _capture = () =>
        {
            if (_bounds() is not { } bounds) return null;
            var scale = Math.Min(1d, 720d / bounds.Height);
            var image = capture.TryGrabGameWindowOnly(bounds, scale);
            var at = GameCaptureService.GrabbedFrameAt;
            var timing = GameCaptureService.GrabbedTiming;
            if (image is null) return null;
            if (!GameCaptureService.GrabbedGameWindowOnly || !timing.Valid || timing.CaptureSessionId <= 0
                || !MerchantLayoutStability.MatchesCaptureSize(bounds.Size, scale, new(image.Width, image.Height)))
            { image.Dispose(); return null; }
            return new(image, bounds, at, timing.CaptureSessionId, timing.SourceFrameId);
        };
        _clock = Stopwatch.GetTimestamp;
        _runWorker = true;
        settings.Changed += SettingsChanged;
        _detach = () => settings.Changed -= SettingsChanged;
    }

    internal GenusMenuObservationService(Func<GenusMenuCapturedFrame?> capture, Func<Rectangle?> bounds,
        Func<long>? clock = null, bool runWorker = false)
    { _capture = capture; _bounds = bounds; _clock = clock ?? Stopwatch.GetTimestamp; _runWorker = runWorker; }

    public event Action? Changed;
    internal event Action? FrameObserved;
    public GenusMenuSnapshot Snapshot
    {
        get
        {
            lock (_gate) if (_disposed || _consumers == 0) return Unknown();
            var bounds = _bounds();
            lock (_gate) return !_disposed && _consumers > 0 && bounds == _sourceBounds && Fresh(_snapshot.FrameAt)
                ? _snapshot : Unknown();
        }
    }
    internal long Revision { get { lock (_gate) return _revision; } }
    private GenusMenuSnapshot Unknown() => new(null, null, 0, _generation);
    private bool Fresh(long at) => at > 0 && at <= _clock() && Stopwatch.GetElapsedTime(at, _clock()) <= ObservationLifetime;

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_disposed) return EmptyLease.Instance;
            if (_consumers++ == 0)
            {
                _revision++; _generation++; ResetSource(); _snapshot = Unknown();
                var run = new CancellationTokenSource();
                _run = run;
                var token = run.Token;
                if (_runWorker) _ = Task.Run(() => Loop(token));
            }
        }
        return new Lease(this);
    }

    private async Task Loop(CancellationToken token)
    {
        var entered = false;
        try
        {
            await _runGate.WaitAsync(token).ConfigureAwait(false); entered = true;
            while (!token.IsCancellationRequested)
            {
                long revision;
                lock (_gate) { if (_disposed || _consumers == 0) return; revision = _revision; }
                try
                {
                    var frame = _capture();
                    if (frame is not null)
                    {
                        using (frame.Image)
                        {
                            if (Fresh(frame.FrameAt)) Observe(_detector.Detect(frame.Image), frame.Bounds, frame.FrameAt, frame.SessionId, frame.FrameId, revision);
                        }
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or ExternalException or ArgumentException
                    or OpenCVException or IOException)
                { Trace.TraceWarning("Genus menu observation: {0}", error.GetType().Name); }
                Expire();
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (entered) _runGate.Release(); }
    }

    internal bool Observe(GenusMenuDetection? detection, Rectangle bounds, long at, long session, long frame, long expectedRevision)
    {
        if (!Fresh(at) || _bounds() != bounds || session <= 0) return false;
        var changed = false;
        lock (_gate)
        {
            if (_disposed || _consumers == 0 || expectedRevision != _revision) return false;
            if (_sourceBounds != bounds || _session != session)
            {
                _generation++; ResetSource(); _sourceBounds = bounds; _session = session;
            }
            if (at <= _lastFrameAt || frame > 0 && frame == _frame) return false;
            _lastFrameAt = at; _frame = frame;
            string? genus = null;
            byte? preset = null;
            if (detection?.Genus is { } selected)
            {
                if (_candidate == selected && _candidatePreset == detection.ViewedPreset && _candidateAt > 0 && at > _candidateAt
                    && Stopwatch.GetElapsedTime(_candidateAt, at) <= ObservationLifetime) _confirmations++;
                else { _candidate = selected; _candidatePreset = detection.ViewedPreset; _confirmations = 1; }
                _candidateAt = at;
                if (_confirmations >= 2) { genus = selected; preset = detection.ViewedPreset is >= 1 and <= 3 ? detection.ViewedPreset : null; }
            }
            else { _candidate = null; _confirmations = 0; _candidateAt = 0; }
            var next = new GenusMenuSnapshot(detection is not null, genus, at, _generation, preset);
            changed = _snapshot.MenuVisible != next.MenuVisible || _snapshot.Genus != next.Genus || _snapshot.ViewedPreset != next.ViewedPreset || _snapshot.SourceGeneration != next.SourceGeneration;
            _snapshot = next;
        }
        if (changed) SafeEvent.Raise(Changed, "Genus-Menü");
        SafeEvent.Raise(FrameObserved, "Frisches Genus-Menübild");
        return true;
    }

    private void Expire()
    {
        var bounds = _bounds();
        var changed = false;
        lock (_gate)
        {
            if (_disposed || _snapshot.MenuVisible is null || bounds == _sourceBounds && Fresh(_snapshot.FrameAt)) return;
            _generation++; ResetSource(); _snapshot = Unknown(); changed = true;
        }
        if (changed) SafeEvent.Raise(Changed, "Genus-Menü");
    }
    private void SettingsChanged()
    {
        // Foreground/background policy changes cannot reuse a frame from the former window binding.
        Expire();
    }
    private void ResetSource()
    { _sourceBounds = null; _session = 0; _frame = 0; _lastFrameAt = 0; _candidate = null; _candidatePreset = null; _confirmations = 0; _candidateAt = 0; }
    private void Release()
    {
        CancellationTokenSource? run = null;
        lock (_gate)
        {
            if (_consumers == 0 || --_consumers > 0) return;
            _revision++; _generation++; ResetSource(); _snapshot = Unknown(); run = _run; _run = null;
        }
        run?.Cancel(); run?.Dispose(); SafeEvent.Raise(Changed, "Genus-Menü");
    }
    public void Dispose()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _consumers = 0; _revision++; _generation++; ResetSource(); _snapshot = Unknown(); run = _run; _run = null;
        }
        run?.Cancel(); run?.Dispose(); _detach?.Invoke();
    }
    private sealed class Lease(GenusMenuObservationService owner) : IDisposable
    {
        private GenusMenuObservationService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
    private sealed class EmptyLease : IDisposable
    { internal static readonly EmptyLease Instance = new(); public void Dispose() { } }
}
