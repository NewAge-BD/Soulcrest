using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class FadedMinimapMarkerTests
{
    public static IEnumerable<object[]> ThemeIds => OverlayThemeDefinition.All.Select(theme => new object[] { theme.Id });
    public static IEnumerable<object[]> ThemeAndCompletion => OverlayThemeDefinition.All.SelectMany(theme =>
        new[] { false, true }.Select(completed => new object[] { theme.Id, completed }));
    private static MapPlacement Placement(bool world) => new("map", new MapFix(1, 0, 0, 0, 1, 0, 0, 0, 0, false),
        1, new Rectangle(0, 0, 400, 180), new(20, 90), WorldMap: world, PlayerWorld: new(20, 90));

    [Theory]
    [MemberData(nameof(ThemeAndCompletion))]
    public void FadedTargetIconsHaveNoFilledBackingOnTheMinimap(string id, bool completed)
    {
        using var icon = Icon();
        var theme = OverlayThemeDefinition.Resolve(id);
        var target = new MapTarget(completed ? "history" : "boss-next", "map", 100, 90, "Station 1 · 80 m",
            "NPCs", null, Color: "#facc15", Completed: completed);
        var icons = new Dictionary<string, Bitmap?> { [target.Id] = icon };
        using var world = RouteOverlayForm.DrawThemed(theme, Placement(true), [(target, 0)], targetIcons: icons);
        using var mini = RouteOverlayForm.DrawThemed(theme, Placement(false), [(target, 0)], targetIcons: icons);
        Assert.True(world.GetPixel(138, 90).A > 0);
        Assert.Equal(0, mini.GetPixel(138, 90).A);
        Assert.InRange(mini.GetPixel(129, 90).A, completed ? 70 : 104, completed ? 72 : 106);
        if (!theme.IsLegacy)
        {
            using var worldText = RouteOverlayForm.DrawThemed(theme, Placement(true), [(target, 0)]);
            using var miniText = RouteOverlayForm.DrawThemed(theme, Placement(false), [(target, 0)]);
            Assert.True(HasRemovedBacking(worldText, miniText, new(125, 75, 180, 32)));
        }
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void FadedResourcesSkipTheDiscWithAndWithoutAnIconOrTint(string id)
    {
        using var icon = Icon();
        var theme = OverlayThemeDefinition.Resolve(id);
        foreach (var image in new Bitmap?[] { icon, null })
        foreach (var tint in new string?[] { "#facc15", null })
        {
            var symbol = new RouteOverlayForm.ResourceSymbol(100, 90, image, tint, .28f);
            using var world = RouteOverlayForm.DrawThemed(theme, Placement(true), [], resources: [symbol]);
            using var mini = RouteOverlayForm.DrawThemed(theme, Placement(false), [], resources: [symbol]);
            Assert.True(world.GetPixel(109, 90).A > 0);
            Assert.Equal(0, mini.GetPixel(109, 90).A);
            Assert.True(mini.GetPixel(100, 90).A > 0);
            var active = symbol with { Opacity = 1 };
            using var activeWorld = RouteOverlayForm.DrawThemed(theme, Placement(true), [], resources: [active]);
            using var activeMini = RouteOverlayForm.DrawThemed(theme, Placement(false), [], resources: [active]);
            SamePixels(activeWorld, activeMini);
        }
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void PetSpriteCacheKeepsFadedMinimapLabelsSeparateFromWorldMapPlates(string id)
    {
        using var icon = Icon();
        var theme = OverlayThemeDefinition.Resolve(id);
        var pet = new RouteOverlayForm.PetSymbol(100, 90, icon, "MAX · 105", true);
        using var world = RouteOverlayForm.DrawThemed(theme, Placement(true), [], [pet]);
        using var mini = RouteOverlayForm.DrawThemed(theme, Placement(false), [], [pet]);
        using var worldAgain = RouteOverlayForm.DrawThemed(theme, Placement(true), [], [pet]);
        using var miniAgain = RouteOverlayForm.DrawThemed(theme, Placement(false), [], [pet]);
        SamePixels(world, worldAgain);
        SamePixels(mini, miniAgain);
        Assert.True(HasRemovedBacking(world, mini, new(116, 78, 90, 24)));
        Assert.Equal(world.GetPixel(100, 90), mini.GetPixel(100, 90));
        var active = pet with { Done = false };
        using var activeWorld = RouteOverlayForm.DrawThemed(theme, Placement(true), [], [active]);
        using var activeMini = RouteOverlayForm.DrawThemed(theme, Placement(false), [], [active]);
        SamePixels(activeWorld, activeMini);
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void ProductionMinimapExampleRendersHistoryPreviewAndActiveTargets(string id)
    {
        var placement = Placement(false) with { Region = new(0, 0, 640, 340), Anchor = new(80, 270) };
        (MapTarget Target, int Index)[] targets =
        [
            (new("history", "map", 110, 90, "Station 1", "Waypoint", null, Color: "#facc15", Completed: true), 0),
            (new("boss-next", "map", 330, 110, "Boss · 250 m", "Waypoint", null, Color: "#ef4444"), 1),
            (new("active", "map", 320, 250, "Station 2 · 180 m", "Waypoint", null, Color: "#facc15"), 2)
        ];
        using var layer = RouteOverlayForm.DrawThemed(OverlayThemeDefinition.Resolve(id), placement, targets,
            resources: [new(110, 90, null, "#facc15", .28f)]);
        if (Environment.GetEnvironmentVariable("SOULCREST_FADED_MARKER_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        using var scene = new Bitmap(640, 340, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(scene);
        graphics.Clear(Color.FromArgb(48, 59, 59));
        using var grid = new Pen(Color.FromArgb(55, 71, 70));
        for (var x = 0; x < scene.Width; x += 32) graphics.DrawLine(grid, x, 0, x, scene.Height);
        for (var y = 0; y < scene.Height; y += 32) graphics.DrawLine(grid, 0, y, scene.Width, y);
        graphics.DrawImageUnscaled(layer, 0, 0);
        scene.Save(Path.Combine(output, "minimap-faded-marker-" + id + ".png"), ImageFormat.Png);
        if (id == OverlayThemeDefinition.DefaultId) scene.Save(Path.Combine(output, "minimap-faded-marker.png"), ImageFormat.Png);
    }

    private static Bitmap Icon()
    {
        var icon = new Bitmap(20, 20, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(icon);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 8, 8, 4, 4);
        return icon;
    }
    private static bool HasRemovedBacking(Bitmap before, Bitmap after, Rectangle area) =>
        Enumerable.Range(area.Top, area.Height).Any(y => Enumerable.Range(area.Left, area.Width).Any(x =>
            before.GetPixel(x, y) is { A: >= 190, R: < 100, G: < 100 } && after.GetPixel(x, y).A == 0));
    private static void SamePixels(Bitmap first, Bitmap second)
    {
        Assert.Equal(first.Size, second.Size);
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++) Assert.Equal(first.GetPixel(x, y), second.GetPixel(x, y));
    }
}
