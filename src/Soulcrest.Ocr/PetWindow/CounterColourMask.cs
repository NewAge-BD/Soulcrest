using OpenCvSharp;

namespace Soulcrest.Ocr.PetWindow;

/// <summary>Calibrates fraction ink from native denominator pixels, before interpolation mixes in the portrait.</summary>
internal static class CounterColourMask
{
    internal static Mat? Create(Mat bgr, Rect rect)
    {
        rect = Intersect(bgr, rect);
        if (rect.Width < 6 || rect.Height < 6) return null;
        var margin = Math.Max(2, rect.Height / 4);
        var left = rect.Height * 6 / 5;
        var area = Intersect(bgr, new Rect(rect.X - left, rect.Y - margin,
            rect.Width + left + margin, rect.Height + 2 * margin));
        using var part = new Mat(bgr, area);
        using var lab = new Mat();
        using var gray = new Mat();
        Cv2.CvtColor(part, lab, ColorConversionCodes.BGR2Lab);
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);

        // The right half contains the denominator. Sampling the entire expanded crop allowed white
        // fur/crystals to set the old reference. Use the bright neutral cores, not antialiased edges.
        var samples = new List<Vec3b>();
        for (var y = rect.Y - area.Y; y < rect.Bottom - area.Y; y++)
            for (var x = rect.X + rect.Width / 2 - area.X; x < rect.Right - area.X; x++)
            {
                var p = part.At<Vec3b>(y, x);
                var min = Math.Min(p.Item0, Math.Min(p.Item1, p.Item2));
                var max = Math.Max(p.Item0, Math.Max(p.Item1, p.Item2));
                if (min >= 150 && max - min <= 45) samples.Add(p);
            }
        if (samples.Count < 5) return null;
        static int Brightness(Vec3b p) => Math.Min(p.Item0, Math.Min(p.Item1, p.Item2));
        var cutoff = samples.Select(Brightness).Order().ElementAt((int)(samples.Count * .85));
        var cores = samples.Where(p => Brightness(p) >= cutoff).ToArray();
        static byte Median(IEnumerable<byte> values) => values.Order().ElementAt(values.Count() / 2);
        using var reference = new Mat(1, 1, MatType.CV_8UC3);
        reference.Set(0, 0, new Vec3b(Median(cores.Select(p => p.Item0)), Median(cores.Select(p => p.Item1)), Median(cores.Select(p => p.Item2))));
        using var referenceLab = new Mat();
        Cv2.CvtColor(reference, referenceLab, ColorConversionCodes.BGR2Lab);
        var colour = referenceLab.At<Vec3b>(0, 0);

        // A small tolerance retains edge coverage. The nearby dark rim separates text from flat white
        // portrait patches. Keep native pixel topology: enlarging first can merge a digit with fur.
        using var darkest = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
        Cv2.Erode(gray, darkest, kernel);
        using var ink = new Mat(area.Height, area.Width, MatType.CV_8UC1, Scalar.Black);
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++)
            {
                var p = lab.At<Vec3b>(y, x);
                int dl = p.Item0 - colour.Item0, da = p.Item1 - colour.Item1, db = p.Item2 - colour.Item2;
                if (dl * dl + da * da + db * db < 40 * 40 && gray.At<byte>(y, x) - darkest.At<byte>(y, x) >= 35)
                    ink.Set(y, x, (byte)255);
            }

        using var labels = new Mat();
        using var stats = new Mat();
        using var centres = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centres, PixelConnectivity.Connectivity8);
        var glyphs = new List<(int Label, Rect Bounds)>();
        for (var i = 1; i < count; i++)
        {
            var r = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            if (r.X > 0 && r.Y > 0 && r.Right < area.Width && r.Bottom < area.Height
                && r.Height >= rect.Height * .32 && r.Height <= rect.Height * .92
                && r.Width <= rect.Height * .95 && stats.At<int>(i, 4) >= 3)
                glyphs.Add((i, r));
        }
        if (glyphs.Count < 3) return null;
        // Anchor the baseline at the right-aligned denominator. A pale portrait fragment left of "3/75"
        // can resemble a leading 4; it has a different baseline/height and must not enter the text run.
        var anchor = glyphs.Where(g => g.Bounds.X + area.X >= rect.X + rect.Width / 2)
            .OrderByDescending(g => g.Bounds.Right).FirstOrDefault();
        if (anchor.Label == 0) return null;
        var line = glyphs.Where(g => g.Bounds.Height >= anchor.Bounds.Height * .75
                && g.Bounds.Height <= anchor.Bounds.Height * 1.45
                && Math.Abs(g.Bounds.Bottom - anchor.Bounds.Bottom) <= Math.Max(2, anchor.Bounds.Height * .15))
            .OrderByDescending(g => g.Bounds.Right).ToList();
        var keep = new HashSet<int>();
        var edge = anchor.Bounds.Right;
        foreach (var g in line)
        {
            if (edge - g.Bounds.Right > anchor.Bounds.Height) break;
            keep.Add(g.Label);
            edge = g.Bounds.X;
        }
        if (keep.Count < 3) return null;
        var result = new Mat(area.Height, area.Width, MatType.CV_8UC1, Scalar.White);
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++)
                if (keep.Contains(labels.At<int>(y, x))) result.Set(y, x, (byte)0);
        return result;
    }

    private static Rect Intersect(Mat image, Rect rect) => rect & new Rect(0, 0, image.Width, image.Height);
}
