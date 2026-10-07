using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Capture;

/// <summary>
/// Desktop capture of a screen rectangle (GDI). Simple and passive; the game window must be
/// borderless/windowed. On HDR desktops Windows delivers a tone-mapped SDR image, which the
/// OCR handles. Windows Graphics Capture with FP16 (Grindcrest) is the planned upgrade.
/// Soulcrest's own overlay is excluded from capture via display affinity.
/// </summary>
public static class ScreenCapture
{
    public static Bitmap Capture(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Captures a screen rectangle directly at a smaller size (StretchBlt with averaging): for the
    /// full-screen world map at a quarter of the size, without copying all pixels first. Live, the
    /// full-size copy plus resize held the world-map overlay at about 15 frames per second.
    /// </summary>
    public static Bitmap CaptureScaled(Rectangle region, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(region.Width * scale));
        var height = Math.Max(1, (int)Math.Round(region.Height * scale));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var target = graphics.GetHdc();
        var screen = GetDC(0);
        try
        {
            SetStretchBltMode(target, Halftone);
            SetBrushOrgEx(target, 0, 0, 0);
            StretchBlt(target, 0, 0, width, height, screen, region.X, region.Y, region.Width, region.Height, SourceCopy);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
            graphics.ReleaseHdc(target);
        }
        return bitmap;
    }

    private const int Halftone = 4;
    private const int SourceCopy = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(nint hdc, int x, int y, nint previous);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint target, int x, int y, int width, int height, nint source,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);

    /// <summary>Cheap fingerprint to skip OCR when the region did not change.</summary>
    public static ulong Fingerprint(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var hash = 14695981039346656037UL;
            var stepY = Math.Max(1, bitmap.Height / 64);
            var stepX = Math.Max(1, bitmap.Width / 64);
            var row = new byte[data.Stride];
            for (var y = 0; y < bitmap.Height; y += stepY)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    var i = x * 4;
                    // Quantise to ignore sensor-like noise from the animated background.
                    hash = (hash ^ (ulong)((row[i] >> 3) | (row[i + 1] >> 3 << 5) | (row[i + 2] >> 3 << 10))) * 1099511628211UL;
                }
            }
            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static string ToDataUrl(Bitmap bitmap, int maxWidth = 520)
    {
        var scale = Math.Min(1.0, (double)maxWidth / bitmap.Width);
        using var small = new Bitmap(bitmap, new Size(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))));
        using var stream = new MemoryStream();
        small.Save(stream, ImageFormat.Jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }
}
