using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// What one map detection costs, step by step, on the live captures of 2026-10-03 (SOULCREST_MAP_COST=1):
/// small-map search global and local, and the world-map probe on the quarter-size screen.
/// </summary>
public sealed class MapDetectionCostExploration(ITestOutputHelper output)
{
    [Fact]
    public void StepCosts()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_MAP_COST") != "1")
            return;
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        Assert.NotNull(mapdata);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking");
        using var region = Cv2.ImRead(Path.Combine(fixtures, "live-2026-10-03-region.png"));
        using var screen = Cv2.ImRead(Path.Combine(fixtures, "live-2026-10-03-screen.png"));
        using var small = new Mat();
        Cv2.Resize(screen, small, new OpenCvSharp.Size(), 0.25, 0.25, InterpolationFlags.Area);
        output.WriteLine($"Minimap {region.Width}×{region.Height}, Bildschirm {screen.Width}×{screen.Height} → {small.Width}×{small.Height}");

        using var fine = Reference(mapdata!, 5);
        using var coarse = Reference(mapdata!, 4);
        using var mini = new MapLocator(fine);
        Time("Minimap global (feine Referenz)", () => { mini.Reset(); mini.Locate(region); });
        mini.Locate(region);
        Time("Minimap lokal (feine Referenz)", () => mini.Locate(region));
        using var world = new MapLocator(coarse);
        Time("Weltkarten-Prüfung global (grobe Referenz, Viertel-Bildschirm)", () => { world.Reset(); world.Locate(small); });
        Time("Viertel-Bildschirm verkleinern", () => { using var s = new Mat(); Cv2.Resize(screen, s, new OpenCvSharp.Size(), 0.25, 0.25, InterpolationFlags.Area); });
        Time("Vorschau-JPEG", () => { Cv2.ImEncode(".jpg", region, out _); });
        foreach (var name in new[] { "live-2026-10-03-verteron2-region.png", "live-2026-10-03-lost2-region.png", "live-2026-10-03-lost-region.png" })
        {
            using var other = Cv2.ImRead(Path.Combine(fixtures, name));
            foreach (var factor in new[] { 1.0, 0.75, 0.6 })
            {
                using var scaled = new Mat();
                Cv2.Resize(other, scaled, new OpenCvSharp.Size(), factor, factor, InterpolationFlags.Area);
                using var locator = new MapLocator(fine);
                var fix = locator.Locate(scaled);
                var centre = fix?.FrameToReference(new Point2d(scaled.Width / 2.0, scaled.Height / 2.0));
                output.WriteLine($"{name} ×{factor}: {locator.LastInfo}, Mitte {(centre is { } c ? $"({c.X * fine.WorldPerPixel:0.0}, {c.Y * fine.WorldPerPixel:0.0})" : "–")}");
            }
        }
        foreach (var factor in new[] { 1.0, 0.75, 0.6, 0.5 })
        {
            using var scaled = new Mat();
            Cv2.Resize(region, scaled, new OpenCvSharp.Size(), factor, factor, InterpolationFlags.Area);
            using var locator = new MapLocator(fine);
            var fix = locator.Locate(scaled);
            var player = fix?.FrameToReference(new Point2d(266 * factor, 237 * factor));
            Time($"Minimap ×{factor} lokal", () => locator.Locate(scaled));
            output.WriteLine($"   ×{factor}: {locator.LastInfo}, Spieler {(player is { } p ? $"({p.X * fine.WorldPerPixel:0.0}, {p.Y * fine.WorldPerPixel:0.0})" : "–")}");
        }
    }

    /// <summary>
    /// Detection with OpenCV on all threads and on one (2026-10-07): the 60 fps flow costs half the CPU
    /// on one thread, and the setting holds for the whole process.
    /// </summary>
    [Fact]
    public void DetectionCostByOpenCvThreads()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_MAP_COST") != "1")
            return;
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        Assert.NotNull(mapdata);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking");
        using var region = Cv2.ImRead(Path.Combine(fixtures, "live-2026-10-03-region.png"));
        using var screen = Cv2.ImRead(Path.Combine(fixtures, "live-2026-10-03-screen.png"));
        using var fine = Reference(mapdata!, 5);
        using var coarse = Reference(mapdata!, 4);
        var threads = Cv2.GetNumThreads();
        foreach (var count in new[] { threads, 4, 2, 1 })
        {
            Cv2.SetNumThreads(count);
            using var mini = new MapLocator(fine);
            mini.Locate(region);
            TimeCpu($"Minimap lokal, OpenCV {count} Threads", () => mini.Locate(region));
            TimeCpu($"Minimap global, OpenCV {count} Threads", () => { mini.Reset(); mini.Locate(region); });
            using var world = new MapLocator(coarse);
            TimeCpu($"Weltkarten-Prüfung (Viertel-Bildschirm verkleinern + suchen), OpenCV {count} Threads", () =>
            {
                using var small = new Mat();
                Cv2.Resize(screen, small, new OpenCvSharp.Size(), 0.25, 0.25, InterpolationFlags.Area);
                world.Reset();
                world.Locate(small);
            });
        }
        Cv2.SetNumThreads(threads);
    }

    private void TimeCpu(string name, Action action)
    {
        action();
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            action();
        process.Refresh();
        output.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds / 10:0.0} ms Zeit, {(process.TotalProcessorTime - cpu).TotalMilliseconds / 10:0.0} ms CPU");
    }

    private void Time(string name, Action action)
    {
        action();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
            action();
        output.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds / 5:0.0} ms");
    }

    private static MapReference Reference(string mapdata, int zoom)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z{zoom}-{version}-full.bin");
        if (MapReference.Load(cache) is { } cached)
            return cached;
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "verteron");
        using var gray = MapReference.ComposeTiles(Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!), zoom);
        var reference = MapReference.Build("verteron", gray, 4096.0 / (256 << zoom));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        reference.Save(cache);
        return reference;
    }
}
