using System.Drawing;
using System.Reflection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapCandidateRecoveryTests : IDisposable
{
    private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;

    public void Dispose()
    {
        if (_settings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WeakCoarseMapSwitchNeedsStrongFineConfirmationBeforeLeavingInstance(bool strongFine)
    {
        // Synthetic texture supplies exact image matches without retaining a user's game capture.
        var pixels = new byte[300 * 300 * 3];
        new Random(7319).NextBytes(pixels);
        using var frame = Mat.FromPixelData(300, 300, MatType.CV_8UC3, pixels);
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var reference = MapReference.Build("altgard", gray, 1);
        Assert.True(reference.Points.Length >= 40);

        var settings = new SettingsService();
        settings.Current.LootTrackingEnabled = false;
        settings.Current.LastMap = "abyss-reshanta-a";
        settings.Current.MiniMapZoom = null;
        settings.Current.PlayerAnchorX = 0.5;
        settings.Current.PlayerAnchorY = 0.5;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        var coarse = new MapLocator(CopyReference(reference, 24)) { Ratio = 0.01 };
        var fine = new MapLocator(CopyReference(reference, strongFine ? reference.Points.Length : 24)) { Ratio = 0.01 };
        Assert.InRange(coarse.Locate(frame)!.Inliers, 12, 29);
        coarse.Reset();
        Locators(tracking, "_coarse").Add("altgard", coarse);
        Locators(tracking, "_fine").Add("altgard", fine);
        var wrongPixels = new byte[pixels.Length];
        new Random(419).NextBytes(wrongPixels);
        using var wrongFrame = Mat.FromPixelData(300, 300, MatType.CV_8UC3, wrongPixels);
        using var wrongGray = new Mat();
        Cv2.CvtColor(wrongFrame, wrongGray, ColorConversionCodes.BGR2GRAY);
        using var wrongReference = MapReference.Build("elthen", wrongGray, 1);
        Locators(tracking, "_coarse").Add("poeta", new MapLocator(CopyReference(wrongReference, wrongReference.Points.Length)));
        Locators(tracking, "_coarse").Add("elthen", new MapLocator(CopyReference(wrongReference, wrongReference.Points.Length)));
        ((List<string>)typeof(MapTrackingService).GetField("_recentMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!).AddRange(["elthen", "poeta"]);
        Field(tracking, "_currentMap", "abyss-reshanta-a");
        Field(tracking, "_searchTick", 0); // the old alternating schedule would retry Abyss, not the new map
        Field(tracking, "_misses", 0); // an instance probe must not wait for three more old-map misses
        tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(31), null);
        typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Paused))!.SetValue(tracking, true);
        Assert.True(tracking.InInstance);
        Assert.True(tracking.Paused);

        var screen = new Rectangle(0, 0, frame.Width, frame.Height);
        tracking.ProcessMiniMapFrame(screen, screen, frame, CancellationToken.None);

        Assert.Equal(strongFine, tracking.Found);
        Assert.Equal(!strongFine, tracking.InInstance);
        Assert.Equal(!strongFine, tracking.Paused);
        Assert.Equal(0, capture.Diagnostics.Grabs);
        if (strongFine)
        {
            var position = Assert.IsType<PlayerPosition>(tracking.Position);
            Assert.Equal("altgard", position.MapId);
            Assert.InRange(position.X, 149, 151);
            Assert.InRange(position.Y, 149, 151);
            Assert.False(Assert.IsType<DetectedMapContext>(tracking.DetectedMap).WorldMap);
            Assert.True(fine.LastInfo.Inliers >= 30);
        }
        else
        {
            Assert.Null(tracking.Position);
            Assert.Null(tracking.DetectedMap);
            Assert.Null(tracking.Placement);
            Assert.InRange(fine.LastInfo.Inliers, 12, 29);
            Assert.Null(settings.Current.MiniMapZoom);
        }
    }

    [Fact]
    public async Task MissingFineReferenceDoesNotBlockProbeOrPublishWeakPosition()
    {
        var pixels = new byte[300 * 300 * 3];
        new Random(7319).NextBytes(pixels);
        using var frame = Mat.FromPixelData(300, 300, MatType.CV_8UC3, pixels);
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var reference = MapReference.Build("altgard", gray, 1);
        var settings = new SettingsService();
        settings.Current.LootTrackingEnabled = false;
        settings.Current.MiniMapZoom = null;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        Locators(tracking, "_coarse").Add("altgard", new MapLocator(CopyReference(reference, 24)) { Ratio = 0.01 });
        Field(tracking, "_currentMap", "abyss-reshanta-a");
        tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(31), null);
        var gates = (System.Collections.Concurrent.ConcurrentDictionary<(string, int), object>)typeof(MapTrackingService)
            .GetField("_loadGates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;
        var gate = gates.GetOrAdd(("altgard", 5), _ => new object());
        using var cancellation = new CancellationTokenSource();
        Task? probe = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { lock (gate) { entered.SetResult(); release.Wait(); } });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var screen = new Rectangle(0, 0, frame.Width, frame.Height);
            probe = Task.Run(() => tracking.ProcessMiniMapFrame(screen, screen, frame, cancellation.Token));
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(tracking.Found);
            Assert.True(tracking.InInstance);
            Assert.Null(tracking.Position);
            Assert.Null(tracking.DetectedMap);
            var preparations = (Dictionary<string, Task>)typeof(MapTrackingService)
                .GetField("_confirmationPreparations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;
            Assert.Single(preparations);
            Assert.False(preparations["altgard"].IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            await blocker;
            if (probe is not null)
            {
                try { await probe; }
                catch (OperationCanceledException) { }
            }
        }
        // Dispose drains the cancelled preparation before releasing native references.
    }

    private static Dictionary<string, MapLocator> Locators(MapTrackingService tracking, string name) =>
        (Dictionary<string, MapLocator>)typeof(MapTrackingService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;

    private static void Field(MapTrackingService tracking, string name, object value) =>
        typeof(MapTrackingService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracking, value);

    private static MapReference CopyReference(MapReference source, int count)
    {
        // Limit the coarse reference to a deliberately insufficient 24 pairs; the fine reference
        // has either the same weak evidence or every descriptor from the exact synthetic image.
        var constructor = typeof(MapReference).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (MapReference)constructor.Invoke([
            source.MapId, source.Width, source.Height, source.WorldPerPixel,
            source.Points.Take(count).ToArray(),
            source.DescriptorData.Take(count * MapReference.DescriptorLength).ToArray()
        ]);
    }
}

public sealed class InstanceMapRecoveryScheduleTests
{
    [Fact]
    public void OneProbeCoversEveryReadyMapOnceAndPrioritizesRecentDestinations()
    {
        string[] ready = ["abyss", "poeta", "altgard", "morheim"];
        var order = MapTrackingService.RecoveryCandidates("abyss", ready, ["abyss", "altgard"], 0);
        Assert.Equal(new[] { "altgard", "poeta", "morheim", "abyss" }, order);
        Assert.Equal(ready.Length, order.Distinct().Count());
    }

    [Fact]
    public void BudgetInterruptedSearchContinuesWithTheNextCandidateIncludingAfterTickOverflow()
    {
        string[] ready = ["abyss", "poeta", "altgard"];
        var first = MapTrackingService.RecoveryCandidates("abyss", ready, [], 0);
        var next = MapTrackingService.RecoveryCandidates("abyss", ready, [], 1);
        Assert.Equal(first[1], next[0]);
        Assert.Equal(3, MapTrackingService.RecoveryCandidates("abyss", ready, [], int.MinValue).Distinct().Count());
        Assert.Empty(MapTrackingService.RecoveryCandidates("abyss", [], ["altgard"], 0));
    }
}
