using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class TimerStripRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static AlertDisplayRow Row(string id, int minutes = 15) => new(id, "Example", "", "#ffffff", Now.AddMinutes(minutes), false);

    [Fact]
    public void OrderingIsStableAcrossCountdownChangesAndAllSelectedTimersRemainVisible()
    {
        var rows = new[] { Row("event:catalog:abyss"), Row("event:catalog:shugo"), Row("event:catalog:rift") };
        var first = TimerStripRenderer.Layout(rows);
        var changed = TimerStripRenderer.Layout(rows.Reverse().Select(row => row with { At = Now, Started = true }).ToArray());
        Assert.Equal(first.Cells.Select(cell => cell.Row.Key), changed.Cells.Select(cell => cell.Row.Key));
        Assert.Equal(first.Cells.Select(cell => cell.Bounds), changed.Cells.Select(cell => cell.Bounds));
        Assert.Equal(TimerStripRenderer.Height, first.Size.Height);
    }

    [Theory]
    [InlineData(200, 4)]
    [InlineData(800, 18)]
    [InlineData(1920, 48)]
    public void ManyTimersWrapWithoutOverlapOrTruncation(int maximumWidth, int gap)
    {
        var rows = Enumerable.Range(0, 24).Select(index => Row("event:" + index)).ToArray();
        var layout = TimerStripRenderer.Layout(rows, gap, maximumWidth);
        Assert.Equal(rows.Length, layout.Cells.Count);
        Assert.InRange(layout.Size.Width, 1, maximumWidth);
        foreach (var cell in layout.Cells)
        {
            Assert.True(new Rectangle(Point.Empty, layout.Size).Contains(cell.Bounds));
            Assert.True(cell.Bounds.Contains(cell.IconBounds));
            Assert.True(cell.Bounds.Contains(cell.TextBounds));
            foreach (var other in layout.Cells.Where(other => other != cell))
                Assert.False(cell.Bounds.IntersectsWith(other.Bounds));
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(640)]
    [InlineData(1920)]
    public void InlineOpenLabelKeepsItsOwnSpaceAndWrapsWithoutCoveringOtherTimers(int maximumWidth)
    {
        var open = Row("event:catalog:rift") with { Started = true, InlineLabel = "Spacetime Rift Open" };
        var rows = new[] { Row("event:catalog:shugo"), open, Row("event:catalog:abyss") };
        var layout = TimerStripRenderer.Layout(rows, maximumWidth: maximumWidth);
        var rift = Assert.Single(layout.Cells, cell => cell.Row.Key == open.Key);
        var label = Assert.IsType<Rectangle>(rift.LabelBounds);
        Assert.True(rift.Bounds.Contains(label));
        Assert.True(label.Width > 0);
        Assert.False(label.IntersectsWith(rift.TextBounds));
        Assert.False(label.IntersectsWith(rift.IconBounds));
        Assert.Equal(TimerStripRenderer.TextWidth, rift.TextBounds.Width);
        Assert.True(layout.Size.Width <= maximumWidth);
        foreach (var cell in layout.Cells)
        {
            Assert.True(new Rectangle(Point.Empty, layout.Size).Contains(cell.Bounds));
            Assert.All(layout.Cells.Where(other => other != cell), other => Assert.False(other.Bounds.IntersectsWith(cell.Bounds)));
        }
        var tick = TimerStripRenderer.Layout(rows.Select(row => row with { At = row.At.AddSeconds(-1) }).ToArray(), maximumWidth: maximumWidth);
        Assert.Equal(layout.Cells.Select(cell => cell.Bounds), tick.Cells.Select(cell => cell.Bounds));
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void OpenRiftLabelIsVisibleWithRealAlphaAndExactlyOneDpiIndependentScale(double scale)
    {
        var open = Row("event:catalog:rift") with { Started = true, InlineLabel = "Spacetime Rift Open" };
        var layout = TimerStripRenderer.Layout([open]);
        var label = Assert.Single(layout.Cells).LabelBounds!.Value;
        byte[]? previous = null;
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var bitmap = new Bitmap((int)Math.Ceiling(layout.Size.Width * scale), (int)Math.Ceiling(layout.Size.Height * scale), PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform((float)scale, (float)scale);
                TimerStripRenderer.Paint(graphics, layout, Now);
            }
            var sample = Rectangle.Intersect(Rectangle.FromLTRB((int)(label.Left * scale), (int)(label.Top * scale),
                (int)(label.Right * scale), (int)(label.Bottom * scale)), new Rectangle(Point.Empty, bitmap.Size));
            Assert.Contains(Enumerable.Range(sample.Top, sample.Height)
                .SelectMany(y => Enumerable.Range(sample.Left, sample.Width).Select(x => bitmap.GetPixel(x, y))), pixel => pixel.A > 200);
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            var pixels = Pixels(bitmap);
            if (previous is not null) Assert.Equal(previous, pixels);
            previous = pixels;
        }
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void RendererHasRealAlphaAndAppliesExactlyOneScaleRegardlessOfBitmapDpi(double scale)
    {
        var layout = TimerStripRenderer.Layout([Row("event:catalog:shugo"), Row("event:catalog:rift"),
            Row("event:catalog:invasion"), Row("event:catalog:abyss")]);
        byte[]? previous = null;
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var bitmap = new Bitmap((int)Math.Ceiling(layout.Size.Width * scale),
                (int)Math.Ceiling(layout.Size.Height * scale), PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform((float)scale, (float)scale);
                TimerStripRenderer.Paint(graphics, layout, Now);
            }
            Assert.Equal(0, bitmap.GetPixel(1, bitmap.Height / 2).A);
            var pixels = Pixels(bitmap);
            if (previous is not null) Assert.Equal(previous, pixels);
            previous = pixels;
        }
    }

    [Fact]
    public void StripAlwaysLeavesTheGapFullyTransparentAndLongTimesCannotPaintOutsideTheStrip()
    {
        var layout = TimerStripRenderer.Layout([Row("event:catalog:shugo"), Row("event:custom") with { At = Now.AddYears(5) }]);
        using var bitmap = new Bitmap(layout.Size.Width + 10, layout.Size.Height + 10);
        using (var graphics = Graphics.FromImage(bitmap))
            TimerStripRenderer.Paint(graphics, layout, Now);
        Assert.Equal(0, bitmap.GetPixel(0, 10).A);
        Assert.Equal(0, bitmap.GetPixel(layout.Cells[0].Bounds.Right + 5, 10).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);
    }

    [Theory]
    [InlineData("shugo", 72, "15e38196031624b5e311ac56fec0a060262c1d367f9cb2d06873727206299678")]
    [InlineData("rift", 128, "b5244784dee01effc5032b1d92b373126cb02ce1e9ad3ab48b9fe0f78b89c68b")]
    [InlineData("invasion", 1254, "d5576990f813ec6011c95d7acec90942988078cea22848fd8bb863df06de129f")]
    [InlineData("abyss", 64, "bbed46762a495631e592934c757dc24110a787163fc1da325d7087075b540135")]
    public void ShippedEventAssetsKeepTheirDocumentedDimensionsAndBytes(string key, int size, string sha256)
    {
        var path = TimerOverlayIcons.LocalPath(key, null)!;
        Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        using var icon = new Bitmap(path);
        Assert.Equal(new Size(size, size), icon.Size);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData(.6)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void StripFitsSmallAndNegativeCoordinateMonitors(double scale)
    {
        foreach (var area in new[] { new Rectangle(0, 0, 300, 180), new Rectangle(-1280, -700, 1280, 700) })
        {
            var bounds = AlertsOverlayForm.FitStripBounds(area, new Point(9000, -9000), new Size(614, 32), scale);
            Assert.True(area.Contains(bounds));
            Assert.True(bounds.Width > 0 && bounds.Height > 0);
        }
    }

    [Fact]
    public void UnlockButtonKeepsMonitorSpaceAndResizeUsesTheOriginalGestureAcrossWrapping()
    {
        var area = new Rectangle(-1280, 0, 1280, 720);
        const double scale = 1.25;
        var available = AlertsOverlayForm.StripArea(area, scale);
        var rows = Enumerable.Range(0, 8).Select(index => Row("event:" + index)).ToArray();
        var layout = TimerStripRenderer.Layout(rows, maximumWidth: (int)(available.Width / scale));
        var bounds = AlertsOverlayForm.FitStripBounds(available, new Point(0, 700), layout.Size, scale);
        Assert.True(bounds.Right + (int)Math.Ceiling(21 * scale) <= area.Right);
        var first = AlertsOverlayForm.ResizeScale(scale, 40, 43);
        // A wrap changes the rendered height, never the original gesture's scale basis.
        var wrapped = TimerStripRenderer.Layout(rows, maximumWidth: (int)(available.Width / first));
        Assert.True(wrapped.Size.Height >= layout.Size.Height);
        Assert.Equal(first, AlertsOverlayForm.ResizeScale(scale, 40, 43));
    }

    [Fact]
    public void ResetCustomAndMissingBossIconsAreDistinctShippedTransparentAssets()
    {
        List<byte[]> pixels = [];
        foreach (var id in new[] { "daily-reset", "weekly-reset", "custom", "boss" })
        {
            using var image = new Bitmap(TimerOverlayIcons.LocalPath(id, null)!);
            Assert.Equal(new Size(64, 64), image.Size);
            Assert.Equal(0, image.GetPixel(0, 0).A);
            var current = Pixels(image);
            Assert.DoesNotContain(pixels, previous => previous.SequenceEqual(current));
            Assert.Contains(current, value => value > 0);
            pixels.Add(current);
        }
    }

    [Fact]
    public void BossPortraitUsesItsRealSourceAndRemainsInsideItsCircularIconArea()
    {
        var row = Row("boss:1:2") with { IconId = "map:icons/example.png" };
        var layout = TimerStripRenderer.Layout([row]);
        using var portrait = new Bitmap(64, 64);
        using (var paint = Graphics.FromImage(portrait)) paint.Clear(Color.Red);
        using var bitmap = new Bitmap(layout.Size.Width, layout.Size.Height);
        var requested = new List<string>();
        using (var graphics = Graphics.FromImage(bitmap))
            TimerStripRenderer.Paint(graphics, layout, Now, icon: id => { requested.Add(id); return portrait; });
        Assert.Equal(new[] { row.IconId }, requested);
        var icon = layout.Cells[0].IconBounds;
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(icon.Left + 14, icon.Top + 14).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(icon.Left, icon.Top).A);
        Assert.Equal(0, bitmap.GetPixel(0, 1).A);
    }

    [Fact]
    public void NativeTimerUsesItsBundledBoldFontRatherThanAnInstalledFont()
    {
        Assert.Equal("Noto Sans", TimerStripTypeface.Family.Name);
        Assert.True(TimerStripTypeface.Family.IsStyleAvailable(FontStyle.Bold));
        Assert.Equal(FontStyle.Bold, TimerStripTypeface.Style);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", "timer", "OFL.txt")));
    }

    [Fact]
    public void MissingOrInvalidBossPortraitFallsBackAndCanRecoverWithoutRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-timer-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var cache = new TimerIconCache(folder);
            var fallback = cache.Load("map:portrait.png");
            Assert.NotNull(fallback);
            Assert.Equal(new Size(64, 64), fallback.Size);
            File.WriteAllText(Path.Combine(folder, "portrait.png"), "not an image");
            Assert.Same(fallback, cache.Load("map:portrait.png"));
            using (var image = new Bitmap(32, 32))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
                image.Save(Path.Combine(folder, "portrait.png"), ImageFormat.Png);
            }
            var restored = cache.Load("map:portrait.png");
            Assert.NotNull(restored);
            Assert.Equal(new Size(32, 32), restored.Size);
            Assert.NotSame(fallback, restored);
            // Cached images are detached from their streams, so the package can be replaced.
            File.Delete(Path.Combine(folder, "portrait.png"));
            Assert.Same(restored, cache.Load("map:portrait.png"));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var result = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            BitConverter.GetBytes(bitmap.GetPixel(x, y).ToArgb()).CopyTo(result, (y * bitmap.Width + x) * 4);
        return result;
    }
}
