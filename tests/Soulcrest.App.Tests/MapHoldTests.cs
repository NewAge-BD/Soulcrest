using System.Drawing;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Colleague's diagnosis 2026-10-07: a small, strongly zoomed minimap over coast and sand (514×292) lost the
/// position again and again until the tracking went to rest.
/// </summary>
public sealed class MapHoldTests
{
    [Fact]
    public void SmallMapAreasAreSearchedAtFullSize()
    {
        Assert.Equal(1.0, MapTrackingService.FineFrameScaleFor(new Size(514, 292)));
        Assert.Equal(MapTrackingService.FineFrameScale, MapTrackingService.FineFrameScaleFor(new Size(518, 469))); // the user's area
    }

    [Fact]
    public void AnUnbrokenFlowHoldsTheLastPositionForAWhile()
    {
        Assert.True(MapTrackingService.HoldsByFlow(flowBroken: false, TimeSpan.FromSeconds(10)));
        Assert.False(MapTrackingService.HoldsByFlow(flowBroken: true, TimeSpan.FromSeconds(2))); // loading screen, window, world map
        Assert.False(MapTrackingService.HoldsByFlow(flowBroken: false, MapTrackingService.FlowHoldLimit)); // then detection must confirm
    }
}
