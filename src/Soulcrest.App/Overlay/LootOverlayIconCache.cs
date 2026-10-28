using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;

namespace Soulcrest.App.Overlay;

/// <summary>Owns bounded local icon surfaces. Drawing and disposal share one lock; images never escape.</summary>
internal sealed class LootOverlayIconCache : IDisposable
{
    private const int IconSize = 64, MaximumFileBytes = 4 * 1024 * 1024, MaximumSourceExtent = 4096;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _recent = new();
    private bool _disposed;

    internal LootOverlayIconCache(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal bool Draw(Graphics graphics, string? asset, RectangleF bounds, Color? background = null)
    {
        lock (_gate)
        {
            if (_disposed || graphics is null || !ValidBounds(bounds)) return false;
            var path = Resolve(asset);
            if (path is null) return false;
            if (!_entries.TryGetValue(path, out var node))
            {
                node = _recent.AddFirst(new Entry(path, Load(path)));
                _entries.Add(path, node);
                while (_entries.Count > _capacity)
                {
                    var oldest = _recent.Last!;
                    _recent.RemoveLast();
                    _entries.Remove(oldest.Value.Path);
                    oldest.Value.Image?.Dispose();
                }
            }
            else
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
            }
            if (node.Value.Image is not { } image) return false;
            try
            {
                // An unavailable asset never leaves a replacement box. Keep the optional backing
                // under the same image/dispose lock, after successful loading and before one draw.
                if (background is { } color)
                {
                    using var backing = new SolidBrush(color);
                    graphics.FillRectangle(backing, bounds);
                }
                graphics.DrawImage(image, bounds, new RectangleF(0, 0, IconSize, IconSize), GraphicsUnit.Pixel);
                return true;
            }
            catch (Exception error) when (Unavailable(error)) { return false; }
        }
    }

    private static bool ValidBounds(RectangleF bounds) => float.IsFinite(bounds.X) && float.IsFinite(bounds.Y)
        && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;

    private static string? Resolve(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset) || asset.StartsWith('\\') || asset.StartsWith('/')) return null;
        try
        {
            string path;
            if (Path.IsPathFullyQualified(asset)) path = Path.GetFullPath(asset);
            else
            {
                var parts = asset.Replace('\\', '/').Split('/');
                if (parts.Length != 2 || !(parts[0].Equals("loot-icons", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("abyss-rank-icons", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("class-icons", StringComparison.OrdinalIgnoreCase))
                    || !ValidFileName(parts[1])) return null;
                path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", parts[0], parts[1]));
            }
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith('\\') || path[root.Length..].Contains(':')
                || new DriveInfo(root).DriveType == DriveType.Network || !ValidFileName(Path.GetFileName(path))) return null;
            return path;
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }

    private static bool ValidFileName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.')
            || !Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var device = name.Split('.')[0].ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal)
                || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] is >= '1' and <= '9');
    }

    private static Bitmap? Load(string path)
    {
        Bitmap? image = null;
        try
        {
            if (!RegularPath(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 33 or > MaximumFileBytes) return null;
            Span<byte> header = stackalloc byte[33];
            stream.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13
                || !header[12..16].SequenceEqual("IHDR"u8)) return null;
            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            if (width is 0 or > MaximumSourceExtent || height is 0 or > MaximumSourceExtent) return null;
            stream.Position = 0;
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (source.RawFormat.Guid != ImageFormat.Png.Guid || source.Width != width || source.Height != height) return null;
            image = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
            using (var target = Graphics.FromImage(image))
            {
                target.Clear(Color.Transparent);
                target.CompositingMode = CompositingMode.SourceCopy;
                target.CompositingQuality = CompositingQuality.HighQuality;
                target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var scale = Math.Min(IconSize / (float)source.Width, IconSize / (float)source.Height);
                var fitted = new RectangleF((IconSize - source.Width * scale) / 2, (IconSize - source.Height * scale) / 2,
                    source.Width * scale, source.Height * scale);
                target.DrawImage(source, fitted, new RectangleF(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return image;
        }
        catch (Exception error) when (Unavailable(error)) { image?.Dispose(); return null; }
    }

    private static bool RegularPath(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        while (ancestors.TryPop(out var current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;
            if (current == path && (attributes & FileAttributes.Directory) != 0) return false;
        }
        return true;
    }

    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or SecurityException or ExternalException or OutOfMemoryException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _recent) entry.Image?.Dispose();
            _recent.Clear();
            _entries.Clear();
        }
    }

    private sealed record Entry(string Path, Bitmap? Image);
}
