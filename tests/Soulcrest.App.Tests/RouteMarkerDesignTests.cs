using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// User request 2026-10-10: target markers covered the resource symbols. The ring lies around the symbol, lines
/// start and end at the rims, and marked resources show their icon beside the ring instead of their name.
/// </summary>
public sealed class RouteMarkerDesignTests
{
    private static MapPlacement Placement()
    {
        var fix = new MapFix(1, 0, 0, 0, 1, 0, 50, 60, 0.3, false);
        return new MapPlacement("altgard", fix, 1, new Rectangle(0, 0, 300, 240), new OpenCvSharp.Point2d(220, 170));
    }

    private static (MapTarget, int)[] Chain() =>
    [
        (new MapTarget("a", "altgard", 80, 200, "Diamond", "Resources · Gem", "icons/diamond.png"), 0),
        (new MapTarget("b", "altgard", 80, 40, "Diamond", "Resources · Gem", "icons/diamond.png", After: "a"), 0),
        (new MapTarget("c", "altgard", 20, 110, "Diamond", "Resources · Gem", "icons/diamond.png", After: "b"), 0),
    ];

    private static Bitmap Icon()
    {
        var icon = new Bitmap(20, 20);
        using var g = Graphics.FromImage(icon);
        g.Clear(Color.Magenta);
        return icon;
    }

    [Fact]
    public void ResourceTargetsShowTheirIconAndLinesLeaveTheRingsAtTheirRim()
    {
        using var icon = Icon();
        var icons = Chain().ToDictionary(t => t.Item1.Id, _ => (Bitmap?)icon);
        using var bitmap = RouteOverlayForm.Draw(Placement(), Chain(), targetIcons: icons);
        var save = Environment.GetEnvironmentVariable("SOULCREST_MARKER_PNG");
        if (!string.IsNullOrEmpty(save))
            bitmap.Save(save, ImageFormat.Png);

        // Inside a ring nothing is drawn: the resource symbol beneath stays visible (line starts at the rim).
        for (var r = 0; r < RouteOverlayForm.RingRadius - 5; r++)
        {
            Assert.Equal(0, bitmap.GetPixel(80, 40 + r).A);  // b, towards its successor and predecessor
            Assert.Equal(0, bitmap.GetPixel(80, 200 - r).A); // a, where the chain leaves
        }
        // The icon sits beside the ring, away from the arriving line: above b would leave the map, so beside it.
        var iconPixel = bitmap.GetPixel((int)(80 + RouteOverlayForm.RingRadius + 5 + 9), 40);
        // a: the line from the player arrives from the right, the icon is on the left.
        var leftOfA = bitmap.GetPixel((int)(80 - RouteOverlayForm.RingRadius - 5 - 9), 200);
        Assert.True(leftOfA.R > 200 && leftOfA.B > 200, $"Icon von a fehlt links: {leftOfA}");
        Assert.True(iconPixel.R > 200 && iconPixel.B > 200 && iconPixel.G < 60, $"Icon fehlt: {iconPixel}");
    }

    [Theory]
    [InlineData("Diamond · 99 m", "99 m")]
    [InlineData("Diamond", "")]
    public void TheIconLabelKeepsOnlyTheDistance(string name, string label) => Assert.Equal(label, RouteOverlayForm.DistanceOf(name));

    [Fact]
    public void OnlyResourcesAndCubesSwapTheirNameForTheIcon()
    {
        Assert.True(RouteOverlayForm.ShowsIconOnly(new MapTarget("r", "altgard", 1, 1, "Diamond", "Resources · Gem", "i.png")));
        Assert.True(RouteOverlayForm.ShowsIconOnly(new MapTarget("c", "altgard", 1, 1, "Hidden Cube", "Collectibles · Hidden Cube", "i.png")));
        Assert.False(RouteOverlayForm.ShowsIconOnly(new MapTarget("k", "altgard", 1, 1, "Kibelisk", "Locations · Kibelisk", "i.png")));
        Assert.False(RouteOverlayForm.ShowsIconOnly(new MapTarget("p", "altgard", 1, 1, "Kuru", "Pets · Fera", "i.png", PetId: "kuru")));
    }

    [Fact]
    public void NoSymbolInsideTheRingOfAResourceTarget()
    {
        // User report 2026-10-10: the symbol from the map legend sat inside the ring and covered the game's own.
        var target = new MapTarget("d", "altgard", 80, 40, "Diamond", "Resources · Gem", "icons/diamond.png");
        var resources = new List<RouteOverlayForm.ResourceSymbol>
        {
            new(80, 40, null), // the marked diamond itself
            new(120, 40, null), // a neighbour stays
        };
        RouteOverlayForm.RemoveSymbolInRing(resources, target);
        Assert.Equal(120, Assert.Single(resources).X);
    }
}
