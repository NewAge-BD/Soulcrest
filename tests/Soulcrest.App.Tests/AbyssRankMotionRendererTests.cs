using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssRankMotionRendererTests
{
    [Theory]
    [InlineData(false, 2640, 560, 1960, 2420)]
    [InlineData(true, 2780, 600, 2100, 2560)]
    public void SingleStageUsesTheApprovedPhasesAndNeverDuplicatesTheNextIcon(bool down, double duration,
        double morphStart, double returnStart, double landing)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        var from = stages[down ? 9 : 8].Threshold + 100;
        var to = stages[down ? 8 : 9].Threshold + 100;
        Assert.Equal(duration, AbyssRankMotionTimeline.GetDurationMs(from, to));
        Assert.Equal(morphStart, AbyssRankMotionTimeline.GetMorphStartMs(from, to));
        Assert.Equal(returnStart, AbyssRankMotionTimeline.GetReturnStartMs(from, to));
        Assert.Equal(landing, AbyssRankMotionTimeline.GetCommitMs(from, to));
        var center = AbyssRankMotionTimeline.Sample(from, to, morphStart);
        Assert.Equal(240, center.CenterX);
        Assert.Equal(76, center.CenterY);
        Assert.Equal(0, center.NextOpacity);
        Assert.Equal(down ? 1 : 0, center.MorphProgress);
        var returning = AbyssRankMotionTimeline.Sample(from, to, returnStart);
        Assert.Equal(down ? 8 : 9, returning.DisplayCurrentStageIndex);
        Assert.Equal(down ? 10 : 9, returning.DisplayNextStageIndex);
        var landed = AbyssRankMotionTimeline.Sample(from, to, landing);
        Assert.Equal(32, landed.CenterX);
        Assert.Equal(1, landed.IconScale);
        Assert.Equal(down ? 9 : 10, landed.DisplayNextStageIndex);
        Assert.Equal(0, landed.NextOpacity);
        var formerEnd = AbyssRankMotionTimeline.Sample(from, to, down ? 1540 : 1460);
        Assert.True(formerEnd.Active);
        Assert.Equal(240, formerEnd.CenterX);
        Assert.InRange(formerEnd.MorphProgress, .1, .9);
        Assert.Equal(0, formerEnd.NextOpacity);
        foreach (var time in Enumerable.Range(0, (int)duration + 1).Where(value => value % 10 == 0))
        {
            var sample = AbyssRankMotionTimeline.Sample(from, to, time);
            Assert.Equal(76, sample.CenterY);
            Assert.InRange(sample.IconScale, 1, down ? 1.06 : 1.10);
            Assert.Equal(sample.TotalOpacity, sample.RemainingOpacity);
        }
    }

    [Fact]
    public void SkippedStagesUseEveryAdjacentPairAndHoldEachIntermediateOriginal()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        foreach (var count in new[] { 2, 3, 5, 18 })
        foreach (var down in new[] { false, true })
        {
            var fromIndex = down ? count : 0;
            var toIndex = down ? 0 : count;
            var from = stages[fromIndex].Threshold;
            var to = stages[toIndex].Threshold;
            for (var step = 1; step <= count; step++)
            {
                var commit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, step);
                var held = AbyssRankMotionTimeline.Sample(from, to, commit - 35);
                Assert.Equal(down ? 0 : 1, held.MorphProgress);
                Assert.Equal(fromIndex + (step - 1) * (down ? -1 : 1), held.MotionFromStageIndex);
                Assert.Equal(fromIndex + step * (down ? -1 : 1), held.MotionToStageIndex);
                Assert.Equal(240, held.CenterX);
            }
            var final = AbyssRankMotionTimeline.Sample(from, to, AbyssRankMotionTimeline.GetDurationMs(from, to));
            Assert.False(final.Active);
            Assert.Equal(toIndex, final.DisplayCurrentStageIndex);
            Assert.InRange(AbyssRankMotionTimeline.GetDurationMs(from, to), 2500, 7000);
        }
    }

    [Fact]
    public void ReducedMotionHasNoTravelPulseOrLightAndCompletesIn720Milliseconds()
    {
        var from = AbyssRankCatalog.Default.Stages[4].Threshold;
        var to = AbyssRankCatalog.Default.Stages[5].Threshold;
        Assert.Equal(720, AbyssRankMotionTimeline.GetDurationMs(from, to, true));
        for (var time = 0; time <= 720; time += 10)
        {
            var frame = AbyssRankMotionTimeline.Sample(from, to, time, true);
            Assert.Equal(32, frame.CenterX);
            Assert.Equal(76, frame.CenterY);
            Assert.Equal(1, frame.IconScale);
            Assert.Equal(0, frame.LightStrength);
            Assert.Equal(1, frame.TotalOpacity);
        }
    }

    [Fact]
    public void ReducedSkippedStagesKeepTheSameReadableHoldsWithoutAFlight()
    {
        var stages = AbyssRankCatalog.Default.Stages;
        var from = stages[1].Threshold;
        var to = stages[4].Threshold;
        Assert.Equal(1620, AbyssRankMotionTimeline.GetDurationMs(from, to, true));
        for (var step = 1; step <= 3; step++)
        {
            var frame = AbyssRankMotionTimeline.Sample(from, to,
                AbyssRankMotionTimeline.GetMorphCommitMs(from, to, step, true) - 15, true);
            Assert.Equal(32, frame.CenterX);
            Assert.Equal(1, frame.MorphProgress);
            Assert.Equal(step, frame.MotionFromStageIndex);
        }
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryAdjacentStartAndEndExactlyMatchesTheExistingRenderer(double scale)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        for (var index = 0; index < stages.Count - 1; index++)
        foreach (var down in new[] { false, true })
        {
            var from = stages[down ? index + 1 : index].Threshold + 10;
            var to = stages[down ? index : index + 1].Threshold + 10;
            var source = Example(from);
            var target = Example(to);
            using var expectedStart = AbyssRankOverlayRenderer.RenderBitmap(source, "aion2-stylized", scale, language: "en");
            using var expectedEnd = AbyssRankOverlayRenderer.RenderBitmap(target, "aion2-stylized", scale, language: "en");
            using var start = AbyssRankOverlayRenderer.RenderBitmap(target, "aion2-stylized", scale, language: "en",
                frame: AbyssRankMotionTimeline.Sample(from, to, 0));
            using var end = AbyssRankOverlayRenderer.RenderBitmap(target, "aion2-stylized", scale, language: "en",
                frame: AbyssRankMotionTimeline.Sample(from, to, AbyssRankMotionTimeline.GetDurationMs(from, to)));
            Assert.Equal(Pixels(expectedStart), Pixels(start));
            Assert.Equal(Pixels(expectedEnd), Pixels(end));
        }
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(1)]
    [InlineData(2)]
    public void MotionLeavesSessionMetricsAndThePanelBelowItsSeparatorUntouched(double scale)
    {
        var from = AbyssRankCatalog.Default.Stages[15].Threshold;
        var to = AbyssRankCatalog.Default.Stages[16].Threshold;
        var snapshot = Example(to);
        var frame = AbyssRankMotionTimeline.Sample(from, to, 680) with { MorphProgress = 0 };
        using var regular = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", scale, language: "en");
        using var motion = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", scale, language: "en", frame: frame);
        var top = (int)Math.Ceiling(105 * scale);
        for (var y = top; y < regular.Height; y++)
        for (var x = 0; x < regular.Width; x++) Assert.Equal(regular.GetPixel(x, y), motion.GetPixel(x, y));
        Assert.True(frame.TotalOpacity < .2);
    }

    [Fact]
    public void TextDimmingChangesOnlyTheTotalAndRemainingPointRegions()
    {
        var from = AbyssRankCatalog.Default.Stages[7].Threshold;
        var to = AbyssRankCatalog.Default.Stages[8].Threshold + 100;
        var snapshot = Example(to);
        var final = AbyssRankMotionTimeline.Sample(from, to, AbyssRankMotionTimeline.GetDurationMs(from, to));
        using var visible = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", language: "en", frame: final);
        using var dimmed = AbyssRankOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", language: "en",
            frame: final with { TotalOpacity = 0, RemainingOpacity = 0 });
        var totalChanged = false;
        var remainingChanged = false;
        for (var y = 0; y < visible.Height; y++)
        for (var x = 0; x < visible.Width; x++)
        {
            if (visible.GetPixel(x, y) == dimmed.GetPixel(x, y)) continue;
            var point = new Point(x, y);
            var inTotal = AbyssRankOverlayRenderer.ProgressBounds.Contains(point);
            var inRemaining = Rectangle.Ceiling(AbyssRankOverlayRenderer.RemainingBounds).Contains(point);
            Assert.True(inTotal || inRemaining);
            totalChanged |= inTotal;
            remainingChanged |= inRemaining;
        }
        Assert.True(totalChanged);
        Assert.True(remainingChanged);
    }

    [Fact]
    public void MissingMorphAssetsHoldTheSourceInsteadOfSwitchingAtHalfway()
    {
        var from = AbyssRankCatalog.Default.Stages[15].Threshold;
        var to = AbyssRankCatalog.Default.Stages[16].Threshold;
        var frame = AbyssRankMotionTimeline.Sample(from, to, 680) with { LightStrength = 0 };
        using var first = AbyssRankOverlayRenderer.RenderBitmap(Example(to), "aion2-stylized", language: "en",
            frame: frame with { MorphProgress = .25 });
        using var second = AbyssRankOverlayRenderer.RenderBitmap(Example(to), "aion2-stylized", language: "en",
            frame: frame with { MorphProgress = .75 });
        Assert.Equal(Pixels(first), Pixels(second));
    }

    [Fact]
    public void MorphCacheIsBoundedNormalizesLikeTheOriginalCacheAndDisposesCleanly()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "Soulcrest-rank-morph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var catalog = AbyssRankCatalog.Default.Stages;
            for (var pair = 0; pair < 3; pair++)
            {
                var directory = Path.Combine(temporary, catalog[pair].Id + "--" + catalog[pair + 1].Id);
                Directory.CreateDirectory(directory);
                for (var frame = 0; frame < AbyssRankMorphCache.FrameCount; frame++)
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "wwwroot", catalog[pair].IconAsset.Replace('/', Path.DirectorySeparatorChar)),
                        Path.Combine(directory, $"{frame:D3}.png"));
            }
            using var iconCache = new LootOverlayIconCache();
            using var morphs = new AbyssRankMorphCache(temporary);
            using var expected = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            using var actual = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(expected)) Assert.True(iconCache.Draw(graphics, catalog[0].IconAsset, new Rectangle(0, 0, 64, 64)));
            using (var graphics = Graphics.FromImage(actual))
            {
                Assert.True(morphs.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
                Assert.Equal(Pixels(expected), Pixels(actual));
                for (var pair = 0; pair < 3; pair++)
                for (var frame = 1; frame < AbyssRankMorphCache.FrameCount - 1; frame++)
                    Assert.True(morphs.Draw(graphics, pair, pair + 1, frame / 60d, new RectangleF(0, 0, 64, 64)));
                Assert.InRange(morphs.Count, 1, 128);
                Assert.False(morphs.Draw(graphics, 0, 1, 0, new RectangleF(0, 0, 64, 64)));
                Assert.False(morphs.Draw(graphics, 0, 2, .5, new RectangleF(0, 0, 64, 64)));
                morphs.Dispose();
                Assert.Equal(0, morphs.Count);
                Assert.False(morphs.Draw(graphics, 0, 1, .5, new RectangleF(0, 0, 64, 64)));
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static AbyssRankOverlaySnapshot Example(long points) => new(points, true, true)
    {
        SessionActive = true, SessionId = "synthetic-rank-session", SessionNetRankPoints = 1234,
        SessionRankPointsPerHour = 3702
    };

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
