using System.Text.Json;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>Probe (SOULCREST_PROBE_IMAGE): can the locator find a minimap picture from a diagnosis package?</summary>
public sealed class ColleagueMinimapExploration(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        var image = Environment.GetEnvironmentVariable("SOULCREST_PROBE_IMAGE");
        if (image is null || !File.Exists(image))
            return;
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        using var picture = Cv2.ImRead(image);
        using var frame = new Mat();
        Cv2.Resize(picture, frame, new OpenCvSharp.Size(514, 292), 0, 0, InterpolationFlags.Cubic);
        foreach (var zoom in new[] { 5 })
        {
            using var reference = Reference(mapdata!, "altgard", zoom);
            foreach (var scale in new[] { 1.0, 0.85, 0.76, 0.6 })
            {
                using var locator = new MapLocator(reference);
                using var scaled = new Mat();
                Cv2.Resize(frame, scaled, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
                var fix = locator.Locate(scaled);
                var at = fix?.FrameToReference(new Point2d(257 * scale, 146 * scale));
                output.WriteLine($"z{zoom} scale {scale}: {locator.LastInfo} -> {(at is { } p ? $"({p.X * reference.WorldPerPixel:0}, {p.Y * reference.WorldPerPixel:0})" : "nicht gefunden")}");
            }
        }
    }

    private static MapReference Reference(string mapdata, string map, int zoom)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"{map}-z{zoom}-{version}-full.bin");
        if (MapReference.Load(cache) is { } cached)
            return cached;
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == map);
        using var gray = MapReference.ComposeTiles(Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!), zoom);
        var size = definition.GetProperty("size").GetDouble();
        var reference = MapReference.Build(map, gray, size / (256 << zoom));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        reference.Save(cache);
        return reference;
    }
}
