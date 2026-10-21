using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Ocr.Daevanion;

/// <summary>Occupied native cells; row/column remain one-based, including the start cell.</summary>
public sealed record DaevanionPatternCell(int Row, int Column, bool IsStart = false);
public sealed record DaevanionBoardPattern(string Family, IReadOnlyList<DaevanionPatternCell> Cells);
public sealed record DaevanionNodeAnchor(int Row, int Column, Rect Bounds, double Confidence);
/// <summary>Origin is the center of native cell (1,1), in the supplied image's pixels.</summary>
public sealed record DaevanionBoardDetection(string Family, Point2d Origin, double Spacing, double NodeSize,
    IReadOnlyList<DaevanionNodeAnchor> Nodes, double Confidence);

/// <summary>
/// Finds a complete board family from current pixels, without text, color-to-skill assumptions or a
/// stored screen rectangle. Native frame artwork confirms individual nodes; the distinct start
/// silhouette and occupied/empty-cell mask independently confirm the board. Covered nodes are omitted.
/// </summary>
public static class DaevanionBoardLocator
{
    private sealed record Candidate(Rect Bounds, Point2d Center);
    private sealed record Cell(int X, int Y, Candidate Candidate, double Confidence);
    private sealed record Hypothesis(DaevanionBoardPattern Pattern, Point2d Origin, double Score,
        IReadOnlyList<DaevanionNodeAnchor> Nodes);
    private sealed record FrameReference(double[] Values, double Norm, double[] RedValues, double RedNorm);
    private static readonly Lazy<(FrameReference[] Frames, Point2d[] Start, Point2d[] Border)> References = new(LoadReferences);
    private const int SamplesPerAxis = 13;
    private const double FirstSample = .16, SampleSpan = .68;

