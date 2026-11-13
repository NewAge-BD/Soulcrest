using System.Drawing;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.Ocr.Shop;
using Xunit;
using static Soulcrest.Testing.MerchantShopFixture;

namespace Soulcrest.App.Tests;

public sealed class MerchantOverlayDetectionTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, .75)]
    [InlineData(true, .75)]
    public void AFirstValidFrameCanBeConfirmedDespiteNewUnrelatedArtwork(bool right, double scale)
    {
        using var original = CreateShop(1);
        var bounds = new Rectangle(0, 0, original.Width, original.Height);
        var state = new MerchantLayoutStability(); var snapshot = new object();
        using var firstImage = new Mat();
        Cv2.Resize(original, firstImage, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        var first = Transform(MerchantCardLocator.Find(firstImage), 1 / scale);
        Assert.Equal(10, first.Count);
        Assert.False(state.Update(snapshot, bounds, 100, first));
        using var current = original.Clone();
        AddSealClutter(current, right);
        using var currentImage = new Mat();
        Cv2.Resize(current, currentImage, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        Assert.Empty(MerchantCardLocator.Find(currentImage)); // The unassisted second frame cannot confirm the shop.
        var detected = Transform(MerchantCardLocator.Find(currentImage,
            Transform(state.GetCandidateAnchors(snapshot, bounds, 101), scale)), 1 / scale);
        Assert.Equal(10, detected.Count);
        Assert.Empty(state.StableAnchors);
        Assert.True(state.Update(snapshot, bounds, 101, detected));
        Assert.Equal(first, state.StableAnchors);

        // A cached hypothesis still cannot supply missing current evidence or keep a closed shop visible.
        using var missing = CreateShop(1, missingPrice: 4);
        AddSealClutter(missing, right);
        using var missingImage = new Mat();
        Cv2.Resize(missing, missingImage, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        var rejected = Transform(MerchantCardLocator.Find(missingImage,
            Transform(state.GetCandidateAnchors(snapshot, bounds, 102), scale)), 1 / scale);
        Assert.Empty(rejected);
        Assert.False(state.Update(snapshot, bounds, 102, rejected));
        Assert.Empty(state.GetCandidateAnchors(snapshot, bounds, 103));
        Assert.Empty(state.StableAnchors);
    }

    private static IReadOnlyList<MerchantCardAnchor> Transform(IReadOnlyList<MerchantCardAnchor> anchors, double scale) =>
        anchors.Select(anchor => anchor with
        {
            PriceBounds = new Rect((int)Math.Round(anchor.PriceBounds.X * scale), (int)Math.Round(anchor.PriceBounds.Y * scale),
                (int)Math.Round(anchor.PriceBounds.Width * scale), (int)Math.Round(anchor.PriceBounds.Height * scale)),
            Scale = anchor.Scale * scale,
        }).ToArray();
}
