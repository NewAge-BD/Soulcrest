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
    private readonly List<(string PetId, Point2f[] Points, Mat Descriptors)> _references = [];
    private readonly object _gate = new();

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
        AddReference(petId, image);
    }

    public void AddReference(string petId, Mat image)
    {
        using var bgr = Flatten(image);
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(160, 160 * bgr.Height / Math.Max(1, bgr.Width)));
        var described = Describe(resized);
        if (described is null)
            return;
        lock (_gate)
            _references.Add((petId, described.Value.Points, described.Value.Descriptors));
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

        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(string PetId, Point2f[] Points, Mat Descriptors)> references;
        lock (_gate)
            references = [.. _references];
        using var matcher = new BFMatcher(NormTypes.L2);
        foreach (var (petId, referencePoints, reference) in references)
        {
            var good = new List<DMatch>();
            foreach (var pair in matcher.KnnMatch(descriptors, reference, 2))
            {
                if (pair.Length == 2 && pair[0].Distance < 0.75f * pair[1].Distance)
                    good.Add(pair[0]);
            }
            var score = ConsistentMatches(good, points, referencePoints);
            if (!best.TryGetValue(petId, out var existing) || score > existing)
                best[petId] = score;
        }
        var ranked = best.OrderByDescending(b => b.Value).Take(2).ToList();
        return ranked.Count switch
        {
            0 => new PortraitMatch(null, 0, null, 0),
            1 => new PortraitMatch(ranked[0].Key, ranked[0].Value, null, 0),
            _ => new PortraitMatch(ranked[0].Key, ranked[0].Value, ranked[1].Key, ranked[1].Value),
        };
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
            foreach (var (_, _, descriptors) in _references)
                descriptors.Dispose();
            _references.Clear();
        }
    }
}
