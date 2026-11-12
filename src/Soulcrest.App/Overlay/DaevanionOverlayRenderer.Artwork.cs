using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

public sealed partial class DaevanionOverlayRenderer
{
    private readonly record struct ArtworkKey(DaevanionNode Node, string Family, Size Size, string Language,
        DaevanionNodeAppearance Appearance);
    private sealed record NodeArtwork(Bitmap Image, int Padding);
    private readonly Dictionary<ArtworkKey, NodeArtwork> _artwork = [];
    private readonly Queue<ArtworkKey> _artworkOrder = [];
    private long _artworkPixels;
    private const int MaxArtworkCount = 512;
    private const long MaxArtworkPixels = 8_000_000;

    private NodeArtwork GetArtwork(DaevanionBoard board, DaevanionNode node, Size size, string language,
        DaevanionNodeAppearance appearance, out bool cached)
    {
        var key = new ArtworkKey(node, board.FamilyId, size, language, appearance);
        if (_artwork.TryGetValue(key, out var found)) { cached = true; return found; }
        const int padding = 0; // Native engraving has no exterior fitting or gap-dependent artwork.
        var image = new Bitmap(size.Width + padding * 2, size.Height + padding * 2, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            PaintNode(graphics, node, new(padding, padding, size.Width, size.Height), language, appearance);
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

    private static void PaintBackground(Graphics graphics, Rectangle nodeBounds, Rectangle inside,
        DaevanionNodeQuality quality, DaevanionNodeAppearance appearance, bool doubleBonus)
    {
        // This is the game's empty stat tile material, not an invented individual stat icon.
        // Draw its full native canvas before clipping/masking so its engraving remains aligned.
        DaevanionNativeArtwork.StatBackground.Paint(graphics, nodeBounds);
        using var shade = new SolidBrush(Color.FromArgb(doubleBonus ? 150 : 125, 13, 21, 23));
        graphics.FillRectangle(shade, inside);
        if (appearance != DaevanionNodeAppearance.Active) return;
        // Only the confirmed native active state lights the material. Unknown and inactive
        // remain identical; a blue level-up pulse alone is not an activation signal.
        var tint = quality switch
        {
            DaevanionNodeQuality.Rare => Color.FromArgb(110, 150, 210, 170),
            DaevanionNodeQuality.Legend => Color.FromArgb(110, 160, 200, 240),
            DaevanionNodeQuality.Unique => Color.FromArgb(110, 240, 190, 120),
            _ => Color.FromArgb(110, 185, 200, 215)
        };
        using var wash = new SolidBrush(tint);
        graphics.FillRectangle(wash, inside);
    }

    private void ClearArtwork()
    {
        foreach (var artwork in _artwork.Values) artwork.Image.Dispose();
        _artwork.Clear(); _artworkOrder.Clear(); _artworkPixels = 0;
    }
}
