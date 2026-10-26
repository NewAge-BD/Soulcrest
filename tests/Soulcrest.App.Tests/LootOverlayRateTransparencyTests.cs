using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootOverlayRateTransparencyTests
{
    [Theory]
    [InlineData("aion2-stylized")]
    [InlineData("aion2-neutral")]
    [InlineData("obsidian")]
    [InlineData("aurora")]
    [InlineData("runic")]
    [InlineData("soulcrest-legacy")]
    public void OnlyPanelFillFadesWhileTextIconsAndProgressRetainTheirOpacity(string theme)
    {
        var example = LootKinaOverlayRendererTests.Example() with
        {
            SessionKinaPerHour = 98_765,
            SessionElapsed = TimeSpan.FromMinutes(12),
            KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031)
        };
        using var regular = LootOverlayRenderer.RenderBitmap(example, theme, backgroundOpacity: 1);
        using var half = LootOverlayRenderer.RenderBitmap(example, theme, backgroundOpacity: .5);
        using var clear = LootOverlayRenderer.RenderBitmap(example, theme, backgroundOpacity: 0);
        var blank = new Point(210, 210);
        Assert.InRange(regular.GetPixel(blank.X, blank.Y).A, 150, 255);
        Assert.InRange(half.GetPixel(blank.X, blank.Y).A, 70, 130);
        Assert.Equal(0, clear.GetPixel(blank.X, blank.Y).A);
        // Fully covered glyph pixels remain the same original green instead of fading with the panel.
        var glyphPixels = Positions(LootOverlayRenderer.SessionKinaNumberBounds).Where(point =>
            regular.GetPixel(point.X, point.Y).ToArgb() == LootOverlayRenderer.Green.ToArgb()).ToArray();
        Assert.NotEmpty(glyphPixels);
        Assert.All(glyphPixels, point => Assert.Equal(regular.GetPixel(point.X, point.Y), clear.GetPixel(point.X, point.Y)));
        var icon = LootOverlayRenderer.SessionKinaIconBounds;
        var iconPixels = Positions(icon).Where(point => clear.GetPixel(point.X, point.Y).A == 255).ToArray();
        Assert.NotEmpty(iconPixels);
        Assert.All(iconPixels, point => Assert.Equal(regular.GetPixel(point.X, point.Y), clear.GetPixel(point.X, point.Y)));
        var bar = LootOverlayRenderer.DailyBar;
        Assert.Equal(regular.GetPixel(bar.X + 20, bar.Y + 4), clear.GetPixel(bar.X + 20, bar.Y + 4));
    }

    [Fact]
    public void RateUsesItsOwnRightHandSpaceAndUnknownNeverBecomesAnInventedZero()
    {
        Assert.True(LootOverlayRenderer.SessionKinaNumberBounds.Right < LootOverlayRenderer.SessionRateNumberBounds.Left);
        Assert.Equal("—", LootOverlayRenderer.RateLabel(null));
        Assert.Equal("—", LootOverlayRenderer.RateLabel(double.NaN));
        Assert.Equal("—", LootOverlayRenderer.RateLabel(double.PositiveInfinity));
        Assert.Equal("—", LootOverlayRenderer.RateLabel(-10));
        Assert.Equal("0", LootOverlayRenderer.RateLabel(0));
        var unknown = LootKinaOverlayRendererTests.Example();
        var known = unknown with { SessionKinaPerHour = 1_234_567, SessionElapsed = TimeSpan.FromMinutes(3) };
        using var missing = LootOverlayRenderer.RenderBitmap(unknown, "aion2-stylized");
        using var filled = LootOverlayRenderer.RenderBitmap(known, "aion2-stylized");
        var bounds = Rectangle.Ceiling(LootOverlayRenderer.SessionRateNumberBounds);
        Assert.Contains(Positions(bounds), point => missing.GetPixel(point.X, point.Y) != filled.GetPixel(point.X, point.Y));
        Assert.Equal(LootOverlayRenderer.Measure(unknown), LootOverlayRenderer.Measure(known));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(.4, .4)]
    public void InvalidOpacityIsNormalizedBeforePainting(double input, double expected) =>
        Assert.Equal(expected, LootOverlayRenderer.NormalizedBackgroundOpacity(input));

    [Fact]
    public void AdditionalOptionsKeepExistingVisibilityAndFullBackgroundByDefault()
    {
        var options = new LootOverlayOptions();
        Assert.Equal(1, options.BackgroundOpacity);
        Assert.False(options.ShowOnSessionStart);
        Assert.False(options.Enabled);
        var configured = options with { BackgroundOpacity = .3, ShowOnSessionStart = true };
        Assert.False(configured.Enabled);
        Assert.Equal(.3, configured.BackgroundOpacity);
    }

    private static IEnumerable<Point> Positions(RectangleF bounds) => Positions(Rectangle.Ceiling(bounds));
    private static IEnumerable<Point> Positions(Rectangle bounds) => Enumerable.Range(bounds.Top, bounds.Height)
        .SelectMany(y => Enumerable.Range(bounds.Left, bounds.Width).Select(x => new Point(x, y)));
}
