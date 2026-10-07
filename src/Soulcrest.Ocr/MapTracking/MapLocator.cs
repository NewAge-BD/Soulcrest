using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;

namespace Soulcrest.Ocr.MapTracking;

/// <summary>
/// Placement of the reference map in a captured frame: frame = A · reference (similarity transform,
/// i.e. shift, zoom and rotation). <see cref="Inliers"/> = keypoint pairs that agree with it.
/// </summary>
/// <param name="InlierBounds">Frame area covered by the agreeing keypoints: tells a full-screen world map from a
/// small map window when both show the same map.</param>
public sealed record MapFix(double A, double B, double Tx, double C, double D, double Ty, int Inliers, int Matches, double Error, bool Local,
    Rect2d? InlierBounds = null)
{
    /// <summary>Frame pixels per reference pixel.</summary>
    public double Scale => Math.Sqrt(A * A + C * C);

    /// <summary>Reference pixel shown at a frame pixel (inverse transform).</summary>
    public Point2d FrameToReference(Point2d frame)
    {
        var det = A * D - B * C;
        var x = frame.X - Tx;
        var y = frame.Y - Ty;
        return new Point2d((D * x - B * y) / det, (-C * x + A * y) / det);
    }

    public Point2d ReferenceToFrame(Point2d reference) =>
        new(A * reference.X + B * reference.Y + Tx, C * reference.X + D * reference.Y + Ty);
}

public sealed record MapLocateInfo(int Keypoints, int Matches, int Inliers, bool Local);

/// <summary>
/// Finds the reference map in a frame of the in-game map (re-implementation of the Map Overlay
/// technique, docs/MAP_TRACKING.md):
/// <list type="bullet">
/// <item>global search: frame keypoints against all reference keypoints (FLANN, kd-tree), used for the
/// first frame and after the map was lost;</item>
/// <item>local search: once placed, only reference keypoints around the previous position take part
/// (brute force on that subset), faster and with fewer false matches;</item>
/// <item>Lowe ratio test, then RANSAC fit of a similarity transform.</item>
/// </list>
/// Not thread-safe: one caller (the tracking loop) at a time.
/// </summary>
public sealed class MapLocator : IDisposable
{
    public const int DefaultFrameFeatures = 2500;

    private readonly MapReference _reference;
    private readonly SIFT _sift;
    private readonly FlannBasedMatcher _global;
    private readonly BFMatcher _local = new(NormTypes.L2);
    private Rect2d? _roi;

    public MapLocator(MapReference reference, int frameFeatures = DefaultFrameFeatures)
    {
        _reference = reference;
        _sift = SIFT.Create(frameFeatures);
        _global = new FlannBasedMatcher(new KDTreeIndexParams(4), new SearchParams(64));
        _global.Add([reference.Descriptors]);
        _global.Train();
    }

    // Wrong maps reach at most 3 consistent pairs on the live capture, the right one 24–71: 12 is safe.
    public double Ratio { get; init; } = 0.8;
    public double ReprojectionThreshold { get; init; } = 4.0;
    public int MinInliers { get; init; } = 12;

    /// <summary>Ignore the yellow auto-path lines the game draws after a waypoint is set. Off: on the live
    /// capture it cost matches (227 vs 242), the lines are not on the reference and rarely match anyway.</summary>
    public bool MaskPathLines { get; init; }

