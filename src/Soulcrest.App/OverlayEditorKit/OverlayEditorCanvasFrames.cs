using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Original native panel pixels for every DOM canvas; independent of snapshots and tracking.</summary>
public static class OverlayEditorCanvasFrames
{
    private static readonly OverlayCanvasFrameCache Cache = new();
    public static string GetDataUri(string? themeId, double width, double height, double backgroundOpacity = 1, bool showBorder = true) =>
        Cache.GetDataUri(themeId, width, height, backgroundOpacity, showBorder);

    public static Size LogicalSize(double width, double height) => new(
        Dimension(width, 360, 1600), Dimension(height, 260, 1200));
    private static int Dimension(double value, int fallback, int maximum) =>
        !double.IsFinite(value) || value <= 0 ? fallback : (int)Math.Ceiling(Math.Clamp(value, 1, maximum));
    internal static double Opacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;

    internal static string Paint(OverlayCanvasFrameKey key)
    {
        using var bitmap = new Bitmap(key.Width, key.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            OverlayThemeDefinition.Resolve(key.ThemeId).Panel(graphics, new(Point.Empty, bitmap.Size), key.Opacity, key.ShowBorder);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
    }
}

internal readonly record struct OverlayCanvasFrameKey(string ThemeId, int Width, int Height, double Opacity, bool ShowBorder);

internal sealed class OverlayCanvasFrameCache
{
    private sealed record Entry(OverlayCanvasFrameKey Key, string Uri, long Bytes);
    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Func<OverlayCanvasFrameKey, string> _paint;
    private readonly Dictionary<OverlayCanvasFrameKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    internal OverlayCanvasFrameCache(int maximumEntries = 64, long maximumBytes = 8 * 1024 * 1024,
        Func<OverlayCanvasFrameKey, string>? paint = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes; _paint = paint ?? OverlayEditorCanvasFrames.Paint;
    }

    internal int EntryCount { get { lock (_gate) return _entries.Count; } }
    internal long CachedBytes { get { lock (_gate) return _bytes; } }

    internal string GetDataUri(string? themeId, double width, double height, double opacity = 1, bool showBorder = true)
    {
        var size = OverlayEditorCanvasFrames.LogicalSize(width, height);
        var key = new OverlayCanvasFrameKey(OverlayEditorThemes.ResolveThemeId(themeId), size.Width, size.Height,
            OverlayEditorCanvasFrames.Opacity(opacity), showBorder);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                _lru.Remove(cached); _lru.AddFirst(cached);
                return cached.Value.Uri;
            }
            var uri = _paint(key);
            // Account for the actual UTF-16 string storage rather than only its ASCII payload.
            var bytes = (long)uri.Length * sizeof(char);
            if (bytes > _maximumBytes) return uri;
            while (_entries.Count >= _maximumEntries || _bytes + bytes > _maximumBytes)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast(); _entries.Remove(oldest.Value.Key); _bytes -= oldest.Value.Bytes;
            }
            var node = _lru.AddFirst(new Entry(key, uri, bytes));
            _entries.Add(key, node); _bytes += bytes;
            return uri;
        }
    }
}