    /// <summary>
    /// Accepts 8-bit BGR/BGRA/gray game-window pixels. A previous geometry is only a spatial hypothesis:
    /// native frames, start ornament and occupied/empty-cell agreement must be proved by this image.
    /// Only a freshly confirmed identical family may retain its exact raster; otherwise search anew.
    /// </summary>
    public static DaevanionBoardDetection? Find(Mat image, IReadOnlyList<DaevanionBoardPattern> patterns,
        DaevanionBoardDetection? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(patterns);
        if (image.Empty() || image.Depth() != MatType.CV_8U || image.Channels() is not (1 or 3 or 4)
            || image.Width < 220 || image.Height < 220) return null;
        var valid = patterns.Where(IsValid).ToArray();
        if (valid.Length == 0) return null;
        using var gray = new Mat();
        if (image.Channels() == 1) image.CopyTo(gray);
        else Cv2.CvtColor(image, gray, image.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var red = image.Channels() == 1 ? null : new Mat();
        if (red is not null) Cv2.ExtractChannel(image, red, 2);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 8, 25);
        if (preferred is not null && ValidatePreferred(gray, red, edges, valid, preferred) is { } frozen)
            return frozen;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);
        var candidates = FindCandidates(closed);
        if (candidates.Count < 35) return null;
        // Width clusters avoid treating the nested border as a second node. No previous image adds evidence.
        var sizes = candidates.GroupBy(c => (int)Math.Round(c.Bounds.Width / 4d))
            .Select(g => (Size: Median(g.Select(c => (double)c.Bounds.Width)), Count: g.Count()))
            .Where(g => g.Count >= 16).OrderByDescending(g => g.Count).ThenByDescending(g => g.Size)
            .Take(4).Select(g => g.Size).ToArray();
        DaevanionBoardDetection? best = null;
        foreach (var size in sizes)
        {
            // A pulse can move adjacent native and luminous contours into neighboring width bins.
            // Merge their center hypotheses before fitting; neither kind becomes artwork evidence.
            var group = candidates.Where(c => Math.Abs(c.Bounds.Width - size) <= Math.Max(3, Math.Ceiling(size * .12))
                && Math.Abs(c.Bounds.Height - size) <= Math.Max(3, Math.Ceiling(size * .12))).ToArray();
            var spacing = FindSpacing(group, size);
            if (spacing is null) continue;
            var lattice = FindLattice(group, spacing.Value, size);
            if (lattice.Count < 35) continue;
            spacing = RefineSpacing(lattice, spacing.Value, size);
            // Normalize the fitted phase independently of contour order.
            var phaseX = Median(lattice.Select(c => c.Candidate.Center.X - c.X * spacing.Value));
            var phaseY = Median(lattice.Select(c => c.Candidate.Center.Y - c.Y * spacing.Value));
            var phase = new Point2d(phaseX, phaseY);
            var contourSize = Median(lattice.Select(c => (c.Candidate.Bounds.Width + c.Candidate.Bounds.Height) / 2d));
            // The level-up pulse can enclose the original frame in a larger luminous contour.
            // A contour supplies the grid hypothesis; current original artwork and the whole board
            // independently select its native frame size, rather than passing the glow box to drawing.
            DaevanionBoardDetection? nativeContour = null;
            foreach (var ratio in new[] { 1d, .96, .94, .92, .90, .88, .86 })
            {
                var actualSize = contourSize * ratio;
                var confirmed = new List<Cell>();
                foreach (var cell in lattice)
                {
                    var center = new Point2d(phase.X + cell.X * spacing.Value, phase.Y + cell.Y * spacing.Value);
                    var confidence = FrameScore(gray, center, actualSize, red);
                    confidence = Math.Max(confidence, FrameScore(gray, cell.Candidate.Center, actualSize, red));
                    if (confidence >= .58) confirmed.Add(cell with { Confidence = confidence });
                }
                if (confirmed.Count < 35) continue;
                using var nearEdges = new Mat();
                var radius = Math.Max(1, (int)Math.Ceiling(actualSize * .018));
                using var proximity = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(radius * 2 + 1, radius * 2 + 1));
                Cv2.Dilate(edges, nearEdges, proximity);
                var hypothesis = MatchPatterns(confirmed, phase, spacing.Value, actualSize, gray, red, nearEdges, valid, image.Size());
                if (hypothesis is null) continue;
                var result = new DaevanionBoardDetection(hypothesis.Pattern.Family, hypothesis.Origin,
                    spacing.Value, actualSize, hypothesis.Nodes, hypothesis.Score);
                if (Better(result, best)) best = result;
                if (ratio == 1 && result.Confidence == 1 && result.Nodes.All(n => FrameScorePlane(gray,
                    Center(result.Origin, result.Spacing, n.Row, n.Column), result.NodeSize, false) >= .58)) nativeContour = result;
            }
            // Fine downsampling can move the best texture sample within four percent of an already
            // fully proved native contour. Keep that frame geometry; an actual pulse requires the
            // distinctly smaller artwork scale and must not inherit its larger luminous boundary.
            if (nativeContour is not null && best is not null && best.NodeSize >= nativeContour.NodeSize * .95)
                return nativeContour;
            // A complete freshly proved mask already resolves this grid. Select its native size
            // from all bounded artwork probes, then avoid repeating an equivalent contour cluster.
            if (best?.Confidence == 1) return best;
        }
        return best;
    }

    private static bool Better(DaevanionBoardDetection candidate, DaevanionBoardDetection? current) =>
        current is null || candidate.Confidence > current.Confidence
        || candidate.Confidence == current.Confidence && candidate.Nodes.Average(n => n.Confidence) > current.Nodes.Average(n => n.Confidence);

    private static DaevanionBoardDetection? ValidatePreferred(Mat gray, Mat? red, Mat edges,
        IReadOnlyList<DaevanionBoardPattern> patterns, DaevanionBoardDetection preferred)
    {
        var pattern = patterns.FirstOrDefault(p => p.Family == preferred.Family);
        var size = preferred.NodeSize; var spacing = preferred.Spacing; var origin = preferred.Origin;
        if (pattern is null || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y)
            || !double.IsFinite(size) || !double.IsFinite(spacing) || size < 14 || spacing <= size * 1.03
            || spacing >= size * 1.65 || !Fits(pattern, origin, spacing, size, gray.Size())) return null;
        using var nearEdges = new Mat();
        var radius = Math.Max(1, (int)Math.Ceiling(size * .018));
        using var proximity = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(radius * 2 + 1, radius * 2 + 1));
        Cv2.Dilate(edges, nearEdges, proximity);
        var start = pattern.Cells.Single(c => c.IsStart);
        if (StartScore(nearEdges, Center(origin, spacing, start.Row, start.Column), size) < .72) return null;
        using var closed = new Mat();
        using var repair = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, repair);
        var contours = FindCandidates(closed);

        // Observe the union of all family cells, including cells empty in the preferred family.
        // A board switch can therefore contradict this hypothesis instead of being painted as holes.
        var visible = new Dictionary<(int Row, int Column), DaevanionNodeAnchor>();
        foreach (var cell in patterns.SelectMany(p => p.Cells).Where(c => !c.IsStart)
            .DistinctBy(c => (c.Row, c.Column)))
        {
            var center = Center(origin, spacing, cell.Row, cell.Column);
            if (center.X - size / 2 < 0 || center.Y - size / 2 < 0
                || center.X + size / 2 >= gray.Width || center.Y + size / 2 >= gray.Height) continue;
            // Match the regular search's independent current-frame proof. Subpixel antialiasing may
            // move a contour by a pixel; its exact current center is used only to validate artwork,
            // never to change the already confirmed rendering grid.
            var contour = contours.Where(c => DistanceSquared(c.Center, center) <= Math.Pow(Math.Max(1.5, size * .035), 2)
                && Math.Abs(c.Bounds.Width - size) <= Math.Max(3, size * .065)
                && Math.Abs(c.Bounds.Height - size) <= Math.Max(3, size * .065))
                .OrderBy(c => DistanceSquared(c.Center, center)).FirstOrDefault();
            if (contour is null && OutlineScore(nearEdges, center, size, References.Value.Border) < .75) continue;
            var confidence = FrameScoreNearNativeSize(gray, center, size, red);
            if (contour is not null)
                confidence = Math.Max(confidence, FrameScoreNearNativeSize(gray, contour.Center, size, red));
            if (confidence < .58) continue;
            visible[(cell.Row, cell.Column)] = new(cell.Row, cell.Column,
                new Rect((int)Math.Round(center.X - size / 2), (int)Math.Round(center.Y - size / 2),
                    (int)Math.Round(size), (int)Math.Round(size)), confidence);
        }
        var hypotheses = new List<Hypothesis>();
        foreach (var candidate in patterns)
        {
            if (!Fits(candidate, origin, spacing, size, gray.Size())) continue;
            var candidateStart = candidate.Cells.Single(c => c.IsStart);
            if (StartScore(nearEdges, Center(origin, spacing, candidateStart.Row, candidateStart.Column), size) < .72) continue;
            var expected = candidate.Cells.Select(c => (c.Row, c.Column)).ToHashSet();
            var matched = visible.Where(entry => expected.Contains(entry.Key)).Select(entry => entry.Value).ToArray();
            var unexpected = visible.Count - matched.Length;
            var score = (matched.Length + 1d) / (candidate.Cells.Count + unexpected);
            if (score < .80 || unexpected > candidate.Cells.Count * .08) continue;
            hypotheses.Add(new(candidate, origin, score, matched.OrderBy(n => n.Row).ThenBy(n => n.Column).ToArray()));
        }
        var ordered = hypotheses.OrderByDescending(h => h.Score).ToArray();
        if (ordered.Length == 0 || ordered[0].Pattern.Family != preferred.Family
            || ordered.Skip(1).Any(h => h.Pattern.Family != preferred.Family && ordered[0].Score - h.Score < .035)) return null;
        // No previous nodes/confidences are reused: every returned anchor has current pixel evidence.
        return new(preferred.Family, origin, spacing, size, ordered[0].Nodes, ordered[0].Score);
    }

    private static Point2d Center(Point2d origin, double spacing, int row, int column) =>
        new(origin.X + (column - 1) * spacing, origin.Y + (row - 1) * spacing);

    private static bool IsValid(DaevanionBoardPattern pattern) => !string.IsNullOrWhiteSpace(pattern.Family)
        && pattern.Cells.Count is >= 35 and <= 225 && pattern.Cells.Count(c => c.IsStart) == 1
        && pattern.Cells.All(c => c.Row is >= 1 and <= 15 && c.Column is >= 1 and <= 15)
        && pattern.Cells.Select(c => (c.Row, c.Column)).Distinct().Count() == pattern.Cells.Count;

    private static List<Candidate> FindCandidates(Mat closed)
    {
        Cv2.FindContours(closed, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var minimum = Math.Max(14, closed.Height * .016);
        var maximum = Math.Min(closed.Width, closed.Height) * .16;
        var result = new List<Candidate>();
        foreach (var contour in contours)
        {
            var b = Cv2.BoundingRect(contour);
            if (b.Width < minimum || b.Width > maximum || b.Height < b.Width * .86 || b.Height > b.Width * 1.15
                || Cv2.ContourArea(contour) < b.Width * b.Height * .67) continue;
            var center = new Point2d(b.X + b.Width / 2d, b.Y + b.Height / 2d);
            if (result.Any(c => Math.Abs(c.Bounds.Width - b.Width) < 4 && DistanceSquared(c.Center, center) < 9)) continue;
            result.Add(new(b, center));
            if (result.Count > 900) return []; // bounded work on a cluttered unrelated screen
        }
        return result;
    }

    private static double? FindSpacing(IReadOnlyList<Candidate> candidates, double size)
    {
        var distances = new List<double>();
        var secondNeighbors = new List<double>();
        for (var i = 0; i < candidates.Count; i++)
        for (var j = i + 1; j < candidates.Count; j++)
        {
            var dx = Math.Abs(candidates[i].Center.X - candidates[j].Center.X);
            var dy = Math.Abs(candidates[i].Center.Y - candidates[j].Center.Y);
            if (dy < size * .09 && dx > size * 1.03 && dx < size * 1.65) distances.Add(dx);
            if (dx < size * .09 && dy > size * 1.03 && dy < size * 1.65) distances.Add(dy);
            if (dy < size * .09 && dx > size * 2.06 && dx < size * 3.30) secondNeighbors.Add(dx / 2);
            if (dx < size * .09 && dy > size * 2.06 && dy < size * 3.30) secondNeighbors.Add(dy / 2);
        }
        if (distances.Count >= 20) return Median(distances);
        // A sparse animated board may have too few adjacent unmerged frames. Independently aligned
        // two-cell spans can confirm the same pitch, but cannot establish another pitch on their own.
        if (distances.Count < 10) return null;
        var pitch = Median(distances);
        distances.AddRange(secondNeighbors.Where(value => Math.Abs(value - pitch) <= Math.Max(1, size * .025)));
        return distances.Count >= 20 ? Median(distances) : null;
    }

    private static List<Cell> FindLattice(IReadOnlyList<Candidate> candidates, double spacing, double size)
    {
        var best = new List<Cell>();
        foreach (var seed in candidates.Where((_, i) => i % Math.Max(1, candidates.Count / 24) == 0).Take(32))
        {
            var cells = new Dictionary<(int X, int Y), Cell>();
            foreach (var candidate in candidates)
            {
                var x = (int)Math.Round((candidate.Center.X - seed.Center.X) / spacing);
                var y = (int)Math.Round((candidate.Center.Y - seed.Center.Y) / spacing);
                if (Math.Abs(candidate.Center.X - seed.Center.X - x * spacing) > Math.Max(1.5, size * .065)
                    || Math.Abs(candidate.Center.Y - seed.Center.Y - y * spacing) > Math.Max(1.5, size * .065)) continue;
                if (!cells.TryGetValue((x, y), out var old) || candidate.Bounds.Width > old.Candidate.Bounds.Width)
                    cells[(x, y)] = new(x, y, candidate, 0);
            }
            if (cells.Count > best.Count) best = cells.Values.ToList();
        }
        return best;
    }

    private static double RefineSpacing(IReadOnlyList<Cell> cells, double initial, double size)
    {
        // Neighbor contour centers are integer/half-integer pixels. Their median pitch can lose a
        // fraction of a pixel, accumulating several pixels at the far edge of a large board. Fit
        // longer current-frame spans after their integer lattice coordinates have been established.
        var slopes = new List<double>();
        for (var i = 0; i < cells.Count; i++)
        for (var j = i + 1; j < cells.Count; j++)
        {
            var dx = cells[j].X - cells[i].X; var dy = cells[j].Y - cells[i].Y;
            if (Math.Abs(dx) >= 3) slopes.Add((cells[j].Candidate.Center.X - cells[i].Candidate.Center.X) / dx);
            if (Math.Abs(dy) >= 3) slopes.Add((cells[j].Candidate.Center.Y - cells[i].Candidate.Center.Y) / dy);
        }
        if (slopes.Count < 20) return initial;
        var refined = Median(slopes);
        return Math.Abs(refined - initial) <= Math.Max(1, size * .025) ? refined : initial;
    }

    private static Hypothesis? MatchPatterns(IReadOnlyList<Cell> cells, Point2d phase, double spacing,
        double size, Mat gray, Mat? red, Mat nearEdges, IReadOnlyList<DaevanionBoardPattern> patterns, Size imageSize)
    {
        var hypotheses = new List<Hypothesis>();
        var minimumX = cells.Min(c => c.X); var maximumX = cells.Max(c => c.X);
        var minimumY = cells.Min(c => c.Y); var maximumY = cells.Max(c => c.Y);
        foreach (var pattern in patterns)
        {
            var expected = pattern.Cells.ToDictionary(c => (c.Column - 1, c.Row - 1));
            var start = pattern.Cells.Single(c => c.IsStart);
            for (var offsetY = minimumY - 14; offsetY <= maximumY; offsetY++)
            for (var offsetX = minimumX - 14; offsetX <= maximumX; offsetX++)
            {
                var origin = new Point2d(phase.X + offsetX * spacing, phase.Y + offsetY * spacing);
                var startCenter = new Point2d(origin.X + (start.Column - 1) * spacing, origin.Y + (start.Row - 1) * spacing);
                if (!Fits(pattern, origin, spacing, size, imageSize) || StartScore(nearEdges, startCenter, size) < .72) continue;
                var matched = new List<DaevanionNodeAnchor>();
                var unexpected = 0;
                foreach (var cell in cells)
                {
                    var x = cell.X - offsetX; var y = cell.Y - offsetY;
                    if (x is < 0 or >= 15 || y is < 0 or >= 15) continue;
                    if (!expected.TryGetValue((x, y), out var node)) { unexpected++; continue; }
                    if (node.IsStart) continue;
                    var cx = origin.X + x * spacing; var cy = origin.Y + y * spacing;
                    // Use the frame's inner content, preserving native borders and selection effects.
                    var bounds = new Rect((int)Math.Round(cx - size / 2), (int)Math.Round(cy - size / 2),
                        Math.Max(1, (int)Math.Round(size)), Math.Max(1, (int)Math.Round(size)));
                    matched.Add(new(node.Row, node.Column, bounds, cell.Confidence));
                }
                // Start ornament edges can join the immediately adjacent frame contours. Recover those
                // only from this image's native border AND interior artwork, never from the grid alone.
                // Bloom can merge many otherwise visible frames into large contours. The same
                // minimum of 35 independently proven native nodes anchors this hypothesis; all
                // remaining cells still require their own current border and artwork below.
                if (matched.Count < 35) continue;
                var seen = matched.Select(n => (n.Row, n.Column)).ToHashSet();
                foreach (var node in pattern.Cells.Where(n => !n.IsStart && !seen.Contains((n.Row, n.Column))))
                {
                    var center = new Point2d(origin.X + (node.Column - 1) * spacing, origin.Y + (node.Row - 1) * spacing);
                    if (OutlineScore(nearEdges, center, size, References.Value.Border) < .75) continue;
                    var confidence = FrameScore(gray, center, size, red);
                    if (confidence < .58) continue;
                    matched.Add(new(node.Row, node.Column, new Rect((int)Math.Round(center.X - size / 2),
                        (int)Math.Round(center.Y - size / 2), (int)Math.Round(size), (int)Math.Round(size)), confidence));
                }
                var score = (matched.Count + 1d) / (pattern.Cells.Count + unexpected);
                if (score < .80 || unexpected > pattern.Cells.Count * .08) continue;
                hypotheses.Add(new(pattern, origin, score, matched.OrderBy(n => n.Row).ThenBy(n => n.Column).ToArray()));
            }
        }
        var ordered = hypotheses.OrderByDescending(h => h.Score).ToArray();
        if (ordered.Length == 0) return null;
        var best = ordered[0];
        // Occluding the distinguishing cells must not turn another family into a confident guess.
        if (ordered.Any(h => h.Pattern.Family != best.Pattern.Family && best.Score - h.Score < .035)) return null;
        return best;
    }

    private static bool Fits(DaevanionBoardPattern pattern, Point2d origin, double spacing, double size, Size imageSize) =>
        pattern.Cells.All(c => origin.X + (c.Column - 1) * spacing - size / 2 >= 0
            && origin.Y + (c.Row - 1) * spacing - size / 2 >= 0
            && origin.X + (c.Column - 1) * spacing + size / 2 < imageSize.Width
            && origin.Y + (c.Row - 1) * spacing + size / 2 < imageSize.Height);

    private static double StartScore(Mat nearEdges, Point2d center, double size)
    {
        return OutlineScore(nearEdges, center, size, References.Value.Start);
    }

    private static double OutlineScore(Mat nearEdges, Point2d center, double size, Point2d[] points)
    {
        var hits = 0;
        foreach (var p in points)
        {
            var x = (int)Math.Round(center.X + p.X * size); var y = (int)Math.Round(center.Y + p.Y * size);
            if (x < 0 || y < 0 || x >= nearEdges.Width || y >= nearEdges.Height) return 0;
            if (nearEdges.At<byte>(y, x) != 0) hits++;
        }
        return hits / (double)points.Length;
    }

    private static double FrameScore(Mat gray, Point2d center, double size, Mat? red = null)
    {
        var ordinary = FrameScorePlane(gray, center, size, false);
        // A cyan pulse selectively changes green/blue luminance. Its red channel still contains
        // the current native glyph. This is new pixel evidence, never a retained previous node.
        return red is null ? ordinary : Math.Max(ordinary, FrameScorePlane(red, center, size, true));
    }

    private static double FrameScoreNearNativeSize(Mat gray, Point2d center, double size, Mat? red)
    {
        var confidence = FrameScore(gray, center, size, red);
        if (confidence >= .58) return confidence;
        // Selected-node pulses can shift the strongest native motif scale by a few percent. Probe
        // only the current interior, after its frame was independently proved; rendering retains
        // the frozen original bounds and never borrows a previous frame's node evidence.
        foreach (var ratio in new[] { .98, .96, .94 })
        {
            confidence = Math.Max(confidence, FrameScore(gray, center, size * ratio, red));
            if (confidence >= .58) break;
        }
        return confidence;
    }

    private static double FrameScorePlane(Mat gray, Point2d center, double size, bool red)
    {
        var samples = new double[SamplesPerAxis * SamplesPerAxis];
        var best = 0d;
        var delta = Math.Max(1, (int)Math.Round(size * .012));
        Point[] offsets = [new(0, 0), new(delta, 0), new(-delta, 0), new(0, delta), new(0, -delta),
            new(delta, delta), new(-delta, -delta), new(delta, -delta), new(-delta, delta),
            new(delta * 2, 0), new(-delta * 2, 0), new(0, delta * 2), new(0, -delta * 2)];
        foreach (var offset in offsets)
        {
            var sum = 0d; var index = 0;
            for (var y = 0; y < SamplesPerAxis; y++)
            for (var x = 0; x < SamplesPerAxis; x++)
            {
                var sx = (int)Math.Round(center.X + offset.X + (FirstSample + x * SampleSpan / (SamplesPerAxis - 1) - .5) * size);
                var sy = (int)Math.Round(center.Y + offset.Y + (FirstSample + y * SampleSpan / (SamplesPerAxis - 1) - .5) * size);
                if (sx < 0 || sy < 0 || sx >= gray.Width || sy >= gray.Height) return 0;
                samples[index] = gray.At<byte>(sy, sx); sum += samples[index++];
            }
            var mean = sum / samples.Length; var squared = 0d;
            for (var i = 0; i < samples.Length; i++) { samples[i] -= mean; squared += samples[i] * samples[i]; }
            var norm = Math.Sqrt(squared);
            if (norm < 10) continue;
            foreach (var reference in References.Value.Frames)
            {
                var dot = 0d;
                var values = red ? reference.RedValues : reference.Values;
                for (var i = 0; i < samples.Length; i++) dot += samples[i] * values[i];
                best = Math.Max(best, dot / (norm * (red ? reference.RedNorm : reference.Norm)));
            }
        }
        return best;
    }

    private static (FrameReference[] Frames, Point2d[] Start, Point2d[] Border) LoadReferences()
    {
        var assembly = typeof(DaevanionBoardLocator).Assembly;
        var frames = new List<FrameReference>();
        Point2d[] start = [];
        Point2d[] border = [];
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains("Daevanion.Assets.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            using var pixels = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Unchanged);
            using var alpha = new Mat(); Cv2.ExtractChannel(pixels, alpha, 3);
            using var mask = new Mat(); Cv2.Threshold(alpha, mask, 180, 255, ThresholdTypes.Binary);
            if (name.Contains("startbg", StringComparison.Ordinal))
            {
                Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                var outline = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                start = outline.Where((_, i) => i % Math.Max(1, outline.Length / 112) == 0)
                    .Select(p => new Point2d((p.X - 100d) / 126, (p.Y - 100d) / 126)).ToArray();
                continue;
            }
            using var gray = new Mat(); Cv2.CvtColor(pixels, gray, ColorConversionCodes.BGRA2GRAY);
            using var red = new Mat(); Cv2.ExtractChannel(pixels, red, 2);
            var bounds = Cv2.BoundingRect(mask);
            if (name.EndsWith("node_common_sprite.png", StringComparison.Ordinal))
            {
                Cv2.FindContours(mask, out Point[][] outlines, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                var outline = outlines.OrderByDescending(c => Cv2.ContourArea(c)).First();
                border = outline.Where((_, i) => i % Math.Max(1, outline.Length / 96) == 0)
                    .Select(p => new Point2d((p.X - bounds.X - (bounds.Width - 1) / 2d) / (bounds.Width - 1),
                        (p.Y - bounds.Y - (bounds.Height - 1) / 2d) / (bounds.Height - 1))).ToArray();
            }
            // The game and WGC may both downsample a frame. A native-resolution-only texture sample
            // overweights fine grain that legitimately disappears in smaller UI/capture scales.
            foreach (var sigma in new[] { 0d, .85 })
            {
                using var sampled = new Mat();
                using var sampledRed = new Mat();
                if (sigma == 0) { gray.CopyTo(sampled); red.CopyTo(sampledRed); }
                else { Cv2.GaussianBlur(gray, sampled, new Size(5, 5), sigma); Cv2.GaussianBlur(red, sampledRed, new Size(5, 5), sigma); }
                var values = new double[SamplesPerAxis * SamplesPerAxis]; var index = 0;
                var redValues = new double[values.Length];
                for (var y = 0; y < SamplesPerAxis; y++)
                for (var x = 0; x < SamplesPerAxis; x++)
                {
                    var sy = (int)Math.Round(bounds.Y + (FirstSample + y * SampleSpan / (SamplesPerAxis - 1)) * (bounds.Height - 1));
                    var sx = (int)Math.Round(bounds.X + (FirstSample + x * SampleSpan / (SamplesPerAxis - 1)) * (bounds.Width - 1));
                    values[index] = sampled.At<byte>(sy, sx); redValues[index++] = sampledRed.At<byte>(sy, sx);
                }
                var mean = values.Average();
                var redMean = redValues.Average();
                for (var i = 0; i < values.Length; i++) { values[i] -= mean; redValues[i] -= redMean; }
                frames.Add(new(values, Math.Sqrt(values.Sum(v => v * v)), redValues, Math.Sqrt(redValues.Sum(v => v * v))));
            }
        }
        if (frames.Count < 8 || start.Length < 50 || border.Length < 50) throw new InvalidDataException("Daevanion recognition references are incomplete.");
        return (frames.ToArray(), start, border);
    }

    private static double Median(IEnumerable<double> values)
    {
        var array = values.Order().ToArray();
        return array.Length % 2 == 0 ? (array[array.Length / 2 - 1] + array[array.Length / 2]) / 2 : array[array.Length / 2];
    }
    private static double DistanceSquared(Point2d a, Point2d b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}
