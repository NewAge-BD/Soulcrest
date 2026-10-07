using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace Soulcrest.Ocr.PetWindow;

public sealed record PortraitMatch(string? PetId, int Score, string? SecondId, int SecondScore)
{
    // Scores are geometrically consistent matches (RANSAC inliers). Fixtures: wrong pets score ≤7, the
    // runner-up ≤6 (except art variants of the same model); greyed-out locked pets 9–24, owned 13–99.
    /// <summary>Confident: many consistent features and clearly ahead of the runner-up.</summary>
    public bool IsConfident => PetId is not null && Score >= 20 && Score >= 3 * Math.Max(1, SecondScore);
    public bool IsPlausible => PetId is not null && Score >= 9 && Score >= 2 * Math.Max(1, SecondScore);
}

/// <summary>
/// Identifies pet card portraits by SIFT feature matching against reference portraits: the
/// circular pet icons of the map data (same game art) plus portraits learned from the game window.
/// Only matches that agree on one placement of the reference count (see <see cref="ConsistentMatches"/>).
/// </summary>
public sealed class PortraitMatcher : IDisposable
{
    // Thread-safe: every call uses its own SIFT and matcher, the references are only read (cards of one
    // capture are matched in parallel; one after another 15 cards took 3.8 s with 188 references).
    private readonly List<(string PetId, Point2f[] Points, Mat Descriptors, Mat? Colour)> _references = [];
    private readonly object _gate = new();
    // Colour variants: the map-data portrait file without "_cv01" etc. names the art (Stone Spirit
    // "…eartheleod_01", Odyle Stone Spirit "…eartheleod_01_cv01"); the first coloured picture of a pet is
    // its hue reference.
    private readonly Dictionary<string, string> _art = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mat> _hue = new(StringComparer.Ordinal);

    public int ReferenceCount
    {
        get { lock (_gate) return _references.Count; }
    }

    /// <summary>Adds a reference image (PNG with alpha allowed; transparent parts become dark grey like the card).</summary>
    public void AddReference(string petId, string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (image.Empty())
            return;
        lock (_gate)
            _art.TryAdd(petId, ArtKey(imagePath));
        AddReference(petId, image);
    }

