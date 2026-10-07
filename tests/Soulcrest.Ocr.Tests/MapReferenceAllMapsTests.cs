using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Automatic map detection tries the other maps while the current one is not found. A cut-out of one
/// map must therefore be found on its own map only, never on another one with enough pairs to switch.
/// </summary>
public sealed class MapReferenceAllMapsTests(ITestOutputHelper output)
{
    [Fact]
    public void CutOutIsFoundOnItsOwnMapOnly()
    {
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        if (mapdata is null)
            return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var maps = manifest.RootElement.GetProperty("maps").EnumerateArray()
            .Select(m => (Id: m.GetProperty("id").GetString()!, Tiles: m.GetProperty("tiles").GetString()!, RefZoom: m.GetProperty("refZoom").GetInt32(), Size: m.GetProperty("size").GetInt32(), MaxZoom: m.GetProperty("maxNativeZoom").GetInt32()))
            .ToList();
        var locators = new List<(string Id, MapLocator Locator, MapReference Reference, Mat Fine, double FineScale)>();
        try
        {
            foreach (var map in maps)
            {
                var zoom = Math.Clamp(map.RefZoom - (int)Math.Round(Math.Log2(map.Size / 4096.0)), 0, map.MaxZoom);
                var fineZoom = Math.Min(map.MaxZoom, zoom + 1);
                var pattern = Path.Combine(mapdata, map.Tiles.Replace('/', Path.DirectorySeparatorChar));
                var watch = Stopwatch.StartNew();
                var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"{map.Id}-z{zoom}-{version}.bin");
                var reference = MapReference.Load(cache);
                if (reference is null)
                {
                    using var gray = MapReference.ComposeTiles(pattern, zoom);
                    reference = MapReference.Build(map.Id, gray, map.Size / (256.0 * (1 << zoom)));
                    reference.Save(cache);
                }
                output.WriteLine($"{map.Id}: {reference.Points.Length} Merkmale, {watch.ElapsedMilliseconds} ms (z{zoom}, {reference.WorldPerPixel} Welt/px)");
                locators.Add((map.Id, new MapLocator(reference), reference, MapReference.ComposeTiles(pattern, fineZoom), Math.Pow(2, fineZoom - zoom)));
            }

            foreach (var source in locators)
            {
                // Cut-out around the densest part of the map (most keypoints), at twice the detail.
                var points = source.Reference.Points;
                var cx = points.Select(p => p.X).Order().ElementAt(points.Length / 2);
                var cy = points.Select(p => p.Y).Order().ElementAt(points.Length / 2);
                var rect = new Rect((int)(cx * source.FineScale) - 300, (int)(cy * source.FineScale) - 220, 600, 440);
                using var frame = new Mat(source.Fine, rect).Clone();
                foreach (var target in locators)
                {
                    target.Locator.Reset();
                    var fix = target.Locator.Locate(frame);
                    if (target.Id == source.Id)
                    {
                        output.WriteLine($"{source.Id} auf sich selbst: {(fix is null ? "NICHT gefunden" : $"{fix.Inliers} Treffer")}");
                        Assert.NotNull(fix);
                        var centre = fix.FrameToReference(new Point2d(300, 220));
                        Assert.InRange(centre.X, cx - 3, cx + 3);
                        Assert.InRange(centre.Y, cy - 3, cy + 3);
                    }
                    else
                    {
                        // Switching maps needs 30 pairs (MapTrackingService.SwitchMinInliers).
                        Assert.True(fix is null || fix.Inliers < 30, $"{source.Id} fälschlich auf {target.Id} gefunden ({fix?.Inliers} Treffer)");
                    }
                }
            }
        }
        finally
        {
            foreach (var (_, locator, reference, fine, _) in locators)
            {
                locator.Dispose();
                reference.Dispose();
                fine.Dispose();
            }
        }
    }
}
