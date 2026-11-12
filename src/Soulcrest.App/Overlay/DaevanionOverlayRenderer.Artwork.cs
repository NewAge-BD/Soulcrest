using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

public sealed partial class DaevanionOverlayRenderer
{
    private readonly record struct ArtworkKey(DaevanionNode Node, string Family, Size Size, string Language,
        DaevanionNodeAppearance Appearance, RectangleF FittingBounds);
    private sealed record NodeArtwork(Bitmap Image, int Padding);
    private readonly Dictionary<ArtworkKey, NodeArtwork> _artwork = [];
    private readonly Queue<ArtworkKey> _artworkOrder = [];
    private long _artworkPixels;
    private const int MaxArtworkCount = 512;
    private const long MaxArtworkPixels = 8_000_000;
    internal int ArtworkCacheCount => _artwork.Count;
    internal long ArtworkCachePixels => _artworkPixels;

    private readonly record struct BackgroundKey(Size Size, Point Origin, bool Active, DaevanionNodeQuality Quality, bool Shaded);
    private readonly Dictionary<BackgroundKey, Bitmap> _backgrounds = [];
    private readonly Queue<BackgroundKey> _backgroundOrder = [];
    private long _backgroundPixels;
    private const int MaxBackgroundCount = 64;
    private const long MaxBackgroundPixels = 1_000_000;
    internal int BackgroundCacheCount => _backgrounds.Count;
    internal long BackgroundCachePixels => _backgroundPixels;

    private NodeArtwork GetArtwork(DaevanionBoard board, DaevanionNode node, Size size, string language,
        DaevanionNodeAppearance appearance, double? spacing, out bool cached)
    {
        var frame = new Rectangle(Point.Empty, size);
        var extended = node.Kind == DaevanionNodeKind.Stat && DaevanionNodeFitting.CanExtend(frame, spacing);
        // Saturated gaps and nonextended interiors share the same artwork. Raw frame-to-frame
        // pitch jitter must not retain identical bitmaps or evict useful language/appearance entries.
        var fitting = extended ? DaevanionNodeFitting.Bounds(frame, spacing) : RectangleF.Empty;
        var materialAppearance = node.Kind == DaevanionNodeKind.Stat ? appearance : DaevanionNodeAppearance.Unknown;
        var key = new ArtworkKey(node, board.FamilyId, size, language, materialAppearance, fitting);
        if (_artwork.TryGetValue(key, out var found)) { cached = true; return found; }
        var padding = extended ? Math.Max(2, (int)Math.Ceiling(Math.Max(size.Width, size.Height) * .20) + 1) : 0;
        var image = new Bitmap(size.Width + padding * 2, size.Height + padding * 2, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            PaintNode(graphics, board, node, new(padding, padding, size.Width, size.Height), language, appearance, spacing);
        }
        catch { image.Dispose(); throw; }
        var artwork = new NodeArtwork(image, padding);
        var pixels = (long)image.Width * image.Height;
        cached = pixels <= MaxArtworkPixels;
        if (!cached) return artwork;
        while (_artwork.Count >= MaxArtworkCount || _artworkPixels + pixels > MaxArtworkPixels)
        {
            var oldest = _artworkOrder.Dequeue();
            var retired = _artwork[oldest];
            _artworkPixels -= (long)retired.Image.Width * retired.Image.Height;
            _artwork.Remove(oldest); retired.Image.Dispose();
        }
        _artwork.Add(key, artwork); _artworkOrder.Enqueue(key); _artworkPixels += pixels;
        return artwork;
    }

