using System.Drawing;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class EscMenuDetectorTests
{
    private static Mat Menu() => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "esc-menu", "user-2026-10-10.png"));

    [Theory]
    [InlineData(.6)] [InlineData(.75)] [InlineData(1)]
    [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void ActualUserMenuMatchesAcrossUiScales(double scale)
    {
        using var source = Menu(); Assert.False(source.Empty());
        using var resized = new Mat();
        Cv2.Resize(source, resized, new OpenCvSharp.Size(), scale, scale, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        Assert.True(EscMenuDetector.Shows(resized));
        Assert.True(new EscMenuDetector().Detect(resized));
    }

    [Theory]
    [InlineData("map-tracking", "live-2026-10-03-screen.png")]
    [InlineData("map-tracking", "live-2026-10-03-lost-screen.png")]
    [InlineData("map-tracking", "live-2026-10-03-worldmap-drift-screen.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-small.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-medium.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-large.png")]
    public void NormalPlayWorldMapAndPetWindowAreNotEscMenu(string folder, string file)
    {
        using var frame = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", folder, file));
        Assert.False(frame.Empty());
        Assert.False(EscMenuDetector.Shows(frame));
    }

    [Fact]
    public void MenuMatchesWhenMovedToTheRightOfAFullLandscapeGameFrame()
    {
        using var source = Menu();
        using var full = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(70));
        using var destination = new Mat(full, new Rect(965, 20, source.Width, source.Height));
        source.CopyTo(destination);
        Assert.True(EscMenuDetector.Shows(full));
    }

    [Fact]
    public void PlayerNameLevelLanguageAndHotkeyLabelsAreNotNeeded()
    {
        using var source = Menu();
        Cv2.Rectangle(source, new Rect(250, 100, 590, 128), Scalar.All(0), -1); // character card
        foreach (var x in new[] { 285, 420, 555, 690 })
            Cv2.Rectangle(source, new Rect(x, 239, 110, 30), Scalar.All(0), -1); // localized action labels
        Cv2.Rectangle(source, new Rect(250, 229, 590, 12), Scalar.All(0), -1); // customized hotkeys
        Assert.True(EscMenuDetector.Shows(source));
    }

    [Fact]
    public void IndividualLookalikeIconsCannotTriggerMenuSuppression()
    {
        using var source = Menu();
        Cv2.Rectangle(source, new Rect(524, 240, 36, 32), Scalar.All(0), -1);
        Cv2.Rectangle(source, new Rect(658, 240, 36, 32), Scalar.All(0), -1);
        Assert.False(EscMenuDetector.Shows(source)); // Stats + Legion alone are insufficient
    }

    [Fact]
    public void ClosingAndReopeningIsDetectedWithoutEscapeKeyEvents()
    {
        var detector = new EscMenuDetector();
        using var menu = Menu();
        using var closed = new Mat(menu.Size(), menu.Type(), Scalar.All(80));
        Assert.True(detector.Detect(menu));
        Assert.False(detector.Detect(closed));
        Assert.True(detector.Detect(menu));
    }
}

[Collection("Settings file")]
public sealed class EscMenuOverlayTests
{
    [Fact]
    public void ARealEscFrameStopsMapPlacementAndPublishesMenuStateWithoutCapture()
    {
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(new SettingsService(), new ProgressService(), capture);
        using var frame = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "esc-menu", "user-2026-10-10.png"));
        tracking.ProcessMiniMapFrame(new Rectangle(0, 0, 100, 100), new Rectangle(0, 0, frame.Width, frame.Height), frame, CancellationToken.None);
        Assert.True(tracking.EscMenuOpen);
        Assert.False(tracking.Found);
        Assert.Null(tracking.Position);
        Assert.Null(tracking.Placement);
        tracking.Shutdown();
        Assert.False(tracking.EscMenuOpen);
    }
}