    /// <summary>
    /// Mask without the game's yellow path lines (and a few pixels around them): they are not on the
    /// reference and, crossing the whole map after a waypoint is set, hid most of its texture
    /// (user report 2026-10-03: "nicht erkannt, als ich eine Markierung gemacht hab").
    /// </summary>
    public static Mat PathLineMask(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, bgr.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.BGR2HSV);
        if (bgr.Channels() == 4)
            Cv2.CvtColor(hsv, hsv, ColorConversionCodes.BGR2HSV);
        using var yellow = new Mat();
        Cv2.InRange(hsv, new Scalar(20, 110, 150), new Scalar(36, 255, 255), yellow);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(7, 7));
        Cv2.Dilate(yellow, yellow, kernel);
        var mask = new Mat();
        Cv2.BitwiseNot(yellow, mask);
        return mask;
    }

    public MapReference Reference => _reference;

    public MapLocateInfo LastInfo { get; private set; } = new(0, 0, 0, false);

    /// <summary>Forget the previous position (next search is global).</summary>
    public void Reset() => _roi = null;

    public MapFix? Locate(Mat frameBgr)
    {
        using var gray = new Mat();
        if (frameBgr.Channels() == 1)
            frameBgr.CopyTo(gray);
        else
            Cv2.CvtColor(frameBgr, gray, frameBgr.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var descriptors = new Mat();
        using var mask = MaskPathLines && frameBgr.Channels() >= 3 ? PathLineMask(frameBgr) : new Mat();
        _sift.DetectAndCompute(gray, mask, out var keypoints, descriptors);
        if (keypoints.Length < MinInliers || descriptors.Empty())
        {
            LastInfo = new MapLocateInfo(keypoints.Length, 0, 0, false);
            _roi = null;
            return null;
        }

        if (LocalSubset() is { } subset)
        {
            var local = Solve(keypoints, descriptors, subset);
            if (local is not null)
            {
                UpdateRoi(local, gray.Width, gray.Height);
                return local;
            }
            // Nothing near the last position (a sharp pan or zoom): search the whole map.
        }
        var fix = Solve(keypoints, descriptors, null);
        if (fix is not null)
            UpdateRoi(fix, gray.Width, gray.Height);
        else
            _roi = null;
        return fix;
    }

    private MapFix? Solve(KeyPoint[] keypoints, Mat descriptors, int[]? subset)
    {
        DMatch[][] pairs;
        if (subset is null)
        {
            pairs = _global.KnnMatch(descriptors, 2);
        }
        else
        {
            var data = new float[subset.Length * MapReference.DescriptorLength];
            for (var i = 0; i < subset.Length; i++)
                Array.Copy(_reference.DescriptorData, subset[i] * MapReference.DescriptorLength, data, i * MapReference.DescriptorLength, MapReference.DescriptorLength);
            using var train = Mat.FromPixelData(subset.Length, MapReference.DescriptorLength, MatType.CV_32FC1, data);
            pairs = _local.KnnMatch(descriptors, train, 2);
        }

        var source = new List<Point2f>();
        var target = new List<Point2f>();
        foreach (var pair in pairs)
        {
            if (pair.Length < 2 || pair[0].Distance >= Ratio * pair[1].Distance)
                continue;
            var index = subset is null ? pair[0].TrainIdx : subset[pair[0].TrainIdx];
            source.Add(_reference.Points[index]);
            target.Add(keypoints[pair[0].QueryIdx].Pt);
        }
        var info = new MapLocateInfo(keypoints.Length, source.Count, 0, subset is not null);
        LastInfo = info;
        if (source.Count < MinInliers)
            return null;

        using var from = InputArray.Create(source);
        using var to = InputArray.Create(target);
        using var mask = new Mat();
        using var transform = Cv2.EstimateAffinePartial2D(from, to, mask, RobustEstimationAlgorithms.RANSAC, ReprojectionThreshold, 3000, 0.995, 10);
        if (transform is null || transform.Empty())
            return null;
        var inliers = Cv2.CountNonZero(mask);
        LastInfo = info with { Inliers = inliers };
        double a = transform.At<double>(0, 0), b = transform.At<double>(0, 1), tx = transform.At<double>(0, 2);
        double c = transform.At<double>(1, 0), d = transform.At<double>(1, 1), ty = transform.At<double>(1, 2);
        var scale = Math.Sqrt(a * a + c * c);
        if (inliers < MinInliers || scale < 0.05 || scale > 20)
            return null;

        // Mean reprojection error of the inliers: the honest accuracy figure.
        mask.GetArray(out byte[] flags);
        double error = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i] == 0)
                continue;
            minX = Math.Min(minX, target[i].X);
            minY = Math.Min(minY, target[i].Y);
            maxX = Math.Max(maxX, target[i].X);
            maxY = Math.Max(maxY, target[i].Y);
            var p = source[i];
            var px = a * p.X + b * p.Y + tx - target[i].X;
            var py = c * p.X + d * p.Y + ty - target[i].Y;
            error += Math.Sqrt(px * px + py * py);
        }
        return new MapFix(a, b, tx, c, d, ty, inliers, source.Count, error / Math.Max(1, inliers), subset is not null,
            new Rect2d(minX, minY, maxX - minX, maxY - minY));
    }

    /// <summary>Where the frame lands on the reference, padded by half its size on each side.</summary>
    private void UpdateRoi(MapFix fix, int width, int height)
    {
        var corners = new[] { new Point2d(0, 0), new Point2d(width, 0), new Point2d(width, height), new Point2d(0, height) }
            .Select(fix.FrameToReference).ToList();
        double x0 = corners.Min(p => p.X), x1 = corners.Max(p => p.X), y0 = corners.Min(p => p.Y), y1 = corners.Max(p => p.Y);
        double mx = (x1 - x0) * 0.5, my = (y1 - y0) * 0.5;
        _roi = new Rect2d(x0 - mx, y0 - my, x1 - x0 + 2 * mx, y1 - y0 + 2 * my);
    }

    private int[]? LocalSubset()
    {
        if (_roi is not { } roi)
            return null;
        var points = _reference.Points;
        var subset = new List<int>();
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i].X >= roi.Left && points[i].X <= roi.Right && points[i].Y >= roi.Top && points[i].Y <= roi.Bottom)
                subset.Add(i);
        }
        return subset.Count >= 2 * MinInliers ? [.. subset] : null;
    }

    public void Dispose()
    {
        _sift.Dispose();
        _global.Dispose();
        _local.Dispose();
    }
}
