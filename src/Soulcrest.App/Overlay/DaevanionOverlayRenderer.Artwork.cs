using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

public sealed partial class DaevanionOverlayRenderer
{
    private readonly record struct ArtworkKey(DaevanionNode Node, string Family, Size Size, string Language,
        DaevanionNodeAppearance Appearance, double? Spacing);
    private sealed record NodeArtwork(Bitmap Image, int Padding);
    private readonly Dictionary<ArtworkKey, NodeArtwork> _artwork = [];
    private readonly Queue<ArtworkKey> _artworkOrder = [];
    private long _artworkPixels;
    private const int MaxArtworkCount = 512;
    private const long MaxArtworkPixels = 8_000_000;

    private NodeArtwork GetArtwork(DaevanionBoard board, DaevanionNode node, Size size, string language,
        DaevanionNodeAppearance appearance, double? spacing, out bool cached)
    {
        var key = new ArtworkKey(node, board.FamilyId, size, language, appearance, spacing);
        if (_artwork.TryGetValue(key, out var found)) { cached = true; return found; }
        var padding = Math.Max(2, (int)Math.Ceiling(Math.Max(size.Width, size.Height) * .20) + 1);
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

    private void PaintBackground(Graphics graphics, Rectangle inside, DaevanionNodeQuality quality,
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
    }
}
