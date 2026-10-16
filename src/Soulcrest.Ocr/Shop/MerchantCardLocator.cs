using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Ocr.Shop;

/// <summary>One offer's price button, in pixels of the supplied image; slots are six above, then four below.</summary>
public sealed record MerchantCardAnchor(int SlotIndex, Rect PriceBounds, double Scale, double Confidence);

/// <summary>
/// Finds the merchant's ten hanging cards from their repeated frame geometry. No text, item artwork,
/// ownership labels or game screenshot templates are read. A partial or unverified layout returns nothing.
/// </summary>
public static class MerchantCardLocator
{
    // Relative to the first upper seal, in units of the upper cards' horizontal spacing. Measured
    // independently on the five UI-scale examples supplied by the user on 2026-10-13.
    private static readonly Point2d[] Slots =
    [
        new(0, 0), new(1, .225), new(2, .305), new(3, .305), new(4, .225), new(5, 0),
        new(.19, 1.25), new(1.195, 1.43), new(3.805, 1.43), new(4.81, 1.25),
    ];
    private const int MinimumSeals = 8;
    private const int MaximumCandidates = 48;
    private const double PriceOffset = .872, PriceWidth = .476, PriceHeight = .094;
    private const double ReferencePriceWidth = 146;
    private sealed record Seal(Rect Bounds, Point2d Center);
    private sealed record Layout(Point2d Origin, double Spacing, int[] Assignments, int Count, double Error);

    /// <summary>
    /// Accepts an 8-bit BGR, BGRA or grayscale game-window image. Returns exactly ten anchors, ordered
    /// left-to-right in the upper six and then lower four, only when every price and both card edges fit.
    /// Callers independently verify frame freshness, shop identity and offer-to-slot correspondence.
    /// Optional previous anchors are a spatial hypothesis in this image's pixels, never current evidence.
    /// </summary>
    public static IReadOnlyList<MerchantCardAnchor> Find(Mat image, IReadOnlyList<MerchantCardAnchor>? previousAnchors = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty() || image.Depth() != MatType.CV_8U || image.Width < 160 || image.Height < 100 ||
            image.Channels() is not (1 or 3 or 4)) return [];
        var anchors = FindAtScale(image, previousAnchors);
        if (anchors.Count != 0 || image.Width < 960 || image.Width >= 1920) return anchors;

