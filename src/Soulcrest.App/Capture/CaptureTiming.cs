using System.Diagnostics;

namespace Soulcrest.App.Capture;

/// <summary>One successful WGC grab, in milliseconds; default means no successful WGC picture.</summary>
internal readonly record struct CaptureTiming(bool Valid, double GateWaitMs, double CopyMs,
    double MapWaitMs, double ConversionMs, double TotalMs, double SourceAgeAtCopyMs = 0,
    long SourceFrameId = 0, long ArrivedCount = 0, long PublishedCount = 0, long FrameGateDropCount = 0,
    long CaptureSessionId = 0)
{
    internal static CaptureTiming FromTicks(long gateWait, long copy, long mapWait, long conversion, long total) =>
        new(true, Milliseconds(gateWait), Milliseconds(copy), Milliseconds(mapWait), Milliseconds(conversion), Milliseconds(total));

    /// <summary>Add the service's serialisation wait; total spans the complete service call.</summary>
    internal CaptureTiming WithOuterTiming(long gateWait, long total) => Valid
        ? this with { GateWaitMs = GateWaitMs + Milliseconds(gateWait), TotalMs = Milliseconds(total) }
        : default;

    /// <summary>Selected staging frame at copy start, plus cumulative counters when its read completed.</summary>
    internal CaptureTiming WithSourceFrame(long frameAt, long copyStartedAt, long frameId, long arrived,
        long published, long frameGateDrops, long sessionId) => Valid
        ? this with
        {
            SourceAgeAtCopyMs = Milliseconds(Math.Max(0, copyStartedAt - frameAt)),
            SourceFrameId = frameId,
            ArrivedCount = arrived,
            PublishedCount = published,
            FrameGateDropCount = frameGateDrops,
            CaptureSessionId = sessionId,
        }
        : default;

    private static double Milliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);
}
