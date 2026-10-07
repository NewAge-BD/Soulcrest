using System.Text.Json;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Live capture 2026-10-03 (tests/fixtures/map-tracking/live-2026-10-03-region.png): the in-game map of
/// Verteron, semi-transparent, with yellow auto-path lines after the user set a waypoint ("wird gerade
/// nicht erkannt"). With the old reference (z4, 100k feature budget) only 30 consistent pairs, just
/// above the old limit of 20. Player marker (blue arrow) at about (266, 237) of the 518×469 area.
/// </summary>
public sealed class MapLiveCaptureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(4, 40)]
    [InlineData(5, 150)]
    public void VerteronWithPathLinesIsFoundClearly(int zoom, int minInliers)
    {
        string? mapdata = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && mapdata is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                mapdata = candidate;
        }
        var live = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", "live-2026-10-03-region.png");
        if (mapdata is null || !File.Exists(live))
            return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z{zoom}-{version}-full.bin");
        using var reference = MapReference.Load(cache) ?? Build(mapdata, zoom, cache);
        using var locator = new MapLocator(reference);
        using var frame = Cv2.ImRead(live);

        var fix = locator.Locate(frame);

        output.WriteLine($"z{zoom}: {locator.LastInfo}");
        Assert.NotNull(fix);
        Assert.True(fix.Inliers >= minInliers, $"nur {fix.Inliers} Treffer");
        var player = fix.FrameToReference(new Point2d(266, 237));
        double x = player.X * reference.WorldPerPixel, y = player.Y * reference.WorldPerPixel;
        output.WriteLine($"Spieler bei ({x:0.0}, {y:0.0})");
        // New public map texture: independently checked with OpenCV ORB/Hamming (229 RANSAC
        // inliers, x=2093.39, y=2224.32), rather than this locator's SIFT features.
        // The former texture used a different crop. See KARTENDATEN_MIGRATION.md.
        Assert.InRange(x, 2086, 2100);
        Assert.InRange(y, 2217, 2231);
    }

    private static MapReference Build(string mapdata, int zoom, string cache)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "verteron");
        using var gray = MapReference.ComposeTiles(Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!), zoom);
        var reference = MapReference.Build("verteron", gray, 4096.0 / (256 << zoom));
        reference.Save(cache);
        return reference;
    }
}
