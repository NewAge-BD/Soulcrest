using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorCanvasFrameTests
{
    public static IEnumerable<object[]> Themes => OverlayThemeDefinition.All.Select(theme => new object[] { theme.Id });
    public static IEnumerable<object[]> ThemeOpacities => OverlayThemeDefinition.All.SelectMany(theme =>
        new[] { 0d, .375, 1d }.Select(opacity => new object[] { theme.Id, opacity }));

    [Theory]
    [MemberData(nameof(ThemeOpacities))]
    public void CanvasPngMatchesTheOriginalNativeDefaultPanelPixels(string id, double opacity)
    {
        var theme = OverlayThemeDefinition.Resolve(id);
        using var expected = Native(theme, opacity);
        using var actual = Decode(OverlayEditorCanvasFrames.GetDataUri(id, 160, 100, opacity));
        AssertPixels(expected, actual);
        using var explicitBorder = Native(theme, opacity, true);
        AssertPixels(expected, explicitBorder);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BorderOffAndZeroSurfaceOpacityIsFullyTransparentForEveryOriginalTheme(string id)
    {
        using var actual = Decode(OverlayEditorCanvasFrames.GetDataUri(id, 160, 100, 0, false));
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++) Assert.Equal(0, actual.GetPixel(x, y).A);
        using var borderOnly = Decode(OverlayEditorCanvasFrames.GetDataUri(id, 160, 100, 0, true));
        Assert.Contains(Enumerable.Range(0, borderOnly.Height), y =>
            Enumerable.Range(0, borderOnly.Width).Any(x => borderOnly.GetPixel(x, y).A > 0));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BorderToggleKeepsTheOriginalInteriorAndSurfaceOpacity(string id)
    {
        var theme = OverlayThemeDefinition.Resolve(id);
        using var framed = Decode(OverlayEditorCanvasFrames.GetDataUri(id, 160, 100, .5, true));
        using var unframed = Decode(OverlayEditorCanvasFrames.GetDataUri(id, 160, 100, .5, false));
        Assert.Equal(framed.GetPixel(80, 50), unframed.GetPixel(80, 50));
        Assert.Equal((int)Math.Round(theme.Surface.A * .5), unframed.GetPixel(80, 50).A);
    }

    [Fact]
    public void CatalogUsesOnlyTheOriginalSixIdsAndPresentationTokens()
    {
        Assert.Equal(OverlayThemeDefinition.All.Select(theme => theme.Id), OverlayEditorThemes.All.Select(theme => theme.Id));
        foreach (var native in OverlayThemeDefinition.All)
        {
            var theme = OverlayEditorThemes.Resolve(native.Id);
            Assert.Equal(native.Label, theme.Label);
            Assert.Contains($"--overlay-surface-rgb:{native.Surface.R},{native.Surface.G},{native.Surface.B};", theme.CssVariables);
            Assert.Contains($"--overlay-heading-font:\"{native.HeadingFamily}\";", theme.CssVariables);
            Assert.Contains($"--overlay-heading-weight:{(native.IsLegacy ? 400 : 700)};", theme.CssVariables);
            Assert.Contains(native.IsLegacy ? "--overlay-body-font:\"Segoe UI\",sans-serif;"
                : "--overlay-body-font:\"Segoe UI Semibold\",\"Segoe UI\",sans-serif;", theme.CssVariables);
            Assert.Equal(theme.CssVariables, OverlayEditorThemes.CssVariables(native.Id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("grindcrest")]
    [InlineData("aion2")]
    [InlineData("unknown")]
    public void FormerKitAndUnknownThemesResolveToTheOriginalNativeDefault(string? id)
    {
        Assert.Equal(OverlayThemeDefinition.DefaultId, OverlayEditorThemes.ResolveThemeId(id));
        Assert.Same(OverlayEditorThemes.Resolve(OverlayThemeDefinition.DefaultId), OverlayEditorThemes.Resolve(id));
    }

    [Theory]
    [InlineData(0, 0, 360, 260)]
    [InlineData(-1, -1, 360, 260)]
    [InlineData(double.NaN, double.PositiveInfinity, 360, 260)]
    [InlineData(.2, .2, 1, 1)]
    [InlineData(160.01, 100.5, 161, 101)]
    [InlineData(double.MaxValue, double.MaxValue, 1600, 1200)]
    public void LogicalDimensionsAreCeiledFiniteAndBounded(double width, double height, int expectedWidth, int expectedHeight) =>
        Assert.Equal(new Size(expectedWidth, expectedHeight), OverlayEditorCanvasFrames.LogicalSize(width, height));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 2)]
    [InlineData(1600, 1200)]
    public void TinyAndMaximumFramesProduceExactlyTheRequestedSafePngSize(int width, int height)
    {
        foreach (var theme in OverlayEditorThemes.All)
        {
            using var image = Decode(OverlayEditorCanvasFrames.GetDataUri(theme.Id, width, height));
            Assert.Equal(new Size(width, height), image.Size);
        }
    }

    [Fact]
    public void EquivalentNormalizedKeysReuseTheExactCachedString()
    {
        var paints = 0;
        var cache = new OverlayCanvasFrameCache(paint: _ => "frame-" + ++paints);
        var first = cache.GetDataUri("grindcrest", 160.2, 100.2, double.NaN);
        Assert.Same(first, cache.GetDataUri(OverlayEditorThemes.DefaultId, 161, 101, 2));
        Assert.Same(first, cache.GetDataUri(null, 161, 101));
        Assert.Equal(1, paints);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void PaletteGeometryOpacityAndBorderAreAllPartOfTheCacheKey()
    {
        var paints = 0;
        var cache = new OverlayCanvasFrameCache(paint: _ => "frame-" + ++paints);
        cache.GetDataUri("obsidian", 160, 100);
        cache.GetDataUri("aurora", 160, 100);
        cache.GetDataUri("obsidian", 161, 100);
        cache.GetDataUri("obsidian", 160, 101);
        cache.GetDataUri("obsidian", 160, 100, .5);
        cache.GetDataUri("obsidian", 160, 100, 1, false);
        Assert.Equal(6, paints);
        Assert.Equal(6, cache.EntryCount);
    }

    [Fact]
    public void LeastRecentlyUsedEntriesAreEvictedAtTheEntryBound()
    {
        var paints = 0;
        var cache = new OverlayCanvasFrameCache(2, paint: _ => "frame-" + ++paints);
        var first = cache.GetDataUri(null, 100, 100);
        cache.GetDataUri(null, 200, 100);
        Assert.Same(first, cache.GetDataUri(null, 100, 100));
        cache.GetDataUri(null, 300, 100);
        cache.GetDataUri(null, 200, 100);
        Assert.Equal(4, paints);
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public void UriBudgetEvictsOldEntriesAndNeverRetainsAnOversizedResult()
    {
        var cache = new OverlayCanvasFrameCache(maximumBytes: 16, paint: _ => new string('x', 4));
        for (var width = 1; width <= 10; width++) cache.GetDataUri(null, width, 10);
        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(16, cache.CachedBytes);
        var paints = 0;
        var oversized = new OverlayCanvasFrameCache(maximumBytes: 16, paint: _ => { paints++; return new string('x', 9); });
        oversized.GetDataUri(null, 100, 100);
        oversized.GetDataUri(null, 100, 100);
        Assert.Equal(2, paints);
        Assert.Equal(0, oversized.EntryCount);
        Assert.Equal(0, oversized.CachedBytes);
    }

    [Fact]
    public void TheDefaultCacheLimitsAndConcurrentMissesAreBounded()
    {
        var paints = 0;
        var cache = new OverlayCanvasFrameCache(paint: _ => "frame-" + Interlocked.Increment(ref paints));
        var results = new ConcurrentBag<string>();
        Parallel.For(0, 100, _ => results.Add(cache.GetDataUri(null, 160, 100)));
        Assert.Equal(1, paints);
        Assert.All(results, result => Assert.Same(results.First(), result));
        for (var width = 200; width < 300; width++) cache.GetDataUri(null, width, 100);
        Assert.Equal(64, cache.EntryCount);
        Assert.InRange(cache.CachedBytes, 0, 8 * 1024 * 1024);
    }

    [Fact]
    public async Task FrameComponentIsDecorativeAndUsesLogicalPixelDimensions()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<OverlayCanvasFrame>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OverlayCanvasFrame.ThemeId)] = "aurora", [nameof(OverlayCanvasFrame.Width)] = 160.2,
                [nameof(OverlayCanvasFrame.Height)] = 100.1, [nameof(OverlayCanvasFrame.BackgroundOpacity)] = .5,
                [nameof(OverlayCanvasFrame.ShowBorder)] = false,
            }));
            return component.ToHtmlString();
        });
        Assert.Contains("class=\"oe-canvas-frame\"", html);
        Assert.Contains("aria-hidden=\"true\"", html);
        Assert.Matches("<img\\b[^>]*\\salt(?:=\"\"|(?=\\s|/?>))", html);
        Assert.Contains("width=\"161\"", html);
        Assert.Contains("height=\"101\"", html);
        Assert.Contains("data:image/png;base64,", html);
    }

    private static Bitmap Native(OverlayThemeDefinition theme, double opacity, bool? showBorder = null)
    {
        var image = new Bitmap(160, 100, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (showBorder is { } border) theme.Panel(graphics, new(Point.Empty, image.Size), opacity, border);
        else theme.Panel(graphics, new(Point.Empty, image.Size), opacity);
        return image;
    }

    private static Bitmap Decode(string uri)
    {
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, uri);
        using var stream = new MemoryStream(Convert.FromBase64String(uri[prefix.Length..]));
        using var image = new Bitmap(stream);
        return (Bitmap)image.Clone();
    }

    private static void AssertPixels(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++) Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }
}
