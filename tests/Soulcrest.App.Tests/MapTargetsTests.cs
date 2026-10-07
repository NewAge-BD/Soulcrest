using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")] // targets.json is shared with MapZoomPlausibilityTests, which also writes settings.json
public sealed class MapTargetsTests
{
    [Fact]
    public void RightClickTogglesAndTargetsSurviveARestart()
    {
        File.Delete(AppPaths.TargetsFile);
        try
        {
            var targets = new MapTargetsService();
            Assert.True(targets.Toggle("altgard", 4100, 3900, "Kuru", "Pets · Fera", "icons/kuru.png"));
            Assert.True(targets.Toggle("altgard", 5000, 4200, "Teleporter", "Locations · Teleport", null));
            Assert.False(targets.Toggle("altgard", 4100, 3900, "Kuru", "Pets · Fera", "icons/kuru.png")); // second right click unmarks

            var reloaded = new MapTargetsService();
            var target = Assert.Single(reloaded.Targets);
            Assert.Equal(("altgard", 5000.0, 4200.0, "Teleporter"), (target.MapId, target.X, target.Y, target.Name));
            reloaded.Clear();
            Assert.Empty(new MapTargetsService().Targets);
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
        }
    }

    /// <summary>User request 2026-10-05: save the Shift + right click sequence, start it again, repeat it.</summary>
    [Fact]
    public void SavedRouteStartsAgainAndRepeatsAfterItsLastStop()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.RoutesFile);
        try
        {
            var targets = new MapTargetsService();
            targets.Toggle("altgard", 50, 50, "Single", "Locations", null);           // not part of the sequence
            targets.Toggle("altgard", 100, 100, "A", "Pets · Fera", null, petId: "kuru");
            targets.Toggle("altgard", 200, 200, "B", "Locations", null, chain: true);
            targets.Toggle("altgard", 300, 300, "C", "Locations", null, chain: true);
            Assert.Equal(["A", "B", "C"], targets.ChainOn("altgard").Select(t => t.Name));
            Assert.Empty(targets.ChainOn("poeta"));

            var route = targets.SaveRoute("altgard", "  Runde  ");
            Assert.NotNull(route);
            Assert.Equal("Runde", route.Name);
            Assert.Equal(["A", "B", "C"], route.Stops.Select(s => s.Name));
            Assert.Equal("kuru", route.Stops[0].PetId);
            Assert.Equal(["A", "B", "C"], targets.Targets.Where(t => t.RouteId == route.Id).Select(t => t.Name)); // the run on screen already counts

            // Saved: a new instance (restart) knows the route; starting it replaces all marks with the chain.
            var reloaded = new MapTargetsService();
            reloaded.StartRoute(Assert.Single(reloaded.Routes).Id);
            var (a, b, c) = (reloaded.Targets[0], reloaded.Targets[1], reloaded.Targets[2]);
            Assert.Equal(3, reloaded.Targets.Count);
            Assert.Equal((null, a.Id, b.Id), (a.After, b.After, c.After));
            Assert.Contains(route.Id, reloaded.ActiveRoutes);

            // Without repeat the route ends after its last stop.
            foreach (var target in reloaded.Targets)
                reloaded.Arrive(target.Id);
            Assert.Empty(reloaded.Targets);

            // With repeat it starts over: player → A → B → C again.
            reloaded.SetRouteRepeat(route.Id, true);
            reloaded.StartRoute(route.Id);
            foreach (var target in reloaded.Targets)
                reloaded.Arrive(target.Id);
            Assert.Equal(["A", "B", "C"], reloaded.Targets.Select(t => t.Name));
            Assert.Null(reloaded.Targets[0].After);

            // Removing by hand never restarts; deleting the route keeps the marks as a plain sequence.
            reloaded.Remove(reloaded.Targets[0].Id);
            reloaded.DeleteRoute(route.Id);
            Assert.Empty(reloaded.Routes);
            Assert.All(reloaded.Targets, t => Assert.Null(t.RouteId));
            Assert.Empty(new MapTargetsService().Routes);
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.RoutesFile);
        }
    }

    [Fact]
    public void ASingleTargetIsNoRoute()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.RoutesFile);
        try
        {
            var targets = new MapTargetsService();
            targets.Toggle("altgard", 100, 100, "A", "Locations", null);
            Assert.Null(targets.SaveRoute("altgard", "x"));
            Assert.Empty(targets.Routes);
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.RoutesFile);
        }
    }

    [Fact]
    public void LineToATargetBeyondTheMapAreaEndsAtItsEdge()
    {
        var area = new RectangleF(0, 0, 400, 300);
        var clipped = RouteGeometry.Clip(new PointF(200, 150), new PointF(800, 150), area);
        Assert.NotNull(clipped);
        Assert.Equal(new PointF(200, 150), clipped.Value.Start);
        Assert.Equal(400, clipped.Value.End.X, 3);
        Assert.False(clipped.Value.ReachesEnd);

        var inside = RouteGeometry.Clip(new PointF(200, 150), new PointF(300, 100), area);
        Assert.True(inside!.Value.ReachesEnd);
    }

    [Fact]
    public void ArrowsPointTowardsTheTarget()
    {
        var arrows = RouteGeometry.Arrows(new PointF(0, 0), new PointF(0, 300), 60);
        Assert.Equal(5, arrows.Count); // at 30, 90, 150, 210, 270
        Assert.All(arrows, a => Assert.Equal(90, a.Angle, 3)); // downwards
        Assert.Equal(30, arrows[0].Point.Y, 3);
    }

    [Fact]
    public void TargetIsDrawnWhereTheMapShowsIt()
    {
        // In-game map shows the reference zoomed ×2 and shifted; Altgard has 2 map units per reference pixel.
        var fix = new MapFix(2, 0, -1000, 0, 2, -600, 50, 60, 0.3, false);
        var placement = new MapPlacement("altgard", fix, 2, new Rectangle(1800, 100, 500, 400), new Point2d(250, 200));
        var screen = placement.WorldToScreen(1300, 900); // reference (650, 450) -> frame (300, 300)
        Assert.Equal(new PointF(2100, 400), screen);
        Assert.Equal(new PointF(2050, 300), placement.PlayerOnScreen);
    }
}

