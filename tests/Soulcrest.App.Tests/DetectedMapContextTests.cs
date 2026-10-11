using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DetectedMapContextTests : IDisposable
{
    private static readonly Rectangle SmallMap = new(1792, 960, 518, 469);
    private static readonly Rectangle Screen = new(0, 0, 2560, 1440);
    private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;

    public void Dispose()
    {
        if (_settings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
    }

    [Fact]
    public void StoredMapPreferenceDoesNotBecomeDetectedEvidence()
    {
        var settings = new SettingsService();
        settings.Current.LastMap = "verteron";
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        Assert.Null(tracking.DetectedMap);
        Assert.Null(tracking.Position);
        Assert.False(tracking.Running);
    }

    [Fact]
    public void RealMinimapAndWorldMapPublishDifferentKindsOfEvidenceWithoutMovingThePlayer()
    {
        using var capture = new GameCaptureService();
        using var tracking = Tracker(capture);
        using var normal = Frame("live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        var player = Assert.IsType<PlayerPosition>(tracking.Position);
        var minimap = Assert.IsType<DetectedMapContext>(tracking.DetectedMap);
        Assert.Equal("verteron", minimap.MapId);
        Assert.False(minimap.WorldMap);
        Assert.Equal(player.At, minimap.At);

        using var world = Frame("live-2026-10-03-lost-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, world, CancellationToken.None);
        var opened = Assert.IsType<DetectedMapContext>(tracking.DetectedMap);
        Assert.Equal("verteron", opened.MapId);
        Assert.True(opened.WorldMap);
        Assert.True(opened.At >= minimap.At);
        Assert.Same(player, tracking.Position);

        using var transform = Mat.FromArray(new double[,] { { 1, 0, -60 }, { 0, 1, -30 } });
        using var panned = new Mat();
        Cv2.WarpAffine(world, panned, transform, world.Size());
        tracking.ProcessMiniMapFrame(SmallMap, Screen, panned, CancellationToken.None);
        var moved = Assert.IsType<DetectedMapContext>(tracking.DetectedMap);
        Assert.True(moved.WorldMap);
        Assert.True(moved.At >= opened.At);
        Assert.Same(player, tracking.Position);

        // The first missing world-map fix is held, with the original evidence time. Closing clears it.
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.Same(moved, tracking.DetectedMap);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.False(tracking.WorldMapOpen);
        Assert.Null(tracking.DetectedMap);
        Assert.Same(player, tracking.Position);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.False(Assert.IsType<DetectedMapContext>(tracking.DetectedMap).WorldMap);
        tracking.Shutdown();
        Assert.Null(tracking.DetectedMap);
    }

    [Fact]
    public void HeldMinimapDoesNotRefreshItsEvidenceAndLoadImmediatelyInvalidatesIt()
    {
        using var capture = new GameCaptureService();
        using var tracking = Tracker(capture);
        using var normal = Frame("live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        var identity = Assert.IsType<DetectedMapContext>(tracking.DetectedMap);
        using var empty = new Mat(normal.Size(), normal.Type(), Scalar.All(40));
        tracking.ProcessMiniMapFrame(SmallMap, Screen, empty, CancellationToken.None);
        Assert.True(tracking.Found);
        Assert.Same(identity, tracking.DetectedMap);
        var notifications = 0;
        tracking.Changed += () => notifications++;
        tracking.NoteLoadingScreen();
        Assert.Null(tracking.DetectedMap);
        Assert.Equal(1, notifications);
        tracking.NoteLoadingScreen();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void MenuInvalidFrameAndInstanceClearIdentityWithoutStartingCapture()
    {
        using var capture = new GameCaptureService();
        using var tracking = Tracker(capture);
        using var normal = Frame("live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.NotNull(tracking.DetectedMap);
        tracking.UpdateEscMenuContext(true);
        Assert.Null(tracking.DetectedMap);
        tracking.UpdateEscMenuContext(false);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.NotNull(tracking.DetectedMap);
        tracking.ProcessMiniMapFrame(new Rectangle(-10, -10, 100, 100), Screen, normal, CancellationToken.None);
        Assert.Null(tracking.DetectedMap);
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.NotNull(tracking.DetectedMap);
        tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(31), null);
        Assert.True(tracking.InInstance);
        Assert.Null(tracking.DetectedMap);
        Assert.Equal(0, capture.Diagnostics.Grabs);
        Assert.False(tracking.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadingOrStopDuringACompletedMatchCannotRepublishItsCapturedPosition(bool stop)
    {
        var settings = new SettingsService();
        settings.Current.LastMap = "verteron";
        settings.Current.MiniMapZoom = null;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        var invalidated = false;
        // SIFT has succeeded when the remembered zoom changes, but publication has not happened.
        // This deterministic notification reproduces a concurrent load/stop at that boundary.
        settings.Changed += () =>
        {
            if (invalidated || settings.Current.MiniMapZoom is null) return;
            invalidated = true;
            if (stop) tracking.Shutdown();
            else tracking.NoteLoadingScreen();
        };
        using var normal = Frame("live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, normal, CancellationToken.None);
        Assert.True(invalidated);
        Assert.Null(tracking.Position);
        Assert.Null(tracking.DetectedMap);
        Assert.False(tracking.Found);
        Assert.Null(tracking.Placement);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameCapturedBeforeLoadingCannotRefreshMinimapOrWorldMapEvidence(bool worldMap)
    {
        using var capture = new GameCaptureService();
        using var tracking = Tracker(capture);
        using var frame = Frame(worldMap ? "live-2026-10-03-lost-screen.png" : "live-2026-10-03-screen.png");
        tracking.ProcessMiniMapFrame(SmallMap, Screen, frame, CancellationToken.None);
        var identity = Assert.IsType<DetectedMapContext>(tracking.DetectedMap);
        Assert.Equal(worldMap, identity.WorldMap);
        var position = tracking.Position;
        tracking.NoteLoadingScreen();
        // The loop snapshots this generation before capture and passes it into the worker.
        tracking.ProcessMiniMapFrame(SmallMap, Screen, frame, CancellationToken.None, contextGeneration: 0);
        Assert.Null(tracking.DetectedMap);
        Assert.Same(position, tracking.Position);
    }

    private static MapTrackingService Tracker(GameCaptureService capture)
    {
        var settings = new SettingsService();
        settings.Current.LastMap = "verteron";
        settings.Current.MiniMapZoom = null;
        return new(settings, new ProgressService(), capture);
    }

    private static Mat Frame(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", name);
        Assert.True(File.Exists(path), $"Required fixture missing: {path}");
        var frame = Cv2.ImRead(path);
        Assert.False(frame.Empty());
        return frame;
    }
}
