using System.Diagnostics;
using Soulcrest.App.Capture;

namespace Soulcrest.App.Services;

/// <summary>
/// What player tracking costs while it runs, for the map-tracking.log line (2026-10-07: Soulcrest used
/// 1.4 cores while tracking). Process CPU since the last line, plus the mean time of one flow step
/// (60 per second) and one detection. Thread-safe: detections finish on another thread.
/// </summary>
internal sealed class TrackingCost
{
    private readonly object _gate = new();
    private readonly object _summaryGate = new();
    private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _since = Stopwatch.GetTimestamp();
    private double _flowMs, _detectionMs, _flowLagMs, _overlayLagMs;
    private int _flows, _detections, _flowLags, _overlayLags;
    private TrackingLatencyProfile _latency = new(), _spareLatency = new();

    public void AddFlow(TimeSpan spent, bool unavailable = false, bool worldMap = false)
    {
        lock (_gate)
        {
            _flowMs += spent.TotalMilliseconds;
            _flows++;
            _latency.AddFlow(unavailable, worldMap);
        }
    }

    public void AddDetection(TimeSpan spent)
    {
        lock (_gate) { _detectionMs += spent.TotalMilliseconds; _detections++; }
    }

    /// <summary>Screen picture arrived -> flow step done.</summary>
    public void AddFlowLag(TimeSpan lag)
    {
        lock (_gate) { _flowLagMs += lag.TotalMilliseconds; _flowLags++; _latency.AddFlowAge(lag.TotalMilliseconds); }
    }

    /// <summary>Screen picture arrived -> overlay picture handed to Windows (the screen shows it with the next refresh).</summary>
    internal void AddOverlayPresentation(OverlayTiming timing)
    {
        lock (_gate)
        {
            if (timing.CapturedAt > 0 && timing.PresentedAt >= timing.CapturedAt)
            {
                _overlayLagMs += Stopwatch.GetElapsedTime(timing.CapturedAt, timing.PresentedAt).TotalMilliseconds;
                _overlayLags++;
            }
            _latency.AddPresentation(timing);
        }
    }

    internal void AddCapture(CaptureTiming timing) { lock (_gate) _latency.AddCapture(timing); }
    internal void AddComposition(TimeSpan spent) { lock (_gate) _latency.AddComposition(spent.TotalMilliseconds); }
    internal void AddWorldDetection(bool found) { lock (_gate) _latency.AddWorldDetection(found); }
    internal void AddCaptureUnavailable() { lock (_gate) _latency.AddCaptureUnavailable(); }
    internal void AddCaptureRejected() { lock (_gate) _latency.AddCaptureRejected(); }
    internal void ResetOverlayPresentation() { lock (_gate) _latency.ResetPresentation(); }

    /// <summary>"cpu=85% (5.3% gesamt) fluss=3.1ms×60/s suchdauer=190ms×4/s"; starts the next window.</summary>
    public string TakeSummary()
    {
        // Sorting/formatting diagnostics must not hold up flow or native presentation. Only one
        // summary drains at a time; producers always write the other preallocated window.
        lock (_summaryGate)
        {
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            double seconds, core, flowMean, flowRate, detectionMean, detectionRate, flowAge, overlayAge, overlayRate;
            TrackingLatencyProfile completed;
            lock (_gate)
            {
                seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_since).TotalSeconds);
                core = (cpu - _cpu).TotalSeconds / seconds * 100;
                flowMean = _flows == 0 ? 0 : _flowMs / _flows;
                flowRate = _flows / seconds;
                detectionMean = _detections == 0 ? 0 : _detectionMs / _detections;
                detectionRate = _detections / seconds;
                flowAge = _flowLags == 0 ? 0 : _flowLagMs / _flowLags;
                overlayAge = _overlayLags == 0 ? 0 : _overlayLagMs / _overlayLags;
                overlayRate = _overlayLags / seconds;
                completed = _latency;
                _latency = _spareLatency;
                _spareLatency = completed;
                _latency.ContinueFrom(completed);
                _cpu = cpu;
                _since = Stopwatch.GetTimestamp();
                _flowMs = _detectionMs = _flowLagMs = _overlayLagMs = 0;
                _flows = _detections = _flowLags = _overlayLags = 0;
            }
            return $"cpu={core:0}% ({core / Environment.ProcessorCount:0.0}% gesamt) " +
                   $"fluss={flowMean:0.0}ms×{flowRate:0}/s suchdauer={detectionMean:0}ms×{detectionRate:0.0}/s " +
                   $"opencv={MapTrackingService.OpenCvThreads} " +
                   $"verzögerung: bildfluss={flowAge:0}ms overlay={overlayAge:0}ms×{overlayRate:0}/s " +
                   completed.Drain().ToLogText();
        }
    }
}
