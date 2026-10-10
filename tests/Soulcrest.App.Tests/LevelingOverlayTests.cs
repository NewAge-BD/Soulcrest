using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LevelingOverlayTests
{
    private static SavedRoute Route => new("route", "Altgard · Level 10–25", [], Category: LevelingRouteStyle.Category);
    private static MapTarget Target(int index, bool completed = false, string? color = null, string? character = "alice") =>
        new($"target-{index}", "altgard", 100 + index * 100, 200, index == 4 ? "The Lost Memory of the Ancient Guardians" : "",
            "NPCs", null, RouteId: "route", Color: color ?? LevelingRouteStyle.MainQuest, Leveling: true,
            Completed: completed, StopIndex: index, CharacterId: character);
    private static LevelingOverlaySnapshot Snapshot(int completed = 4, int total = 10)
    {
        var targets = Enumerable.Range(0, total).Select(i => Target(i, i < completed,
            new[] { LevelingRouteStyle.MainQuest, LevelingRouteStyle.Teleport,
                LevelingRouteStyle.Exploration, LevelingRouteStyle.RegionalQuest }[i % 4])).ToArray();
        return LevelingOverlayForm.BuildSnapshot(Route, new(completed, total), "alice", "Testheld", targets)!;
    }

    [Fact]
    public void SnapshotKeepsStableStationNumbersAndOnlyThreePreviousAndThreeUpcomingStops()
    {
        var snapshot = Snapshot();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, snapshot.Window.Select(t => t.StopIndex!.Value));
        Assert.Equal(3, snapshot.Window.Count(t => t.Completed));
        Assert.Equal(3, snapshot.Window.Count(t => !t.Completed));
        Assert.Equal(4, snapshot.Current!.StopIndex);
        Assert.Equal(new RouteProgress(4, 10), snapshot.Progress);
        Assert.Equal("alice", snapshot.CharacterId);
        Assert.Equal("Testheld", snapshot.CharacterName);
    }

    [Fact]
    public void SnapshotFiltersOtherCharactersRoutesAndOrdinaryMarks()
    {
        MapTarget[] targets = [Target(0) with { CharacterId = "bob" },
            Target(1) with { RouteId = "other" }, Target(2) with { Leveling = false }, Target(3), Target(4)];
        var snapshot = LevelingOverlayForm.BuildSnapshot(Route, new(3, 10), "alice", "Alice", targets)!;
        Assert.Equal(new[] { 3, 4 }, snapshot.Window.Select(t => t.StopIndex!.Value));
        Assert.Equal(3, snapshot.Current!.StopIndex);
        Assert.Null(LevelingOverlayForm.BuildSnapshot(Route with { Category = null }, new(0, 10), "alice", "Alice", targets));
        Assert.Null(LevelingOverlayForm.BuildSnapshot(Route, null, "alice", "Alice", targets));
        Assert.Null(LevelingOverlayForm.BuildSnapshot(null, new(0, 10), "alice", "Alice", targets));
    }

    [Fact]
    public void FinishedRouteRetainsItsHudAndHistoryForBackwardNavigation()
    {
        var snapshot = Snapshot(completed: 10);
        Assert.Null(snapshot.Current);
        Assert.Equal(new[] { 7, 8, 9 }, snapshot.Window.Select(t => t.StopIndex!.Value));
        Assert.True(LevelingOverlayForm.PanelVisible(new AppSettings { LevelingOverlayEnabled = true }, snapshot));
        Assert.False(LevelingOverlayForm.PanelVisible(new AppSettings { LevelingOverlayEnabled = false }, snapshot));
        Assert.False(LevelingOverlayForm.PanelVisible(new AppSettings { LevelingOverlayEnabled = true }, null));
        Assert.True(snapshot.Progress.CanGoBack);
        Assert.False(snapshot.Progress.CanGoNext);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(4, true, true)]
    [InlineData(10, true, false)]
    public void ButtonsAreInteractiveOnlyWhileAltInteractionIsEnabledAndNeverStartADrag(int completed, bool back, bool next)
    {
        var snapshot = Snapshot(completed);
        foreach (var (action, enabled) in new[] { (LevelingOverlayAction.Back, back),
            (LevelingOverlayAction.Next, next), (LevelingOverlayAction.Cancel, true) })
        {
            var bounds = LevelingOverlayForm.ButtonBounds(action, snapshot.Window.Count);
            var center = new PointF(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Assert.Equal(enabled ? action : LevelingOverlayAction.None, LevelingOverlayForm.HitTest(center, false, snapshot));
            Assert.Equal(LevelingOverlayAction.None, LevelingOverlayForm.HitTest(center, true, snapshot));
            Assert.Equal(LevelingOverlayAction.None, LevelingOverlayForm.HitTest(center, false, null));
        }
        Assert.Equal(LevelingOverlayAction.Drag, LevelingOverlayForm.HitTest(new(100, 40), false, snapshot));
        Assert.Equal(LevelingOverlayAction.None, LevelingOverlayForm.HitTest(new(100, 40), true, snapshot));
        Assert.Equal(LevelingOverlayAction.None, LevelingOverlayForm.HitTest(new(100, 160), false, snapshot));
    }

    [Theory]
    [InlineData("route", "alice", "route", "alice", true)]
    [InlineData("route", "alice", "route", "bob", false)]
    [InlineData("route", "alice", "other", "alice", false)]
    [InlineData("route", "alice", null, "alice", false)]
    [InlineData(null, "alice", null, "alice", false)]
    public void ClicksStartedBeforeCharacterOrRouteSwitchAreDiscarded(string? pressedRoute, string pressedCharacter,
        string? activeRoute, string activeCharacter, bool expected) =>
        Assert.Equal(expected, LevelingOverlayForm.CanApplyAction(pressedRoute, pressedCharacter, activeRoute, activeCharacter));

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(4, 3, false)]
    public void AutomaticArrivalOrCursorChangeDuringButtonPressDiscardsThePendingClick(int pressed, int current, bool expected)
    {
        Assert.Equal(expected, LevelingOverlayForm.CanApplyAction("route", "alice", "route", "alice",
            new(pressed, 10), new(current, 10)));
        Assert.False(LevelingOverlayForm.CanApplyAction("route", "alice", "route", "alice", new(pressed, 10), null));
        Assert.False(LevelingOverlayForm.CanApplyAction("route", "alice", "route", "bob", new(pressed, 10), new(current, 10)));
        Assert.False(LevelingOverlayForm.CanApplyAction("route", "alice", "next-route", "alice", new(pressed, 10), new(current, 10)));
    }

    [Theory]
    [InlineData(-9999, 9999, -1920, 0, 1920, 1080, -1920, 642)]
    [InlineData(9999, -9999, 0, 0, 1920, 1080, 1560, 0)]
    [InlineData(840, 200, 0, 0, 1920, 1080, 840, 200)]
    [InlineData(840, 200, 0, 0, 300, 300, 0, 0)]
    public void PanelReturnsToReachableMonitorBounds(int x, int y, int left, int top, int width, int height, int expectedX, int expectedY)
    {
        Assert.Equal(new Point(expectedX, expectedY), LevelingOverlayForm.ClampLocation(new(x, y),
            new(LevelingOverlayForm.PanelWidth, LevelingOverlayForm.LogicalHeight(6)), new(left, top, width, height)));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(.1, .7)]
    [InlineData(5, 1.6)]
    [InlineData(1.25, 1.25)]
    public void NonFiniteAndOutOfBoundsScaleCannotBreakNativeWindowBounds(double input, double expected) =>
        Assert.Equal(expected, LevelingOverlayForm.Scale(input));

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void AllPanelHintsExplainTheSameHeldAltGesture(string language)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            foreach (var locked in new[] { false, true })
            {
                var expected = language == "de"
                    ? locked ? "Schloss klicken zum Entsperren" : "Schloss: sperren · Kopf ziehen"
                    : locked ? "Click the lock to unlock" : "Lock to pass clicks through · Drag header";
                Assert.Equal(expected, LevelingOverlayForm.HotkeyHint(locked));
                Assert.Equal(expected, PetOverlayForm.HotkeyHint(locked));
                Assert.Equal(expected, BossOverlayForm.HotkeyHint(locked));
            }
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void RenderingPreservesAllObjectiveColoursAndFadesOnlyHistory(string language)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            var snapshot = Snapshot();
            using var bitmap = Render(snapshot, false);
            for (var i = 0; i < snapshot.Window.Count; i++)
            {
                var target = snapshot.Window[i];
                var color = LevelingOverlayForm.ObjectiveColor(target);
                var expected = target.Completed ? LevelingOverlayForm.Mix(color, Color.FromArgb(15, 22, 31), .28) : color;
                Assert.Equal(expected.ToArgb(), bitmap.GetPixel(25, LevelingOverlayForm.WindowTop + i * LevelingOverlayForm.RowStride + 13).ToArgb());
            }
            using var g = Graphics.FromImage(bitmap);
            using var small = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
            foreach (var locked in new[] { true, false })
                Assert.True(g.MeasureString(LevelingOverlayForm.HotkeyHint(locked), small).Width < LevelingOverlayForm.PanelWidth - 32,
                    "The held-Alt interaction hint must fit without truncation.");
            Assert.Equal(language == "de" ? "Station 4" : "Stop 4", LevelingOverlayForm.TargetLabel(Target(3)));
            Assert.Equal("The Lost Memory of the Ancient Guardians", LevelingOverlayForm.TargetLabel(Target(4)));
            if (Environment.GetEnvironmentVariable("SOULCREST_LEVELING_OVERLAY_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                // Verified titles and objective colours from the imported first Asmodian route.
                // This illustrative cursor exists only in the renderer; no character data is changed.
                var exampleRoute = Route with { Name = "Asmodian 1/8 - Level 10-17" };
                MapTarget[] exampleTargets =
                [
                    Target(1, true, LevelingRouteStyle.Exploration),
                    Target(2, true) with { Name = "Finding Nemon" },
                    Target(3, true) with { Name = "Finding Nemon" },
                    Target(4) with { Name = "Survival Supplies" },
                    Target(5) with { Name = "Gathering Essentials" },
                    Target(6, false, LevelingRouteStyle.Teleport)
                ];
                var example = LevelingOverlayForm.BuildSnapshot(exampleRoute, new(4, 58), "alice", "Testheld", exampleTargets)!;
                using var preview = Render(example, false);
                preview.Save(Path.Combine(output, $"leveling-overlay-{language}.png"), ImageFormat.Png);
                using var locked = Render(example, true);
                locked.Save(Path.Combine(output, $"leveling-overlay-locked-{language}.png"), ImageFormat.Png);
                var complete = LevelingOverlayForm.BuildSnapshot(exampleRoute, new(58, 58), "alice", "Testheld",
                [
                    Target(55, true) with { Name = "Dead Men Tell No Tales" },
                    Target(56, true) with { Name = "Dead Men Tell No Tales" },
                    Target(57, true) with { Name = "Buried Atrocities" }
                ])!;
                using var finished = Render(complete, false);
                finished.Save(Path.Combine(output, $"leveling-overlay-finished-{language}.png"), ImageFormat.Png);
            }
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(.7)]
    [InlineData(1.6)]
    public void ScaledPanelKeepsAllSixRowsControlsAndFooterInsideBounds(double scale)
    {
        var snapshot = Snapshot();
        using var bitmap = new Bitmap((int)Math.Ceiling(LevelingOverlayForm.PanelWidth * scale),
            (int)Math.Ceiling(LevelingOverlayForm.LogicalHeight(snapshot.Window.Count) * scale));
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform((float)scale, (float)scale);
        LevelingOverlayForm.PaintPanel(g, snapshot, false);
        foreach (var action in new[] { LevelingOverlayAction.Back, LevelingOverlayAction.Next, LevelingOverlayAction.Cancel })
        {
            var bounds = LevelingOverlayForm.ButtonBounds(action, snapshot.Window.Count);
            Assert.True(bounds.Right <= LevelingOverlayForm.PanelWidth);
            Assert.True(bounds.Bottom + 30 <= LevelingOverlayForm.LogicalHeight(snapshot.Window.Count));
        }
        Assert.True(bitmap.GetPixel((int)(25 * scale), (int)((LevelingOverlayForm.WindowTop + 5 * LevelingOverlayForm.RowStride + 13) * scale)).A > 0);
    }

    private static Bitmap Render(LevelingOverlaySnapshot snapshot, bool locked)
    {
        var bitmap = new Bitmap(LevelingOverlayForm.PanelWidth, LevelingOverlayForm.LogicalHeight(snapshot.Window.Count));
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        LevelingOverlayForm.PaintPanel(g, snapshot, locked);
        return bitmap;
    }
}