    /// <summary>"…/ut_vehicle_portrait_eartheleod_01_cv01.png" → "ut_vehicle_portrait_eartheleod_01".</summary>
    public static string ArtKey(string imagePath) =>
        System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(imagePath), @"_cv\d+$", "");

    public void AddReference(string petId, Mat image)
    {
        using var bgr = Flatten(image);
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(160, 160 * bgr.Height / Math.Max(1, bgr.Width)));
        var described = Describe(resized);
        if (described is null)
            return;
        var colour = ColourSignature(resized);
        lock (_gate)
        {
            _references.Add((petId, described.Value.Points, described.Value.Descriptors, colour));
            if (colour is not null)
                _hue.TryAdd(petId, colour);
        }
    }

    public PortraitMatch Match(Mat portrait)
    {
        using var bgr = Flatten(portrait);
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(bgr.Width * 2, bgr.Height * 2));
        var described = Describe(resized);
        if (described is null)
            return new PortraitMatch(null, 0, null, 0);
        var (points, descriptors) = described.Value;
        using var _ = descriptors;

        var best = new Dictionary<string, (int Score, Mat? Colour)>(StringComparer.Ordinal);
        List<(string PetId, Point2f[] Points, Mat Descriptors, Mat? Colour)> references;
        lock (_gate)
            references = [.. _references];
        using var matcher = new BFMatcher(NormTypes.L2);
        foreach (var (petId, referencePoints, reference, colour) in references)
        {
            var good = new List<DMatch>();
            foreach (var pair in matcher.KnnMatch(descriptors, reference, 2))
            {
                if (pair.Length == 2 && pair[0].Distance < 0.75f * pair[1].Distance)
                    good.Add(pair[0]);
            }
            var score = ConsistentMatches(good, points, referencePoints);
            if (!best.TryGetValue(petId, out var existing) || score > existing.Score)
                best[petId] = (score, colour);
        }
        var ranked = best.OrderByDescending(b => b.Value.Score).Take(2).ToList();
        var byShape = ranked.Count switch
        {
            0 => new PortraitMatch(null, 0, null, 0),
            1 => new PortraitMatch(ranked[0].Key, ranked[0].Value.Score, null, 0),
            _ => new PortraitMatch(ranked[0].Key, ranked[0].Value.Score, ranked[1].Key, ranked[1].Value.Score),
        };
        if (ranked.Count > 0 && Variants(ranked[0].Key) is { Count: > 1 } variants)
        {
            using var colour = ColourSignature(resized);
            return ByColour(byShape, colour, variants);
        }
        return byShape;
    }

    /// <summary>The pet and its colour variants (same art), each with its hue reference; null without variants.</summary>
    private List<(string PetId, Mat? Hue)>? Variants(string petId)
    {
        lock (_gate)
        {
            if (!_art.TryGetValue(petId, out var art))
                return null;
            return _art.Where(a => a.Value == art).Select(a => (a.Key, _hue.GetValueOrDefault(a.Key))).ToList();
        }
    }

    /// <summary>
    /// Colour variants (21 pets in the map data, e.g. Stone Spirit blue, Odyle Stone Spirit green) are one
    /// picture recoloured, and the shape matching works on grey: a real Stone Spirit card recoloured green
    /// still scored 71 to 3 for Stone Spirit, and a colleague's scan took one for the other (2026-10-07).
    /// Among the variants a clear hue difference decides. A grey (locked) portrait or hues too alike (Agrint,
    /// Ashen Agrint) keep the shape's result.
    /// </summary>
    private static PortraitMatch ByColour(PortraitMatch byShape, Mat? portrait, List<(string PetId, Mat? Hue)> variants)
    {
        if (portrait is null || variants.Any(v => v.Hue is null))
            return byShape;
        var ranked = variants.Select(v => (v.PetId, Distance: Cv2.CompareHist(portrait, v.Hue!, HistCompMethods.Bhattacharyya)))
            .OrderBy(v => v.Distance).ToList();
        if (ranked[1].Distance - ranked[0].Distance < ClearHueDifference)
            return byShape;
        return new PortraitMatch(ranked[0].PetId, byShape.Score, ranked[1].PetId, Math.Min(byShape.Score / 3, ranked[0].PetId == byShape.PetId ? byShape.SecondScore : int.MaxValue));
    }

    private const double ClearHueDifference = 0.15;

    /// <summary>Hue histogram of the clearly coloured pixels; null for a (nearly) grey picture.</summary>
    public static Mat? ColourSignature(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 70, 50), new Scalar(180, 255, 255), mask);
        if (Cv2.CountNonZero(mask) < bgr.Rows * bgr.Cols / 10)
            return null;
        var hist = new Mat();
        Cv2.CalcHist([hsv], [0], mask, hist, 1, [30], [new Rangef(0, 180)]);
        Cv2.Normalize(hist, hist, 1, 0, NormTypes.L1);
        return hist;
    }

    /// <summary>
    /// Matches that agree on one placement (scale, rotation, shift) of the reference in the portrait.
    /// Raw descriptor matches alone let repetitive art win: a ring-shaped orb scored 63 against the
    /// spider icon (nocounter fixture), mostly via the icons' circular frame.
    /// </summary>
    private static int ConsistentMatches(List<DMatch> good, Point2f[] query, Point2f[] reference)
    {
        if (good.Count < 4)
            return good.Count / 2;
        var from = InputArray.Create(good.Select(m => reference[m.TrainIdx]).ToArray());
        var to = InputArray.Create(good.Select(m => query[m.QueryIdx]).ToArray());
        using var inliers = new Mat();
        using var transform = Cv2.EstimateAffinePartial2D(from, to, inliers, RobustEstimationAlgorithms.RANSAC, 6);
        if (transform is null || transform.Empty())
            return 0;
        // Card portraits show the icon art enlarged or at a similar size, never mirrored or tiny.
        double a = transform.At<double>(0, 0), b = transform.At<double>(1, 0);
        var scale = Math.Sqrt(a * a + b * b);
        if (scale < 0.3 || scale > 6)
            return 0;
        return Cv2.CountNonZero(inliers);
    }

    private static (Point2f[] Points, Mat Descriptors)? Describe(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var descriptors = new Mat();
        using var sift = SIFT.Create();
        sift.DetectAndCompute(gray, null, out var keypoints, descriptors);
        if (descriptors.Empty())
        {
            descriptors.Dispose();
            return null;
        }
        return (keypoints.Select(k => k.Pt).ToArray(), descriptors);
    }

    private static Mat Flatten(Mat image)
    {
        if (image.Channels() == 3)
            return image.Clone();
        if (image.Channels() == 1)
        {
            var bgr1 = new Mat();
            Cv2.CvtColor(image, bgr1, ColorConversionCodes.GRAY2BGR);
            return bgr1;
        }
        // BGRA: composite on the card's dark slate background (portraits are small, a pixel loop is fine).
        const int background = 40;
        int rows = image.Rows, cols = image.Cols;
        var result = new Mat(rows, cols, MatType.CV_8UC3);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var p = image.At<Vec4b>(y, x);
                var a = p.Item3 / 255.0;
                result.At<Vec3b>(y, x) = new Vec3b(
                    (byte)(p.Item0 * a + background * (1 - a)),
                    (byte)(p.Item1 * a + background * (1 - a)),
                    (byte)(p.Item2 * a + background * (1 - a)));
            }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, _, descriptors, colour) in _references)
            {
                descriptors.Dispose();
                colour?.Dispose();
            }
            _hue.Clear();
            _art.Clear();
            _references.Clear();
        }
    }
}
