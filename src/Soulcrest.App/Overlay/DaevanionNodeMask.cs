using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>The user's original round node cutout, interpreted only through its alpha channel.</summary>
public sealed class DaevanionNodeMask : IDisposable
{
    private const string ResourceName = "Soulcrest.DaevanionNodeRoundMask.png";
    private readonly record struct SourceMask(int Width, int Height, byte[] Coverage);
    private sealed record ScaledMask(byte[] Coverage, Rectangle Bounds);
    private readonly SourceMask _source;
    private readonly Dictionary<Size, ScaledMask> _scaled = [];
    private readonly Queue<Size> _cacheOrder = [];
    private readonly object _gate = new();
    private bool _disposed;

    public DaevanionNodeMask() : this(ReadResource()) { }
    internal DaevanionNodeMask(Bitmap original) : this(ReadAlpha(original)) { }
    private DaevanionNodeMask(SourceMask source) => _source = source;

    /// <summary>Bounds of the connected transparent interior, scaled into the complete node rectangle.</summary>
    public Rectangle ContentBounds(Size size) => GetScaled(size).Bounds;

    /// <summary>Multiplies premultiplied BGRA by the scaled interior alpha, preserving premultiplication.</summary>
    public void Apply(Bitmap premultipliedSurface)
    {
        ArgumentNullException.ThrowIfNull(premultipliedSurface);
        if (premultipliedSurface.PixelFormat != PixelFormat.Format32bppPArgb)
            throw new ArgumentException("The node surface must use premultiplied 32-bit alpha.", nameof(premultipliedSurface));
        var mask = GetScaled(premultipliedSurface.Size).Coverage;
        var data = premultipliedSurface.LockBits(new Rectangle(Point.Empty, premultipliedSurface.Size),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[premultipliedSurface.Width * 4];
            for (var y = 0; y < premultipliedSurface.Height; y++)
            {
                var address = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(address, row, 0, row.Length);
                for (var x = 0; x < premultipliedSurface.Width; x++)
                {
                    var weight = mask[y * premultipliedSurface.Width + x];
                    for (var channel = 0; channel < 4; channel++)
                        row[x * 4 + channel] = (byte)((row[x * 4 + channel] * weight + 127) / 255);
                }
                Marshal.Copy(row, 0, address, row.Length);
            }
        }
        finally { premultipliedSurface.UnlockBits(data); }
    }

    private ScaledMask GetScaled(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scaled.TryGetValue(size, out var cached)) return cached;
            var coverage = new byte[checked(size.Width * size.Height)];
            var left = size.Width; var top = size.Height; var right = -1; var bottom = -1;
            for (var y = 0; y < size.Height; y++)
            {
                var sy = Math.Clamp((y + .5) * _source.Height / size.Height - .5, 0, _source.Height - 1);
                var y0 = (int)sy; var y1 = Math.Min(y0 + 1, _source.Height - 1); var fy = sy - y0;
                for (var x = 0; x < size.Width; x++)
                {
                    var sx = Math.Clamp((x + .5) * _source.Width / size.Width - .5, 0, _source.Width - 1);
                    var x0 = (int)sx; var x1 = Math.Min(x0 + 1, _source.Width - 1); var fx = sx - x0;
                    var upper = _source.Coverage[y0 * _source.Width + x0] * (1 - fx) + _source.Coverage[y0 * _source.Width + x1] * fx;
                    var lower = _source.Coverage[y1 * _source.Width + x0] * (1 - fx) + _source.Coverage[y1 * _source.Width + x1] * fx;
                    var value = (byte)Math.Clamp((int)Math.Round(upper * (1 - fy) + lower * fy, MidpointRounding.AwayFromZero), 0, 255);
                    coverage[y * size.Width + x] = value;
                    if (value == 0) continue;
                    left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            }
            var bounds = right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            var result = new ScaledMask(coverage, bounds);
            // Window/DPI jitter must not retain every historical scale for the app's lifetime.
            if (_scaled.Count >= 16) _scaled.Remove(_cacheOrder.Dequeue());
            _scaled.Add(size, result); _cacheOrder.Enqueue(size);
            return result;
        }
    }

    private static SourceMask ReadResource()
    {
        using var stream = typeof(DaevanionNodeMask).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Missing embedded Daevanion node cutout.");
        using var image = new Bitmap(stream);
        return ReadAlpha(image);
    }

    private static SourceMask ReadAlpha(Bitmap original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var width = original.Width; var height = original.Height;
        var inverse = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            inverse[y * width + x] = (byte)(255 - original.GetPixel(x, y).A);
        var center = (height / 2) * width + width / 2;
        if (inverse[center] == 0) throw new InvalidDataException("The original cutout has no transparent center.");
        var connected = new byte[inverse.Length];
        var queue = new Queue<int>();
        connected[center] = inverse[center]; queue.Enqueue(center);
        while (queue.TryDequeue(out var at))
        {
            var x = at % width; var y = at / width;
            // Preserve connected feather pixels, while discarding any isolated exterior alpha.
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                var next = ny * width + nx;
                if (inverse[next] == 0 || connected[next] != 0) continue;
                connected[next] = inverse[next]; queue.Enqueue(next);
            }
        }
        return new(width, height, connected);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _scaled.Clear(); _cacheOrder.Clear(); }
    }
}