public sealed class RouteOverlayDrawingTests
{
    [Fact]
    public void DrawsLinesOnlyInsideTheMapArea()
    {
        var fix = new MapFix(2, 0, -1000, 0, 2, -600, 50, 60, 0.3, false);
        var placement = new MapPlacement("altgard", fix, 2, new Rectangle(1800, 100, 500, 400), new Point2d(250, 200));
        var near = new MapTarget("a", "altgard", 1300, 900, "Kuru", "Pets · Fera", null);    // inside the area
        var far = new MapTarget("b", "altgard", 3000, 400, "Teleporter", "Locations", null);  // beyond the right edge
        using var icon = new Bitmap(26, 26);
        using (var g = Graphics.FromImage(icon))
            g.Clear(Color.OrangeRed);
        var pets = new List<Soulcrest.App.Overlay.RouteOverlayForm.PetSymbol>
        {
            new(1100, 700, icon, "St. 1 · 6/25", false), // reference (550, 350) -> frame (100, 100)
            new(1150, 1000, icon, "St. 3 · max", true),
        };
        using var bitmap = Soulcrest.App.Overlay.RouteOverlayForm.Draw(placement, [(near, 0), (far, 1)], pets);
        bitmap.Save(Path.Combine(Path.GetTempPath(), "soulcrest-route-overlay.png"));

        Assert.Equal(new System.Drawing.Size(500, 400), bitmap.Size);
        Assert.True(bitmap.GetPixel(275, 250).A > 0, "Linie zum nahen Ziel fehlt"); // halfway player (250,200) -> target (300,300)
        Assert.Equal(0, bitmap.GetPixel(20, 380).A); // nothing in an empty corner
        Assert.True(bitmap.GetPixel(100, 100).R > 200, "Pet-Symbol fehlt"); // pet icon at its map position
    }

