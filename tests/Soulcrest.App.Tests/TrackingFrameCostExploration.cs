using System.Diagnostics;
using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// What one frame of the 60 fps small-map loop costs, part by part (SOULCREST_TRACKING_COST=1): flow step on
/// the live minimap capture of 2026-10-03 and one overlay picture over it with the Altgard pets.
/// Measured 2026-10-07 because Soulcrest used 1.4 cores while tracking.
/// </summary>
public sealed class TrackingFrameCostExploration(ITestOutputHelper output)
{
    [Fact]
    public void FrameCosts()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_TRACKING_COST") != "1")
            return;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking");
        using var region = Cv2.ImRead(Path.Combine(fixtures, "live-2026-10-03-region.png"));
        output.WriteLine($"Minimap {region.Width}×{region.Height}");

        // Consecutive frames: the map moves by a pixel each frame, like walking.
        var frames = Enumerable.Range(0, 8).Select(i =>
        {
            var shifted = new Mat();
            using var m = Mat.FromArray(new double[,] { { 1, 0, i }, { 0, 1, i * 0.5 } });
            Cv2.WarpAffine(region, shifted, m, region.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            return shifted;
        }).ToList();
        var threads = Cv2.GetNumThreads();
        foreach (var count in new[] { threads, 4, 2, 1 })
        {
            Cv2.SetNumThreads(count);
            using var flow = new MapFlow();
            var n = 0;
            Time($"Graustufen + Bildfluss (je Bild, OpenCV {count} Threads)", 60, () =>
            {
                using var gray = new Mat();
                Cv2.CvtColor(frames[n++ % frames.Count], gray, ColorConversionCodes.BGR2GRAY);
                flow.Step(gray);
            });
            Time($"Bildfluss bei Stillstand (OpenCV {count} Threads)", 60, () =>
            {
                using var gray = new Mat();
                Cv2.CvtColor(frames[0], gray, ColorConversionCodes.BGR2GRAY);
                flow.Step(gray);
            });
        }
        Cv2.SetNumThreads(threads);

        var mapdata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "imports", "generated", "mapdata"));
        using var icon = new Bitmap(26, 26);
        var pets = MapPetMarkers.For(mapdata, "altgard").Select(s => new RouteOverlayForm.PetSymbol(s.X, s.Y, icon, "St. 1 · 6/25", false)).ToList();
        const double scale = 1.68; // map-tracking.log of the measured session
        var fix = new MapFix(scale, 0, region.Width / 2.0 - 4000 * scale, 0, scale, region.Height / 2.0 - 4000 * scale, 100, 100, 0.4, true);
        var placement = new MapPlacement("altgard", fix, 1, new Rectangle(0, 0, region.Width, region.Height), new Point2d(region.Width / 2.0, region.Height / 2.0));
        Time($"Overlay-Bild Minimap ({pets.Count} Pets, DIB)", 60, () => RouteOverlayForm.RenderFrame(placement, [], pets).Dispose());
        Time("Overlay-Bild Minimap leer", 60, () => RouteOverlayForm.RenderFrame(placement, [], []).Dispose());
        foreach (var f in frames)
            f.Dispose();
    }

    private void Time(string name, int perSecond, Action action)
    {
        for (var i = 0; i < 5; i++)
            action();
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        const int runs = 60;
        for (var i = 0; i < runs; i++)
            action();
        process.Refresh();
        var ms = watch.Elapsed.TotalMilliseconds / runs;
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds / runs;
        output.WriteLine($"{name}: {ms:0.00} ms Zeit, {cpuMs:0.00} ms CPU → bei {perSecond}/s {cpuMs * perSecond / 10:0.0} % eines Kerns");
    }
}
