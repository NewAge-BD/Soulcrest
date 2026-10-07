using System.Drawing.Imaging;
using OpenCvSharp;

namespace Soulcrest.Ocr;

/// <summary>Copies a bitmap into an owned BGR OpenCV matrix (from Grindcrest CompanionFrameDecoder).</summary>
public static class BitmapMat
{
    public static Mat ToBgr(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var converted = bitmap.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb
            ? null
            : bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
        var source = converted ?? bitmap;
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, source.PixelFormat);
        try
        {
            using var view = data.Stride >= 0
                ? Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, data.Scan0, data.Stride)
                : FlipBottomUp(data, source.Width, source.Height);
            var result = new Mat();
            Cv2.CvtColor(view, result, ColorConversionCodes.BGRA2BGR);
            return result;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static Mat FlipBottomUp(BitmapData data, int width, int height)
    {
        using var bottomUp = Mat.FromPixelData(height, width, MatType.CV_8UC4, IntPtr.Add(data.Scan0, data.Stride * (height - 1)), -data.Stride);
        var topDown = new Mat();
        Cv2.Flip(bottomUp, topDown, FlipMode.X);
        return topDown;
    }

    /// <summary>
    /// BGR matrix into a 32-bit bitmap by a direct pixel copy (no PNG round trip: ~260 ms for a 2560×1440
    /// screen with <see cref="ToBitmap"/>). Used for live captures.
    /// </summary>
    public static Bitmap ToBitmapFast(Mat bgr)
    {
        var bitmap = new Bitmap(bgr.Width, bgr.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bgr.Width, bgr.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var target = Mat.FromPixelData(bgr.Height, bgr.Width, MatType.CV_8UC4, data.Scan0, data.Stride);
            Cv2.CvtColor(bgr, target, ColorConversionCodes.BGR2BGRA);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public static Bitmap ToBitmap(Mat bgr)
    {
        Cv2.ImEncode(".png", bgr, out var png);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }
}