    /// <summary>User report 2026-10-06: target names were cut off at the edge of the map area.</summary>
    [Fact]
    public void LabelsStayInsideTheMapArea()
    {
        var area = new RectangleF(4, 4, 492, 392);
        // Beside a ring near the right edge: moves to the ring's left side (right edge at 430).
        var (dx, dy) = Soulcrest.App.Overlay.RouteOverlayForm.LabelShift(new RectangleF(450, 100, 120, 20), area, leftOf: 430);
        Assert.Equal((430f, 0f), (572 + dx, dy)); // inflated right edge 572 -> 430
        // Without a ring: pushed back just inside the right and bottom edge.
        (dx, dy) = Soulcrest.App.Overlay.RouteOverlayForm.LabelShift(new RectangleF(450, 390, 120, 20), area);
        Assert.Equal((496f, 396f), (572 + dx, 412 + dy));
        // Already inside: untouched.
        Assert.Equal((0f, 0f), Soulcrest.App.Overlay.RouteOverlayForm.LabelShift(new RectangleF(100, 100, 120, 20), area));
    }

    [Fact]
    public void DrawsSoulMonstersOfMarkedPetsAsRedDots()
    {
        var fix = new MapFix(2, 0, -1000, 0, 2, -600, 50, 60, 0.3, false);
        var placement = new MapPlacement("altgard", fix, 2, new Rectangle(1800, 100, 500, 400), new Point2d(250, 200));
        using var bitmap = Soulcrest.App.Overlay.RouteOverlayForm.Draw(placement, [], souls: [new(1100, 700)]); // frame (100, 100)

        var dot = bitmap.GetPixel(100, 100);
        Assert.True(dot.R > 200 && dot.G < 150, "Quellmonster-Punkt fehlt");
        Assert.Equal(0, bitmap.GetPixel(20, 380).A);
    }

    [Fact]
    public void DrawsResourcesWithTheirLegendIconBelowPets()
    {
        var fix = new MapFix(2, 0, -1000, 0, 2, -600, 50, 60, 0.3, false);
        var placement = new MapPlacement("altgard", fix, 2, new Rectangle(1800, 100, 500, 400), new Point2d(250, 200));
        using var herb = new Bitmap(20, 20);
        using (var g = Graphics.FromImage(herb))
            g.Clear(Color.LimeGreen);
        var resources = new List<Soulcrest.App.Overlay.RouteOverlayForm.ResourceSymbol>
        {
            new(1100, 700, herb), // reference (550, 350) -> frame (100, 100)
            new(1200, 700, null), // without icon: a dot at frame (200, 100)
        };
        using var bitmap = Soulcrest.App.Overlay.RouteOverlayForm.Draw(placement, [], resources: resources);

        var icon = bitmap.GetPixel(100, 100);
        Assert.True(icon.G > 150 && icon.R < 100, "Ressourcen-Icon fehlt");
        Assert.True(bitmap.GetPixel(200, 100).A > 0, "Ersatzpunkt fehlt");
        Assert.Equal(0, bitmap.GetPixel(20, 380).A);
    }
}

/// <summary>User report 2026-10-03: "wenn die position einmal verloren ist, wird sie nie wiedergefunden".</summary>
public sealed class MapSearchScheduleTests
{
    [Fact]
    public void CurrentMapKeepsItsTurnsWhileOtherMapsAreSearched()
    {
        string[] others = ["altgard", "elthen", "poeta"]; // most recently found first
        var misses = MapTrackingService.MissesBeforeOtherMaps;
        var tried = new List<string?>();
        for (var tick = 0; tick < 12; tick++)
        {
            var candidate = MapTrackingService.NextCandidate("verteron", misses, others, tick);
            tried.Add(candidate);
            if (candidate == "verteron")
                misses++; // still not found on the current map
        }

        Assert.Equal(6, tried.Count(t => t == "verteron")); // every other frame
        Assert.Equal(["altgard", "elthen", "poeta", "altgard", "elthen", "poeta"], tried.Where(t => t != "verteron"));
    }

    [Fact]
    public void OnlyTheCurrentMapBeforeTheFirstMisses() =>
        Assert.All(Enumerable.Range(0, 6), tick => Assert.Equal("verteron", MapTrackingService.NextCandidate("verteron", 2, ["altgard"], tick)));
}

