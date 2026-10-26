using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure production-renderer checks. No capture, game window, app host or settings access.</summary>
[Collection("Settings file")]
public sealed class LootKinaOverlayRendererTests
{
    public static IEnumerable<object[]> Themes => OverlayThemeDefinition.All.Select(theme => new object[] { theme.Id });

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryThemeRendersAllDropRowsAndScalesItsMeasuredBounds(string theme)
    {
        var snapshot = Example() with { Drops = Enumerable.Range(0, 24)
            .Select(index => new LootOverlayDrop(index.ToString(), "Example item " + index, index + 1)).ToArray() };
        using var full = LootOverlayRenderer.RenderBitmap(snapshot, theme);
        using var compact = LootOverlayRenderer.RenderBitmap(snapshot, theme, .6);
        Assert.Equal(400, full.Width);
        Assert.Equal(LootOverlayRenderer.DropsTop + 24 * LootOverlayRenderer.RowHeight + LootOverlayRenderer.FooterHeight, full.Height);
        Assert.Equal((int)Math.Ceiling(full.Width * .6), compact.Width);
        Assert.Equal((int)Math.Ceiling(full.Height * .6), compact.Height);
        for (var index = 0; index < snapshot.Drops.Count; index++)
            Assert.Contains(RowPixels(full, LootOverlayRenderer.DropsTop + index * LootOverlayRenderer.RowHeight + 8, 12),
                color => color.A > 200 && color.R > 170 && color.G > 160);
    }

    [Fact]
    public void UnknownDailyTotalDoesNotUseTheSessionAmountAsItsProgress()
    {
        var unknown = Example() with { DailyMonsterKina = null, SessionKina = 900_000 };
        using var unknownImage = LootOverlayRenderer.RenderBitmap(unknown, "aion2-stylized");
        using var zero = LootOverlayRenderer.RenderBitmap(unknown with { DailyMonsterKina = 0 }, "aion2-stylized");
        using var sessionAsDaily = LootOverlayRenderer.RenderBitmap(unknown with { DailyMonsterKina = unknown.SessionKina }, "aion2-stylized");
        Assert.False(unknown.DailyLimitReached);
        Assert.NotEqual(Pixels(zero), Pixels(unknownImage));
        Assert.NotEqual(Pixels(sessionAsDaily), Pixels(unknownImage));
        Assert.DoesNotContain(Enumerable.Range(20, 90).Select(x => unknownImage.GetPixel(x, 84)),
            color => color.B > 150 && color.A > 150);
    }

    [Fact]
    public void DailyProgressIsBlueUntilTheLimitThenStaysGoldAfterCelebrationEnds()
    {
        using var blue = LootOverlayRenderer.RenderBitmap(Example(), "aion2-stylized");
        var complete = Example() with { DailyMonsterKina = LootOverlaySnapshot.DailyLimit };
        using var first = LootOverlayRenderer.RenderBitmap(complete, "aion2-stylized", completionSeconds: 0);
        using var late = LootOverlayRenderer.RenderBitmap(complete, "aion2-stylized", animationSeconds: 500, completionSeconds: 100);
        var bluePixel = blue.GetPixel(80, 84);
        var goldPixel = late.GetPixel(80, 84);
        Assert.True(bluePixel.B > bluePixel.R + 60);
        Assert.True(goldPixel.R > goldPixel.B + 60 && goldPixel.G > goldPixel.B + 30);
        using var stable = LootOverlayRenderer.RenderBitmap(complete with { SessionActive = false }, "aion2-stylized");
        using var longAfter = LootOverlayRenderer.RenderBitmap(complete with { SessionActive = false }, "aion2-stylized", animationSeconds: 10000);
        Assert.Equal(Pixels(stable), Pixels(longAfter));
        Assert.NotEqual(Pixels(first), Pixels(late));
    }

    [Fact]
    public void LimitCelebrationNeverCoversTheSubtitleOrDailyStatus()
    {
        var snapshot = Example() with { DailyMonsterKina = 1_000_000, SessionActive = false };
        using var baseline = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized");
        using var celebration = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", completionSeconds: .55);
        var track = LootOverlayRenderer.DailyBar;
        for (var y = 0; y < baseline.Height; y++)
        {
            if (y >= track.Top && y < track.Bottom) continue;
            for (var x = 0; x < baseline.Width; x++) Assert.Equal(baseline.GetPixel(x, y), celebration.GetPixel(x, y));
        }
        Assert.NotEqual(Pixels(baseline), Pixels(celebration));
    }

