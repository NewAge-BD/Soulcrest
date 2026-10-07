using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>Optical flow carries the in-game overlay between detections (60 fps, docs/MAP_TRACKING.md).</summary>
public sealed class MapFlowTests
{
    [Fact]
    public void MeasuresTheShiftOfTheMap()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-tracking", "live-2026-10-03-region.png");
        if (!File.Exists(file))
            return;
        using var image = Cv2.ImRead(file, ImreadModes.Grayscale);
        using var first = new Mat(image, new Rect(20, 20, 440, 400)).Clone();
        using var second = new Mat(image, new Rect(26, 17, 440, 400)).Clone(); // view moved right 6, up 3 -> picture moves left 6, down 3
        using var flow = new MapFlow();

        Assert.Null(flow.Step(first)); // first frame only seeds
        var shift = flow.Step(second);

        Assert.NotNull(shift);
        Assert.InRange(shift.Value.Tx, -6.5, -5.5);
        Assert.InRange(shift.Value.Ty, 2.5, 3.5);
        Assert.InRange(shift.Value.A, 0.99, 1.01);
    }

    [Fact]
    public void ShiftCarriesThePlacement()
    {
        var fix = new MapFix(2, 0, 100, 0, 2, 50, 30, 40, 0.3, false);
        var moved = new FrameShift(1, 0, -6, 0, 1, 3).Apply(fix);
        Assert.Equal(new Point2d(206 - 6, 110 + 3), new Point2d(Math.Round(moved.ReferenceToFrame(new Point2d(53, 30)).X), Math.Round(moved.ReferenceToFrame(new Point2d(53, 30)).Y)));
        var twice = FrameShift.Identity.Then(new FrameShift(1, 0, 2, 0, 1, 0)).Then(new FrameShift(1, 0, 3, 0, 1, 1));
        Assert.Equal((5.0, 1.0), (twice.Tx, twice.Ty));
    }
}
