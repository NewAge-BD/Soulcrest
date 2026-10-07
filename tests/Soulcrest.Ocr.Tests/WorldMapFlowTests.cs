using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// The world map dragged while its panels (list, top bar, buttons) stand still (user report 2026-10-07:
/// the overlay trailed far behind the world map). Frames at the quarter scale of the tracking loop. Seeded
/// everywhere, the standing panels pulled the fit to 2 to 42 px of the 66 px, depending on the panels' size.
/// </summary>
public sealed class WorldMapFlowTests
{
    private const int Steps = 12, Dx = 6, Dy = 3;

    [Fact]
    public void FollowsTheDraggedMapNotItsPanels()
    {
        var (tx, ty) = Drag();
        Assert.InRange(tx, (Steps - 1) * Dx - 3, (Steps - 1) * Dx + 3);
        Assert.InRange(ty, (Steps - 1) * Dy - 3, (Steps - 1) * Dy + 3);
    }

    private static (double Tx, double Ty) Drag()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", "live-2026-10-03-worldmap-drift-screen.png");
        using var full = Cv2.ImRead(file, ImreadModes.Grayscale);
        using var screen = new Mat();
        Cv2.Resize(full, screen, new OpenCvSharp.Size(), 0.25, 0.25, InterpolationFlags.Area);
        var w = screen.Width;
        var h = screen.Height;
        // Panels of the screenshot: list left, bar on top, buttons right.
        var panels = new[] { new Rect(0, 0, (int)(w * 0.17), h), new Rect(0, 0, w, (int)(h * 0.09)), new Rect((int)(w * 0.95), 0, w - (int)(w * 0.95), h) };
        using var flow = new MapFlow { SeedArea = MapFlow.WorldMapArea(w, h) };
        double tx = 0, ty = 0;
        for (var i = 0; i < Steps; i++)
        {
            using var m = Mat.FromArray(new double[,] { { 1, 0, i * Dx }, { 0, 1, i * Dy } });
            using var frame = new Mat();
            Cv2.WarpAffine(screen, frame, m, screen.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            foreach (var r in panels)
            {
                using var from = new Mat(screen, r);
                using var to = new Mat(frame, r);
                from.CopyTo(to);
            }
            if (flow.Step(frame) is { } shift)
            {
                tx += shift.Tx;
                ty += shift.Ty;
            }
        }
        return (tx, ty);
    }
}
