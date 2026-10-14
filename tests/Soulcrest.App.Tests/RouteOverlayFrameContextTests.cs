using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class RouteOverlayFrameContextTests
{
    private static MapPlacement Placement() => new("map", new MapFix(1, 0, 0, 0, 1, 0, 50, 60, 0, false),
        1, new Rectangle(50, 60, 300, 200), new Point2d(150, 100));

    [Fact]
    public void OrdinaryFlowMovementKeepsTheComposedFrameEligible()
    {
        var placement = Placement();
        var frame = new RouteOverlayForm.Frame(0, placement.Region.Location, placement.Region.Size, Placement: placement);
        var newerFlow = placement with { Fix = placement.Fix with { Tx = 14, Ty = -7 }, CapturedAt = 123 };
        Assert.True(RouteOverlayForm.MatchesCurrentPlacement(frame, newerFlow));
    }

    [Fact]
    public void LostContextMapSurfaceAndWindowChangesRejectAnAlreadyClaimedOldFrame()
    {
        var placement = Placement();
        var frame = new RouteOverlayForm.Frame(0, placement.Region.Location, placement.Region.Size, Placement: placement);
        Assert.False(RouteOverlayForm.MatchesCurrentPlacement(frame, null));
        Assert.False(RouteOverlayForm.MatchesCurrentPlacement(frame, placement with { MapId = "other" }));
        Assert.False(RouteOverlayForm.MatchesCurrentPlacement(frame, placement with { WorldMap = true }));
        Assert.False(RouteOverlayForm.MatchesCurrentPlacement(frame, placement with { Region = new Rectangle(60, 60, 300, 200) }));
        Assert.False(RouteOverlayForm.MatchesCurrentPlacement(frame, placement with { Region = new Rectangle(50, 60, 400, 200) }));
    }
}
