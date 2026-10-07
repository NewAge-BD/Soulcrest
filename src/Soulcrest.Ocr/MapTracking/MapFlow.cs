using OpenCvSharp;

namespace Soulcrest.Ocr.MapTracking;

/// <summary>Shift of the picture between two frames: p' = (A B; C D)·p + (Tx, Ty).</summary>
public readonly record struct FrameShift(double A, double B, double Tx, double C, double D, double Ty)
{
    public static FrameShift Identity { get; } = new(1, 0, 0, 0, 1, 0);

    /// <summary>First this, then <paramref name="next"/>.</summary>
    public FrameShift Then(FrameShift next) => new(
        next.A * A + next.B * C, next.A * B + next.B * D, next.A * Tx + next.B * Ty + next.Tx,
        next.C * A + next.D * C, next.C * B + next.D * D, next.C * Tx + next.D * Ty + next.Ty);

    /// <summary>A map placement (reference -> frame) carried along by this shift.</summary>
    public MapFix Apply(MapFix fix) => fix with
    {
        A = this.A * fix.A + B * fix.C, B = this.A * fix.B + B * fix.D, Tx = this.A * fix.Tx + B * fix.Ty + this.Tx,
        C = this.C * fix.A + D * fix.C, D = this.C * fix.B + D * fix.D, Ty = this.C * fix.Tx + D * fix.Ty + this.Ty,
    };
}

/// <summary>
/// How far the in-game map moved between two consecutive frames (optical flow, re-implementation of the
/// Map Overlay technique): a few hundred corners tracked with pyramidal Lucas-Kanade, then a RANSAC
/// similarity fit. A few milliseconds per frame instead of ~100 ms for a full detection, so the overlay
/// can follow the map at 60 fps; detection only corrects the drift now and then.
/// One thread, consecutive frames; <see cref="Reset"/> when the sequence breaks.
/// </summary>
public sealed class MapFlow : IDisposable
{
    private readonly int _maxPoints;
    private readonly int _minPoints;
    private Mat? _previous;
    private Point2f[]? _points;

    public MapFlow(int maxPoints = 200)
    {
        _maxPoints = Math.Max(40, maxPoints);
        _minPoints = Math.Max(12, _maxPoints / 5);
    }

    public int Points => _points?.Length ?? 0;

    /// <summary>
    /// Where new points are picked (null = everywhere). Points that wander out of it are kept while the
    /// fit agrees with them.
    /// </summary>
    public Rect? SeedArea { get; set; }

    /// <summary>
    /// The map part of the open world map: without the list on the left, the bar on top and the buttons on
    /// the right. Those stand still while the map is dragged, and their text has the strongest corners, so
    /// the fit used to follow them and measured no movement at all (user report 2026-10-07: the overlay
    /// trailed behind; probe on the world-map screenshot: 0 px instead of 66 px).
    /// </summary>
    public static Rect WorldMapArea(int width, int height)
    {
        var left = (int)(width * 0.18);
        var top = (int)(height * 0.10);
        var right = (int)(width * 0.94);
        return new Rect(left, top, right - left, height - top);
    }

    public void Reset()
    {
        _previous?.Dispose();
        _previous = null;
        _points = null;
    }

    /// <summary>Shift previous frame -> this one; null when it could not be measured (first frame, too few points).</summary>
    public FrameShift? Step(Mat gray)
    {
        var previous = _previous;
        _previous = gray.Clone();
        if (previous is null)
        {
            _points = Seed(gray);
            return null;
        }
        using (previous)
        {
            // Points ran out on the last step: seed on the previous frame and track straight through.
            _points ??= Seed(previous);
            if (_points is null)
                return null;

            var next = new Point2f[_points.Length];
            Cv2.CalcOpticalFlowPyrLK(previous, gray, _points, ref next, out var status, out _,
                new OpenCvSharp.Size(21, 21), 3, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.Count, 20, 0.03));
            var from = new List<Point2f>();
            var to = new List<Point2f>();
            for (var i = 0; i < status.Length; i++)
            {
                if (status[i] == 0)
                    continue;
                from.Add(_points[i]);
                to.Add(next[i]);
            }
            if (from.Count < _minPoints)
            {
                _points = null;
                return null;
            }

            using var fromArray = InputArray.Create(from);
            using var toArray = InputArray.Create(to);
            using var mask = new Mat();
            using var transform = Cv2.EstimateAffinePartial2D(fromArray, toArray, mask, RobustEstimationAlgorithms.RANSAC, 2.0, 500, 0.99, 5);
            if (transform is null || transform.Empty() || Cv2.CountNonZero(mask) < _minPoints)
            {
                _points = null;
                return null;
            }
            mask.GetArray(out byte[] keep);
            _points = to.Where((_, i) => keep[i] != 0).ToArray();
            if (_points.Length < _maxPoints / 2)
                _points = Seed(gray) ?? _points;
            return new FrameShift(
                transform.At<double>(0, 0), transform.At<double>(0, 1), transform.At<double>(0, 2),
                transform.At<double>(1, 0), transform.At<double>(1, 1), transform.At<double>(1, 2));
        }
    }

    private Point2f[]? Seed(Mat gray)
    {
        using var mask = new Mat();
        if (SeedArea is { } area && (area & new Rect(0, 0, gray.Width, gray.Height)) is { Width: > 0, Height: > 0 } inside)
        {
            mask.Create(gray.Size(), MatType.CV_8UC1);
            mask.SetTo(Scalar.All(0));
            using var seed = new Mat(mask, inside);
            seed.SetTo(Scalar.All(255));
        }
        var corners = Cv2.GoodFeaturesToTrack(gray, _maxPoints, 0.01, 12, mask, 7, false, 0.04);
        return corners.Length >= _minPoints ? corners : null;
    }

    public void Dispose() => Reset();
}
