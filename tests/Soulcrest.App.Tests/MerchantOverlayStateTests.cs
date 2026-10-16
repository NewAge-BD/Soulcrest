using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
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

    [Fact]
    public void FrozenAnchorsAndTheirCaptureEvidenceExistOnlyAfterTwoDistinctFrames()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors(offset: 80);
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        Assert.Equal(anchors, state.StableAnchors.ToArray());
        Assert.Equal(101, state.ConfirmedFrameAt);
        state.Reset();
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(snapshot, Bounds, 102, anchors));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
    }

    [Theory]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void FiftyNoisyFramesKeepEveryFrozenAnchorAndRenderedBadgeAtTheSameGeometry(double scale)
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors(offset: 80, scale: scale);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        var frozen = state.StableAnchors.ToArray();
        MerchantCounterRow[] rows = Enumerable.Range(0, 10).Select(slot => new MerchantCounterRow(slot,
            "synthetic-pet-" + slot, slot == 0 ? "0/5" : slot == 1 ? "max" : "3/25", true, slot == 1,
            slot == 2 ? "Special" : "Abyss / Altgard / Verteron")).ToArray();
        var expected = MerchantOverlayForm.BuildBadges(Bounds.Size, frozen, rows).ToArray();
        Assert.Equal(10, expected.Length);
        var rawGeometryWouldMove = false;
        for (var index = 0; index < 50; index++)
        {
            var jitter = index % 2 == 0 ? 1 : -1;
            var next = anchors.Select(anchor => anchor with
            {
                PriceBounds = new Rect(anchor.PriceBounds.X + jitter, anchor.PriceBounds.Y - jitter,
                    anchor.PriceBounds.Width + jitter, anchor.PriceBounds.Height - jitter),
                Scale = scale + jitter * .0015,
                Confidence = .72 + index % 10 * .027,
            }).ToArray();
            if (index % 2 != 0) next = Enumerable.Reverse(next).ToArray();
            rawGeometryWouldMove |= !expected.SequenceEqual(MerchantOverlayForm.BuildBadges(Bounds.Size, next, rows));
            Assert.True(state.Update(snapshot, Bounds, 102 + index, next));
            Assert.Equal(102 + index, state.ConfirmedFrameAt);
            Assert.Equal(frozen, state.StableAnchors.ToArray());
            Assert.Equal(expected, MerchantOverlayForm.BuildBadges(Bounds.Size, state.StableAnchors, rows).ToArray());
        }
        Assert.True(rawGeometryWouldMove, "The test must exercise input changes that would visibly move an unfrozen badge.");
    }

    [Theory]
    [InlineData("anchor-move")]
    [InlineData("anchor-scale")]
    [InlineData("window-move")]
    [InlineData("window-resize")]
    [InlineData("snapshot")]
    public void ARealGeometryOrContextChangeDiscardsFrozenPlacementUntilTwoNewFramesAgree(string change)
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors(offset: 80);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        var nextSnapshot = change == "snapshot" ? new object() : snapshot;
        var nextBounds = change == "window-move" ? Bounds with { X = Bounds.X + 1 }
            : change == "window-resize" ? Bounds with { Width = Bounds.Width + 1 } : Bounds;
        var next = change == "anchor-move" ? Anchors(offset: 84)
            : change == "anchor-scale" ? Anchors(offset: 80, scale: 1.125) : Anchors(offset: 80);
        Assert.False(state.Update(nextSnapshot, nextBounds, 102, next));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(nextSnapshot, nextBounds, 102, next));
        Assert.Empty(state.StableAnchors);
        Assert.True(state.Update(nextSnapshot, nextBounds, 103, next));
        Assert.Equal(next, state.StableAnchors.ToArray());
        Assert.Equal(103, state.ConfirmedFrameAt);
    }

    [Fact]
    public void FreshMissingCapturesRetainConfirmedPlacementWithoutRenewingItsFrameTime()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors(offset: 80);
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        var frozen = state.StableAnchors.ToArray();
        for (var index = 0; index < 50; index++)
        {
            Assert.True(state.RetainAfterMissingCapture(snapshot, Bounds, frameIsFresh: true));
            Assert.Equal(101, state.ConfirmedFrameAt);
            Assert.Equal(frozen, state.StableAnchors.ToArray());
        }
        Assert.False(state.RetainAfterMissingCapture(snapshot, Bounds, frameIsFresh: false));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(snapshot, Bounds, 102, anchors));
        Assert.True(state.Update(snapshot, Bounds, 103, anchors));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("snapshot")]
    [InlineData("missing-snapshot")]
    [InlineData("window")]
    [InlineData("empty-window")]
    public void MissingCapturesCannotRetainAnExpiredOrDifferentContext(string change)
    {
        var state = new MerchantLayoutStability(); var snapshot = new SnapshotIdentity("same"); var anchors = Anchors();
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.True(state.Update(snapshot, Bounds, 101, anchors));
        var nextSnapshot = change == "snapshot" ? new SnapshotIdentity("same") : change == "missing-snapshot" ? null : snapshot;
        var nextBounds = change == "window" ? Bounds with { X = Bounds.X + 1 } : change == "empty-window" ? Rectangle.Empty : Bounds;
        Assert.False(state.RetainAfterMissingCapture(nextSnapshot!, nextBounds, frameIsFresh: change != "stale"));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.RetainAfterMissingCapture(snapshot, Bounds, frameIsFresh: true));
        Assert.False(state.Update(snapshot, Bounds, 102, anchors));
        Assert.True(state.Update(snapshot, Bounds, 103, anchors));
    }

    [Fact]
    public void MissingCapturesCannotFinishAPendingLayoutAndDetectedPurchaseDialogsStillResetImmediately()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var anchors = Anchors();
        Assert.False(state.Update(snapshot, Bounds, 100, anchors));
        Assert.False(state.RetainAfterMissingCapture(snapshot, Bounds, frameIsFresh: true));
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.Update(snapshot, Bounds, 101, anchors));
        Assert.True(state.Update(snapshot, Bounds, 102, anchors));
        Assert.False(state.Update(snapshot, Bounds, 103, [])); // a captured invalid layout is different from no capture
        Assert.Empty(state.StableAnchors);
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.False(state.RetainAfterMissingCapture(snapshot, Bounds, frameIsFresh: true));
        Assert.False(state.Update(snapshot, Bounds, 104, anchors));
        Assert.True(state.Update(snapshot, Bounds, 105, anchors)); // the same network offers recover after the dialog
    }

    [Fact]
    public void FrozenPlacementCopiesTheFirstFrameAndCannotBeMutatedThroughEitherCallerInputOrItsPublicList()
    {
        var state = new MerchantLayoutStability(); var snapshot = new object(); var first = Anchors(offset: 80);
        var expected = first.ToArray();
        Assert.False(state.Update(snapshot, Bounds, 100, first));
        first[0] = first[0] with { PriceBounds = new Rect(400, 100, 100, 20), Scale = 1.5 };
        var confirmation = expected.Select(anchor => anchor with { Scale = anchor.Scale + .001, Confidence = .8 }).ToArray();
        Assert.True(state.Update(snapshot, Bounds, 101, confirmation));
        confirmation[1] = confirmation[1] with { PriceBounds = new Rect(500, 100, 100, 20), Scale = 1.5 };
        Assert.NotSame(first, state.StableAnchors);
        Assert.NotSame(confirmation, state.StableAnchors);
        Assert.Equal(expected, state.StableAnchors.ToArray());
        if (state.StableAnchors is IList<MerchantCardAnchor> list)
            Assert.Throws<NotSupportedException>(() => list[0] = first[0]);
        Assert.Equal(expected, state.StableAnchors.ToArray());
        Assert.Equal(101, state.ConfirmedFrameAt);
    }

    [Theory]
    [InlineData(1600, 900, 1, 1600, 900, true)]
    [InlineData(1600, 900, .5, 800, 450, true)]
    [InlineData(1601, 901, .5, 800, 450, true)]
    [InlineData(1601, 901, .5, 801, 451, true)]
    [InlineData(1600, 900, 1, 1598, 900, false)]
    [InlineData(1600, 900, .5, 800, 448, false)]
    [InlineData(1600, 900, 0, 1600, 900, false)]
    [InlineData(1600, 900, -1, 1600, 900, false)]
    [InlineData(1600, 900, 1.1, 1760, 990, false)]
    [InlineData(1600, 900, double.NaN, 1600, 900, false)]
    [InlineData(1600, 900, double.PositiveInfinity, 1600, 900, false)]
    [InlineData(0, 900, 1, 1600, 900, false)]
    [InlineData(1600, 0, 1, 1600, 900, false)]
    [InlineData(1600, 900, 1, 0, 900, false)]
    [InlineData(1600, 900, 1, 1600, 0, false)]
    public void CaptureDimensionsAllowOneRoundingPixelButRejectAnUnrelatedFrameOrInvalidScale(
        int width, int height, double scale, int imageWidth, int imageHeight, bool matches)
    {
        Assert.Equal(matches, MerchantLayoutStability.MatchesCaptureSize(new Size(width, height), scale, new Size(imageWidth, imageHeight)));
    }

    private static MerchantCardAnchor[] Anchors(int offset = 0, double scale = 1) => Enumerable.Range(0, 10).Select(i =>
        new MerchantCardAnchor(i, new Rect(50 + i % 5 * 200 + offset, 100 + i / 5 * 300, 100, 20), scale, .9)).ToArray();
    private sealed record SnapshotIdentity(string Label);
}
