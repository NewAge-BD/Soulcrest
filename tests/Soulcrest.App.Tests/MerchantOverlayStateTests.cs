using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.Ocr.Shop;
using Xunit;
using Rect = OpenCvSharp.Rect;

namespace Soulcrest.App.Tests;

public sealed class MerchantOverlayStateTests
{
    [Fact]
    public void ATemporaryPurchaseDialogCanRecoverWithoutAnotherNetworkOfferList()
    {
        var state = new MerchantLayoutStability();
        var offers = new object();
        var bounds = new Rectangle(0, 0, 1600, 900);
        var anchors = Anchors();
        Assert.False(state.Update(offers, bounds, 1, anchors));
        Assert.True(state.Update(offers, bounds, 2, anchors));
        Assert.False(state.Update(offers, bounds, 3, []));
        Assert.False(state.Update(offers, bounds, 4, anchors));
        Assert.True(state.Update(offers, bounds, 5, anchors));
    }

    private static readonly Rectangle Bounds = new(-100, 50, 1200, 800);

    [Fact]
    public void TwoDistinctFramesAreRequiredAndRepeatedFramesCannotCreateReadiness()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors();
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors)); // previously established readiness
        state.Reset();
        Assert.False(state.Update(snapshot, Bounds, 102, anchors));
        Assert.True(state.Update(snapshot, Bounds, 103, anchors));
    }

    [Fact]
    public void SnapshotIdentityUsesReferenceEqualityAndNeverValueEquality()
    {
        var state = new MerchantLayoutStability();
        var first = new SnapshotIdentity("same"); var second = new SnapshotIdentity("same");
        Assert.Equal(first, second);
        Assert.False(state.Update(first, Bounds, 100, Anchors()));
        Assert.True(state.Update(first, Bounds, 101, Anchors()));
        Assert.False(state.Update(second, Bounds, 102, Anchors()));
        Assert.False(state.Update(second, Bounds, 102, Anchors()));
        Assert.True(state.Update(second, Bounds, 103, Anchors()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowMovementAndResizeRequireANewPair(bool move)
    {
        var state = new MerchantLayoutStability(); var snapshot = new object();
        Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
        Assert.True(state.Update(snapshot, Bounds, 101, Anchors()));
        var changed = move ? new Rectangle(Bounds.X + 1, Bounds.Y, Bounds.Width, Bounds.Height)
            : new Rectangle(Bounds.X, Bounds.Y, Bounds.Width + 1, Bounds.Height);
        Assert.False(state.Update(snapshot, changed, 102, Anchors()));
        Assert.True(state.Update(snapshot, changed, 103, Anchors()));
    }

    [Theory]
    [InlineData(1d, 3, true)]
    [InlineData(1d, 4, false)]
    [InlineData(2d, 6, true)]
    [InlineData(2d, 7, false)]
    [InlineData(.5d, 3, true)]
    [InlineData(.5d, 4, false)]
    public void PositionToleranceIncludesItsExactPixelAndScaleBoundaries(double scale, int offset, bool ready)
    {
        var state = new MerchantLayoutStability(); var snapshot = new object();
        Assert.False(state.Update(snapshot, Bounds, 100, Anchors(scale: scale)));
        Assert.Equal(ready, state.Update(snapshot, Bounds, 101, Anchors(offset, scale)));
    }

    [Fact]
    public void MovementBeginsANewPairAndSmallDriftCannotAccumulateFromFrameToFrame()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object();
        Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
        Assert.True(state.Update(snapshot, Bounds, 101, Anchors(offset: 3)));
        Assert.False(state.Update(snapshot, Bounds, 102, Anchors(offset: 6)));
        Assert.False(state.Update(snapshot, Bounds, 102, Anchors(offset: 6)));
        Assert.True(state.Update(snapshot, Bounds, 103, Anchors(offset: 6)));
    }

    [Fact]
    public void RectangleSizeAndOppositeEdgesAreCheckedAlongWithTheOrigin()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object();
        Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
        var wider = Anchors(); var original = wider[7].PriceBounds;
        wider[7] = wider[7] with { PriceBounds = new Rect(original.X, original.Y, original.Width + 4, original.Height) };
        Assert.False(state.Update(snapshot, Bounds, 101, wider));
        Assert.True(state.Update(snapshot, Bounds, 102, wider));
    }

    [Fact]
    public void SlotOrderDoesNotMatterButMutableInputCannotChangeTheRememberedReference()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors();
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        anchors[0] = anchors[0] with { PriceBounds = new Rect(400, 100, 100, 20) };
        Assert.False(state.Update(snapshot, Bounds, 101, anchors));
        Assert.True(state.Update(snapshot, Bounds, 102, Enumerable.Reverse(anchors).ToArray()));
    }

    [Fact]
    public void NonpositiveOrBackwardsCaptureTimesDiscardThePreviousConfirmation()
    {
        foreach (var invalidTime in new[] { 0L, -1L, 99L })
        {
            var state = new MerchantLayoutStability(); var snapshot = new object();
            Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
            Assert.True(state.Update(snapshot, Bounds, 101, Anchors()));
            Assert.False(state.Update(snapshot, Bounds, invalidTime, Anchors()));
            Assert.False(state.Update(snapshot, Bounds, 102, Anchors()));
            Assert.True(state.Update(snapshot, Bounds, 103, Anchors()));
        }
    }

    [Fact]
    public void ExactConfidenceAndLocalImageEdgesAreAllowedEvenOnANegativeDesktopOrigin()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors();
        anchors[9] = new(9, new Rect(Bounds.Width - 100, Bounds.Height - 20, 100, 20), 1, .72);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
    }

    [Fact]
    public void InvalidLayoutsResetRatherThanReuseAnyEarlierReadyLayout()
    {
        var invalid = new List<IReadOnlyList<MerchantCardAnchor>> { Anchors().Take(9).ToArray(), Anchors().Append(Anchors()[0]).ToArray(), null! };
        var changes = new Func<MerchantCardAnchor, MerchantCardAnchor>[]
        {
            a => a with { SlotIndex = -1 }, a => a with { SlotIndex = 10 }, a => a with { SlotIndex = 1 },
            _ => null!, a => a with { PriceBounds = new Rect(-1, 10, 100, 20) },
            a => a with { PriceBounds = new Rect(10, -1, 100, 20) },
            a => a with { PriceBounds = new Rect(10, 10, 0, 20) }, a => a with { PriceBounds = new Rect(10, 10, 100, 0) },
            a => a with { PriceBounds = new Rect(Bounds.Width - 99, 10, 100, 20) },
            a => a with { PriceBounds = new Rect(10, Bounds.Height - 19, 100, 20) },
            a => a with { PriceBounds = new Rect(int.MaxValue, 10, int.MaxValue, 20) },
            a => a with { Scale = 0 }, a => a with { Scale = -1 }, a => a with { Scale = double.NaN },
            a => a with { Scale = double.PositiveInfinity }, a => a with { Scale = double.MaxValue },
            a => a with { Confidence = .719999 }, a => a with { Confidence = double.NaN },
            a => a with { Confidence = double.PositiveInfinity },
        };
        foreach (var change in changes) { var anchors = Anchors(); anchors[0] = change(anchors[0]); invalid.Add(anchors); }
        foreach (var anchors in invalid)
        {
            var state = new MerchantLayoutStability(); var snapshot = new object();
            Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
            Assert.True(state.Update(snapshot, Bounds, 101, Anchors()));
            Assert.False(state.Update(snapshot, Bounds, 102, anchors));
            Assert.False(state.Update(snapshot, Bounds, 103, Anchors()));
            Assert.True(state.Update(snapshot, Bounds, 104, Anchors()));
        }
    }

    [Fact]
    public void MissingSnapshotAndEmptyWindowCannotRetainReadyState()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object();
        Assert.False(state.Update(snapshot, Bounds, 100, Anchors()));
        Assert.True(state.Update(snapshot, Bounds, 101, Anchors()));
        Assert.False(state.Update(null!, Bounds, 102, Anchors()));
        Assert.False(state.Update(snapshot, Bounds, 103, Anchors()));
        Assert.True(state.Update(snapshot, Bounds, 104, Anchors()));
        Assert.False(state.Update(snapshot, Rectangle.Empty, 105, Anchors()));
        Assert.False(state.Update(snapshot, Bounds, 106, Anchors()));
    }

    private static MerchantCardAnchor[] Anchors(int offset = 0, double scale = 1) => Enumerable.Range(0, 10).Select(i =>
        new MerchantCardAnchor(i, new Rect(50 + i % 5 * 200 + offset, 100 + i / 5 * 300, 100, 20), scale, .9)).ToArray();
    private sealed record SnapshotIdentity(string Label);
}
