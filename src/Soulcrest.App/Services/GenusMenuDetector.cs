using System.Diagnostics;
using OpenCvSharp;

namespace Soulcrest.App.Services;

public sealed record GenusMenuDetection(string? Genus, double MatchScore, byte? ViewedPreset = null);

/// <summary>Five native glyphs, their menu geometry, nine wheel gems and one selected outer ring. No text or game input.</summary>
public sealed class GenusMenuDetector
{
    private sealed record Pattern(double Scale, Mat[] Symbols);
    private static readonly double[] Scales = [.5, .45, .55, .4, .6, .35, .65, .3, .7, .25, .8, 1];
    private static readonly Point2d[] Symbols = [new(94, -343), new(31, -172), new(0, 0), new(31, 172), new(94, 344)];
    private static readonly Point2d[] Wheel = [new(823, -362), new(657, -268), new(560, -104), new(560, 88),
        new(657, 250), new(823, 348), new(1011, 348), new(1175, 250), new(1270, 88)];
    private static readonly string[] Genera = ["cogni", "fera", "natura", "varian", "special"];
    private static readonly Lazy<Pattern[]> Patterns = new(LoadPatterns);
    private double _lastScale = .5;
    private long _lastBroadSearch;

    public GenusMenuDetection? Detect(Mat frame)
    {
        if (frame.Empty() || frame.Width < 320 || frame.Height < 200 || frame.Channels() is not (1 or 3 or 4)) return null;
        using var image = Prepare(frame);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        // Match broad glyph structure across subpixel UI resampling; the colored ring/wheel proof stays unblurred.
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), .6);
        using var search = new Mat(gray, new Rect(0, 0, Math.Max(1, (int)(gray.Width * .4)), gray.Height));
        var preferred = Patterns.Value.FirstOrDefault(pattern => pattern.Scale == _lastScale);
        if (preferred is not null && Find(image, gray, search, preferred) is { } current) return current;
        if (_lastBroadSearch != 0 && Stopwatch.GetElapsedTime(_lastBroadSearch) < TimeSpan.FromSeconds(2)) return null;
        _lastBroadSearch = Stopwatch.GetTimestamp();
        foreach (var pattern in Patterns.Value)
        {
            if (pattern == preferred || Find(image, gray, search, pattern) is not { } found) continue;
            _lastScale = pattern.Scale;
            return found;
        }
        return null;
    }

    private static Mat Prepare(Mat frame)
    {
        using var color = new Mat();
        if (frame.Channels() == 1) Cv2.CvtColor(frame, color, ColorConversionCodes.GRAY2BGR);
        else if (frame.Channels() == 4) Cv2.CvtColor(frame, color, ColorConversionCodes.BGRA2BGR);
        else frame.CopyTo(color);
        var image = new Mat();
        var scale = Math.Min(1d, 720d / color.Height);
        Cv2.Resize(color, image, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        return image;
    }

    private static GenusMenuDetection? Find(Mat image, Mat gray, Mat search, Pattern pattern)
    {
        var anchor = pattern.Symbols[2];
        if (anchor.Width >= search.Width || anchor.Height >= search.Height) return null;
        using var scores = new Mat();
        Cv2.MatchTemplate(search, anchor, scores, TemplateMatchModes.CCoeffNormed);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Cv2.MinMaxLoc(scores, out _, out var score, out _, out OpenCvSharp.Point at);
            if (!double.IsFinite(score) || score < .80) return null;
            var origin = new Point2d(at.X + anchor.Width / 2d, at.Y + anchor.Height / 2d);
            var centers = new Point2d[5];
            var minimumMatch = 1d;
            var matched = true;
            for (var index = 0; index < 5; index++)
            {
                var symbol = pattern.Symbols[index];
                var x = (int)Math.Round(origin.X + Symbols[index].X * pattern.Scale - symbol.Width / 2d);
                var y = (int)Math.Round(origin.Y + Symbols[index].Y * pattern.Scale - symbol.Height / 2d);
                var tolerance = Math.Max(2, (int)Math.Round(5 * pattern.Scale));
                var left = Math.Max(0, x - tolerance); var top = Math.Max(0, y - tolerance);
                var right = Math.Min(gray.Width, x + symbol.Width + tolerance);
                var bottom = Math.Min(gray.Height, y + symbol.Height + tolerance);
                if (right - left < symbol.Width || bottom - top < symbol.Height) { matched = false; break; }
                using var area = new Mat(gray, new Rect(left, top, right - left, bottom - top));
                using var confirmation = new Mat();
                Cv2.MatchTemplate(area, symbol, confirmation, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(confirmation, out _, out var value, out _, out OpenCvSharp.Point position);
                if (!double.IsFinite(value) || value < .78) { matched = false; break; }
                minimumMatch = Math.Min(minimumMatch, value);
                centers[index] = new(left + position.X + symbol.Width / 2d, top + position.Y + symbol.Height / 2d);
            }
            if (matched && Wheel.Count(offset => CircleScore(image,
                new(origin.X + offset.X * pattern.Scale, origin.Y + offset.Y * pattern.Scale), pattern.Scale, selected: false) >= .65) >= 7)
            {
                var rings = centers.Select(center => CircleScore(image, center, pattern.Scale, selected: true)).ToArray();
                var ranking = Enumerable.Range(0, 5).OrderByDescending(index => rings[index]).ToArray();
                var selected = rings[ranking[0]] >= .55 && rings[ranking[0]] - rings[ranking[1]] >= .30 ? Genera[ranking[0]] : null;
                return new(selected, minimumMatch, SelectedPreset(image, origin, pattern.Scale));
            }
            var excludeLeft = Math.Max(0, at.X - anchor.Width); var excludeTop = Math.Max(0, at.Y - anchor.Height);
            var excludeRight = Math.Min(scores.Width, at.X + anchor.Width); var excludeBottom = Math.Min(scores.Height, at.Y + anchor.Height);
            if (excludeRight > excludeLeft && excludeBottom > excludeTop)
                Cv2.Rectangle(scores, new Rect(excludeLeft, excludeTop, excludeRight - excludeLeft, excludeBottom - excludeTop), Scalar.All(-1), -1);
        }
        return null;
    }

    private static byte? SelectedPreset(Mat image, Point2d origin, double scale)
    {
        var scores = Enumerable.Range(0, 3).Select(index =>
        {
            var center = new Point2d(origin.X + (1689 + index * 56) * scale, origin.Y - 616 * scale);
            var hits = 0;
            // Selected native preset has a bright square perimeter. The digits are never sampled.
            for (var side = 0; side < 4; side++)
                for (var step = -12; step <= 12; step += 3)
                {
                    var hit = false;
                    for (var radius = 18d; radius <= 22; radius++)
                    {
                        var dx = side < 2 ? step : (side == 2 ? -radius : radius);
                        var dy = side >= 2 ? step : (side == 0 ? -radius : radius);
                        var x = (int)Math.Round(center.X + dx * scale); var y = (int)Math.Round(center.Y + dy * scale);
                        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                        var pixel = image.At<Vec3b>(y, x);
                        var maximum = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2));
                        var minimum = Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
                        if (minimum >= 115 && maximum - minimum < 115) { hit = true; break; }
                    }
                    if (hit) hits++;
                }
            return hits / 36d;
        }).ToArray();
        var order = Enumerable.Range(0, 3).OrderByDescending(index => scores[index]).ToArray();
        return scores[order[0]] >= .65 && scores[order[0]] - scores[order[1]] >= .35 ? (byte)(order[0] + 1) : null;
    }

    private static double CircleScore(Mat image, Point2d center, double scale, bool selected)
    {
        var samples = selected ? 64 : 32;
        var minimumRadius = (selected ? 68 : 30) * scale;
        var maximumRadius = (selected ? 77 : 44) * scale;
        var hits = 0;
        for (var index = 0; index < samples; index++)
        {
            var angle = 2 * Math.PI * index / samples;
            for (var radius = minimumRadius; radius <= maximumRadius + .1; radius += Math.Max(.5, scale))
            {
                var x = (int)Math.Round(center.X + Math.Cos(angle) * radius);
                var y = (int)Math.Round(center.Y + Math.Sin(angle) * radius);
                if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                var pixel = image.At<Vec3b>(y, x);
                var maximum = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2));
                var minimum = Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
                var valid = selected ? pixel.Item0 > 140 && pixel.Item1 > 140 && pixel.Item2 > 100 && maximum - minimum < 95 : maximum > 145;
                if (!valid) continue;
                hits++; break;
            }
        }
        return hits / (double)samples;
    }

    internal static Mat ReadSymbols()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "genus-menu", "symbols.png");
        using var decoded = Cv2.ImRead(file, ImreadModes.Grayscale);
        if (decoded.Width != 280 || decoded.Height != 56) throw new InvalidDataException("The five fixed genus-menu glyphs are unavailable.");
        return decoded.Clone();
    }

    private static Pattern[] LoadPatterns()
    {
        using var strip = ReadSymbols();
        return Scales.Select(scale => new Pattern(scale, Enumerable.Range(0, 5).Select(index =>
        {
            using var symbol = new Mat(strip, new Rect(index * 56, 0, 56, 56));
            var resized = new Mat();
            Cv2.Resize(symbol, resized, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
            Cv2.GaussianBlur(resized, resized, new OpenCvSharp.Size(3, 3), .6);
            return resized;
        }).ToArray())).ToArray();
    }
}