        // 720p reference checks: normalize only a failed small frame, never an accepted layout.
        // The same detector still requires independent seals and every raw price/card-side edge.
        var normalization = 1920d / image.Width;
        using var normalized = new Mat();
        Cv2.Resize(image, normalized, new Size(), normalization, normalization, InterpolationFlags.Linear);
        var found = FindAtScale(normalized);
        var restored = found.Select(anchor => anchor with
        {
            PriceBounds = new Rect((int)Math.Round(anchor.PriceBounds.X / normalization),
                (int)Math.Round(anchor.PriceBounds.Y / normalization),
                (int)Math.Round(anchor.PriceBounds.Width / normalization),
                (int)Math.Round(anchor.PriceBounds.Height / normalization)),
            Scale = anchor.Scale / normalization
        }).ToArray();
        if (restored.Any(anchor => anchor.PriceBounds.Width <= 0 || anchor.PriceBounds.Height <= 0 ||
            anchor.PriceBounds.X < 0 || anchor.PriceBounds.Y < 0 ||
            anchor.PriceBounds.Right >= image.Width || anchor.PriceBounds.Bottom >= image.Height)) return [];
        return restored;
    }

    private static IReadOnlyList<MerchantCardAnchor> FindAtScale(Mat image,
        IReadOnlyList<MerchantCardAnchor>? previousAnchors = null)
    {
        using var gray = new Mat();
        if (image.Channels() == 1) image.CopyTo(gray);
        else Cv2.CvtColor(image, gray, image.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 8, 25);
        using var closed = new Mat();
        using var closingKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, closingKernel);
        var seals = FindSeals(closed);
        if (seals.Count < MinimumSeals && image.Width <= 1920)
        {
            // User reference 2026-10-13: Area downsampling can leave small gaps in tiny seals.
            // Repair candidate contours only; every price and both sides still require raw edges.
            using var repairKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.MorphologyEx(edges, closed, MorphTypes.Close, repairKernel);
            seals = FindSeals(closed);
        }
        if (seals.Count is < MinimumSeals or > MaximumCandidates) return [];

        // User report 2026-10-16: tiny changes in antialiased seal contours can move a new fit
        // by one pixel. An earlier layout is only a hypothesis: this very image must still
        // supply eight independently matched seals and every raw price/card-side edge.
        if (PreferredLayout(previousAnchors, seals, image.Size()) is { } preferred)
        {
            using var preferredEdges = NearbyEdges(edges, preferred.Spacing);
            var verified = ValidateCardEdges(preferred, preferredEdges, image.Size(), out _);
            if (verified.Count == Slots.Length)
                return previousAnchors!.OrderBy(anchor => anchor.SlotIndex).Select((anchor, index) =>
                    anchor with { Confidence = verified[index].Confidence }).ToArray();
        }
        if (FitLayout(seals, image.Size()) is not { } layout) return [];

        // A small edge tolerance covers antialiasing and the measured rounded-coordinate differences.
        // Validate raw edges, not the closed image: morphology may repair a seal but cannot invent a price.
        using var nearbyEdges = NearbyEdges(edges, layout.Spacing);
        var anchors = ValidateCardEdges(layout, nearbyEdges, image.Size(), out var edgeMismatch);
        if (anchors.Count == Slots.Length || !edgeMismatch) return anchors;

        // Refined centers of antialiased seals may round a pixel away from the price geometry.
        // Try one shared translation, never independent guesses or a changed scale/tolerance.
        Point2d[] offsets = [new(0, -1), new(-1, 0), new(1, 0), new(0, 1),
            new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)];
        foreach (var offset in offsets)
        {
            var shifted = layout with { Origin = new(layout.Origin.X + offset.X, layout.Origin.Y + offset.Y) };
            anchors = ValidateCardEdges(shifted, nearbyEdges, image.Size(), out _);
            if (anchors.Count == Slots.Length) return anchors;
        }
        return [];
    }

    private static Mat NearbyEdges(Mat edges, double spacing)
    {
        var nearby = new Mat();
        try
        {
            var radius = (int)Math.Ceiling(Math.Max(1.5, spacing * .007));
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(radius * 2 + 1, radius * 2 + 1));
            Cv2.Dilate(edges, nearby, kernel);
            return nearby;
        }
        catch { nearby.Dispose(); throw; }
    }

    private static Layout? PreferredLayout(IReadOnlyList<MerchantCardAnchor>? previousAnchors,
        IReadOnlyList<Seal> seals, Size imageSize)
    {
        if (previousAnchors is null || previousAnchors.Count != Slots.Length) return null;
        var anchors = new MerchantCardAnchor[Slots.Length];
        var seen = new bool[Slots.Length];
        foreach (var anchor in previousAnchors)
        {
            if (anchor is null || anchor.SlotIndex is < 0 or >= 10 || seen[anchor.SlotIndex] ||
                !double.IsFinite(anchor.Scale) || anchor.Scale <= 0 ||
                !double.IsFinite(anchor.Confidence) || anchor.Confidence < .72) return null;
            var price = anchor.PriceBounds;
            if (price.X < 0 || price.Y < 0 || price.Width <= 0 || price.Height <= 0 ||
                (long)price.X + price.Width >= imageSize.Width ||
                (long)price.Y + price.Height >= imageSize.Height) return null;
            seen[anchor.SlotIndex] = true;
            anchors[anchor.SlotIndex] = anchor;
        }
        var first = anchors[0];
        var spacing = first.Scale * ReferencePriceWidth / PriceWidth;
        if (!double.IsFinite(spacing) || spacing < imageSize.Height * .08 || spacing > imageSize.Width * .25 ||
            anchors.Any(anchor => Math.Abs(anchor.Scale - first.Scale) > first.Scale * 1e-9)) return null;
        var priceCenter = new Point2d(first.PriceBounds.X + first.PriceBounds.Width / 2d,
            first.PriceBounds.Y + first.PriceBounds.Height / 2d);
        var layout = new Layout(new(priceCenter.X, priceCenter.Y - spacing * PriceOffset), spacing, [], 0, 0);
        var width = (int)Math.Round(spacing * PriceWidth);
        var height = (int)Math.Round(spacing * PriceHeight);
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            var seal = Position(layout, Slots[slot]);
            var expected = new Rect((int)Math.Round(seal.X - spacing * PriceWidth / 2),
                (int)Math.Round(seal.Y + spacing * PriceOffset - spacing * PriceHeight / 2), width, height);
            var price = anchors[slot].PriceBounds;
            // Reconstructing from a rounded rectangle may differ by one pixel. Inconsistent
            // supplied slots are discarded; none can bypass the following current-image checks.
            if (Math.Abs((long)price.X - expected.X) > 1 || Math.Abs((long)price.Y - expected.Y) > 1 ||
                Math.Abs((long)price.X + price.Width - expected.Right) > 1 ||
                Math.Abs((long)price.Y + price.Height - expected.Bottom) > 1) return null;
        }
        var used = new HashSet<int>();
        var count = 0;
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            var predicted = Position(layout, Slots[slot]);
            var nearest = -1;
            var distance = Math.Pow(spacing * .035, 2);
            for (var candidate = 0; candidate < seals.Count; candidate++)
            {
                if (used.Contains(candidate) || spacing < seals[candidate].Bounds.Height * 7 ||
                    spacing > seals[candidate].Bounds.Height * 12) continue;
                var next = DistanceSquared(predicted, seals[candidate].Center);
                if (next >= distance) continue;
                nearest = candidate;
                distance = next;
            }
            if (nearest < 0) continue;
            used.Add(nearest);
            count++;
        }
        return count >= MinimumSeals ? layout with { Count = count } : null;
    }

    private static IReadOnlyList<MerchantCardAnchor> ValidateCardEdges(Layout layout, Mat nearbyEdges,
        Size imageSize, out bool edgeMismatch)
    {
        edgeMismatch = false;
        var anchors = new MerchantCardAnchor[Slots.Length];
        for (var index = 0; index < Slots.Length; index++)
        {
            var seal = Position(layout, Slots[index]);
            var center = new Point2d(seal.X, seal.Y + layout.Spacing * PriceOffset);
            var width = layout.Spacing * PriceWidth;
            var height = layout.Spacing * PriceHeight;
            var bounds = new Rect((int)Math.Round(center.X - width / 2), (int)Math.Round(center.Y - height / 2),
                (int)Math.Round(width), (int)Math.Round(height));
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < 0 || bounds.Y < 0 ||
                bounds.Right >= imageSize.Width || bounds.Bottom >= imageSize.Height) return [];
            var priceScore = EdgeScore(nearbyEdges, Capsule(center, width, height));
            var sideScore = EdgeScore(nearbyEdges, CardSides(seal, center.Y, layout.Spacing));
            if (priceScore < .72 || sideScore < .75)
            {
                edgeMismatch = true;
                return [];
            }
            anchors[index] = new(index, bounds, width / ReferencePriceWidth, Math.Min(priceScore, sideScore));
        }
        return anchors;
    }

    private static List<Seal> FindSeals(Mat closed)
    {
        Cv2.FindContours(closed, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var seals = new List<Seal>();
        var minimumSize = Math.Max(7, closed.Height * .01);
        var maximumSize = closed.Height * .06;
        foreach (var contour in contours)
        {
            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < minimumSize || bounds.Width > maximumSize ||
                bounds.Width < bounds.Height * .8 || bounds.Width > bounds.Height * 1.2 ||
                bounds.Y < closed.Height * .12 || bounds.Y > closed.Height * .85 ||
                Cv2.ContourArea(contour) < bounds.Width * bounds.Height * .8) continue;
            if (Cv2.ApproxPolyDP(contour, Cv2.ArcLength(contour, true) * .03, true).Length > 8) continue;
            var center = new Point2d(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
            if (seals.Any(seal => DistanceSquared(seal.Center, center) < Math.Pow(Math.Min(bounds.Width, seal.Bounds.Width) * .25, 2))) continue;
            seals.Add(new(bounds, center));
            if (seals.Count > MaximumCandidates) return seals; // bound work on unrelated/cluttered screens
        }
        return seals.OrderBy(seal => seal.Center.X).ToList();
    }

    private static Layout? FitLayout(IReadOnlyList<Seal> seals, Size imageSize)
    {
        Layout? best = null;
        for (var a = 0; a < seals.Count; a++)
        for (var b = a + 1; b < seals.Count; b++)
        for (var first = 0; first < Slots.Length; first++)
        for (var second = 0; second < Slots.Length; second++)
        {
            var modelWidth = Slots[second].X - Slots[first].X;
            if (modelWidth < 2) continue; // widely separated anchors make scale estimation stable
            var spacing = (seals[b].Center.X - seals[a].Center.X) / modelWidth;
            if (spacing < imageSize.Height * .08 || spacing > imageSize.Width * .25 ||
                spacing < seals[a].Bounds.Height * 7 || spacing > seals[a].Bounds.Height * 12 ||
                spacing < seals[b].Bounds.Height * 7 || spacing > seals[b].Bounds.Height * 12) continue;
            var origin = new Point2d(seals[a].Center.X - Slots[first].X * spacing, seals[a].Center.Y - Slots[first].Y * spacing);
            var preliminary = new Layout(origin, spacing, [], 0, 0);
            if (DistanceSquared(Position(preliminary, Slots[second]), seals[b].Center) > Math.Pow(spacing * .04, 2)) continue;
            var assignments = Enumerable.Repeat(-1, Slots.Length).ToArray();
            var count = 0;
            var error = 0.0;
            for (var slot = 0; slot < Slots.Length; slot++)
            {
                var predicted = Position(preliminary, Slots[slot]);
                var nearest = -1;
                var distance = Math.Pow(spacing * .035, 2);
                for (var candidate = 0; candidate < seals.Count; candidate++)
                {
                    var next = DistanceSquared(predicted, seals[candidate].Center);
                    if (next >= distance) continue;
                    nearest = candidate; distance = next;
                }
                if (nearest < 0) continue;
                assignments[slot] = nearest;
                count++;
                error += Math.Sqrt(distance) / spacing;
            }
            if (count < MinimumSeals || best is not null && (count < best.Count || count == best.Count && error >= best.Error)) continue;
            best = preliminary with { Assignments = assignments, Count = count, Error = error };
        }
        if (best is null) return null;
        // Refine a single shared scale and x/y translation from all independent observed seals.
        var modelMean = new Point2d();
        var observedMean = new Point2d();
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if (best.Assignments[slot] < 0) continue;
            modelMean.X += Slots[slot].X / best.Count; modelMean.Y += Slots[slot].Y / best.Count;
            observedMean.X += seals[best.Assignments[slot]].Center.X / best.Count;
            observedMean.Y += seals[best.Assignments[slot]].Center.Y / best.Count;
        }
        var numerator = 0.0;
        var denominator = 0.0;
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if (best.Assignments[slot] < 0) continue;
            var model = new Point2d(Slots[slot].X - modelMean.X, Slots[slot].Y - modelMean.Y);
            var observed = seals[best.Assignments[slot]].Center;
            numerator += model.X * (observed.X - observedMean.X) + model.Y * (observed.Y - observedMean.Y);
            denominator += model.X * model.X + model.Y * model.Y;
        }
        var refined = numerator / denominator;
        return best with { Spacing = refined, Origin = new(observedMean.X - modelMean.X * refined, observedMean.Y - modelMean.Y * refined) };
    }

    private static Point2d Position(Layout layout, Point2d slot) => new(layout.Origin.X + slot.X * layout.Spacing, layout.Origin.Y + slot.Y * layout.Spacing);
    private static double DistanceSquared(Point2d a, Point2d b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static IEnumerable<Point2d> Capsule(Point2d center, double width, double height)
    {
        var radius = height / 2;
        var steps = Math.Max(8, (int)(width / 3));
        for (var step = 0; step < steps; step++)
        {
            var x = center.X - width / 2 + radius + (width - height) * step / (steps - 1);
            yield return new(x, center.Y - radius);
            yield return new(x, center.Y + radius);
        }
        for (var step = 0; step < 12; step++)
        {
            var angle = Math.PI / 2 + Math.PI * step / 11;
            yield return new(center.X - width / 2 + radius + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            yield return new(center.X + width / 2 - radius - radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }
    }

    private static IEnumerable<Point2d> CardSides(Point2d seal, double priceY, double spacing)
    {
        for (var step = 0; step < 60; step++)
        {
            var y = seal.Y + spacing * .15 + (priceY - spacing * .04 - seal.Y - spacing * .15) * step / 59;
            yield return new(seal.X - spacing * .345, y);
            yield return new(seal.X + spacing * .345, y);
        }
    }

    private static double EdgeScore(Mat nearbyEdges, IEnumerable<Point2d> samples)
    {
        var found = 0;
        var count = 0;
        foreach (var sample in samples)
        {
            var x = (int)Math.Round(sample.X); var y = (int)Math.Round(sample.Y);
            if (x < 0 || y < 0 || x >= nearbyEdges.Width || y >= nearbyEdges.Height) return 0;
            if (nearbyEdges.At<byte>(y, x) != 0) found++;
            count++;
        }
        return count == 0 ? 0 : (double)found / count;
    }
}
