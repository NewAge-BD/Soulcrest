using System.Text.Json;
using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Services;
using Soulcrest.App.Capture;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// User report 2026-10-03: opening the in-game world map scrambled the overlay. Two real full-screen
/// captures (2560×1440): the open world map of Verteron, and normal play with the small map window at
/// (1792, 960, 518, 469). Only the first may count as "world map open".
/// </summary>
public sealed class WorldMapDetectionTests(ITestOutputHelper output)
{
    private static readonly Rectangle SmallMap = new(1792, 960, 518, 469);
    private static readonly Rectangle Screen = new(0, 0, 2560, 1440);

    [Theory]
    [InlineData("live-2026-10-03-lost-screen.png", true)]   // "Map: Verteron", full screen
    [InlineData("live-2026-10-03-screen.png", false)]       // fight scene, small map bottom right
    public void TellsTheWorldMapFromTheSmallMap(string file, bool worldMap)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", file);
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !File.Exists(path))
            return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(progress.MapDataDirectory, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z4-{version}-full-app.bin");
        using var reference = MapReference.Load(cache) ?? Build(progress.MapDataDirectory, cache);
        using var locator = new MapLocator(reference);
        using var full = Cv2.ImRead(path);
        using var small = new Mat();
        Cv2.Resize(full, small, new OpenCvSharp.Size(), MapTrackingService.WorldMapScale, MapTrackingService.WorldMapScale, InterpolationFlags.Area);

        var fix = locator.Locate(small);

        output.WriteLine($"{file}: {locator.LastInfo} bounds {fix?.InlierBounds}");
        var open = fix is not null && fix.Inliers >= 30 && MapTrackingService.SpreadsBeyond(fix, SmallMap, Screen);
        Assert.Equal(worldMap, open);
    }

    private static MapReference Build(string mapdata, string cache)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var definition = manifest.RootElement.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "verteron");
        using var gray = MapReference.ComposeTiles(Path.Combine(mapdata, definition.GetProperty("tiles").GetString()!), 4);
        var reference = MapReference.Build("verteron", gray, 1);
        reference.Save(cache);
        return reference;
    }

    [Fact]
    public void WorldMapAtTheSavedMinimapZoomCannotPublishAPlayerPosition()
    {
        var progress = new ProgressService();
        Assert.NotNull(progress.MapDataDirectory);
        using var full = LoadFrame("live-2026-10-03-lost-screen.png");
        using var crop = new Mat(full, new OpenCvSharp.Rect(SmallMap.X, SmallMap.Y, SmallMap.Width, SmallMap.Height));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(progress.MapDataDirectory, "manifest.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var cache = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref", $"verteron-z4-{version}-full-app.bin");
        using var reference = MapReference.Load(cache) ?? Build(progress.MapDataDirectory, cache);
        using var locator = new MapLocator(reference);
        var cropFix = locator.Locate(crop);
        Assert.NotNull(cropFix); // reproduces the misleading successful match in the minimap rectangle

        var settings = new SettingsService();
        settings.Current.LastMap = "verteron";
        settings.Current.MiniMapZoom = cropFix.Scale / reference.WorldPerPixel;
        var savedZoom = settings.Current.MiniMapZoom;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, progress, capture);
        var published = new List<PlayerPosition?>();
        tracking.Changed += () => published.Add(tracking.Position);

        tracking.ProcessMiniMapFrame(SmallMap, Screen, full, CancellationToken.None);

        Assert.True(tracking.WorldMapOpen);
        Assert.Null(tracking.Position);
        Assert.All(published, p => Assert.Null(p));
        Assert.Equal(savedZoom, settings.Current.MiniMapZoom);
        Assert.True(tracking.Placement!.WorldMap);
    }

    [Fact]
    public void OpeningAndMovingWorldMapKeepsLastConfirmedPlayerAndZoom()
    {
        var progress = new ProgressService();
        Assert.NotNull(progress.MapDataDirectory);
        var settings = new SettingsService();
        settings.Current.LastMap = "verteron";
        settings.Current.MiniMapZoom = null;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, progress, capture);
        using var normal = LoadFrame("live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        var player = tracking.Position;
        Assert.NotNull(player);
        Assert.False(tracking.WorldMapOpen);
        var zoom = settings.Current.MiniMapZoom;
        var published = new List<PlayerPosition?>();
        tracking.Changed += () => published.Add(tracking.Position);
        using var world = LoadFrame("live-2026-10-03-lost-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, world, CancellationToken.None);
        // A pan translates the map under the fixed anchor, without moving the player.
        using var transform = Mat.FromArray(new double[,] { { 1, 0, -60 }, { 0, 1, -30 } });
        using var panned = new Mat();
        Cv2.WarpAffine(world, panned, transform, world.Size());
        tracking.ProcessMiniMapFrame(SmallMap, Screen, panned, CancellationToken.None);

        Assert.True(tracking.WorldMapOpen);
        Assert.Same(player, tracking.Position);
        Assert.All(published, p => Assert.Same(player, p));
        Assert.Equal(zoom, settings.Current.MiniMapZoom);
        Assert.Equal(new Point2d(player.X, player.Y), tracking.Placement!.PlayerWorld);

        // Two missing full-screen matches close world-map mode, then normal tracking resumes.
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.Same(player, tracking.Position);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.False(tracking.WorldMapOpen);
        Assert.Same(player, tracking.Position);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.NotSame(player, tracking.Position);
        Assert.InRange(Math.Abs(tracking.Position!.X - player.X), 0, 3);
        Assert.InRange(Math.Abs(tracking.Position.Y - player.Y), 0, 3);
    }

    private static Mat LoadFrame(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", name);
        Assert.True(File.Exists(path), $"Required regression fixture missing: {path}");
        var frame = Cv2.ImRead(path);
        Assert.False(frame.Empty());
        return frame;
    }
}
