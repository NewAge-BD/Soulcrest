using System.Diagnostics;
using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Cost of one in-game overlay picture on the world map with real data (SOULCREST_OVERLAY_COST=1).</summary>
public sealed class OverlayCostExploration(ITestOutputHelper output)
{
    [Fact]
    public void WorldMapPictureCost()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_COST") != "1")
            return;
        var mapdata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "imports", "generated", "mapdata"));
        var spawns = MapPetMarkers.For(mapdata, "altgard");
        using var icon = new Bitmap(26, 26);
        var pets = spawns.Select(s => new RouteOverlayForm.PetSymbol(s.X, s.Y, icon, "St. 1 · 6/25", false)).ToList();
        // Altgard (8192 world units) fills the 1440 px high screen.
        var scale = 1440.0 / 8192;
        var fix = new MapFix(scale, 0, 560, 0, scale, 0, 100, 100, 0.3, false);
        var placement = new MapPlacement("altgard", fix, 1, new Rectangle(0, 0, 2560, 1440), default, 1, true);
        RouteOverlayForm.Draw(placement, [], pets).Dispose();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            using var bitmap = RouteOverlayForm.Draw(placement, [], pets);
            var h = bitmap.GetHbitmap(Color.FromArgb(0));
            NativeMethods.DeleteObject(h);
        }
        output.WriteLine($"{pets.Count} Pets: {watch.Elapsed.TotalMilliseconds / 5:0} ms pro Bild (Zeichnen + HBITMAP)");
        watch.Restart();
        for (var i = 0; i < 5; i++) RouteOverlayForm.Draw(placement, [], []).Dispose();
        output.WriteLine($"leer zeichnen: {watch.Elapsed.TotalMilliseconds / 5:0} ms");
        watch.Restart();
        for (var i = 0; i < 5; i++) RouteOverlayForm.Draw(placement, [], pets).Dispose();
        output.WriteLine($"mit Pets zeichnen: {watch.Elapsed.TotalMilliseconds / 5:0} ms");
        watch.Restart();
        for (var i = 0; i < 5; i++) RouteOverlayForm.RenderFrame(placement, [], pets).Dispose();
        output.WriteLine($"DIB-Section direkt (wie im Overlay): {watch.Elapsed.TotalMilliseconds / 5:0} ms");
    }
}
