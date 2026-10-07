using System.Diagnostics;
using Soulcrest.App.Capture;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// Diagnose (SOULCREST_WGC_LATENCY=1, game on the primary screen): how old the picture of the world-map loop
/// is when it is read, at the loop's pace (60 per second, quarter size). Live 2026-10-07: 130-160 ms.
/// </summary>
public sealed class WgcLatencyExploration(ITestOutputHelper output)
{
    [Fact]
    public void AgeOfTheWorldMapPicture()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_WGC_LATENCY") != "1")
            return;
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var wgc = MonitorCapture.TryStart(screen);
        Assert.NotNull(wgc);
        output.WriteLine($"HDR: {wgc.IsHdr}");
        Thread.Sleep(300);
        var grabs = new List<double>();
        var ages = new List<double>();
        var arrived = wgc.FramesArrived;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 4000)
        {
            var started = Stopwatch.GetTimestamp();
            using var mat = wgc.CaptureMat(screen, 0.25);
            grabs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (wgc.ReadFrameAt != 0)
                ages.Add(Stopwatch.GetElapsedTime(wgc.ReadFrameAt).TotalMilliseconds);
            var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (spent < 16.7)
                Thread.Sleep(TimeSpan.FromMilliseconds(16.7 - spent));
        }
        var seconds = watch.Elapsed.TotalSeconds;
        output.WriteLine($"Bilder gelesen: {grabs.Count / seconds:0}/s, WGC-Bilder angekommen: {(wgc.FramesArrived - arrived) / seconds:0}/s");
        output.WriteLine($"Lesen: Mittel {grabs.Average():0.0} ms, max {grabs.Max():0.0} ms");
        if (ages.Count > 0)
            output.WriteLine($"Alter beim Lesen: Mittel {ages.Average():0} ms, min {ages.Min():0}, max {ages.Max():0}");
    }
}
