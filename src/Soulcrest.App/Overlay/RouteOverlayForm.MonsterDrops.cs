using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Soulcrest.App.Overlay;

public sealed partial class RouteOverlayForm
{
    private static readonly Dictionary<(string Items, string Theme), Bitmap> MonsterDropSprites = [];

    internal static int MonsterDropRowWidth(int count) => count <= 0 ? 0 : Math.Min(3, count) * 22 + 2 + (count > 3 ? 26 : 0);

    private static void DrawMonsterDrops(Graphics graphics, PointF at, MonsterDropIcons drops, OverlayThemeDefinition theme)
    {
        lock (MonsterDropSprites)
        {
            var sprite = MonsterDropSprite(drops, theme);
            graphics.DrawImageUnscaled(sprite, (int)MathF.Round(at.X - sprite.Width / 2f), (int)MathF.Round(at.Y - sprite.Height / 2f));
        }
    }

    /// <summary>The whole selected icon row is composed once; optical-flow frames only copy its bitmap.</summary>
    internal static Bitmap MonsterDropSprite(MonsterDropIcons drops, OverlayThemeDefinition theme)
    {
        var key = (drops.CacheKey, theme.Id);
        lock (MonsterDropSprites)
        {
            if (MonsterDropSprites.TryGetValue(key, out var cached)) return cached;
            if (MonsterDropSprites.Count >= SpriteCacheLimit)
            {
                foreach (var old in MonsterDropSprites.Values) old.Dispose();
                MonsterDropSprites.Clear();
            }
            var count = drops.Icons.Count + drops.Additional;
            var sprite = new Bitmap(Math.Max(1, MonsterDropRowWidth(count)), 24, PixelFormat.Format32bppPArgb);
            using var canvas = Graphics.FromImage(sprite);
            canvas.Clear(Color.Transparent);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (var i = 0; i < drops.Icons.Count; i++)
                DrawResource(canvas, new PointF(12 + i * 22, 12), drops.Icons[i], null, 1, theme, false);
            if (drops.Additional > 0)
            {
                var x = drops.Icons.Count * 22 + 2;
                using var foreground = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                canvas.DrawString("+" + drops.Additional.ToString(System.Globalization.CultureInfo.InvariantCulture), font,
                    foreground, new RectangleF(x, 2, 24, 20), format);
            }
            MonsterDropSprites.Add(key, sprite);
            return sprite;
        }
    }
}
