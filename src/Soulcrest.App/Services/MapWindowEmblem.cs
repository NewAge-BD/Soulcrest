using OpenCvSharp;

namespace Soulcrest.App.Services;

/// <summary>
/// The symbol in the top left corner of full-screen game windows (world map "Map: Altgard", also the pet
/// window). Found in a few milliseconds, it decides whether the expensive world-map check is needed at
/// all. Several sizes cover the in-game UI scale; measured on the fixtures: windows 0.91–1.00, normal
/// play 0.45–0.57 (2026-10-05).
/// </summary>
public static class MapWindowEmblem
{
    private const double Threshold = 0.8;
    private static readonly double[] Sizes = [0.8, 0.9, 1.0, 1.12, 1.25, 1.4, 1.6];
    private static readonly Lazy<Mat?> Template = new(Load);

    /// <summary>Whether the header symbol is shown; <paramref name="screen"/> is the full game picture (BGR).</summary>
    public static bool Shows(Mat screen) => Score(screen) >= Threshold;

    internal static double Score(Mat screen)
    {
        if (Template.Value is not { } template || screen.Empty())
            return 0;
        // Symbol at about (26, 22) of a 2560×1440 picture; the UI grows with the screen height.
        using var corner = new Mat(screen, new OpenCvSharp.Rect(0, 0, Math.Max(1, screen.Width * 8 / 100), Math.Max(1, screen.Height * 12 / 100)));
        using var gray = new Mat();
        if (corner.Channels() == 1)
            corner.CopyTo(gray);
        else
            Cv2.CvtColor(corner, gray, corner.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        var best = 0.0;
        foreach (var size in Sizes)
        {
            var factor = screen.Height / 1440.0 * size;
            using var scaled = new Mat();
            Cv2.Resize(template, scaled, new OpenCvSharp.Size(), factor, factor, factor < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            if (scaled.Width >= gray.Width || scaled.Height >= gray.Height)
                continue;
            using var result = new Mat();
            Cv2.MatchTemplate(gray, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double max);
            best = Math.Max(best, max);
        }
        return best;
    }

    private static Mat? Load()
    {
        using var stream = typeof(MapWindowEmblem).Assembly.GetManifestResourceStream("Soulcrest.MapWindowEmblem.png");
        if (stream is null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var image = Cv2.ImDecode(memory.ToArray(), ImreadModes.Grayscale);
        return image.Empty() ? null : image;
    }
}
