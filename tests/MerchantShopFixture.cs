using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Testing;

/// <summary>Synthetic shop edges shared by locator and presentation-gate regressions.</summary>
public static class MerchantShopFixture
{
    // Independent rounded measurements of the small-UI example, in a 2560 x 1440 game window.
    private static readonly Point[] ReferenceSeals =
    [
        new(512, 366), new(819, 435), new(1126, 460), new(1433, 460), new(1740, 435), new(2048, 366),
        new(571, 750), new(878, 805), new(1680, 805), new(1989, 750),
    ];

    public static void AddSealClutter(Mat image, bool right, int count = 64)
    {
        var size = (int)Math.Round(image.Height * .025);
        for (var index = 0; index < count; index++)
        {
            var x = right ? image.Width - 40 - index % 8 * 44 : 24 + index % 8 * 44;
            var y = (int)Math.Round(image.Height * .2) + index / 8 * (size + 30);
            var bounds = new Rect(x, y, size, size);
            Cv2.Rectangle(image, bounds, Scalar.All(28), -1);
            Cv2.Rectangle(image, bounds, Scalar.All(145), 1, LineTypes.AntiAlias);
        }
    }

    public static Point SealCenter(int slot, double scale) => new(
        (int)Math.Round(1280 + (ReferenceSeals[slot].X - 1280) * scale),
        (int)Math.Round(720 + (ReferenceSeals[slot].Y - 720) * scale));

    public static Point PriceCenter(int slot, double scale)
    {
        var seal = SealCenter(slot, scale);
        return new(seal.X, seal.Y + (int)Math.Round(268 * scale));
    }

    public static Mat CreateShop(double scale, bool whiteHover = false, int[]? missingSeals = null, int? missingPrice = null,
        bool frames = true, int sealOffsetY = 0, int layoutOffsetX = 0, int layoutOffsetY = 0)
    {
        var image = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(18));
        for (var slot = 0; slot < 10; slot++)
        {
            var seal = SealCenter(slot, scale);
            var price = PriceCenter(slot, scale);
            seal = new(seal.X + layoutOffsetX, seal.Y + layoutOffsetY);
            price = new(price.X + layoutOffsetX, price.Y + layoutOffsetY);
            var border = whiteHover && slot % 2 == 1 ? Scalar.All(230) : new Scalar(105, 135, 150);
            var halfWidth = (int)Math.Round(106 * scale);
            if (frames)
            {
                Cv2.Rectangle(image, new Rect(seal.X - halfWidth, seal.Y - (int)Math.Round(10 * scale), halfWidth * 2,
                    price.Y - seal.Y + (int)Math.Round(28 * scale)), border, 1, LineTypes.AntiAlias);
                var lower = new Point(seal.X, price.Y + (int)Math.Round(47 * scale));
                Cv2.Line(image, new Point(seal.X - halfWidth, price.Y + (int)Math.Round(18 * scale)), lower, border, 1, LineTypes.AntiAlias);
                Cv2.Line(image, lower, new Point(seal.X + halfWidth, price.Y + (int)Math.Round(18 * scale)), border, 1, LineTypes.AntiAlias);
            }
            if (missingSeals?.Contains(slot) != true)
            {
                var size = (int)Math.Round(36 * scale);
                var sealBounds = new Rect(seal.X - size / 2, seal.Y - size / 2 + sealOffsetY, size, size);
                Cv2.Rectangle(image, sealBounds, Scalar.All(28), -1);
                Cv2.Rectangle(image, sealBounds, border, 1, LineTypes.AntiAlias);
            }
            if (missingPrice == slot) continue;
            var width = (int)Math.Round(146 * scale);
            var height = (int)Math.Round(30 * scale);
            var radius = height / 2;
            var left = new Point(price.X - width / 2 + radius, price.Y);
            var right = new Point(price.X + width / 2 - radius, price.Y);
            Cv2.Ellipse(image, left, new Size(radius, radius), 0, 90, 270, border, 1, LineTypes.AntiAlias);
            Cv2.Ellipse(image, right, new Size(radius, radius), 0, -90, 90, border, 1, LineTypes.AntiAlias);
            Cv2.Line(image, new Point(left.X, price.Y - radius), new Point(right.X, price.Y - radius), border, 1, LineTypes.AntiAlias);
            Cv2.Line(image, new Point(left.X, price.Y + radius), new Point(right.X, price.Y + radius), border, 1, LineTypes.AntiAlias);
        }
        return image;
    }
}
