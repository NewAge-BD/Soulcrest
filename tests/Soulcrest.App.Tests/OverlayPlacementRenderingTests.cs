using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayPlacementRenderingTests
{
    private static OverlayPreviewRenderer Renderer() => new(new PetCatalog(
        [new PetDefinition { Id = "preview-one", En = "Preview one", Genus = "cogni" },
         new PetDefinition { Id = "preview-two", En = "Preview two", Genus = "fera" }]));

    [Fact]
    public void AllEightExamplesRenderWithoutLiveServicesOrChangingSettings()
    {
        var settings = new AppSettings
        {
            OverlayEnabled = false, BossRushEnabled = false, BossOverlayEnabled = false,
            LevelingOverlayEnabled = false, AlertsOverlayEnabled = false, EventTimelineOverlayEnabled = false,
            MerchantOverlayEnabled = false, MapOverlayEnabled = false, TimerOverlayTimerIds = []
        };
        var before = JsonSerializer.Serialize(settings);
        using var renderer = Renderer();
        foreach (var descriptor in OverlayRegistry.All)
        {
            using var image = renderer.Render(descriptor.Id, settings);
            Assert.True(image.Width > 0 && image.Height > 0);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.True(HasVisiblePixel(image), descriptor.Id);
            SavePreview(image, descriptor.Id + ".png");
        }
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void LogicalImagesIgnoreUserScaleWhileIndependentWidthsAndTimerWrapChangeTheirGeometry()
    {
        using var renderer = Renderer();
        var settings = new AppSettings();
        foreach (var descriptor in OverlayRegistry.All.Where(item => item.CanPlace))
        {
            OverlayRegistry.WriteLayout(settings, descriptor.Id, new(0, 0, descriptor.MinScale));
            using var minimum = renderer.Render(descriptor.Id, settings);
            OverlayRegistry.WriteLayout(settings, descriptor.Id, new(0, 0, descriptor.MaxScale));
            using var maximum = renderer.Render(descriptor.Id, settings);
            Assert.Equal(minimum.Size, maximum.Size);
        }
        settings.AlertNotificationsWidth = 600;
        settings.EventTimelineOverlayWidth = 960;
        using var notifications = renderer.Render("notifications", settings);
        using var timeline = renderer.Render("event-timeline", settings);
        Assert.Equal(600, notifications.Width);
        Assert.Equal(960, timeline.Width);
        using var wide = renderer.Render("alerts", settings, 1200);
        using var wrapped = renderer.Render("alerts", settings, 300);
        Assert.True(wrapped.Height > wide.Height);
        Assert.True(wrapped.Width <= 300);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("merchant")]
    [InlineData("daevanion")]
    public void BoundPreviewsHaveStandalonePngDataAndFitTheirRequestedWidth(string id)
    {
        using var renderer = Renderer();
        var settings = new AppSettings();
        using var image = renderer.Render(id, settings, 310);
        Assert.Equal(310, image.Width);
        var data = renderer.ToDataUri(id, settings, 310);
        Assert.StartsWith("data:image/png;base64,", data);
        using var stream = new MemoryStream(Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]));
        using var decoded = Image.FromStream(stream);
        Assert.Equal(image.Size, decoded.Size);
    }

    [Fact]
    public void AlphaSceneDimsOnlyTheBackgroundAndKeepsTheExampleBrightOnNegativeMonitorCoordinates()
    {
        using var sample = new Bitmap(60, 50, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(sample)) graphics.Clear(Color.FromArgb(255, 248, 228, 193));
        OverlayPlacementPreview[] items = [new("loot", "Pet-Fortschritt", sample, new(-1880, 160, 1))];
        using var frame = OverlayPlacementForm.RenderFrame(new(320, 240), items, new(-1920, 100), "loot", false);
        var background = frame.GetPixel(3, 3);
        var example = frame.GetPixel(65, 85);
        Assert.Equal(OverlayPlacementForm.DimAlpha, background.A);
        Assert.Equal(0, background.R);
        Assert.Equal(255, example.A);
        Assert.Equal(248, example.R);
        Assert.Equal(228, example.G);
        Assert.Equal(193, example.B);
        using var grid = OverlayPlacementForm.RenderFrame(new(320, 240), [], Point.Empty, "loot", true);
        Assert.True(grid.GetPixel(16, 8).A > grid.GetPixel(8, 8).A);
    }

    [Fact]
    public void SixOverlaySceneCanBeInspectedOfflineWithoutOpeningAWindow()
    {
        using var renderer = Renderer();
        var settings = new AppSettings();
        var layouts = new Dictionary<string, OverlayLayout>
        {
            ["loot"] = new(40, 150, 1), ["farm-loot"] = new(1280, 450, 1), ["boss"] = new(440, 150, 1),
            ["leveling"] = new(880, 150, 1), ["alerts"] = new(104, 40, 1),
            ["notifications"] = new(440, 640, 1, 340), ["event-timeline"] = new(40, 840, 1, 720),
            ["abyss-rank"] = new(1080, 840, 1)
        };
        var previews = OverlayRegistry.All.Where(item => item.CanPlace).Select(descriptor =>
            new OverlayPlacementPreview(descriptor.Id, descriptor.Title, renderer.Render(descriptor.Id, settings), layouts[descriptor.Id])).ToArray();
        try
        {
            using var frame = OverlayPlacementForm.RenderFrame(new(1920, 1080), previews, Point.Empty, "event-timeline", true);
            Assert.Equal(8, previews.Length);
            Assert.True(frame.GetPixel(100, 200).A > OverlayPlacementForm.DimAlpha);
            SavePreview(frame, "place-mode-scene.png");
        }
        finally { foreach (var preview in previews) preview.Image.Dispose(); }
    }

    [Fact]
    public void OffMonitorVirtualPlacementDoesNotCountAsACompletedEditUntilTheUserChangesIt()
    {
        var saved = new OverlayLayout(4000, 2000, 1);
        var virtualLayout = OverlayPlacementForm.FitLayout(saved, new(360, 300), new(-1920, 0, 1920, 1080));
        Assert.NotEqual(saved, virtualLayout);
        Assert.False(OverlayPlacementForm.ShouldSaveGesture(virtualLayout, virtualLayout));
        Assert.True(OverlayPlacementForm.ShouldSaveGesture(virtualLayout, virtualLayout with { X = virtualLayout.X - 16 }));
        Assert.Equal(new OverlayLayout(4000, 2000, 1), saved);
    }

    [Theory]
    [InlineData(-1905, -1, -1920, -1904)]
    [InlineData(-1905, -1, 0, -1904)]
    [InlineData(23, 9, 0, 32)]
    public void DragSnapsInPhysicalPixelsRelativeToTheSelectedMonitor(int x, int delta, int origin, int expected)
    {
        var layout = OverlayPlacementForm.DragLayout(new(x, 80, 1), new(delta, 5), new(origin, 0, 1920, 1080), true);
        Assert.Equal(expected, layout.X);
        Assert.Equal(80, layout.Y);
        var free = OverlayPlacementForm.DragLayout(new(x, 80, 1), new(delta, 5), new(origin, 0, 1920, 1080), false);
        Assert.Equal(x + delta, free.X);
        Assert.Equal(85, free.Y);
    }

    [Theory]
    [InlineData((int)(OverlayPlacementGrip.Left | OverlayPlacementGrip.Top), -100, -50, 100, 150)]
    [InlineData((int)(OverlayPlacementGrip.Right | OverlayPlacementGrip.Bottom), 100, 50, 200, 200)]
    public void CornerResizeIsProportionalAndKeepsTheOppositeCornerFixed(int gripValue,
        int dx, int dy, int expectedX, int expectedY)
    {
        var grip = (OverlayPlacementGrip)gripValue;
        var original = new OverlayLayout(200, 200, 1, 340);
        var descriptor = OverlayRegistry.Get("notifications");
        var resized = OverlayPlacementForm.ResizeLayout(original, new(200, 200, 400, 200), new(dx, dy),
            grip, descriptor, new(0, 0, 1920, 1080), false);
        Assert.Equal(1.25, resized.Scale);
        Assert.Equal(expectedX, resized.X);
        Assert.Equal(expectedY, resized.Y);
        Assert.Equal(340, resized.Width);
        var oversized = OverlayPlacementForm.ResizeLayout(original, new(200, 200, 400, 200), new(10000, 10000),
            OverlayPlacementGrip.Right | OverlayPlacementGrip.Bottom, descriptor, new(0, 0, 1920, 1080), false);
        Assert.Equal(descriptor.MaxScale, oversized.Scale);
    }

    [Fact]
    public void WidthHandleIsDistinctFromTheFourScaleCornersAndLayoutsFitOffsetSmallScreens()
    {
        var bounds = new Rectangle(40, 80, 360, 240);
        Assert.Equal(OverlayPlacementGrip.Width, OverlayPlacementForm.GripAt(bounds, new(400, 200), true));
        Assert.Equal(OverlayPlacementGrip.None, OverlayPlacementForm.GripAt(bounds, new(400, 200), false));
        Assert.Equal(OverlayPlacementGrip.Right | OverlayPlacementGrip.Top,
            OverlayPlacementForm.GripAt(bounds, new(400, 80), true));
        var monitor = new Rectangle(-1280, -100, 1280, 720);
        var fitted = OverlayPlacementForm.FitLayout(new(100, 800, 2), new(720, 500), monitor);
        var actual = new Rectangle(fitted.X, fitted.Y, (int)Math.Ceiling(720 * fitted.Scale), (int)Math.Ceiling(500 * fitted.Scale));
        Assert.True(monitor.Contains(actual));
    }

    [Theory]
    [InlineData("loot")]
    [InlineData("boss")]
    [InlineData("leveling")]
    [InlineData("alerts")]
    [InlineData("notifications")]
    [InlineData("event-timeline")]
    public void SmallMonitorFitNeverCreatesAScaleBelowThePanelsPersistentMinimum(string id)
    {
        var descriptor = OverlayRegistry.Get(id);
        var saved = new OverlayLayout(8000, -9000, descriptor.MaxScale, descriptor.MaxWidth);
        var fitted = OverlayPlacementForm.FitLayout(saved, new(1600, 1400), new(-640, 0, 640, 480), descriptor.MinScale);
        Assert.Equal(descriptor.MinScale, fitted.Scale);
        Assert.Equal(fitted.Scale, OverlayRegistry.NormalizeLayout(id, fitted).Scale);
        Assert.Equal((-640, 0), (fitted.X, fitted.Y));
        Assert.Equal(new OverlayLayout(8000, -9000, descriptor.MaxScale, descriptor.MaxWidth), saved);
        Assert.False(OverlayPlacementForm.ShouldSaveGesture(fitted, fitted));
    }

    [Fact]
    public void ExplicitBringToMonitorUsesTheShownPositionWithoutChangingStoredWidthOrScale()
    {
        var saved = new OverlayLayout(8000, -9000, 2, 1600);
        var shown = OverlayPlacementForm.FitLayout(saved, new(1600, 300), new(-1280, 0, 1280, 720), .6);
        Assert.NotEqual(saved.Scale, shown.Scale);
        var relocated = OverlayPlacementForm.RelocateLayout(saved, shown);
        Assert.Equal((shown.X, shown.Y), (relocated.X, relocated.Y));
        Assert.Equal(2, relocated.Scale);
        Assert.Equal(1600, relocated.Width);
        Assert.Equal(new OverlayLayout(8000, -9000, 2, 1600), saved);
    }

    [Fact]
    public void PlacementFitsTheWorkingAreaWhileDimmingTheEntireMonitorIncludingTheTaskbar()
    {
        var monitor = new OverlayMonitor("left", "Left", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040));
        using var image = new Bitmap(360, 200, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        var layout = OverlayPlacementForm.FitLayout(new(-1800, 1000, 1), image.Size, monitor.WorkingArea, .6);
        Assert.Equal(840, layout.Y);
        Assert.Equal(1040, layout.Y + image.Height);
        using var frame = OverlayPlacementForm.RenderFrame(monitor.Bounds.Size,
            [new("loot", "Pet-Fortschritt", image, layout)], monitor.Bounds.Location, "loot", false);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(255, frame.GetPixel(150, 1000).A);
        Assert.Equal(OverlayPlacementForm.DimAlpha, frame.GetPixel(150, 1070).A);
    }

    [Fact]
    public void TimerScaleFitsAfterWrappingInsteadOfBeingCappedByItsPreviousSingleRow()
    {
        using var renderer = Renderer();
        var settings = new AppSettings();
        var workingArea = new Rectangle(-800, 0, 800, 600);
        using var original = renderer.Render("alerts", settings, OverlayPlacementForm.MaximumLogicalWidth(workingArea, 1));
        Assert.True(original.Width * 2 > workingArea.Width);
        using var scaled = renderer.Render("alerts", settings, OverlayPlacementForm.MaximumLogicalWidth(workingArea, 2));
        Assert.True(scaled.Height > original.Height);
        var fitted = OverlayPlacementForm.FitLayout(new(-800, 0, 2), scaled.Size, workingArea, .6);
        Assert.Equal(2, fitted.Scale);
        Assert.True(scaled.Width * fitted.Scale <= AlertsOverlayForm.StripArea(workingArea, fitted.Scale).Width);
    }

    private static bool HasVisiblePixel(Bitmap image)
    {
        for (var y = 0; y < image.Height; y += Math.Max(1, image.Height / 20))
            for (var x = 0; x < image.Width; x += Math.Max(1, image.Width / 20))
                if (image.GetPixel(x, y).A > 0) return true;
        return false;
    }

    private static void SavePreview(Bitmap image, string file)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_PREVIEW_OUTPUT") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        image.Save(Path.Combine(output, file), ImageFormat.Png);
    }
}