[Collection("Settings file")] // writes targets.json and settings.json of the test data folder
public sealed class MapZoomPlausibilityTests
{
    [Theory] // live log 2026-10-03: normal zoom 0.89 px per map unit
    [InlineData(14, 0.55, false)] // fading map, 14 pairs at another zoom: rejected
    [InlineData(15, 0.85, true)]  // narrow but same zoom: kept
    [InlineData(600, 0.45, true)] // user zoomed out, many pairs: kept
    public void NarrowFixesMustKeepTheZoom(int inliers, double zoom, bool plausible) =>
        Assert.Equal(plausible, MapTrackingService.PlausibleZoom(inliers, zoom, 0.89));

    [Fact]
    public void ShiftRightClickChainsTargetsAndRemovingOneKeepsTheChain()
    {
        File.Delete(AppPaths.TargetsFile);
        try
        {
            var targets = new MapTargetsService();
            targets.Toggle("altgard", 100, 100, "A", "Pets · Fera", null);
            targets.Toggle("altgard", 200, 200, "B", "Pets · Fera", null, chain: true);
            targets.Toggle("altgard", 300, 300, "C", "Pets · Fera", null, chain: true);
            var (a, b, c) = (targets.Targets[0], targets.Targets[1], targets.Targets[2]);
            Assert.Null(a.After);          // player → A
            Assert.Equal(a.Id, b.After);   // A → B
            Assert.Equal(b.Id, c.After);   // B → C

            targets.Remove(b.Id);           // C now continues from A
            Assert.Equal(a.Id, targets.Targets.Single(t => t.Name == "C").After);
            Assert.Equal(a.Id, new MapTargetsService().Targets.Single(t => t.Name == "C").After); // saved
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
        }
    }

    /// <summary>User request 2026-10-06: a pet marked by hand stays until it reaches its next level.</summary>
    [Fact]
    public void AHandMarkedPetStaysUntilItsNextLevel()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.ProgressFile);
        try
        {
            var settings = new SettingsService();
            var progress = new ProgressService();
            progress.AddSouls("kailin", 5, "test", "");    // level 1
            var targets = new MapTargetsService();
            using var cleaner = new FarmedTargetCleaner(targets, progress, settings);
            targets.Toggle("altgard", 100, 100, "Kailin", "Pets · Fera", null, petId: "kailin");
            var levelTwo = progress.Thresholds.Steps.Take(2).Sum();

            progress.AddSouls("kailin", levelTwo - 6, "test", "");   // one short of level 2: stays
            Assert.Single(targets.Targets);
            progress.AddSouls("kailin", 1, "test", "");               // level 2 reached: mark removed
            Assert.Empty(targets.Targets);
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.ProgressFile);
        }
    }

    [Fact]
    public void SoulCountShowsTheCurrentLevel()
    {
        File.Delete(AppPaths.ProgressFile);
        try
        {
            var progress = new ProgressService();
            var levelTwo = progress.Thresholds.Steps.Take(2).Sum();
            progress.AddSouls("kailin", levelTwo - 1, "test", "");
            Assert.Equal($"{progress.Thresholds.Steps[1] - 1}/{progress.Thresholds.Steps[1]}", progress.SoulCount("kailin")); // e.g. 24/25
            progress.AddSouls("kailin", progress.Thresholds.Max, "test", "");
            Assert.Equal("max", progress.SoulCount("kailin"));
        }
        finally
        {
            File.Delete(AppPaths.ProgressFile);
        }
    }

    [Fact]
    public void AMarkVanishesWhenItsPetIsFarmedIntoAHiddenLevel()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.ProgressFile);
        try
        {
            var settings = new SettingsService();
            settings.Update(s => s.HideLevel1 = true);
            var progress = new ProgressService();
            var targets = new MapTargetsService();
            using var cleaner = new FarmedTargetCleaner(targets, progress, settings);
            targets.Toggle("altgard", 100, 100, "Kailin", "Pets · Fera", null, petId: "kailin");
            targets.Toggle("altgard", 200, 200, "Ursus", "Pets · Fera", null, petId: "ursus");

            progress.AddSouls("kailin", 4, "test", "");   // 4/5: still locked, still shown
            Assert.Equal(2, targets.Targets.Count);
            progress.AddSouls("kailin", 1, "test", "");   // level 1 reached, hidden on the map: mark removed
            Assert.Equal(["Ursus"], targets.Targets.Select(t => t.Name));
        }
        finally
        {
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.ProgressFile);
            new SettingsService().Update(s => s.HideLevel1 = false);
        }
    }
}
