using System.Drawing;
using OpenCvSharp;
using Soulcrest.Ocr;

namespace Soulcrest.App.Services;

/// <summary>
/// A second, list-only OCR view for dimmed exploration names. The original capture remains
/// the evidence for completion symbols and motion; neither the image size nor coordinates change.
/// </summary>
internal static class ExplorationTextEnhancer
{
    internal static Bitmap Create(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var bgr = BitmapMat.ToBgr(source);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var enhanced = new Mat();
        // Bounded local contrast restores gray letter strokes against a translucent map.
        // No thresholding, dilation or erosion: those merge punctuation and neighboring strokes.
        using var contrast = Cv2.CreateCLAHE(3, new OpenCvSharp.Size(8, 16));
        contrast.Apply(gray, enhanced);
        using var output = new Mat();
        Cv2.CvtColor(enhanced, output, ColorConversionCodes.GRAY2BGR);
        return BitmapMat.ToBitmapFast(output);
    }
}
