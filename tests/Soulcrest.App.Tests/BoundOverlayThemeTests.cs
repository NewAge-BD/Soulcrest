using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure theme regression coverage; no live window, capture or game inputs.</summary>
public sealed class BoundOverlayThemeTests
{
    public static IEnumerable<object[]> ThemeIds => OverlayThemeDefinition.All.Select(theme => new object[] { theme.Id });
    public static IEnumerable<object[]> ThemeTransitions => OverlayThemeDefinition.All.SelectMany(shown =>
        OverlayThemeDefinition.All.Where(selected => selected.Id != shown.Id).Select(selected => new object[] { shown.Id, selected.Id }));
    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void MapSpritesSwitchThemesInBothDirectionsWithoutReusingOldColors(string id)
    {
        var theme = OverlayThemeDefinition.Resolve(id);
        using var icon = SolidIcon();
        RouteOverlayForm.PetSymbol[] pets = [new(80, 70, icon, "Level 1 · 6/25", false)];
        RouteOverlayForm.ResourceSymbol[] resources = [new(250, 140, icon)];
        using var first = RouteOverlayForm.DrawThemed(theme, Placement(), [], pets, resources);
        using var legacy = RouteOverlayForm.DrawThemed(OverlayThemeDefinition.Legacy, Placement(), [], pets, resources);
        using var again = RouteOverlayForm.DrawThemed(theme, Placement(), [], pets, resources);
        Assert.Equal(Pixels(first), Pixels(again));
        if (!theme.IsLegacy) Assert.NotEqual(Pixels(first), Pixels(legacy));
        foreach (var image in new[] { first, legacy, again })
        {
            Assert.Equal(Color.Magenta.ToArgb(), image.GetPixel(80, 70).ToArgb());
            Assert.Equal(Color.Magenta.ToArgb(), image.GetPixel(250, 140).ToArgb());
            Assert.Equal(0, image.GetPixel(1, 1).A);
        }
        Export(first, "map-" + id);
        Export(legacy, "map-legacy");
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void MapRouteGeometryAndSemanticColorsStayIdenticalAcrossThemes(string id)
    {
        (MapTarget Target, int Index)[] targets =
        [
            (new("one", "map", 230, 160, "", "Waypoint", null, Color: "#ef4444"), 0),
            (new("two", "map", 230, 50, "", "Waypoint", null, After: "one", Color: "#facc15"), 1)
        ];
        using var aion = RouteOverlayForm.DrawThemed(OverlayThemeDefinition.Resolve(id), Placement(), targets);
        using var legacy = RouteOverlayForm.DrawThemed(OverlayThemeDefinition.Legacy, Placement(), targets);
        Assert.Equal(Pixels(aion), Pixels(legacy));
        for (var offset = 0; offset < RouteOverlayForm.RingRadius - 5; offset++)
        {
            Assert.Equal(0, aion.GetPixel(230, 50 + offset).A);
            Assert.Equal(0, aion.GetPixel(230, 160 - offset).A);
        }
    }

    [Theory]
    [MemberData(nameof(ThemeTransitions))]
    public void AnAlreadyComposedFrameIsRejectedAfterTheThemeChanges(string shown, string selected)
    {
        var placement = Placement();
        var frame = new RouteOverlayForm.Frame(0, Point.Empty, placement.Region.Size, Placement: placement, ThemeId: shown);
        Assert.True(RouteOverlayForm.MatchesCurrentTheme(frame, shown));
        Assert.False(RouteOverlayForm.MatchesCurrentTheme(frame, selected));
        Assert.True(RouteOverlayForm.MatchesCurrentPlacement(frame, placement));
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void FixedMerchantStyleLeavesTheNativePriceCapsuleUntouchedAndTheMiddleTransparent(string globalTheme)
    {
        var badge = Badge();
        using var image = Merchant([badge]);
        for (var y = badge.PriceBounds.Top; y < badge.PriceBounds.Bottom; y++)
        for (var x = badge.PriceBounds.Left; x < badge.PriceBounds.Right; x++)
        {
            var radius = badge.PriceBounds.Height / 2d;
            var capsuleX = Math.Clamp(x + .5, badge.PriceBounds.Left + radius, badge.PriceBounds.Right - radius);
            var dx = x + .5 - capsuleX;
            var dy = y + .5 - badge.PriceBounds.Top - radius;
            if (dx * dx + dy * dy <= radius * radius) Assert.Equal(0, image.GetPixel(x, y).A);
        }
        for (var y = badge.CategoryBounds.Bottom; y < badge.Bounds.Top; y++)
        for (var x = badge.Bounds.Left; x < badge.Bounds.Right; x++)
            Assert.Equal(0, image.GetPixel(x, y).A);
        Assert.Contains(Pixels(image), value => value != 0);
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        using var preview = renderer.Render("merchant", new AppSettings { OverlayTheme = globalTheme });
        using var reference = renderer.Render("merchant", new AppSettings { OverlayTheme = "soulcrest-legacy" });
        Assert.Equal(Pixels(reference), Pixels(preview));
        Export(image, "merchant-fixed-" + globalTheme);
    }

    [Fact]
    public void MerchantLivePainterAndPreviewRemainUnchangedAcrossGlobalThemeSwitches()
    {
        var badge = Badge();
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        var settings = new AppSettings { OverlayTheme = OverlayThemeDefinition.DefaultId };
        using var before = Merchant([badge]);
        using var previewBefore = renderer.Render("merchant", settings);
        foreach (var theme in OverlayThemeDefinition.All)
        {
            settings.OverlayTheme = theme.Id;
            using var after = Merchant([badge]);
            using var previewAfter = renderer.Render("merchant", settings);
            Assert.Equal(Pixels(before), Pixels(after));
            Assert.Equal(Pixels(previewBefore), Pixels(previewAfter));
        }
        settings.OverlayTheme = OverlayThemeDefinition.DefaultId;
        using var restored = renderer.Render("merchant", settings);
        Assert.Equal(Pixels(previewBefore), Pixels(restored));
        // The production painter intentionally has no theme parameter or global settings input.
        var painter = typeof(MerchantOverlayForm).GetMethod("DrawBadges", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(3, painter.GetParameters().Length);
        Assert.DoesNotContain(painter.GetParameters(), parameter => parameter.ParameterType == typeof(OverlayThemeDefinition));
    }

    [Fact]
    public void FixedMerchantStyleKeepsItsBrightGreenMaxAccent()
    {
        var badge = Badge() with { Counter = "MAX", IsMax = true };
        using var image = Merchant([badge]);
        Assert.Contains(Enumerable.Range(badge.PriceBounds.Bottom, badge.Bounds.Bottom - badge.PriceBounds.Bottom)
            .SelectMany(y => Enumerable.Range(badge.Bounds.Left, badge.Bounds.Width).Select(x => image.GetPixel(x, y))),
            color => color.A > 200 && color.G > 210 && color.G > color.R + 15);
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void TimerThemeKeepsTheTransparentGapAndDpiIndependentBundledCountdown(string id)
    {
        var theme = OverlayThemeDefinition.Resolve(id);
        var now = DateTimeOffset.UnixEpoch;
        AlertDisplayRow[] rows = [new("event:catalog:shugo", "Shugo Festival", "", "#edc96f", now.AddMinutes(20), false),
            new("event:catalog:rift", "Spacetime Rift", "", "#edc96f", now.AddMinutes(35), false)];
        var layout = TimerStripRenderer.Layout(rows);
        byte[]? previous = null;
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var image = new Bitmap((int)Math.Ceiling(layout.Size.Width * 1.25), (int)Math.Ceiling(layout.Size.Height * 1.25), PixelFormat.Format32bppPArgb);
            image.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(1.25f, 1.25f);
                TimerStripRenderer.Paint(graphics, layout, now, theme: theme);
            }
            Assert.Equal(0, image.GetPixel(0, 12).A);
            Assert.Equal(0, image.GetPixel((int)((layout.Cells[0].Bounds.Right + 5) * 1.25), 12).A);
            var current = Pixels(image);
            if (previous is not null) Assert.Equal(previous, current);
            previous = current;
            if (dpi == 96) Export(image, "timers-" + id);
        }
        using var font = theme.Countdown(18);
        Assert.Equal(TimerStripTypeface.Family.Name, font.FontFamily.Name);
        Assert.Equal(TimerStripTypeface.Style, font.Style);
    }

    private static MapPlacement Placement() => new("map", new MapFix(1, 0, 0, 0, 1, 0, 0, 0, 0, false),
        1, new Rectangle(0, 0, 400, 240), new OpenCvSharp.Point2d(35, 200));
    private static Bitmap SolidIcon()
    {
        var image = new Bitmap(26, 26);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Magenta);
        return image;
    }
    private static MerchantOverlayForm.Badge Badge() => new(new Rectangle(20, 90, 212, 80), "24/25", true, false,
        "Altgard", new Rectangle(72, 90, 108, 24), "Fera", "Altgard (398)", new Rectangle(78, 10, 96, 20));
    private static Bitmap Merchant(IReadOnlyList<MerchantOverlayForm.Badge> badges)
    {
        var image = new Bitmap(270, 190, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        MerchantOverlayForm.DrawBadges(graphics, Point.Empty, badges);
        return image;
    }
    private static byte[] Pixels(Bitmap image)
    {
        var result = new byte[image.Width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            BitConverter.GetBytes(image.GetPixel(x, y).ToArgb()).CopyTo(result, (y * image.Width + x) * 4);
        return result;
    }
    private static void Export(Bitmap image, string name)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_THEME_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
    }
}