    [Fact]
    public void SessionSweepTravelsRightInsideTheDailySessionContribution()
    {
        var snapshot = Example();
        using var left = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: .5);
        using var right = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 1.5);
        Assert.True(BrightestSessionX(right, snapshot) > BrightestSessionX(left, snapshot) + 20);
        var bounds = Rectangle.Ceiling(LootOverlayRenderer.SessionSegmentBounds(snapshot));
        for (var y = 0; y < left.Height; y++)
        {
            for (var x = 0; x < left.Width; x++)
                if (!bounds.Contains(x, y)) Assert.Equal(left.GetPixel(x, y), right.GetPixel(x, y));
        }
        using var stopped = LootOverlayRenderer.RenderBitmap(snapshot with { SessionActive = false }, "aion2-stylized", animationSeconds: .5);
        using var stoppedLater = LootOverlayRenderer.RenderBitmap(snapshot with { SessionActive = false }, "aion2-stylized", animationSeconds: 10);
        Assert.Equal(Pixels(stopped), Pixels(stoppedLater));
    }

    [Fact]
    public void CurrentSessionGrowsProportionallyFromTheBluePriorDailyAmount()
    {
        var first = Example() with { DailyMonsterKina = 600_000, SessionKina = 100_000 };
        var next = first with { DailyMonsterKina = 750_000, SessionKina = 250_000 };
        var before = LootOverlayRenderer.SessionSegmentBounds(first);
        var after = LootOverlayRenderer.SessionSegmentBounds(next);
        Assert.Equal(before.Left, after.Left);
        Assert.Equal(200f, before.Left);
        Assert.Equal(36.6, before.Width, 3);
        Assert.Equal(91.5, after.Width, 3);
        using var image = LootOverlayRenderer.RenderBitmap(next, "aion2-stylized", animationSeconds: 0);
        var blue = image.GetPixel(80, LootOverlayRenderer.DailyBar.Top + 2);
        var green = image.GetPixel(220, LootOverlayRenderer.DailyBar.Top + 2);
        Assert.True(blue.B > blue.R + 60);
        Assert.True(green.G > green.R + 40 && green.G > green.B + 20);
    }

    [Fact]
    public void SessionContributionCannotFillBeyondTheKnownDailyAmount()
    {
        var snapshot = Example() with { DailyMonsterKina = 50_000, SessionKina = 500_000 };
        var segment = LootOverlayRenderer.SessionSegmentBounds(snapshot);
        Assert.Equal(17f, segment.Left);
        Assert.Equal(18.3, segment.Width, 3);
        using var clipped = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 1);
        using var baseline = LootOverlayRenderer.RenderBitmap(snapshot with { SessionKina = 0 }, "aion2-stylized", animationSeconds: 1);
        for (var x = (int)Math.Ceiling(segment.Right) + 1; x < LootOverlayRenderer.DailyBar.Right - 1; x++)
            Assert.Equal(baseline.GetPixel(x, 82), clipped.GetPixel(x, 82));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void InactiveUnavailableOrCachedSessionHasNoGuessedGreenContribution(bool active, bool available, bool confirmed)
    {
        var snapshot = Example() with { SessionActive = active, TrackingAvailable = available, DailyConfirmed = confirmed };
        Assert.Equal(RectangleF.Empty, LootOverlayRenderer.SessionSegmentBounds(snapshot));
        Assert.False(new LootOverlayAnimation().IsAnimating(snapshot, 0));
        using var first = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: .5);
        using var later = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 1.5);
        Assert.Equal(Pixels(first), Pixels(later));
    }

    [Theory]
    [InlineData(null, 500000L)]
    [InlineData(0L, 500000L)]
    [InlineData(600000L, 0L)]
    [InlineData(1000000L, 500000L)]
    public void UnknownEmptyAndCompletedDailyTotalsNeedNoSessionAnimation(long? daily, long session)
    {
        var snapshot = Example() with { DailyMonsterKina = daily, SessionKina = session };
        Assert.Equal(RectangleF.Empty, LootOverlayRenderer.SessionSegmentBounds(snapshot));
        Assert.False(new LootOverlayAnimation().IsAnimating(snapshot, 0));
    }

    [Fact]
    public void AOneKinaContributionKeepsItsExactSubpixelExtentWithoutExpandingTheTrack()
    {
        var snapshot = Example() with { DailyMonsterKina = 500_001, SessionKina = 1 };
        Assert.InRange(LootOverlayRenderer.SessionSegmentBounds(snapshot).Width, 0, .001f);
        using var image = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized");
        Assert.Equal(400, image.Width);
    }

    [Fact]
    public void ExactIconsHaveDedicatedBoundsAndNeverCoverKinaDigitsOrDropCounts()
    {
        var path = Path.Combine(Path.GetTempPath(), "Soulcrest-LootIcon-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var icon = new Bitmap(24, 24, PixelFormat.Format32bppPArgb))
            {
                using (var graphics = Graphics.FromImage(icon)) graphics.Clear(Color.Magenta);
                icon.Save(path, ImageFormat.Png);
            }
            var example = Example();
            var snapshot = example with
            {
                KinaIconPath = path,
                Drops = [example.Drops[0] with { IconPath = path }, .. example.Drops.Skip(1)]
            };
            using var cache = new LootOverlayIconCache();
            using var image = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", icons: cache);
            Rectangle[] iconBounds = [LootOverlayRenderer.DailyKinaIconBounds,
                LootOverlayRenderer.SessionKinaIconBounds, LootOverlayRenderer.DropIconBounds(0)];
            Assert.Equal(1, cache.Count);
            foreach (var bounds in iconBounds)
            {
                var color = image.GetPixel(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                Assert.True(color.R > 240 && color.B > 240 && color.G < 20);
            }
            Assert.True(LootOverlayRenderer.DailyKinaIconBounds.Right + 4 <= LootOverlayRenderer.DailyKinaNumberBounds.Left);
            Assert.True(LootOverlayRenderer.SessionKinaIconBounds.Right + 4 <= LootOverlayRenderer.SessionKinaNumberBounds.Left);
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                if (color.R > 230 && color.B > 230 && color.G < 25)
                    Assert.Contains(iconBounds, bounds => bounds.Contains(x, y));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownIconsLeaveTheSlotEmptyInsteadOfInventingAnItemGraphic()
    {
        using var image = LootOverlayRenderer.RenderBitmap(Example(), "aion2-stylized");
        var row = LootOverlayRenderer.DropIconBounds(0);
        Assert.Equal(image.GetPixel(10, row.Top + row.Height / 2),
            image.GetPixel(row.Left + row.Width / 2, row.Top + row.Height / 2));
    }

    [Fact]
    public void LimitCelebrationRequiresAnObservedTransitionWithinTheSameCharacterDay()
    {
        var animation = new LootOverlayAnimation();
        var incomplete = Example() with { CompletionScopeKey = "character-a:day-one" };
        var complete = incomplete with { DailyMonsterKina = 1_000_000 };
        animation.Observe(incomplete, 0);
        Assert.Null(animation.CompletionSeconds(0));
        animation.Observe(complete, 5);
        Assert.Equal(.5, animation.CompletionSeconds(5.5));
        animation.Observe(complete with { SessionId = "next-session" }, 6);
        Assert.Equal(1.5, animation.CompletionSeconds(6.5));
        Assert.Null(animation.CompletionSeconds(8));

        animation.Observe(incomplete, 10);
        animation.Observe(complete with { CompletionScopeKey = "character-b:day-one" }, 11);
        Assert.Null(animation.CompletionSeconds(11));
        Assert.True((complete with { CompletionScopeKey = "character-b:day-one" }).DailyLimitReached);
        animation.Observe(complete with { CompletionScopeKey = "character-b:day-two" }, 12);
        Assert.Null(animation.CompletionSeconds(12));
    }

    [Fact]
    public void StartingWithAnAlreadyCompletedTotalDoesNotReplayTheCelebration()
    {
        var animation = new LootOverlayAnimation();
        animation.Observe(Example() with { DailyMonsterKina = 1_000_000, SessionActive = false }, 0);
        Assert.Null(animation.CompletionSeconds(0));
        Assert.False(animation.IsAnimating(Example() with { SessionActive = false }, 1));
        animation.Observe(Example() with { DailyMonsterKina = null }, 3);
        animation.Observe(Example() with { DailyMonsterKina = 1_000_000 }, 4);
        Assert.Null(animation.CompletionSeconds(4));
    }

    [Fact]
    public void CachedTotalsStayDistinctAndCannotCreateAReconnectCelebration()
    {
        var cached = Example() with { DailyConfirmed = false };
        using var cacheImage = LootOverlayRenderer.RenderBitmap(cached, "aion2-stylized");
        using var liveImage = LootOverlayRenderer.RenderBitmap(cached with { DailyConfirmed = true }, "aion2-stylized");
        Assert.NotEqual(Pixels(cacheImage), Pixels(liveImage));
        var animation = new LootOverlayAnimation();
        animation.Observe(cached, 0);
        animation.Observe(cached with { DailyMonsterKina = 1_000_000, DailyConfirmed = true }, 1);
        Assert.Null(animation.CompletionSeconds(1));
        animation.Observe(Example(), 2);
        animation.Observe(Example() with { DailyMonsterKina = 1_000_000, DailyConfirmed = false }, 3);
        Assert.Null(animation.CompletionSeconds(3));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(-10, .6)]
    [InlineData(10, 2)]
    public void InvalidOrExtremeScaleUsesTheSafeSharedRange(double requested, double expected) =>
        Assert.Equal(expected, LootOverlayRenderer.NormalizedScale(requested));

    internal static LootOverlaySnapshot Example() => new("synthetic-session", true, 184_720, 684_720,
    [
        new("synthetic-crystal-a", "Soul Crystal", 24, LootOverlayDropKind.SoulCrystal),
        new("synthetic-item-a", "Example item A", 12),
        new("synthetic-item-b", "Example item B", 3),
        new("synthetic-item-c", "Example item C", 1)
    ], CompletionScopeKey: "synthetic-character:synthetic-day");

    private static IEnumerable<Color> RowPixels(Bitmap image, int y, int height) => Enumerable.Range(y, height)
        .SelectMany(row => Enumerable.Range(36, image.Width - 52).Select(x => image.GetPixel(x, row)));

    private static int BrightestSessionX(Bitmap bitmap, LootOverlaySnapshot snapshot)
    {
        var segment = Rectangle.Ceiling(LootOverlayRenderer.SessionSegmentBounds(snapshot));
        return Enumerable.Range(segment.Left, segment.Width)
            .OrderByDescending(x => bitmap.GetPixel(x, segment.Top + 2).G).First();
    }

    private static byte[] Pixels(Bitmap image)
    {
        using var memory = new MemoryStream();
        image.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }
}

[Collection("Settings file")]
public sealed class LootKinaOverlayRenderExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderSyntheticLootOverlay()
    {
        var output = TestPrerequisites.RequiredValue("SOULCREST_LOOT_OVERLAY_PNG_DIRECTORY");
        Directory.CreateDirectory(output);
        var originalLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                // The placement preview uses the same bundled labels; every quantity is synthetic.
                var example = OverlayPreviewRenderer.FarmLootExample(language);
                foreach (var (name, snapshot, completion) in new (string, LootOverlaySnapshot, double?)[]
                {
                    ("regular", example, null),
                    ("complete", example with { DailyMonsterKina = 1_000_000 }, null),
                    ("celebration", example with { DailyMonsterKina = 1_000_000 }, .55),
                    ("cached", example with { DailyConfirmed = false }, null),
                    ("unknown", example with { DailyMonsterKina = null }, null)
                })
                {
                    using var image = LootOverlayRenderer.RenderPreview(snapshot, "aion2-stylized", 1.8, 1.4, completion);
                    image.Save(Path.Combine(output, "loot-overlay-" + language + "-" + name + ".png"), ImageFormat.Png);
                }
            }
        }
        finally { UiText.Language = originalLanguage; }
    }
}
