using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.Shop;
using Xunit;
using Rect = OpenCvSharp.Rect;

namespace Soulcrest.App.Tests;

/// <summary>Pure card-foot projection and off-screen bitmap drawing. No shown window, capture or native input.</summary>
public sealed class MerchantOverlayFormTests
{
    private static readonly Size GameSize = new(1600, 900);

    [Fact]
    public void MixedOfferSlotsOnlyReceiveBadgesForTheirSuppliedPetRows()
    {
        var anchors = Anchors();
        MerchantCounterRow[] rows = [new(9, "synthetic-pet-a", "?", false, false),
            new(0, "synthetic-pet-b", "3/25", true, false), new(3, "synthetic-pet-c", "max", true, true)];

        var badges = MerchantOverlayForm.BuildBadges(GameSize, anchors, rows);

        Assert.Equal(3, badges.Count);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].Counter, badges[i].Counter);
            Assert.Equal(rows[i].IsFresh, badges[i].IsFresh);
            Assert.Equal(rows[i].IsMax, badges[i].IsMax);
            var price = anchors[rows[i].SlotIndex].PriceBounds;
            Assert.InRange(Math.Abs(price.X + price.Width / 2d - badges[i].Bounds.X - badges[i].Bounds.Width / 2d), 0, .5);
            Assert.Equal(price.Top, badges[i].Bounds.Top);
            Assert.Equal(new Rectangle(price.X, price.Y, price.Width, price.Height), badges[i].PriceBounds);
            Assert.True(new Rectangle(Point.Empty, GameSize).Contains(badges[i].Bounds));
        }
        // The other seven offers, including skins and unknown items, have no supplied pet row.
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, anchors, []));
    }

    [Theory]
    [InlineData("Abyss")]
    [InlineData("Altgard")]
    [InlineData("Verteron")]
    [InlineData("Special")]
    [InlineData("Abyss / Altgard / Verteron")]
    [InlineData("?")]
    public void FarmMapsGetTheirOwnVisibleLineWithoutReplacingTheCounter(string regions)
    {
        foreach (var scale in new[] { .5, .9375, 1, 1.0625, 1.125, 1.25 })
        {
            var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale),
                [new(0, "synthetic-pet", "max", true, true, regions)]));
            Assert.Equal("max", badge.Counter);
            Assert.Equal(regions, badge.FarmRegions);
            Assert.True(badge.Bounds.Height > (int)Math.Round(36 * scale));
            using var image = Render(new Size(400, 300), Point.Empty, [badge]);
            using var blankLine = Render(new Size(400, 300), Point.Empty, [badge with { FarmRegions = " " }]);
            var mapTop = badge.Bounds.Y + (int)Math.Round(badge.Bounds.Height * .5);
            var mapInk = false;
            for (var y = mapTop; y < badge.Bounds.Bottom; y++)
            for (var x = badge.Bounds.X; x < badge.Bounds.Right; x++)
            {
                mapInk |= image.GetPixel(x, y) != blankLine.GetPixel(x, y);
            }
            Assert.True(mapInk, "Farm-map text must remain visible beneath the counter.");
            AssertTransparentOutside(image, [badge.Bounds]);
        }
    }

    [Theory]
    [InlineData(.25, 53)]
    [InlineData(.5, 106)]
    [InlineData(.9375, 199)]
    [InlineData(1, 212)]
    [InlineData(1.0625, 225)]
    [InlineData(1.125, 238)]
    [InlineData(1.25, 265)]
    [InlineData(2, 424)]
    public void CardFootDimensionsFollowTheAnchorScaleWithoutAbsoluteMinimums(double scale, int width)
    {
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale),
            [new(4, "synthetic-pet", "max", true, true)]));
        Assert.Equal(width, badge.Bounds.Width);
        Assert.Equal((int)Math.Round(36 * scale), badge.Bounds.Height - badge.PriceBounds.Height - Math.Max(1, (int)Math.Round(scale)));
        Assert.True(new Rectangle(Point.Empty, GameSize).Contains(badge.Bounds));
    }

    [Fact]
    public void AGameResolutionChangeDoesNotRescaleTheSamePhysicalCardAnchors()
    {
        var anchors = Anchors(1.125);
        MerchantCounterRow[] rows = [new(0, "synthetic-a", "24/25", true, false, "Altgard"),
            new(4, "synthetic-b", "max", true, true, "Abyss / Altgard / Verteron"),
            new(9, "synthetic-c", "?", false, false, "Special")];
        var original = MerchantOverlayForm.BuildBadges(GameSize, anchors, rows);
        var largerWindow = MerchantOverlayForm.BuildBadges(new Size(3840, 2160), anchors, rows);

        Assert.Equal(rows.Length, original.Count);
        Assert.Equal(original.ToArray(), largerWindow.ToArray());
    }

    [Theory]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void CardFeetStayAttachedAndSeparateAcrossUiScalesAndImageResolutions(double uiScale)
    {
        MerchantCounterRow[] rows = Enumerable.Range(0, 10).Select(slot =>
            new MerchantCounterRow(slot, "synthetic-pet-" + slot, "24/25", true, false, "Abyss / Altgard / Verteron")).ToArray();
        var original = MerchantOverlayForm.BuildBadges(new Size(2560, 1440), ShopAnchors(uiScale, 1), rows);
        Assert.Equal(10, original.Count);
        foreach (var frameScale in new[] { .5, .75, 1 })
        {
            var anchors = ShopAnchors(uiScale, frameScale);
            var size = new Size((int)Math.Round(2560 * frameScale), (int)Math.Round(1440 * frameScale));
            var badges = MerchantOverlayForm.BuildBadges(size, anchors, rows);
            Assert.Equal(10, badges.Count);
            for (var slot = 0; slot < 10; slot++)
            {
                var bounds = badges[slot].Bounds;
                var price = anchors[slot].PriceBounds;
                Assert.True(new Rectangle(Point.Empty, size).Contains(bounds));
                Assert.Equal(price.Top, bounds.Top);
                Assert.Equal(new Rectangle(price.X, price.Y, price.Width, price.Height), badges[slot].PriceBounds);
                Assert.InRange(Math.Abs(price.X + price.Width / 2d - bounds.X - bounds.Width / 2d), 0, .5);
                Assert.InRange(Math.Abs(bounds.Left - original[slot].Bounds.Left * frameScale), 0, 2);
                Assert.InRange(Math.Abs(bounds.Right - original[slot].Bounds.Right * frameScale), 0, 2);
                Assert.InRange(Math.Abs(bounds.Top - original[slot].Bounds.Top * frameScale), 0, 2);
                Assert.InRange(Math.Abs(bounds.Bottom - original[slot].Bounds.Bottom * frameScale), 0, 2);
                for (var other = slot + 1; other < badges.Count; other++)
                    Assert.False(bounds.IntersectsWith(badges[other].Bounds), "Neighboring card feet must remain separate.");
            }
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void PhysicalPixelRenderingDoesNotApplyBitmapDpiASecondTime(float dpi)
    {
        MerchantOverlayForm.Badge[] badges = [
            new(new Rectangle(20, 20, 212, 52), "0/5", true, false, "Abyss / Altgard / Verteron"),
            new(new Rectangle(260, 20, 212, 52), "max", true, true, "Altgard"),
            new(new Rectangle(500, 20, 212, 52), "?", false, false, "Special")];
        using var reference = Render(new Size(740, 96), Point.Empty, badges, 96);
        using var highDpi = Render(reference.Size, Point.Empty, badges, dpi);

        for (var y = 0; y < reference.Height; y++)
            for (var x = 0; x < reference.Width; x++) Assert.Equal(reference.GetPixel(x, y), highDpi.GetPixel(x, y));
        AssertTransparentOutside(highDpi, badges.Select(b => b.Bounds).ToArray());
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void NativePriceButtonAndItsSafetyMarginReceiveNoOverlayPixels(double scale)
    {
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale),
            [new(0, "synthetic-pet", "24/25", true, false, "Abyss / Altgard / Verteron")]));
        using var image = Render(new Size(400, 300), Point.Empty, [badge]);
        for (var y = badge.PriceBounds.Top; y < badge.PriceBounds.Bottom; y++)
            for (var x = badge.PriceBounds.Left; x < badge.PriceBounds.Right; x++)
            {
                var radius = badge.PriceBounds.Height / 2d;
                var capsuleX = Math.Clamp(x + .5, badge.PriceBounds.Left + radius, badge.PriceBounds.Right - radius);
                var dx = x + .5 - capsuleX;
                var dy = y + .5 - badge.PriceBounds.Top - radius;
                if (dx * dx + dy * dy <= radius * radius) Assert.Equal(0, image.GetPixel(x, y).A);
            }
        AssertContainsInk(image, new Rectangle(badge.Bounds.Left, badge.PriceBounds.Bottom + 5,
            badge.Bounds.Width, badge.Bounds.Bottom - badge.PriceBounds.Bottom - 5));
        AssertTransparentOutside(image, [badge.Bounds]);
    }

    [Fact]
    public void UnlockSpecialAndMaxUseTheSameFootprintWithoutHighlightingLaterZeroBalances()
    {
        var bounds = new Rectangle(20, 20, 212, 52);
        using var normal = Render(new Size(260, 90), Point.Empty, [new(bounds, "24/25", true, false, "Altgard")]);
        var regularEdge = normal.GetPixel(bounds.Left, bounds.Top + 10);
        Assert.True(regularEdge.A > 0);
        foreach (var (counter, regions) in new[] { ("0/5", "Altgard"), ("24/25", "Special"), ("?", "Special"), ("max", "Special"), ("max", "Altgard") })
        {
            using var priority = Render(normal.Size, Point.Empty,
                [new(bounds, counter, counter != "?", counter == "max", regions)]);
            var priorityEdge = priority.GetPixel(bounds.Left, bounds.Top + 10);
            Assert.True(priorityEdge.GetBrightness() > regularEdge.GetBrightness());
            AssertTransparentOutside(priority, [bounds]);
        }
        foreach (var counter in new[] { "0/25", "0/75", "?" })
        {
            using var later = Render(normal.Size, Point.Empty, [new(bounds, counter, true, counter == "max", "Altgard")]);
            Assert.Equal(regularEdge, later.GetPixel(bounds.Left, bounds.Top + 10));
        }
    }

    [Fact]
    public void PartialMissingAndOffscreenLayoutsCannotProduceAPartialBadgeSet()
    {
        MerchantCounterRow[] rows = [new(0, "synthetic-a", "0/5", true, false), new(9, "synthetic-b", "?", false, false)];
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, Anchors().Take(9).ToArray(), rows));
        var duplicate = Anchors(); duplicate[9] = duplicate[0];
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, duplicate, rows));
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, Anchors(), [new(10, "synthetic-missing", "?", false, false)]));
        var offscreen = Anchors();
        offscreen[9] = offscreen[9] with { PriceBounds = new Rect(100, GameSize.Height - 27, 146, 27) };
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, offscreen, rows));
    }

    [Theory]
    [InlineData("0/5", true, false)]
    [InlineData("4/5", true, false)]
    [InlineData("0/25", true, false)]
    [InlineData("24/25", true, false)]
    [InlineData("0/75", true, false)]
    [InlineData("74/75", true, false)]
    [InlineData("100/25", true, false)]
    [InlineData("?", false, false)]
    [InlineData("max", true, true)]
    [InlineData("max", false, true)]
    public void CountersAndMaxDrawInsideTheirBadgeOnly(string counter, bool fresh, bool max)
    {
        MerchantOverlayForm.Badge[] badges = [new(new Rectangle(20, 20, 212, 36), counter, fresh, max)];
        using var bitmap = Render(new Size(260, 80), Point.Empty, badges);
        AssertContainsInk(bitmap, badges[0].Bounds);
        AssertTransparentOutside(bitmap, badges.Select(b => b.Bounds).ToArray());
    }

    [Fact]
    public void TranslatedBadgeSurfacesLeaveAllGapsAndOutsidePixelsFullyTransparent()
    {
        var origin = new Point(200, 300);
        MerchantOverlayForm.Badge[] badges = [new(new Rectangle(220, 320, 212, 36), "?", false, false),
            new(new Rectangle(470, 380, 212, 36), "max", true, true)];
        using var bitmap = Render(new Size(520, 140), origin, badges);
        var translated = badges.Select(b => new Rectangle(b.Bounds.X - origin.X, b.Bounds.Y - origin.Y, b.Bounds.Width, b.Bounds.Height)).ToArray();
        Assert.All(translated, footprint => AssertContainsInk(bitmap, footprint));
        Assert.Equal(0, bitmap.GetPixel(250, 65).A);
        AssertTransparentOutside(bitmap, translated);
    }

    [Fact]
    public void MaxLabelIsUppercaseAndAnEmptyPageDrawsNothing()
    {
        var bounds = new Rectangle(10, 10, 212, 36);
        using var lower = Render(new Size(250, 60), Point.Empty, [new(bounds, "max", true, true)]);
        using var upper = Render(new Size(250, 60), Point.Empty, [new(bounds, "MAX", true, true)]);
        for (var y = 0; y < lower.Height; y++)
            for (var x = 0; x < lower.Width; x++) Assert.Equal(lower.GetPixel(x, y), upper.GetPixel(x, y));
        using var empty = Render(new Size(40, 30), Point.Empty, []);
        AssertTransparentOutside(empty, []);
    }

    [Fact]
    public void WindowParametersRemainClickThroughAndNonactivatingWithoutCreatingAHandle()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Construction stores these collaborators; none is touched until Start/Present.
                using var form = new MerchantOverlayForm(null!, null!, null!);
                Assert.False(form.IsHandleCreated);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var parameters = Assert.IsType<CreateParams>(typeof(MerchantOverlayForm).GetProperty("CreateParams", flags)!.GetValue(form));
                foreach (var required in new[] { NativeMethods.WS_EX_LAYERED, NativeMethods.WS_EX_TRANSPARENT,
                    NativeMethods.WS_EX_NOACTIVATE, NativeMethods.WS_EX_TOOLWINDOW, NativeMethods.WS_EX_TOPMOST })
                    Assert.NotEqual(0, parameters.ExStyle & required);
                Assert.True(Assert.IsType<bool>(typeof(MerchantOverlayForm).GetProperty("ShowWithoutActivation", flags)!.GetValue(form)));
                Assert.False(form.ShowInTaskbar);
                Assert.False(form.IsHandleCreated);
                Assert.False(form.Visible);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static MerchantCardAnchor[] Anchors(double scale = 1) => Enumerable.Range(0, 10).Select(i =>
        new MerchantCardAnchor(i, new Rect(100 + i % 5 * 250, 100 + i / 5 * 300, (int)(146 * scale), (int)(27 * scale)), scale, .9)).ToArray();

    private static MerchantCardAnchor[] ShopAnchors(double uiScale, double frameScale)
    {
        // Independent measurements also used by the locator's procedural shop fixtures.
        Point[] seals = [new(512, 366), new(819, 435), new(1126, 460), new(1433, 460), new(1740, 435),
            new(2048, 366), new(571, 750), new(878, 805), new(1680, 805), new(1989, 750)];
        return seals.Select((seal, slot) =>
        {
            var scale = uiScale * frameScale;
            var centerX = (1280 + (seal.X - 1280) * uiScale) * frameScale;
            var centerY = (720 + (seal.Y - 720 + 268) * uiScale) * frameScale;
            var width = (int)Math.Round(146 * scale);
            var height = (int)Math.Round(27 * scale);
            return new MerchantCardAnchor(slot,
                new Rect((int)Math.Round(centerX - width / 2d), (int)Math.Round(centerY - height / 2d), width, height), scale, .9);
        }).ToArray();
    }

    private static Bitmap Render(Size size, Point origin, IReadOnlyList<MerchantOverlayForm.Badge> badges, float dpi = 96)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        MerchantOverlayForm.DrawBadges(graphics, origin, badges);
        return bitmap;
    }

    private static void AssertContainsInk(Bitmap bitmap, Rectangle footprint)
    {
        for (var y = footprint.Top; y < footprint.Bottom; y++)
            for (var x = footprint.Left; x < footprint.Right; x++)
                if (bitmap.GetPixel(x, y).A > 0) return;
        Assert.Fail("The card foot must contain visible pixels.");
    }

    private static void AssertTransparentOutside(Bitmap bitmap, IReadOnlyList<Rectangle> footprints)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (!footprints.Any(rect => rect.Contains(x, y)))
                    Assert.True(bitmap.GetPixel(x, y).A == 0, $"Unexpected ink outside badge footprint at ({x},{y}).");
    }
}
