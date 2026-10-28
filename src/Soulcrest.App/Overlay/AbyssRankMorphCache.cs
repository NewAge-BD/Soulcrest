using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Bounded offline morph surfaces; the production renderer owns exact original endpoints.</summary>
public sealed class AbyssRankMorphCache : IDisposable
{
    public const int FrameCount = 61;
    public const int FrameSize = 64;
    public const int FrameCapacity = 128;
    public const int MaximumPairCount = 18;
    private const int AtlasColumns = 8;
    private const int AtlasSize = FrameSize * AtlasColumns;
    private const string ResourcePrefix = "Soulcrest.AbyssRankMorphs.";
    private readonly string? _directory;
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Image)>> _frames = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Bitmap Image)> _recent = new();
    private readonly Dictionary<string, Bitmap> _atlases = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly object _preloadGate = new();
    private bool _disposed;

    /// <summary>Uses bundled resources exclusively. Call PreloadAllAsync before starting the animation timer.</summary>
    public AbyssRankMorphCache() { }

    /// <summary>Offline authoring/preview only; directory frames may be loaded lazily by Draw.</summary>
    public AbyssRankMorphCache(string morphDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(morphDirectory);
        _directory = Path.GetFullPath(morphDirectory);
    }

    public int Count { get { lock (_gate) return _frames.Count; } }
    public int CachedPairCount { get { lock (_gate) return _atlases.Count; } }

    public bool IsPairReady(int fromStageIndex, int toStageIndex)
    {
        if (!TryPair(fromStageIndex, toStageIndex, out var pair)) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            return _directory is null ? _atlases.ContainsKey(pair)
                : Enumerable.Range(0, FrameCount).All(frame => _frames.ContainsKey(FrameKey(pair, frame)));
        }
    }

    /// <summary>Decode/copy resources on the caller's thread, before drawing begins.</summary>
    public bool PreloadPair(int fromStageIndex, int toStageIndex)
    {
        if (!TryPair(fromStageIndex, toStageIndex, out var pair)) return false;
        // Decode outside the drawing gate. A late background warmup must never
        // stall a frame while holding the lock used by the renderer.
        lock (_preloadGate)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_directory is null && _atlases.ContainsKey(pair)) return true;
            }
            if (_directory is not null)
            {
                for (var frame = 0; frame < FrameCount; frame++)
                {
                    lock (_gate)
                    {
                        if (_disposed || GetFrame(pair, frame) is null) return false;
                    }
                }
                return true;
            }
            var atlas = LoadAtlas(pair);
            if (atlas is null) return false;
            lock (_gate)
            {
                if (_disposed || _atlases.Count >= MaximumPairCount)
                {
                    atlas.Dispose();
                    return false;
                }
                _atlases.Add(pair, atlas);
                return true;
            }
        }
    }

    public Task<bool> PreloadPairAsync(int fromStageIndex, int toStageIndex, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = PreloadPair(fromStageIndex, toStageIndex);
            cancellationToken.ThrowIfCancellationRequested();
            return ready;
        }, cancellationToken);

    /// <summary>One bounded background warmup permits arbitrary multi-stage changes without IO in timer ticks.</summary>
    public Task<bool> PreloadAllAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var count = AbyssRankCatalog.Default.Stages.Count;
        if (count - 1 > MaximumPairCount) return false;
        for (var stage = 0; stage < count - 1; stage++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PreloadPair(stage, stage + 1)) return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }, cancellationToken);

    public bool Draw(Graphics graphics, int fromStageIndex, int toStageIndex, double upwardProgress, RectangleF bounds,
        double lightStrength = 0)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (!double.IsFinite(upwardProgress) || upwardProgress is <= 0 or >= 1
            || !TryPair(fromStageIndex, toStageIndex, out var pair)
            || !float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        var frame = Math.Clamp((int)Math.Round(upwardProgress * (FrameCount - 1)), 0, FrameCount - 1);
        lock (_gate)
        {
            if (_disposed) return false;
            var image = GetFrame(pair, frame);
            if (image is null) return false;
            graphics.DrawImage(image, bounds, new RectangleF(0, 0, FrameSize, FrameSize), GraphicsUnit.Pixel);
            if (double.IsFinite(lightStrength) && lightStrength > 0)
            {
                using var contour = ContourLight(image, upwardProgress, Math.Clamp(lightStrength, 0, 1));
                graphics.DrawImage(contour, bounds, new RectangleF(0, 0, 64, 64), GraphicsUnit.Pixel);
            }
            return true;
        }
    }

    private Bitmap? GetFrame(string pair, int frame)
    {
        var key = FrameKey(pair, frame);
        if (_frames.TryGetValue(key, out var node))
        {
            _recent.Remove(node); _recent.AddFirst(node);
            return node.Value.Image;
        }
        Bitmap? image = null;
        try
        {
            if (_directory is null)
            {
                // Deliberately no resource stream access or PNG decoding here.
                // Clone isolates each tile so interpolation cannot bleed an
                // adjacent atlas cell into icons touching the full canvas.
                if (!_atlases.TryGetValue(pair, out var atlas)) return null;
                image = atlas.Clone(new Rectangle(frame % AtlasColumns * FrameSize,
                    frame / AtlasColumns * FrameSize, FrameSize, FrameSize), PixelFormat.Format32bppPArgb);
            }
            else
            {
                var path = Path.Combine(_directory, pair, $"{frame:D3}.png");
                if (!File.Exists(path)) return null;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 4 * 1024 * 1024) return null;
                using var source = Image.FromStream(input, false, true);
                if (source.Width != 128 || source.Height != 128 || source.RawFormat.Guid != ImageFormat.Png.Guid) return null;
                image = new Bitmap(FrameSize, FrameSize, PixelFormat.Format32bppPArgb);
                using var target = Graphics.FromImage(image);
                target.Clear(Color.Transparent);
                target.CompositingMode = CompositingMode.SourceCopy;
                target.CompositingQuality = CompositingQuality.HighQuality;
                target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                target.DrawImage(source, new RectangleF(0, 0, FrameSize, FrameSize), new RectangleF(0, 0, 128, 128), GraphicsUnit.Pixel);
            }
            node = _recent.AddFirst((key, image));
            _frames.Add(key, node);
            while (_frames.Count > FrameCapacity)
            {
                var last = _recent.Last!;
                _recent.RemoveLast(); _frames.Remove(last.Value.Key); last.Value.Image.Dispose();
            }
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or ExternalException or OutOfMemoryException)
        { image?.Dispose(); return null; }
    }

    private static Bitmap? LoadAtlas(string pair)
    {
        Bitmap? atlas = null;
        try
        {
            using var input = typeof(AbyssRankMorphCache).Assembly.GetManifestResourceStream(ResourcePrefix + pair + ".png");
            if (input is null || input.Length > 4 * 1024 * 1024) return null;
            using var source = Image.FromStream(input, false, true);
            if (source.Width != AtlasSize || source.Height != AtlasSize || source.RawFormat.Guid != ImageFormat.Png.Guid) return null;
            atlas = new Bitmap(AtlasSize, AtlasSize, PixelFormat.Format32bppPArgb);
            using var target = Graphics.FromImage(atlas);
            target.Clear(Color.Transparent);
            target.CompositingMode = CompositingMode.SourceCopy;
            target.DrawImageUnscaled(source, 0, 0);
            return atlas;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or ExternalException or OutOfMemoryException)
        { atlas?.Dispose(); return null; }
    }

    private static string FrameKey(string pair, int frame) => pair + "/" + frame.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryPair(int fromStageIndex, int toStageIndex, out string pair)
    {
        pair = string.Empty;
        var catalog = AbyssRankCatalog.Default.Stages;
        var lower = Math.Min(fromStageIndex, toStageIndex);
        var higher = Math.Max(fromStageIndex, toStageIndex);
        if (lower < 0 || higher >= catalog.Count || higher - lower != 1) return false;
        pair = catalog[lower].Id + "--" + catalog[higher].Id;
        return true;
    }

    private static Bitmap ContourLight(Bitmap source, double progress, double strength)
    {
        var bounds = new Rectangle(0, 0, 64, 64);
        var sourceData = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var alpha = new byte[64 * 64];
        try
        {
            var bytes = new byte[Math.Abs(sourceData.Stride) * 64];
            Marshal.Copy(sourceData.Scan0, bytes, 0, bytes.Length);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++) alpha[y * 64 + x] = bytes[y * sourceData.Stride + x * 4 + 3];
        }
        finally { source.UnlockBits(sourceData); }
        var result = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        try
        {
            var data = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[Math.Abs(data.Stride) * 64];
                var bandY = 58 - progress * 52;
                for (var y = 1; y < 63; y++)
                for (var x = 1; x < 63; x++)
                {
                    var a = alpha[y * 64 + x];
                    var maximum = Math.Max(Math.Max(alpha[(y - 1) * 64 + x], alpha[(y + 1) * 64 + x]),
                        Math.Max(alpha[y * 64 + x - 1], alpha[y * 64 + x + 1]));
                    var minimum = Math.Min(Math.Min(alpha[(y - 1) * 64 + x], alpha[(y + 1) * 64 + x]),
                        Math.Min(alpha[y * 64 + x - 1], alpha[y * 64 + x + 1]));
                    var edge = Math.Clamp(Math.Max(0, maximum - a) + .4 * Math.Max(0, a - minimum), 0, 255) / 255;
                    var band = Math.Exp(-Math.Pow((y - bandY) / 10, 2));
                    var opacity = (byte)Math.Round(90 * strength * edge * band);
                    var index = y * data.Stride + x * 4;
                    bytes[index] = (byte)(165 * opacity / 255);
                    bytes[index + 1] = (byte)(221 * opacity / 255);
                    bytes[index + 2] = (byte)(244 * opacity / 255);
                    bytes[index + 3] = opacity;
                }
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally { result.UnlockBits(data); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _recent) entry.Image.Dispose();
            foreach (var atlas in _atlases.Values) atlas.Dispose();
            _atlases.Clear();
            _frames.Clear(); _recent.Clear();
        }
    }
}
