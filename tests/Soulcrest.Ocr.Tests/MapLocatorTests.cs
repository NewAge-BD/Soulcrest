using System.Text.Json;
using System.Diagnostics;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Player position from the in-game map, checked on a synthetic "in-game map": a cut-out of the next
/// finer tile level (twice the detail of the reference), darkened and with a UI frame drawn over it.
/// The cut-out's centre stands for the player; its map coordinates are known exactly.
/// </summary>
public sealed class MapLocatorTests(ITestOutputHelper output)
{
    private static string? MapData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                return candidate;
        }
        return null;
    }

    [Theory]
    [InlineData(2100, 1900)]
    [InlineData(1700, 2350)]
    public void FindsThePlayerOnACutOutOfVerteron(int worldX, int worldY)
    {
        if (MapData() is not { } mapdata)
            return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "verteron");
        var pattern = Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!);
        var version = manifest.RootElement.GetProperty("version").GetString();
        var watch = Stopwatch.StartNew();
        using var gray = MapReference.ComposeTiles(pattern, 4);
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z4-{version}.bin");
        using var reference = MapReference.Load(cache) ?? Build(gray, cache);
        output.WriteLine($"Referenz: {reference.Points.Length} Merkmale, {watch.ElapsedMilliseconds} ms");

        // "In-game map": 640×480 around the player from zoom 5 (world 4096 at zoom 4 -> 2 px per world unit).
        using var fine = MapReference.ComposeTiles(pattern, 5);
        using var color = new Mat();
        Cv2.CvtColor(fine, color, ColorConversionCodes.GRAY2BGR);
        var frameRect = new Rect(worldX * 2 - 320, worldY * 2 - 240, 640, 480);
        using var frame = new Mat(color, frameRect).Clone();
        frame.ConvertTo(frame, -1, 0.8, 10);
        Cv2.Rectangle(frame, new Rect(0, 0, 640, 36), Scalar.All(25), -1); // title bar of the map window
        Cv2.Circle(frame, new OpenCvSharp.Point(320, 240), 9, new Scalar(0, 200, 255), -1); // player arrow

        using var locator = new MapLocator(reference);
        watch.Restart();
        var fix = locator.Locate(frame);
        output.WriteLine($"Suche: {watch.ElapsedMilliseconds} ms, {locator.LastInfo}");
        Assert.NotNull(fix);
        var player = fix.FrameToReference(new Point2d(320, 240));
        double x = player.X * reference.WorldPerPixel, y = player.Y * reference.WorldPerPixel;
        output.WriteLine($"Spieler bei ({x:0.0}, {y:0.0}), erwartet ({worldX}, {worldY}), Maßstab {fix.Scale:0.00}, Fehler {fix.Error:0.00} px");
        Assert.InRange(x, worldX - 4, worldX + 4);
        Assert.InRange(y, worldY - 4, worldY + 4);
        Assert.InRange(fix.Scale, 1.9, 2.1);

        // Second frame: moved a little -> local search near the previous position.
        using var moved = new Mat(color, new Rect(frameRect.X + 60, frameRect.Y - 40, 640, 480)).Clone();
        var next = locator.Locate(moved);
        Assert.NotNull(next);
        Assert.True(next.Local);
        var p2 = next.FrameToReference(new Point2d(320, 240));
        Assert.InRange(p2.X * reference.WorldPerPixel, worldX + 30 - 4, worldX + 30 + 4);
        Assert.InRange(p2.Y * reference.WorldPerPixel, worldY - 20 - 4, worldY - 20 + 4);
    }

    [Fact]
    public void ForeignPictureIsNotPlaced()
    {
        if (MapData() is not { } mapdata)
            return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "verteron");
        var pattern = Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!);
        var version = manifest.RootElement.GetProperty("version").GetString();
        using var gray = MapReference.ComposeTiles(pattern, 4);
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z4-{version}.bin");
        using var reference = MapReference.Load(cache) ?? Build(gray, cache);
        using var locator = new MapLocator(reference);
        // A pet window screenshot is no map.
        using var foreign = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
        Assert.Null(locator.Locate(foreign));
    }

    private static MapReference Build(Mat gray, string cache)
    {
        var reference = MapReference.Build("verteron", gray, 1.0);
        reference.Save(cache);
        return reference;
    }
}
