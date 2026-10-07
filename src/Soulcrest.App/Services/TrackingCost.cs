using System.Diagnostics;

namespace Soulcrest.App.Services;

/// <summary>
/// What player tracking costs while it runs, for the map-tracking.log line (2026-10-07: Soulcrest used
/// 1.4 cores while tracking). Process CPU since the last line, plus the mean time of one flow step
/// (60 per second) and one detection. Thread-safe: detections finish on another thread.
/// </summary>
internal sealed class TrackingCost
{
    private readonly object _gate = new();
    private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
    private long _since = Stopwatch.GetTimestamp();
    private double _flowMs, _detectionMs, _flowLagMs, _overlayLagMs;
    private int _flows, _detections, _flowLags, _overlayLags;

    public void AddFlow(TimeSpan spent)
    {
        lock (_gate) { _flowMs += spent.TotalMilliseconds; _flows++; }
    }

    public void AddDetection(TimeSpan spent)
    {
        lock (_gate) { _detectionMs += spent.TotalMilliseconds; _detections++; }
    }

    /// <summary>Screen picture arrived -> flow step done.</summary>
    public void AddFlowLag(TimeSpan lag)
    {
        lock (_gate) { _flowLagMs += lag.TotalMilliseconds; _flowLags++; }
    }

    /// <summary>Screen picture arrived -> overlay picture handed to Windows (the screen shows it with the next refresh).</summary>
    public void AddOverlayLag(TimeSpan lag)
    {
        lock (_gate) { _overlayLagMs += lag.TotalMilliseconds; _overlayLags++; }
    }

    /// <summary>"cpu=85% (5.3% gesamt) fluss=3.1ms×60/s suchdauer=190ms×4/s"; starts the next window.</summary>
    public string TakeSummary()
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        lock (_gate)
        {
            var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_since).TotalSeconds);
            var core = (cpu - _cpu).TotalSeconds / seconds * 100;
            var text = $"cpu={core:0}% ({core / Environment.ProcessorCount:0.0}% gesamt) " +
                       $"fluss={(_flows == 0 ? 0 : _flowMs / _flows):0.0}ms×{_flows / seconds:0}/s " +
                       $"suchdauer={(_detections == 0 ? 0 : _detectionMs / _detections):0}ms×{_detections / seconds:0.0}/s " +
                       $"opencv={MapTrackingService.OpenCvThreads} " +
                       $"verzögerung: bildfluss={(_flowLags == 0 ? 0 : _flowLagMs / _flowLags):0}ms overlay={(_overlayLags == 0 ? 0 : _overlayLagMs / _overlayLags):0}ms×{_overlayLags / seconds:0}/s";
            _cpu = cpu;
            _since = Stopwatch.GetTimestamp();
            _flowMs = _detectionMs = _flowLagMs = _overlayLagMs = 0;
            _flows = _detections = _flowLags = _overlayLags = 0;
            return text;
        }
    }
}
