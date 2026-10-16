using OpenCvSharp;
using Soulcrest.Ocr.Shop;
using Xunit;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Ocr.Tests;

/// <summary>Procedurally drawn shop geometry; no game screenshots, item artwork or OCR fixtures.</summary>
public sealed class MerchantCardLocatorTests
{
    // Independent rounded measurements of the small-UI example, in a 2560 x 1440 game window.
    private static readonly Point[] ReferenceSeals =
    [
        new(512, 366), new(819, 435), new(1126, 460), new(1433, 460), new(1740, 435), new(2048, 366),
        new(571, 750), new(878, 805), new(1680, 805), new(1989, 750),
    ];

    [Theory]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void FindsAllTenPriceButtonsAcrossTheFiveUiScales(double scale)
    {
        using var image = Shop(scale);
        var anchors = MerchantCardLocator.Find(image);
        Assert.Equal(10, anchors.Count);
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var price = PriceCenter(index, scale);
            Assert.Equal(index, anchor.SlotIndex);
            Assert.InRange(Math.Abs(anchor.PriceBounds.X + anchor.PriceBounds.Width / 2.0 - price.X), 0, 4);
            Assert.InRange(Math.Abs(anchor.PriceBounds.Y + anchor.PriceBounds.Height / 2.0 - price.Y), 0, 4);
            Assert.InRange(anchor.PriceBounds.Width, 142 * scale, 150 * scale);
            Assert.InRange(anchor.Scale, scale * .96, scale * 1.04);
            Assert.InRange(anchor.Confidence, .72, 1);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void SupportsGrayscaleBgrAndBgraWithoutMutatingTheInput(int channels)
    {
        using var color = Shop(1);
        using var image = new Mat();
        if (channels == 3) color.CopyTo(image);
        else Cv2.CvtColor(color, image, channels == 1 ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGR2BGRA);
        using var before = image.Clone();
        Assert.Equal(10, MerchantCardLocator.Find(image).Count);
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(.75)]
    public void CoordinatesFollowTheActualImageResolution(double frameScale)
    {
        using var full = Shop(1);
        using var reduced = new Mat();
        Cv2.Resize(full, reduced, new Size(), frameScale, frameScale, InterpolationFlags.Area);
        var anchors = MerchantCardLocator.Find(reduced);
        Assert.Equal(10, anchors.Count);
        for (var slot = 0; slot < 10; slot++)
        {
            var price = PriceCenter(slot, 1);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.X + anchors[slot].PriceBounds.Width / 2.0 - price.X * frameScale), 0, 4);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.Y + anchors[slot].PriceBounds.Height / 2.0 - price.Y * frameScale), 0, 4);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardArtworkAndWhiteHoverFramesDoNotChangeTheAnchors(bool whiteHover)
    {
        using var image = Shop(1, whiteHover: whiteHover);
        // Arbitrary different contents stand in for pets, non-pets, ownership labels and sold-out bands.
        for (var index = 0; index < 10; index++)
        {
            var seal = SealCenter(index, 1);
            Cv2.Circle(image, new Point(seal.X, seal.Y + 120), 36, new Scalar(index * 17, 30, 180 - index * 10), -1);
            if (index % 2 == 0)
                Cv2.Rectangle(image, new Rect(seal.X - 70, seal.Y + 170, 140, 22), new Scalar(35, 35, 35), -1);
        }
        var anchors = MerchantCardLocator.Find(image);
        Assert.Equal(10, anchors.Count);
        Assert.Equal(Enumerable.Range(0, 10), anchors.Select(anchor => anchor.SlotIndex));
    }

    [Fact]
    public void TwoFaintSealsAreAllowedOnlyWhileEveryCardAndPriceRemainVerified()
    {
        using var image = Shop(1, missingSeals: [6, 8]);
        Assert.Equal(10, MerchantCardLocator.Find(image).Count);
        var price = PriceCenter(8, 1);
        Cv2.Rectangle(image, new Rect(price.X - 80, price.Y - 20, 160, 40), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void ThreeMissingSealsNeverProduceAnAssumedTenCardLayout()
    {
        using var image = Shop(1, missingSeals: [6, 7, 8]);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void OneMissingPriceRejectsTheEntireLayout(int slot)
    {
        using var image = Shop(1, missingPrice: slot);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void PriceButtonsAndSealsWithoutTheCardFramesAreInsufficient()
    {
        using var image = Shop(1, frames: false);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void ClosingTheShopDoesNotRetainThePreviousResult()
    {
        using var image = Shop(1);
        Assert.Equal(10, MerchantCardLocator.Find(image).Count);
        image.SetTo(Scalar.All(18));
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void UnrelatedBoxesAndAClippedOrDistortedShopFailClosed()
    {
        using var boxes = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(18));
        for (var index = 0; index < 10; index++)
            Cv2.Rectangle(boxes, new Rect(180 + index * 200, 350 + index % 2 * 320, 36, 36), Scalar.All(160), 2);
        Assert.Empty(MerchantCardLocator.Find(boxes));
        using var full = Shop(1);
        using var clipped = new Mat(full, new Rect(0, 0, 2560, 1000));
        Assert.Empty(MerchantCardLocator.Find(clipped));
        using var distorted = new Mat();
        Cv2.Resize(full, distorted, new Size(), 1.15, 1, InterpolationFlags.Linear);
        Assert.Empty(MerchantCardLocator.Find(distorted));
    }

    [Fact]
    public void EmptyAndUnsupportedPixelFormatsReturnNoAnchors()
    {
        using var empty = new Mat();
        using var floating = new Mat(1440, 2560, MatType.CV_32FC3, Scalar.All(0));
        using var twoChannels = new Mat(100, 160, MatType.CV_8UC2, Scalar.All(0));
        Assert.Empty(MerchantCardLocator.Find(empty));
        Assert.Empty(MerchantCardLocator.Find(floating));
        Assert.Empty(MerchantCardLocator.Find(twoChannels));
    }

    [Fact]
    public void ReducedSealQuantizationCanUseOneSharedPixelCorrection()
    {
        using var original = Shop(.9375, sealOffsetY: 4);
        using var reduced = new Mat();
        Cv2.Resize(original, reduced, new Size(), .75, .75, InterpolationFlags.Area);
        var anchors = MerchantCardLocator.Find(reduced);
        Assert.Equal(10, anchors.Count);
        for (var slot = 0; slot < anchors.Count; slot++)
        {
            var price = PriceCenter(slot, .9375);
            Assert.Equal(slot, anchors[slot].SlotIndex);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.X + anchors[slot].PriceBounds.Width / 2d - price.X * .75), 0, 3);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.Y + anchors[slot].PriceBounds.Height / 2d - price.Y * .75), 0, 3);
            Assert.InRange(anchors[slot].Scale, .9375 * .75 * .96, .9375 * .75 * 1.04);
        }
    }

    [Fact]
    public void ADisplacementBeyondTheSinglePixelCorrectionStillFailsClosed()
    {
        using var original = Shop(.9375, sealOffsetY: 5);
        using var reduced = new Mat();
        Cv2.Resize(original, reduced, new Size(), .75, .75, InterpolationFlags.Area);
        Assert.Empty(MerchantCardLocator.Find(reduced));
    }

    [Fact]
    public void TinyGapsInReducedSealsCanBeRepairedWithoutChangingPriceOrSideThresholds()
    {
        using var reduced = ReducedShopWithTinySealGaps();
        var anchors = MerchantCardLocator.Find(reduced);
        Assert.Equal(10, anchors.Count);
        Assert.All(anchors, anchor => Assert.InRange(anchor.Confidence, .72, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void SealGapRepairAndTranslationCannotReplaceAMissingRawPrice(int slot)
    {
        using var reduced = ReducedShopWithTinySealGaps();
        var price = PriceCenter(slot, .9375);
        var center = new Point((int)Math.Round(price.X * .75), (int)Math.Round(price.Y * .75));
        Cv2.Rectangle(reduced, new Rect(center.X - 60, center.Y - 16, 120, 32), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(reduced));
    }

    [Fact]
    public void ThreeMissingReducedSealsRemainInsufficientAfterGapRepair()
    {
        using var reduced = ReducedShopWithTinySealGaps(missingSeals: [6, 7, 8]);
        Assert.Empty(MerchantCardLocator.Find(reduced));
    }

    [Fact]
    public void SealGapRepairIsNotAppliedToAWideImage()
    {
        using var reduced = ReducedShopWithTinySealGaps();
        using var wide = new Mat(reduced.Height, 2560, MatType.CV_8UC3, Scalar.All(18));
        using var target = new Mat(wide, new Rect(0, 0, reduced.Width, reduced.Height));
        reduced.CopyTo(target);
        Assert.Empty(MerchantCardLocator.Find(wide));
    }
    [Fact]
    public void FineAntialiasedSealCornersAt720pAreNormalizedOnceWithoutChangingInput()
    {
        using var image = FineSealsAt720p();
        using var before = image.Clone();
        var anchors = MerchantCardLocator.Find(image);
        Assert.Equal(10, anchors.Count);
        for (var slot = 0; slot < anchors.Count; slot++)
        {
            var price = PriceCenter(slot, .9375);
            Assert.Equal(slot, anchors[slot].SlotIndex);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.X + anchors[slot].PriceBounds.Width / 2d - price.X * .5), 0, 2);
            Assert.InRange(Math.Abs(anchors[slot].PriceBounds.Y + anchors[slot].PriceBounds.Height / 2d - price.Y * .5), 0, 2);
            Assert.InRange(anchors[slot].Scale, .9375 * .5 * .96, .9375 * .5 * 1.04);
            Assert.InRange(anchors[slot].Confidence, .72, 1);
        }
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void LowResolutionNormalizationCannotReplaceAMissingRawPrice(int slot)
    {
        using var image = FineSealsAt720p();
        var price = PriceCenter(slot, .9375);
        var center = new Point((int)Math.Round(price.X * .5), (int)Math.Round(price.Y * .5));
        Cv2.Rectangle(image, new Rect(center.X - 40, center.Y - 12, 80, 24), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void LowResolutionNormalizationCannotReplaceAMissingRawCardSide()
    {
        using var image = FineSealsAt720p();
        var seal = SealCenter(0, .9375);
        var left = (int)Math.Round((seal.X - (int)Math.Round(106 * .9375)) * .5);
        var top = (int)Math.Round(seal.Y * .5) + 5;
        var height = (int)Math.Round(250 * .9375 * .5) - 10;
        Cv2.Rectangle(image, new Rect(left - 6, top, 12, height), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Fact]
    public void LowResolutionNormalizationStillRequiresEightIndependentSeals()
    {
        using var image = FineSealsAt720p(missingSeals: [6, 7, 8]);
        Assert.Empty(MerchantCardLocator.Find(image));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void PreferredGeometryIsRevalidatedWithoutFollowingSealQuantizationOrHover(bool whiteHover, int sealOffset)
    {
        using var original = Shop(1);
        var previous = MerchantCardLocator.Find(original);
        Assert.Equal(10, previous.Count);
        using var current = Shop(1, whiteHover: whiteHover, sealOffsetY: sealOffset);
        using var before = current.Clone();

        var anchors = MerchantCardLocator.Find(current, Enumerable.Reverse(previous).ToArray());

        AssertSameGeometry(previous, anchors);
        Assert.All(anchors, anchor => Assert.InRange(anchor.Confidence, .72, 1));
        Assert.Equal(0, Cv2.Norm(before, current, NormTypes.INF));
    }

    [Fact]
    public void AFalseOrInconsistentPreferredLayoutFallsBackToTheCurrentImage()
    {
        using var image = Shop(1);
        var expected = MerchantCardLocator.Find(image);
        Assert.Equal(10, expected.Count);
        var moved = expected.Select(anchor => anchor with
        {
            PriceBounds = new Rect(anchor.PriceBounds.X + 40, anchor.PriceBounds.Y + 25,
                anchor.PriceBounds.Width, anchor.PriceBounds.Height)
        }).ToArray();
        var scaled = expected.Select(anchor => anchor with { Scale = anchor.Scale * 1.25 }).ToArray();
        var inconsistent = expected.ToArray();
        var price = inconsistent[7].PriceBounds;
        inconsistent[7] = inconsistent[7] with { PriceBounds = new Rect(price.X + 20, price.Y, price.Width, price.Height) };
        var duplicate = expected.ToArray();
        duplicate[9] = duplicate[0];
        foreach (var prior in new IReadOnlyList<MerchantCardAnchor>[] { moved, scaled, inconsistent, duplicate, expected.Take(9).ToArray() })
            AssertSameGeometry(expected, MerchantCardLocator.Find(image, prior));
    }

    [Theory]
    [InlineData(1, 20, 12)]
    [InlineData(1.125, 0, 0)]
    public void ARealCardMoveOrScaleChangeIsDetectedInsteadOfReusingThePreferredLayout(double scale, int x, int y)
    {
        using var original = Shop(1);
        var previous = MerchantCardLocator.Find(original);
        Assert.Equal(10, previous.Count);
        using var current = Shop(scale, layoutOffsetX: x, layoutOffsetY: y);
        var expected = MerchantCardLocator.Find(current);

        var anchors = MerchantCardLocator.Find(current, previous);

        AssertSameGeometry(expected, anchors);
        Assert.NotEqual(previous[0].PriceBounds, anchors[0].PriceBounds);
    }

    [Fact]
    public void PreferredGeometryStillRequiresEightIndependentCurrentSeals()
    {
        using var original = Shop(1);
        var previous = MerchantCardLocator.Find(original);
        Assert.Equal(10, previous.Count);
        using var eight = Shop(1, missingSeals: [6, 7]);
        AssertSameGeometry(previous, MerchantCardLocator.Find(eight, previous));
        using var seven = Shop(1, missingSeals: [6, 7, 8]);
        Assert.Empty(MerchantCardLocator.Find(seven, previous));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void PreferredGeometryCannotReplaceOneMissingRawPrice(int slot)
    {
        using var original = Shop(1);
        var previous = MerchantCardLocator.Find(original);
        Assert.Equal(10, previous.Count);
        using var current = Shop(1, missingPrice: slot);
        Assert.Empty(MerchantCardLocator.Find(current, previous));
    }

    [Fact]
    public void PreferredGeometryCannotReplaceOneMissingRawCardSide()
    {
        using var original = Shop(1);
        var previous = MerchantCardLocator.Find(original);
        Assert.Equal(10, previous.Count);
        using var current = Shop(1);
        var seal = SealCenter(0, 1);
        Cv2.Rectangle(current, new Rect(seal.X - 111, seal.Y + 40, 10, 220), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(current, previous));
    }

    [Fact]
    public void PreferredGeometryCannotBridgeAPurchaseDialogAndCanBeRevalidatedAfterDismissal()
    {
        using var shop = Shop(1);
        var previous = MerchantCardLocator.Find(shop);
        Assert.Equal(10, previous.Count);
        using var dialog = shop.Clone();
        Cv2.Rectangle(dialog, new Rect(750, 300, 850, 760), Scalar.All(35), -1);
        Assert.Empty(MerchantCardLocator.Find(dialog, previous));
        AssertSameGeometry(previous, MerchantCardLocator.Find(shop, previous));
    }

    [Fact]
    public void APreferredSmallLayoutStillUsesTheUnchangedNormalizationAndMissingEdgeChecks()
    {
        using var image = FineSealsAt720p();
        var previous = MerchantCardLocator.Find(image);
        Assert.Equal(10, previous.Count);
        AssertSameGeometry(previous, MerchantCardLocator.Find(image, previous));
        var price = PriceCenter(0, .9375);
        var center = new Point((int)Math.Round(price.X * .5), (int)Math.Round(price.Y * .5));
        Cv2.Rectangle(image, new Rect(center.X - 40, center.Y - 12, 80, 24), Scalar.All(18), -1);
        Assert.Empty(MerchantCardLocator.Find(image, previous));
    }

    private static void AssertSameGeometry(IReadOnlyList<MerchantCardAnchor> expected,
        IReadOnlyList<MerchantCardAnchor> actual)
    {
        Assert.Equal(10, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].SlotIndex, actual[index].SlotIndex);
            Assert.Equal(expected[index].PriceBounds, actual[index].PriceBounds);
            Assert.Equal(expected[index].Scale, actual[index].Scale);
        }
    }

    private static Point SealCenter(int slot, double scale) => new(
        (int)Math.Round(1280 + (ReferenceSeals[slot].X - 1280) * scale),
        (int)Math.Round(720 + (ReferenceSeals[slot].Y - 720) * scale));

    private static Point PriceCenter(int slot, double scale)
    {
        var seal = SealCenter(slot, scale);
        return new(seal.X, seal.Y + (int)Math.Round(268 * scale));
    }

    private static Mat FineSealsAt720p(int[]? missingSeals = null)
    {
        const double uiScale = .9375, frameScale = .5;
        using var original = Shop(uiScale);
        var reduced = new Mat();
        Cv2.Resize(original, reduced, new Size(), frameScale, frameScale, InterpolationFlags.Area);
        for (var slot = 0; slot < 10; slot++)
        {
            var seal = SealCenter(slot, uiScale);
            var center = new Point((int)Math.Round(seal.X * frameScale), (int)Math.Round(seal.Y * frameScale));
            Cv2.Rectangle(reduced, new Rect(center.X - 18, center.Y - 18, 36, 36), Scalar.All(18), -1);
            if (missingSeals?.Contains(slot) == true) continue;
            // Tiny antialiased clipped corners lose a closed, high-area contour at 720p.
            // A bounded linear normalization restores that contour without changing any shop edges.
            const int size = 17, corner = 4;
            var left = center.X - size / 2;
            var top = center.Y - size / 2;
            var right = left + size - 1;
            var bottom = top + size - 1;
            Point[] shape = [new(left + corner, top), new(right - corner, top), new(right, top + corner),
                new(right, bottom - corner), new(right - corner, bottom), new(left + corner, bottom),
                new(left, bottom - corner), new(left, top + corner)];
            Cv2.FillConvexPoly(reduced, shape, Scalar.All(28), LineTypes.AntiAlias);
            Cv2.Polylines(reduced, [shape], true, new Scalar(105, 135, 150), 1, LineTypes.AntiAlias);
        }
        return reduced;
    }
    private static Mat ReducedShopWithTinySealGaps(int[]? missingSeals = null)
    {
        const double uiScale = .9375, frameScale = .75;
        using var original = Shop(uiScale, missingSeals: missingSeals);
        var reduced = new Mat();
        Cv2.Resize(original, reduced, new Size(), frameScale, frameScale, InterpolationFlags.Area);
        for (var slot = 0; slot < 4; slot++)
        {
            var seal = SealCenter(slot, uiScale);
            var center = new Point((int)Math.Round(seal.X * frameScale), (int)Math.Round(seal.Y * frameScale));
            var top = center.Y - (int)Math.Round(36 * uiScale * frameScale) / 2;
            // Three analysis pixels interrupt the tiny seal's top contour. Repairing this gap
            // supplies only a candidate; the unchanged raw price and card-side edges remain mandatory.
            Cv2.Rectangle(reduced, new Rect(center.X - 1, top - 2, 3, 5), Scalar.All(18), -1);
        }
        return reduced;
    }
    private static Mat Shop(double scale, bool whiteHover = false, int[]? missingSeals = null, int? missingPrice = null,
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
