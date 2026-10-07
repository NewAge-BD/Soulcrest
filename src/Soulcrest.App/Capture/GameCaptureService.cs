using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Windows.Security.Authorization.AppCapabilityAccess;
using Soulcrest.Ocr;

namespace Soulcrest.App.Capture;

/// <summary>
/// One capture of the game for all trackers (player tracking, loot feed, pet window): Windows Graphics
/// Capture of the AION2 window, so Soulcrest's overlays never get into the pictures, also when they are
/// visible for recordings (user request 2026-10-03). Falls back to the monitor, then to GDI.
/// Thread-safe: callers on different threads are serialised.
/// </summary>
public sealed class GameCaptureService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private MonitorCapture? _capture;
    private DateTime _nextTry = DateTime.MinValue;
    private long _grabs, _gdiFallbacks, _busyFrames;
    private sealed record Operation(string Stage, DateTime Since);
    private Operation _operation = new("Bereit", DateTime.UtcNow);
    private CaptureDiagnostics _snapshot = new("GDI", null, 0, null, 0, 0, null);
    private string? _lastFailure;
    private int _disposed;

    private void Stage(string stage) => Volatile.Write(ref _operation, new Operation(stage, DateTime.UtcNow));

    private void UpdateSnapshot()
    {
        var capture = _capture;
        Volatile.Write(ref _snapshot, new CaptureDiagnostics(
            capture is null ? "GDI" : capture.IsWindow ? "Spielfenster (WGC)" : "Monitor (WGC)",
            capture?.Bounds, capture?.FramesArrived ?? 0, capture?.LastFrameAt,
            Interlocked.Read(ref _grabs), Interlocked.Read(ref _gdiFallbacks), _lastFailure ?? MonitorCapture.LastProblem)
        {
            Hdr = capture is { IsHdr: true } ? $"HDR, SDR-Weiß {capture.SdrWhite * 80:0} nits" : null,
        });
    }

    /// <summary>Capture state for the diagnosis report.</summary>
    public sealed record CaptureDiagnostics(string Method, Rectangle? Bounds, long FramesArrived, DateTime? LastFrameAt, long Grabs, long GdiFallbacks, string? LastProblem)
    {
        public string Stage { get; init; } = "Bereit";
        public DateTime StageSince { get; init; }
        public long BusyFrames { get; init; }

        /// <summary>"HDR, SDR-Weiß 200 nits" while the monitor runs in HDR (FP16 capture), else null.</summary>
        public string? Hdr { get; init; }
    }

    public CaptureDiagnostics Diagnostics
    {
        get
        {
            // Reporting must remain available even when a graphics driver holds Grab's lock.
            var snapshot = Volatile.Read(ref _snapshot);
            var operation = Volatile.Read(ref _operation);
            var capture = Volatile.Read(ref _capture);
            return snapshot with { Stage = operation.Stage, StageSince = operation.Since, BusyFrames = Interlocked.Read(ref _busyFrames),
                FramesArrived = capture?.FramesArrived ?? snapshot.FramesArrived, LastFrameAt = capture?.LastFrameAt ?? snapshot.LastFrameAt,
                Grabs = Interlocked.Read(ref _grabs), GdiFallbacks = Interlocked.Read(ref _gdiFallbacks),
                LastProblem = Volatile.Read(ref _lastFailure) ?? MonitorCapture.LastProblem };
        }
    }

    /// <summary>"Spielfenster (WGC)", "Monitor (WGC)" or "GDI".</summary>
    public string Method => Volatile.Read(ref _snapshot).Method;

    /// <summary>Method plus the HDR state, for logs and status texts ("Spielfenster (WGC) · HDR, SDR-Weiß 200 nits").</summary>
    public string MethodText
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot.Hdr is { } hdr ? $"{snapshot.Method} · {hdr}" : snapshot.Method;
        }
    }

    /// <summary>Screen rectangle of the game window (or monitor) while it is captured; null with GDI.</summary>
    public Rectangle? GameBounds
    {
        get
        {
            // UI callers must not wait behind a stalled native readback.
            return Volatile.Read(ref _snapshot).Bounds;
        }
    }

    /// <summary>
    /// When the picture of this thread's last <see cref="Grab"/> came from the screen (Stopwatch timestamp,
    /// WGC frame arrival or the GDI copy), to measure how far the overlay trails behind the game.
    /// </summary>
    [ThreadStatic] public static long GrabbedFrameAt;

    /// <summary>Picture of a screen rectangle as BGR image, optionally scaled.</summary>
    public Mat Grab(Rectangle area, double scale = 1.0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _grabs);
        lock (_gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                Stage("WGC starten / Fenster suchen");
                Ensure(area);
                UpdateSnapshot();
                if (_capture is { } capture)
                {
                    var bounds = capture.Bounds;
                    if (bounds.IsEmpty)
                    {
                        Stage("WGC schließen");
                        capture.Dispose();
                        _capture = null;
                        _nextTry = DateTime.Now;
                    }
                    else if (bounds.Contains(area))
                    {
                        Stage("WGC-Bild / GPU-Lesekopie");
                        if (capture.CaptureMat(area, scale, Stage) is { } picture)
                        {
                            _lastFailure = null;
                            GrabbedFrameAt = capture.ReadFrameAt;
                            return picture;
                        }
                    }
                }
                Stage("GDI-Bildschirmkopie");
                Interlocked.Increment(ref _gdiFallbacks);
                GrabbedFrameAt = System.Diagnostics.Stopwatch.GetTimestamp();
                using var bitmap = scale < 1 ? ScreenCapture.CaptureScaled(area, scale) : ScreenCapture.Capture(area);
                return BitmapMat.ToBgr(bitmap);
            }
            catch (Exception error)
            {
                if (CaptureReadback.IsBusy(error)) Interlocked.Increment(ref _busyFrames);
                var failedStage = error.Data["CaptureStage"] as string ?? Volatile.Read(ref _operation).Stage;
                _lastFailure = $"{failedStage}: {error.GetType().Name} (0x{error.HResult:X8}): {error.Message}";
                System.Diagnostics.Trace.TraceWarning("Capture failed: {0}", _lastFailure);
                throw;
            }
            finally
            {
                UpdateSnapshot();
                Stage("Bereit");
            }
        }
    }

    /// <summary>Like <see cref="Grab"/>, as 32-bit bitmap (OCR, previews).</summary>
    public Bitmap GrabBitmap(Rectangle area)
    {
        using var picture = Grab(area);
        return BitmapMat.ToBitmapFast(picture);
    }

    private void Ensure(Rectangle area)
    {
        // A monitor capture is only a stand-in: switch to the game window once it exists.
        if (_capture is { IsWindow: true } || DateTime.Now < _nextTry)
            return;
        _nextTry = DateTime.Now + RetryInterval;
        var started = MonitorCapture.TryStartForGame(area);
        if (started is null)
            return;
        if (_capture is not null && !started.IsWindow)
        {
            started.Dispose(); // still no game window: keep the running monitor capture
            return;
        }
        _capture?.Dispose();
        _capture = started;
    }

    /// <summary>Whether the running capture is confirmed without the yellow border; null without a WGC capture.</summary>
    public bool? BorderSuppressed => Volatile.Read(ref _capture)?.IsBorderSuppressed;

    /// <summary>
    /// Grindcrest's PrepareWindowCaptureAsync: asks Windows for borderless capture (a prompt only if needed)
    /// outside the capture lock, then restarts a capture that still has its border. Windows only takes
    /// IsBorderRequired before StartCapture; setting it on the running session left the border (0.1.11).
    /// </summary>
    public async Task<AppCapabilityAccessStatus?> PrepareBorderlessAsync()
    {
        if (!MonitorCapture.SupportsBorderSuppression)
            return null;
        AppCapabilityAccessStatus? access;
        try { access = await Task.Run(MonitorCapture.RequestBorderlessAccess); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            System.Diagnostics.Trace.TraceWarning("Windows could not prepare borderless capture: {0}", error.Message);
            return null;
        }
        if (access == AppCapabilityAccessStatus.Allowed)
            await Task.Run(RestartBorderedCapture);
        return access;
    }

    private void RestartBorderedCapture()
    {
        if (Volatile.Read(ref _disposed) != 0 || !Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2)))
            return;
        try
        {
            if (_capture is not { IsBorderSuppressed: false } bordered)
                return;
            _capture = null;
            _nextTry = DateTime.MinValue; // the next Grab starts a new, borderless session
            UpdateSnapshot();
            bordered.Dispose();
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>Options button: request the permission, restart the capture and report what Windows did.</summary>
    public async Task<string> DisableBorderAsync()
    {
        if (!MonitorCapture.SupportsBorderSuppression)
            return "Diese Windows-Version unterstützt die Randfreigabe nicht.";
        var access = await PrepareBorderlessAsync();
        if (access is null)
            return "Windows-Freigabe konnte nicht abgefragt werden.";
        if (access != AppCapabilityAccessStatus.Allowed)
            return "Windows hat die randlose Aufnahme nicht freigegeben.";
        // The trackers grab several times a second: give them a moment to start the new session.
        for (var waited = 0; waited < 3000 && BorderSuppressed is null; waited += 100)
            await Task.Delay(100);
        return BorderSuppressed switch
        {
            true => "Aufnahmerand entfernt.",
            false => "Freigabe erteilt, aber Windows behält den Rand (ein anderes Aufnahmeprogramm verlangt ihn).",
            null => "Freigabe erteilt. Die nächste Aufnahme startet ohne Rand.",
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Never free a native resource under an active read. If the driver is stuck, the
        // exiting process owns cleanup; waiting forever would prevent the window from closing.
        if (!Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(250)))
        {
            System.Diagnostics.Trace.TraceWarning("Capture shutdown: active read did not finish; native resources retained until process exit.");
            return;
        }
        MonitorCapture? capture;
        try { capture = _capture; _capture = null; }
        finally { Monitor.Exit(_gate); }
        if (capture is null) return;
        var cleanup = Task.Run(() =>
        {
            try { capture.Dispose(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Capture shutdown: {0}", error.Message); }
        });
        if (!cleanup.Wait(TimeSpan.FromSeconds(1)))
            System.Diagnostics.Trace.TraceWarning("Capture shutdown: driver cleanup still pending; allowing process exit.");
    }
}
