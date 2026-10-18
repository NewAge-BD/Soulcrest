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
            new MerchantCounterRow(slot, "synthetic-pet-" + slot, "24/25", true, false, "Abyss / Altgard / Verteron",
                new[] { "Cogni", "Fera", "Natura", "Varian" }[slot % 4],
                "Abyss (209) / Altgard (398) / Verteron (377)")).ToArray();
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
                Assert.Equal(rows[slot].PetCategory, badges[slot].PetCategory);
                Assert.Equal(rows[slot].FarmSpawns, badges[slot].FarmSpawns);
                var header = badges[slot].CategoryBounds;
                Assert.False(header.IsEmpty);
                Assert.True(header.Bottom < price.Top);
                Assert.InRange(Math.Abs(price.X + price.Width / 2d - header.X - header.Width / 2d), 0, .5);
                Assert.True(new Rectangle(Point.Empty, size).Contains(MerchantOverlayForm.SurfaceBounds(badges[slot])));
                Assert.InRange(Math.Abs(header.Left - original[slot].CategoryBounds.Left * frameScale), 0, 2);
                Assert.InRange(Math.Abs(header.Top - original[slot].CategoryBounds.Top * frameScale), 0, 2);
                Assert.InRange(Math.Abs(header.Right - original[slot].CategoryBounds.Right * frameScale), 0, 2);
                Assert.InRange(Math.Abs(header.Bottom - original[slot].CategoryBounds.Bottom * frameScale), 0, 2);
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
            new(new Rectangle(20, 60, 212, 52), "0/5", true, false, "Abyss / Altgard / Verteron",
                PetCategory: "Varian", FarmSpawns: "Abyss (209) / Altgard (398) / Verteron (377)",
                CategoryBounds: new Rectangle(78, 10, 96, 20)),
            new(new Rectangle(260, 60, 212, 52), "max", true, true, "Altgard",
                PetCategory: "Cogni", FarmSpawns: "Altgard (398)", CategoryBounds: new Rectangle(318, 10, 96, 20)),
            new(new Rectangle(500, 60, 212, 52), "?", false, false, "Special",
                PetCategory: "Natura", FarmSpawns: "Special", CategoryBounds: new Rectangle(558, 10, 96, 20))];
        using var reference = Render(new Size(740, 140), Point.Empty, badges, 96);
        using var highDpi = Render(reference.Size, Point.Empty, badges, dpi);

        for (var y = 0; y < reference.Height; y++)
            for (var x = 0; x < reference.Width; x++) Assert.Equal(reference.GetPixel(x, y), highDpi.GetPixel(x, y));
        Assert.All(badges, badge => AssertContainsInk(highDpi, badge.CategoryBounds));
        AssertTransparentOutside(highDpi, badges.SelectMany(Regions).ToArray());
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
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale, top: 300),
            [new(0, "synthetic-pet", "24/25", true, false, "Abyss / Altgard / Verteron",
                "Natura", "Abyss (209) / Altgard (398) / Verteron (377)")]));
        using var image = Render(new Size(400, 500), Point.Empty, [badge]);
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
        AssertTransparentOutside(image, Regions(badge));
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
    public void CategoryAndSpawnMetadataAreProjectedWithoutChangingTheFooterOrPriceBounds()
    {
        MerchantCounterRow[] rows = [new(0, "synthetic-a", "24/25", true, false,
                "Abyss / Altgard / Verteron", "Varian", "Abyss (209) / Altgard (398) / Verteron (377)"),
            new(9, "synthetic-b", "max", true, true, "Special", "Natura", "Special")];
        var badges = MerchantOverlayForm.BuildBadges(GameSize, Anchors(top: 300), rows);
        var old = MerchantOverlayForm.BuildBadges(GameSize, Anchors(top: 300), rows.Select(row =>
            row with { PetCategory = "", FarmSpawns = "" }).ToArray());

        Assert.Equal(rows.Length, badges.Count);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].PetCategory, badges[i].PetCategory);
            Assert.Equal(rows[i].FarmSpawns, badges[i].FarmSpawns);
            Assert.Equal(rows[i].FarmRegions, badges[i].FarmRegions);
            Assert.Equal(old[i].Bounds, badges[i].Bounds);
            Assert.Equal(old[i].PriceBounds, badges[i].PriceBounds);
        }
    }

    [Theory]
    [InlineData("Cogni")]
    [InlineData("Fera")]
    [InlineData("Natura")]
    [InlineData("Varian")]
    [InlineData("?")]
    public void PetCategoryIsVisibleInItsOwnHeaderAboveThePetName(string category)
    {
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(top: 300),
            [new(0, "synthetic-pet", "24/25", true, false, "Altgard", category, "Altgard (398)")]));
        Assert.False(badge.CategoryBounds.IsEmpty);
        Assert.True(badge.CategoryBounds.Bottom < badge.PriceBounds.Top);
        Assert.InRange(Math.Abs(badge.CategoryBounds.X + badge.CategoryBounds.Width / 2d
            - badge.PriceBounds.X - badge.PriceBounds.Width / 2d), 0, .5);
        using var image = Render(new Size(400, 500), Point.Empty, [badge]);
        using var absent = Render(image.Size, Point.Empty, [badge with { PetCategory = "", CategoryBounds = Rectangle.Empty }]);
        AssertPixelsDiffer(image, absent, badge.CategoryBounds);
        AssertContainsInk(image, badge.CategoryBounds);
        AssertTransparentOutside(image, Regions(badge));
    }

    [Theory]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void CategoryHeadersLeaveTheCounterCenteredAndTheNativeCardBetweenRegionsTransparent(double scale)
    {
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale, top: 300),
            [new(0, "synthetic-pet", "24/25", true, false, "Altgard", "Natura", "Altgard (398)")]));
        using var withCategory = Render(new Size(400, 500), Point.Empty, [badge]);
        using var withoutCategory = Render(withCategory.Size, Point.Empty,
            [badge with { PetCategory = "", CategoryBounds = Rectangle.Empty }]);
        Assert.InRange(Math.Abs(badge.Bounds.X + badge.Bounds.Width / 2d
            - badge.PriceBounds.X - badge.PriceBounds.Width / 2d), 0, .5);
        for (var y = badge.Bounds.Top; y < badge.Bounds.Bottom; y++)
            for (var x = badge.Bounds.Left; x < badge.Bounds.Right; x++)
                Assert.Equal(withoutCategory.GetPixel(x, y), withCategory.GetPixel(x, y));
        Assert.Equal(Rectangle.Union(badge.Bounds, badge.CategoryBounds), MerchantOverlayForm.SurfaceBounds(badge));
        Assert.Equal(badge.Bounds, MerchantOverlayForm.SurfaceBounds(badge with { CategoryBounds = Rectangle.Empty }));
        AssertContainsInk(withCategory, badge.CategoryBounds);
        AssertContainsInk(withCategory, badge.Bounds);
        // Allow each drawn region separately; allowing the union would hide accidental paint
        // across the native pet name, artwork or other controls between header and footer.
        AssertTransparentOutside(withCategory, Regions(badge));
    }

    [Fact]
    public void SpawnCountsReplaceTheRegionTextWithinItsExistingSecondLine()
    {
        var bounds = new Rectangle(20, 20, 212, 52);
        var badge = new MerchantOverlayForm.Badge(bounds, "24/25", true, false, "Altgard",
            PetCategory: "Fera", FarmSpawns: "Altgard (398)");
        using var image = Render(new Size(260, 90), Point.Empty, [badge]);
        using var namesOnly = Render(image.Size, Point.Empty, [badge with { FarmSpawns = "" }]);
        AssertPixelsDiffer(image, namesOnly, new Rectangle(bounds.Left + 8, bounds.Top + 22, 196, 16));
        AssertTransparentOutside(image, [bounds]);
    }

    [Theory]
    [InlineData(.9375)]
    [InlineData(1)]
    [InlineData(1.0625)]
    [InlineData(1.125)]
    [InlineData(1.25)]
    public void LongSpawnLinesStayInsideTheCardContourAndBelowTheCounter(double scale)
    {
        var badge = Assert.Single(MerchantOverlayForm.BuildBadges(GameSize, Anchors(scale, top: 300),
            [new(0, "synthetic-pet", "24/25", true, false, "Abyss / Altgard / Verteron",
                "Natura", "Abyss (209) / Altgard (398) / Verteron (377)")]));
        var blank = badge with { FarmRegions = "" };
        using var image = Render(new Size(400, 500), Point.Empty, [badge]);
        using var withoutFarmText = Render(image.Size, Point.Empty, [blank]);
        using var counterLabelOnly = Render(image.Size, Point.Empty, [blank with { Counter = "" }]);
        // Derive the actual counter glyph height and the foot silhouette from rendered pixels,
        // independently of the implementation's chosen line offsets or font sizes.
        var counterBottom = -1;
        for (var y = badge.Bounds.Top; y < badge.Bounds.Bottom; y++)
            for (var x = badge.Bounds.Left; x < badge.Bounds.Right; x++)
                if (withoutFarmText.GetPixel(x, y) != counterLabelOnly.GetPixel(x, y)) counterBottom = y;
        Assert.True(counterBottom >= badge.PriceBounds.Bottom);
        var farmPixels = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image.GetPixel(x, y) == withoutFarmText.GetPixel(x, y)) continue;
            farmPixels++;
            Assert.True(y > counterBottom, $"Farm text overlaps the counter at ({x},{y}), scale {scale}.");
            var touchesFoot = false;
            // One physical pixel permits antialiasing and the subtle text shadow at the edge.
            for (var nearbyY = Math.Max(0, y - 1); nearbyY <= Math.Min(image.Height - 1, y + 1); nearbyY++)
                for (var nearbyX = Math.Max(0, x - 1); nearbyX <= Math.Min(image.Width - 1, x + 1); nearbyX++)
                    touchesFoot |= withoutFarmText.GetPixel(nearbyX, nearbyY).A > 0;
            Assert.True(touchesFoot, $"Farm text escapes the foot contour at ({x},{y}), scale {scale}.");
        }
        Assert.True(farmPixels > 0, "The long farm line must remain visible.");
        AssertTransparentOutside(image, Regions(badge));
    }

    [Theory]
    [InlineData("Altgard")]
    [InlineData("Special")]
    public void AnEmptySpawnLabelRetainsTheOriginalFarmRegionRendering(string regions)
    {
        var badge = new MerchantOverlayForm.Badge(new Rectangle(20, 20, 212, 52), "24/25", true, false,
            regions, PetCategory: "Cogni");
        using var fallback = Render(new Size(260, 90), Point.Empty, [badge]);
        using var explicitLabel = Render(fallback.Size, Point.Empty, [badge with { FarmSpawns = regions }]);
        for (var y = 0; y < fallback.Height; y++)
            for (var x = 0; x < fallback.Width; x++)
                Assert.Equal(fallback.GetPixel(x, y), explicitLabel.GetPixel(x, y));
    }

    [Fact]
    public void SpecialHighlightDependsOnFarmClassificationRatherThanCategoryOrDisplayedSpawnText()
    {
        var bounds = new Rectangle(20, 20, 212, 52);
        var normal = new MerchantOverlayForm.Badge(bounds, "24/25", true, false, "Altgard",
            PetCategory: "Natura", FarmSpawns: "Altgard (398)");
        using var reference = Render(new Size(260, 90), Point.Empty, [normal]);
        using var misleadingText = Render(reference.Size, Point.Empty,
            [normal with { PetCategory = "Special", FarmSpawns = "Special" }]);
        using var actualSpecial = Render(reference.Size, Point.Empty,
            [normal with { FarmRegions = "Special" }]);
        var edge = new Point(bounds.Left, bounds.Top + 10);
        Assert.Equal(reference.GetPixel(edge.X, edge.Y), misleadingText.GetPixel(edge.X, edge.Y));
        Assert.True(actualSpecial.GetPixel(edge.X, edge.Y).GetBrightness() > reference.GetPixel(edge.X, edge.Y).GetBrightness());
    }

    [Fact]
    public void AnOffscreenCategoryHeaderRejectsTheWholeSuppliedLayout()
    {
        MerchantCounterRow[] rows = [new(0, "synthetic-a", "24/25", true, false, "Altgard", "Cogni", "Altgard (398)"),
            new(1, "synthetic-b", "?", false, false, "Special", "Natura", "Special")];
        var anchors = Anchors(top: 300);
        anchors[1] = anchors[1] with { PriceBounds = new Rect(350, 100, 146, 27) };
        Assert.Empty(MerchantOverlayForm.BuildBadges(GameSize, anchors, rows));
        var withoutHeaders = MerchantOverlayForm.BuildBadges(GameSize, anchors,
            rows.Select(row => row with { PetCategory = "" }).ToArray());
        Assert.Equal(rows.Length, withoutHeaders.Count);
        Assert.All(withoutHeaders, badge => Assert.True(badge.CategoryBounds.IsEmpty));
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
        MerchantOverlayForm.Badge[] badges = [new(new Rectangle(220, 320, 212, 36), "?", false, false,
                PetCategory: "Cogni", CategoryBounds: new Rectangle(278, 305, 96, 10)),
            new(new Rectangle(470, 380, 212, 36), "max", true, true,
                PetCategory: "Fera", CategoryBounds: new Rectangle(528, 305, 96, 10))];
        using var bitmap = Render(new Size(520, 140), origin, badges);
        var translated = badges.SelectMany(Regions).Select(bounds =>
            new Rectangle(bounds.X - origin.X, bounds.Y - origin.Y, bounds.Width, bounds.Height)).ToArray();
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

    private static MerchantCardAnchor[] Anchors(double scale = 1, int top = 100) => Enumerable.Range(0, 10).Select(i =>
        new MerchantCardAnchor(i, new Rect(100 + i % 5 * 250, top + i / 5 * 300, (int)(146 * scale), (int)(27 * scale)), scale, .9)).ToArray();

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

    private static Rectangle[] Regions(MerchantOverlayForm.Badge badge) => badge.CategoryBounds.IsEmpty
        ? [badge.Bounds] : [badge.Bounds, badge.CategoryBounds];

    private static Bitmap Render(Size size, Point origin, IReadOnlyList<MerchantOverlayForm.Badge> badges, float dpi = 96)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        MerchantOverlayForm.DrawBadges(graphics, origin, badges);
        return bitmap;
    }

    private static void AssertPixelsDiffer(Bitmap first, Bitmap second, Rectangle area)
    {
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return;
        Assert.Fail("The requested metadata must add visible text within its existing line.");
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
