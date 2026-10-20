using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayThemePanelTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(28, 28)]
    [InlineData(1, 28)]
    [InlineData(28, 2)]
    public void EveryNonlegacyPanelStaysInsideItsBoundsEvenWhenTheFrameIsSmallerThanItsOrnaments(int width, int height)
    {
        var bounds = new Rectangle(13, 17, width, height);
        foreach (var theme in OverlayThemeDefinition.All.Where(theme => !theme.IsLegacy))
        {
            using var bitmap = new Bitmap(72, 72, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                theme.Panel(graphics, bounds);
            }
            var visible = false;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var alpha = bitmap.GetPixel(x, y).A;
                    if (bounds.Contains(x, y)) visible |= alpha > 0;
                    else Assert.True(alpha == 0, $"{theme.Id} painted outside {width}×{height} bounds at ({x}, {y}).");
                }
            Assert.True(visible, $"{theme.Id} must still paint a {width}×{height} panel.");
        }
    }

    [Fact]
    public void EveryNonlegacyPanelIntersectsAndRestoresTheCallersComplexClipAndTransform()
    {
        foreach (var theme in OverlayThemeDefinition.All.Where(theme => !theme.IsLegacy))
        {
            using var bitmap = new Bitmap(100, 100, PixelFormat.Format32bppPArgb);
            using var mask = new Bitmap(100, 100, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(7, 9);
            graphics.ScaleTransform(1.25f, 1.5f);
            graphics.SetClip(new Rectangle(5, 7, 37, 40));
            graphics.ExcludeClip(new Rectangle(18, 20, 5, 6));
            using var previousTransform = graphics.Transform;
            using var previousClip = graphics.Clip;
            using (var maskGraphics = Graphics.FromImage(mask))
            {
                maskGraphics.Clear(Color.Transparent);
                maskGraphics.Transform = previousTransform;
                maskGraphics.Clip = previousClip;
                using var ink = new SolidBrush(Color.White);
                maskGraphics.FillRectangle(ink, -1000, -1000, 2000, 2000);
            }

            theme.Panel(graphics, new Rectangle(2, 4, 48, 48));

            using var actualTransform = graphics.Transform;
            using var actualClip = graphics.Clip;
            Assert.Equal(previousTransform.Elements, actualTransform.Elements);
            Assert.True(previousClip.Equals(actualClip, graphics), $"{theme.Id} changed the caller's clip.");
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    if (mask.GetPixel(x, y).A == 0)
                        Assert.True(bitmap.GetPixel(x, y).A == 0, $"{theme.Id} escaped the caller's clip at ({x}, {y}).");
        }
    }
}
