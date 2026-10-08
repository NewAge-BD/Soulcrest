using OpenCvSharp;
using Soulcrest.Ocr;
using Rectangle = System.Drawing.Rectangle;

namespace Soulcrest.App.Services;

/// <summary>Gold circle AND check from the user's completion symbol, independent of map background.</summary>
internal static class ExplorationCompletionSymbol
{
    private static readonly Lazy<Mat> Template = new(Load);

    internal static IReadOnlyList<Rectangle> Find(Bitmap capture, ExplorationLayout layout)
    {
        using var bgr = BitmapMat.ToBgr(capture);
        using var gold = Gold(bgr);
        // Completion symbols form a column to the right of names. Do not search map markers.
        var left = (int)(layout.Bounds.Width * .60);
        var right = Math.Min(capture.Width, (int)(layout.Bounds.Width * .85));
        var top = Math.Max(0, layout.Bounds.Top - layout.CaptureBounds.Top);
        if (right <= left || top >= capture.Height) return [];
        using var column = new Mat(gold, new Rect(left, top, right - left, capture.Height - top));
        var hits = new List<(Rectangle Bounds, double Score)>();
        // Both UI scale and window height may change the supplied symbol's size.
        var heightScale = layout.Bounds.Bottom / .98 / 1440;
        foreach (var scale in Enumerable.Range(10, 21).Select(step => step / 20d))
        {
            using var scaled = new Mat();
            Cv2.Resize(Template.Value, scaled, new OpenCvSharp.Size(), heightScale * scale, heightScale * scale, InterpolationFlags.Area);
            if (scaled.Width >= column.Width || scaled.Height >= column.Height || scaled.Width < 10) continue;
            using var result = new Mat();
            Cv2.MatchTemplate(column, scaled, result, TemplateMatchModes.CCoeffNormed);
            for (var count = 0; count < 40; count++)
            {
                Cv2.MinMaxLoc(result, out _, out var score, out _, out var point);
                if (!double.IsFinite(score) || score < .70) break;
                hits.Add((new Rectangle(point.X + left + layout.CaptureBounds.X,
                    point.Y + top + layout.CaptureBounds.Y, scaled.Width, scaled.Height), score));
                Cv2.Rectangle(result, new Rect(Math.Max(0, point.X - scaled.Width / 2), Math.Max(0, point.Y - scaled.Height / 2),
                    Math.Min(result.Width - Math.Max(0, point.X - scaled.Width / 2), scaled.Width),
                    Math.Min(result.Height - Math.Max(0, point.Y - scaled.Height / 2), scaled.Height)), Scalar.All(-1), -1);
            }
        }
        var found = new List<Rectangle>();
        foreach (var hit in hits.OrderByDescending(h => h.Score))
            if (!found.Any(r => Math.Abs(CenterY(r) - CenterY(hit.Bounds)) < Math.Max(r.Height, hit.Bounds.Height) * .65))
                found.Add(hit.Bounds);
        return found.OrderBy(r => r.Top).ToArray();
    }

    internal static double CenterY(Rectangle bounds) => bounds.Top + bounds.Height / 2d;

    private static Mat Gold(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var gold = new Mat();
        Cv2.InRange(hsv, new Scalar(15, 45, 100), new Scalar(40, 255, 255), gold);
        return gold;
    }

    private static Mat Load()
    {
        using var stream = typeof(ExplorationCompletionSymbol).Assembly.GetManifestResourceStream("Soulcrest.ExplorationComplete.png")
            ?? throw new InvalidOperationException("Missing exploration completion symbol.");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        using var image = Cv2.ImDecode(memory.ToArray(), ImreadModes.Color);
        return Gold(image);
    }
}
