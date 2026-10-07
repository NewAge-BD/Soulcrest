using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The fine reference follows the player with a 0.6× picture; the fix must equal the full-size one.</summary>
public sealed class MapLocateScaleTests
{
    [Fact]
    public void ShrunkPictureGivesTheSamePlayerPosition()
    {
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        var live = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "map-tracking", "live-2026-10-03-region.png");
        if (mapdata is null || !File.Exists(live))
            return;
        using var reference = Reference(mapdata, 5);
        using var frame = Cv2.ImRead(live);
        using var full = new MapLocator(reference);
        using var shrunk = new MapLocator(reference);

        var expected = MapTrackingService.LocateAt(full, frame, 1.0);
        var actual = MapTrackingService.LocateAt(shrunk, frame, MapTrackingService.FineFrameScale);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        var anchor = new Point2d(266, 237); // player marker in the 518×469 area
        var a = expected.FrameToReference(anchor);
        var b = actual.FrameToReference(anchor);
        Assert.InRange(Math.Abs(a.X - b.X) * reference.WorldPerPixel, 0, 2);
        Assert.InRange(Math.Abs(a.Y - b.Y) * reference.WorldPerPixel, 0, 2);
        Assert.InRange(actual.Scale / expected.Scale, 0.98, 1.02);
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
