using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Soulcrest.App.Services;

/// <summary>
/// Tells whether the pet grid stands still between two scan frames. Compares only the pixels of the
/// card grid: the animated pet model in the middle of the window must not count as movement (user
/// report 2026-10-03: "Bild bewegt sich" all the time with a large idle animation).
/// </summary>
public sealed class GridMotion(double stillThreshold = 6)
{
    private const int ThumbnailWidth = 120;
    // Mean absolute grey difference (0–255) below which the grid counts as unchanged. A scroll of a few
    // pixels shifts every portrait and gives far more; capture noise and glow pulses stay well below.
    // Lower thresholds support sparse text lists; the pet-grid default remains 6.

    private Rectangle _lastArea;
    private byte[]? _lastPixels;
    private int _lastWidth;

    public void Reset()
    {
        _lastArea = Rectangle.Empty;
        _lastPixels = null;
    }

    public bool IsStill(Bitmap frame, IReadOnlyList<Rectangle> cards)
    {
        if (cards.Count == 0)
        {
            Reset();
            return false;
        }
        var area = cards.Aggregate(Rectangle.Union);
        area.Intersect(new Rectangle(Point.Empty, frame.Size));
        if (area.Width <= 0 || area.Height <= 0)
        {
            Reset();
            return false;
        }
        var (pixels, width) = Thumbnail(frame, area);
        // The grid itself moved (scrolled to a new row position or a row appeared/disappeared).
        var sameGrid = _lastPixels is not null && width == _lastWidth && pixels.Length == _lastPixels.Length
            && Math.Abs(area.X - _lastArea.X) <= 3 && Math.Abs(area.Y - _lastArea.Y) <= 3
            && Math.Abs(area.Width - _lastArea.Width) <= 3 && Math.Abs(area.Height - _lastArea.Height) <= 3;
        var still = sameGrid && MeanDifference(pixels, _lastPixels!) < stillThreshold;
        _lastArea = area;
        _lastPixels = pixels;
        _lastWidth = width;
        return still;
    }

    private static (byte[] Pixels, int Width) Thumbnail(Bitmap frame, Rectangle area)
    {
        var width = Math.Min(ThumbnailWidth, area.Width);
        var height = Math.Max(1, area.Height * width / area.Width);
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(frame, new Rectangle(0, 0, width, height), area, GraphicsUnit.Pixel);
        }
        var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var raw = new byte[data.Stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var grey = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * data.Stride + x * 3;
                    grey[y * width + x] = (byte)((raw[i] * 29 + raw[i + 1] * 150 + raw[i + 2] * 77) >> 8);
                }
            }
            return (grey, width);
        }
        finally
        {
            small.UnlockBits(data);
        }
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
