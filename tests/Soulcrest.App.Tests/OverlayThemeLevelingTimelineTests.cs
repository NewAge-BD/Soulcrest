using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayThemeLevelingTimelineTests
{
    private static LevelingOverlaySnapshot Leveling(int completed = 2)
    {
        var targets = Enumerable.Range(0, 6).Select(index => new MapTarget("theme-stop-" + index,
            "altgard", 100, 200, "A long quest objective " + index, "NPCs", null, RouteId: "theme-route",
            Color: index % 2 == 0 ? LevelingRouteStyle.MainQuest : LevelingRouteStyle.RegionalQuest,
            Leveling: true, Completed: index < completed, StopIndex: index)).ToArray();
        return new("theme-route", "Altgard · Quest route", "theme-profile", "Preview profile", new(completed, 6),
            targets.FirstOrDefault(target => !target.Completed), targets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public void QuestTrackerControlsUseTheSameThemeGeometryForPaintingAndHitTesting(int completed)
    {
        var snapshot = Leveling(completed);
        foreach (var theme in new[] { OverlayThemeDefinition.Aion2, OverlayThemeDefinition.Legacy })
        {
            var layout = LevelingOverlayForm.Measure(snapshot.Window.Count, theme);
            Assert.Equal(LevelingOverlayForm.LogicalHeight(snapshot.Window.Count, theme), layout.Size.Height);
            Assert.True(new RectangleF(PointF.Empty, layout.Size).Contains(layout.DragBounds));
            Assert.True(new RectangleF(PointF.Empty, layout.Size).Contains(layout.LockBounds));
            foreach (var action in new[] { LevelingOverlayAction.Back, LevelingOverlayAction.Next, LevelingOverlayAction.Cancel })
            {
                var bounds = LevelingOverlayForm.ButtonBounds(action, snapshot.Window.Count, theme);
                Assert.True(new RectangleF(PointF.Empty, layout.Size).Contains(bounds));
                var center = new PointF(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var enabled = action == LevelingOverlayAction.Cancel || (action == LevelingOverlayAction.Back
                    ? snapshot.Progress.CanGoBack : snapshot.Progress.CanGoNext);
                Assert.Equal(enabled ? action : LevelingOverlayAction.None,
                    LevelingOverlayForm.HitTest(center, false, snapshot, theme));
                Assert.Equal(LevelingOverlayAction.None, LevelingOverlayForm.HitTest(center, true, snapshot, theme));
            }
        }
        Assert.NotEqual(LevelingOverlayForm.Measure(6, OverlayThemeDefinition.Legacy).RowsTop,
            LevelingOverlayForm.Measure(6, OverlayThemeDefinition.Aion2).RowsTop);
    }

    [Theory]
    [InlineData("de", .7)]
    [InlineData("en", 1.6)]
    public void Aion2QuestTrackerHasTransparentMaterialAndKeepsEveryRowAndControlInsideItsSurface(string language, double scale)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            var snapshot = Leveling();
            var theme = OverlayThemeDefinition.Aion2;
            var size = LevelingOverlayForm.Measure(snapshot.Window.Count, theme).Size;
            var width = (int)Math.Ceiling(size.Width * scale);
            var height = (int)Math.Ceiling(size.Height * scale);
            using var bitmap = new Bitmap(width + 16, height + 16, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.ScaleTransform((float)scale, (float)scale);
                LevelingOverlayForm.PaintPanel(graphics, snapshot, false, theme: theme);
            }
            Assert.InRange(bitmap.GetPixel((int)(5 * scale), (int)(100 * scale)).A, 200, 210);
            Assert.Equal(0, bitmap.GetPixel(width + 8, height / 2).A);
            Assert.Equal(0, bitmap.GetPixel(width / 2, height + 8).A);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(360, 12)]
    [InlineData(720, 24)]
    [InlineData(1600, 48)]
    public void Aion2TimelineRetainsOverlappingBossAndResetTooltipsWithinMeasuredBounds(int width, int hours)
    {
        var first = EventTimelineTests.Boss(1, TimeSpan.FromHours(2));
        var second = EventTimelineTests.Boss(2, TimeSpan.FromHours(2)) with { Cached = true };
        var snapshot = EventTimelineTests.Create([first, second], [first.Place.Key, second.Place.Key], hours);
        var theme = OverlayThemeDefinition.Aion2;
        var layout = EventTimelineOverlayForm.Measure(snapshot, width, theme);
        using var bitmap = new Bitmap(layout.Size.Width + 16, layout.Size.Height + 16, PixelFormat.Format32bppPArgb);
        IReadOnlyList<EventTimelineOverlayForm.TimelineHit> hits;
        using (var graphics = Graphics.FromImage(bitmap))
            hits = EventTimelineOverlayForm.PaintPanel(graphics, snapshot, false, width, theme: theme);
        Assert.Equal(layout.Size.Height, EventTimelineOverlayForm.LogicalHeight(snapshot, theme));
        Assert.Equal(layout.TrackStart, EventTimelineOverlayForm.TrackStart(width, theme));
        Assert.Contains(hits, hit => hit.Tooltip.Contains(first.Place.Boss.En, StringComparison.Ordinal));
        Assert.Contains(hits, hit => hit.Tooltip.Contains(second.Place.Boss.En, StringComparison.Ordinal)
            && hit.Tooltip.Contains(UiText.T("Unbestätigt"), StringComparison.Ordinal));
        Assert.All(hits, hit => Assert.True(new RectangleF(PointF.Empty, layout.Size).Contains(hit.Bounds)));
        Assert.InRange(bitmap.GetPixel(4, 40).A, 200, 210);
        Assert.Equal(0, bitmap.GetPixel(width + 8, layout.Size.Height / 2).A);
        Assert.Equal(0, bitmap.GetPixel(width / 2, layout.Size.Height + 8).A);
    }

    [Fact]
    public void OmittedThemeKeepsTheOriginalLegacyPixelsAndGeometry()
    {
        var snapshot = Leveling();
        Assert.Equal(438, LevelingOverlayForm.LogicalHeight(6));
        using var expected = new Bitmap(360, 438, PixelFormat.Format32bppPArgb);
        using var actual = new Bitmap(360, 438, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(expected)) LevelingOverlayForm.PaintPanel(graphics, snapshot, true);
        using (var graphics = Graphics.FromImage(actual)) LevelingOverlayForm.PaintPanel(graphics, snapshot, true, theme: OverlayThemeDefinition.Legacy);
        AssertSamePixels(expected, actual);
        var timeline = EventTimelineTests.Create();
        var height = EventTimelineOverlayForm.LogicalHeight(timeline);
        Assert.Equal(104, height);
        using var expectedTimeline = new Bitmap(720, height, PixelFormat.Format32bppPArgb);
        using var actualTimeline = new Bitmap(720, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(expectedTimeline)) EventTimelineOverlayForm.PaintPanel(graphics, timeline, true);
        using (var graphics = Graphics.FromImage(actualTimeline)) EventTimelineOverlayForm.PaintPanel(graphics, timeline, true, theme: OverlayThemeDefinition.Legacy);
        AssertSamePixels(expectedTimeline, actualTimeline);
    }

    private static void AssertSamePixels(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }
}
