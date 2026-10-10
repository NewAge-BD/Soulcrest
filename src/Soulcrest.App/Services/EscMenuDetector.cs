using System.Diagnostics;
using OpenCvSharp;

namespace Soulcrest.App.Services;

/// <summary>Recognizes the ESC menu by three aligned action icons; ignores names, levels and hotkey labels.</summary>
public sealed class EscMenuDetector
{
    private sealed record Pattern(double Scale, Mat[] Icons);
    private static readonly double[] Scales = [1, .75, 1.25, 1.5, .6, .9, 1.12, .8, 1.4, 1.6, 1.75, 1.8, 2];
    private static readonly int[] Offsets = [-135, 0, 133, 267];
    private static readonly Lazy<Pattern[]> Patterns = new(Load);
    private double _lastScale = 1;
    private long _lastBroadSearch;

    public bool Detect(Mat frame)
    {
        using var image = Prepare(frame);
        if (image.Empty()) return false;
        var preferred = Patterns.Value.FirstOrDefault(p => p.Scale == _lastScale);
        if (preferred is not null && Matches(image, preferred)) return true;
        // A full scale search is occasional; normal map detection pays for only one small template.
        if (_lastBroadSearch != 0 && Stopwatch.GetElapsedTime(_lastBroadSearch) < TimeSpan.FromSeconds(2)) return false;
        _lastBroadSearch = Stopwatch.GetTimestamp();
        foreach (var pattern in Patterns.Value)
            if (pattern != preferred && Matches(image, pattern)) { _lastScale = pattern.Scale; return true; }
        return false;
    }

    internal static bool Shows(Mat frame)
    {
        using var image = Prepare(frame);
        return !image.Empty() && Patterns.Value.Any(p => Matches(image, p));
    }

    private static Mat Prepare(Mat frame)
    {
        if (frame.Empty() || frame.Width < 100 || frame.Height < 100) return new Mat();
        // The ESC panel is on the right of a landscape game window. Cropped portrait fixtures retain
        // the whole width. Search only the upper action rows, not the animated scene below them.
        var left = frame.Width >= frame.Height * 1.4 ? (int)(frame.Width * .35) : 0;
        var top = (int)(frame.Height * .07);
        using var area = new Mat(frame, new Rect(left, top, frame.Width-left, (int)(frame.Height*.55)-top));
        using var gray = new Mat();
        if (area.Channels() == 1) area.CopyTo(gray);
        else Cv2.CvtColor(area, gray, area.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        var small = new Mat();
        Cv2.Resize(gray, small, new OpenCvSharp.Size(), .5, .5, InterpolationFlags.Area);
        return small;
    }

    private static bool Matches(Mat image, Pattern pattern)
    {
        var anchor = pattern.Icons[1];
        if (anchor.Width >= image.Width || anchor.Height >= image.Height) return false;
        using var scores = new Mat();
        Cv2.MatchTemplate(image, anchor, scores, TemplateMatchModes.CCoeffNormed);
        for (var candidate = 0; candidate < 4; candidate++)
        {
            Cv2.MinMaxLoc(scores, out _, out double best, out _, out OpenCvSharp.Point at);
            if (best < .78) return false;
            var votes = 1;
            for (var i = 0; i < 4; i++)
            {
                if (i == 1) continue;
                var icon = pattern.Icons[i];
                var x = at.X + (int)Math.Round(Offsets[i] * pattern.Scale * .5);
                var tolerance = Math.Max(2, (int)Math.Round(2 * pattern.Scale));
                var left = Math.Max(0, x-tolerance); var top = Math.Max(0, at.Y-tolerance);
                var right = Math.Min(image.Width, x+icon.Width+tolerance);
                var bottom = Math.Min(image.Height, at.Y+icon.Height+tolerance);
                if (right-left < icon.Width || bottom-top < icon.Height) continue;
                using var area = new Mat(image, new Rect(left, top, right-left, bottom-top));
                using var confirmation = new Mat();
                Cv2.MatchTemplate(area, icon, confirmation, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(confirmation, out _, out double match);
                if (match >= .76) votes++;
            }
            if (votes >= 3) return true;
            var exclusion = new Rect(Math.Max(0, at.X-anchor.Width), Math.Max(0, at.Y-anchor.Height),
                Math.Min(scores.Width, at.X+anchor.Width)-Math.Max(0, at.X-anchor.Width),
                Math.Min(scores.Height, at.Y+anchor.Height)-Math.Max(0, at.Y-anchor.Height));
            Cv2.Rectangle(scores, exclusion, Scalar.All(-1), -1);
        }
        return false;
    }

    private static Pattern[] Load()
    {
        using var stream = typeof(EscMenuDetector).Assembly.GetManifestResourceStream("Soulcrest.EscMenuIcons.png");
        if (stream is null) return [];
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        using var strip = Cv2.ImDecode(memory.ToArray(), ImreadModes.Grayscale);
        if (strip.Empty()) return [];
        return Scales.Select(scale => new Pattern(scale, Enumerable.Range(0, 4).Select(i =>
        {
            using var icon = new Mat(strip, new Rect(i*28, 0, 28, 28));
            var resized = new Mat();
            Cv2.Resize(icon, resized, new OpenCvSharp.Size(), scale*.5, scale*.5, InterpolationFlags.Area);
            return resized;
        }).ToArray())).ToArray();
    }
}