    internal void PaintBackground(Graphics graphics, Rectangle inside, DaevanionNodeQuality quality,
        DaevanionNodeAppearance appearance, bool extended)
    {
        var active = appearance == DaevanionNodeAppearance.Active;
        var activeQuality = quality is DaevanionNodeQuality.Rare or DaevanionNodeQuality.Legend or DaevanionNodeQuality.Unique
            ? quality : DaevanionNodeQuality.Common;
        var key = new BackgroundKey(inside.Size, inside.Location, active,
            active ? activeQuality : DaevanionNodeQuality.None, !active && extended);
        if (_backgrounds.TryGetValue(key, out var cached))
        {
            PaintMaterial(graphics, cached, inside.Location);
            return;
        }
        Bitmap material;
        // GDI+ can round a bicubic reduction differently at a different integer origin.
        // Preserve the exact node-local raster phase, then copy its ROI without resampling.
        using (var raster = new Bitmap(inside.Right + Math.Max(1, inside.X),
            inside.Bottom + Math.Max(1, inside.Y), PixelFormat.Format32bppPArgb))
        {
            using (var canvas = Graphics.FromImage(raster))
            {
                canvas.Clear(Color.Transparent);
                canvas.PageUnit = GraphicsUnit.Pixel;
                canvas.SmoothingMode = SmoothingMode.AntiAlias;
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                canvas.SetClip(inside);
                PaintBackgroundDirect(canvas, inside, quality, appearance, extended);
            }
            // Only this small ROI remains cached; the padded producer raster is transient.
            material = raster.Clone(inside, PixelFormat.Format32bppPArgb);
        }
        var pixels = (long)material.Width * material.Height;
        if (pixels > MaxBackgroundPixels)
        {
            try { PaintMaterial(graphics, material, inside.Location); }
            finally { material.Dispose(); }
            return;
        }
        while (_backgrounds.Count >= MaxBackgroundCount || _backgroundPixels + pixels > MaxBackgroundPixels)
        {
            var oldest = _backgroundOrder.Dequeue();
            var retired = _backgrounds[oldest];
            _backgroundPixels -= (long)retired.Width * retired.Height;
            _backgrounds.Remove(oldest); retired.Dispose();
        }
        _backgrounds.Add(key, material); _backgroundOrder.Enqueue(key); _backgroundPixels += pixels;
        PaintMaterial(graphics, material, inside.Location);
    }

    private static void PaintMaterial(Graphics graphics, Bitmap material, Point location)
    {
        var saved = graphics.Save();
        try
        {
            // Cached pixels are already filtered. A second bicubic pass attenuates
            // transparent bitmap-edge samples even when DrawImageUnscaled is used.
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImageUnscaled(material, location);
        }
        finally { graphics.Restore(saved); }
    }

    // Keep the original composition as the material producer and pixel-equality reference.
    internal void PaintBackgroundDirect(Graphics graphics, Rectangle inside, DaevanionNodeQuality quality,
        DaevanionNodeAppearance appearance, bool extended)
    {
        if (appearance == DaevanionNodeAppearance.Active)
        {
            // Brighten the supplied stone itself; keep the number ink and original activation ring.
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix([
                [1.45f, 0, 0, 0, 0], [0, 1.45f, 0, 0, 0], [0, 0, 1.45f, 0, 0],
                [0, 0, 0, 1, 0], [.035f, .035f, .035f, 0, 1]]));
            graphics.DrawImage(_background, inside, BackgroundCrop.X, BackgroundCrop.Y, BackgroundCrop.Width,
                BackgroundCrop.Height, GraphicsUnit.Pixel, attributes);
            var tint = quality switch
            {
                DaevanionNodeQuality.Rare => Color.FromArgb(42, 100, 180, 135),
                DaevanionNodeQuality.Legend => Color.FromArgb(42, 115, 166, 223),
                DaevanionNodeQuality.Unique => Color.FromArgb(42, 230, 157, 82),
                _ => Color.FromArgb(42, 143, 158, 174)
            };
            using var wash = new SolidBrush(tint);
            graphics.FillRectangle(wash, inside);
        }
        else
        {
            graphics.DrawImage(_background, inside, BackgroundCrop, GraphicsUnit.Pixel);
            if (!extended) return;
            using var shade = new SolidBrush(Color.FromArgb(68, 9, 14, 14));
            graphics.FillRectangle(shade, inside);
        }
    }

    private void ClearArtwork()
    {
        foreach (var artwork in _artwork.Values) artwork.Image.Dispose();
        _artwork.Clear(); _artworkOrder.Clear(); _artworkPixels = 0;
        foreach (var background in _backgrounds.Values) background.Dispose();
        _backgrounds.Clear(); _backgroundOrder.Clear(); _backgroundPixels = 0;
    }
}
