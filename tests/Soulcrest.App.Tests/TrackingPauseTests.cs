using Soulcrest.App.Services;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Tracking rests in instances without a world map (user request 2026-10-06).</summary>
[Collection("Settings file")]
public sealed class TrackingPauseTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void AfterALoadingScreenEightSecondsWithoutMapMeanInstance()
    {
        Assert.False(MapTrackingService.ShouldPause(true, false, false, S(7), S(9)));
        Assert.True(MapTrackingService.ShouldPause(true, false, false, S(8), S(10)));
    }

    [Fact]
    public void WithoutALoadingScreenItWaitsLonger()
    {
        Assert.False(MapTrackingService.ShouldPause(true, false, false, S(20), null));        // cutscene, pet window
        Assert.True(MapTrackingService.ShouldPause(true, false, false, S(30), null));
        Assert.False(MapTrackingService.ShouldPause(true, false, false, S(20), S(600)));      // that loading screen was long before
    }

    [Fact]
    public void ItLooksOftenFirstThenRarely()
    {
        Assert.Equal(2000, MapTrackingService.PausedProbeMs(S(3)));
        Assert.Equal(5000, MapTrackingService.PausedProbeMs(S(25)));
    }

    [Fact]
    public void NeverWhileFoundOnTheWorldMapOrSwitchedOff()
    {
        Assert.False(MapTrackingService.ShouldPause(true, true, false, S(60), S(61)));
        Assert.False(MapTrackingService.ShouldPause(true, false, true, S(60), S(61)));
        Assert.False(MapTrackingService.ShouldPause(false, false, false, S(60), S(61)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstanceVisibilityIsIndependentOfPowerSavingPause(bool pauseTracking)
    {
        var settings = new SettingsService();
        settings.Current.PauseTrackingInInstances = pauseTracking;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);

        tracking.UpdateInstanceContext(false, false, S(7), S(9));
        Assert.False(tracking.InInstance);
        tracking.UpdateInstanceContext(false, false, S(8), S(10));
        Assert.True(tracking.InInstance);
        Assert.False(tracking.Paused); // context detection itself never pauses tracking

        tracking.UpdateInstanceContext(true, false, S(0), S(11));
        Assert.False(tracking.InInstance);
    }

    [Fact]
    public void LoadingWakeKeepsInstanceHiddenUntilAMapIsConfirmed()
    {
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(new SettingsService(), new ProgressService(), capture);
        tracking.UpdateInstanceContext(false, false, S(30), null);

        tracking.NoteLoadingScreen();
        tracking.UpdateInstanceContext(false, false, S(0), S(0));
        Assert.True(tracking.InInstance);

        tracking.UpdateInstanceContext(false, true, S(0), S(1));
        Assert.False(tracking.InInstance);
        tracking.UpdateInstanceContext(false, false, S(0), S(1));
        Assert.False(tracking.InInstance); // closing the world map starts a fresh minimap search
    }

    [Fact]
    public void ShutdownClearsInstanceContextAndNotifiesOverlays()
    {
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(new SettingsService(), new ProgressService(), capture);
        var contexts = new List<bool>();
        tracking.Changed += () => contexts.Add(tracking.InInstance);

        tracking.UpdateInstanceContext(false, false, S(30), null);
        tracking.UpdateInstanceContext(false, false, S(40), null);
        Assert.Equal(new[] { true }, contexts); // prolonged loss only publishes a change once
        tracking.Shutdown();

        Assert.False(tracking.InInstance);
        Assert.Equal(new[] { true, false }, contexts);
    }
}
