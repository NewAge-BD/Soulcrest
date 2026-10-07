using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Tracking rests in instances without a world map (user request 2026-10-06).</summary>
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
}
