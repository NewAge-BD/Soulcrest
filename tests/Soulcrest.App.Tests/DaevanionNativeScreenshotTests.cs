using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNativeScreenshotTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void ProductionContentAndIndependentControlsRenderOnTheConfirmedScreenshot(string language)
    {
        var catalog = DaevanionCatalog.Default;
        var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "daevanion", "nezekan-board.png");
        using var pixels = OpenCvSharp.Cv2.ImRead(source);
        var patterns = catalog.Boards.GroupBy(board => board.FamilyId).Select(group =>
            new DaevanionBoardPattern(group.Key, group.First().Nodes.Select(node =>
                new DaevanionPatternCell(node.Row, node.Column, node.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();
        var detection = DaevanionBoardLocator.Find(pixels, patterns);
        Assert.NotNull(detection);
        Assert.Equal("nezekan", detection.Family);
        var board = catalog.GetBoard("Cleric", detection.Family)!;
        using var screenshot = new Bitmap(source);
        using var renderer = new DaevanionOverlayRenderer();
        var size = new Size(screenshot.Width + 230, screenshot.Height);
        using var content = renderer.Render(size, board, detection.Nodes, language, spacing: detection.Spacing);
        // The native outer frame and corners are unchanged even on disabled nodes.
        foreach (var anchor in detection.Nodes)
        {
            var frame = anchor.Bounds;
            Assert.Equal(0, content.GetPixel(frame.X, frame.Y).A);
            Assert.Equal(0, content.GetPixel(frame.X + 1, frame.Y + frame.Height / 2).A);
        }
        var selected = new HashSet<string>(["Critical", "CoolTimeDecrease"], StringComparer.Ordinal);
        var toggle = DaevanionToggleLayout.Bounds(board, detection, size);
        var rail = DaevanionStatRailLayout.Build(board, detection, size, selected, language, toggle);
        Assert.False(toggle.IsEmpty);
        Assert.True(rail.HasSpace);
        Assert.False(toggle.IntersectsWith(rail.Bounds));
        foreach (var visible in new[] { true, false })
        {
            using var scene = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(scene))
            {
                graphics.Clear(Color.FromArgb(26, 33, 35));
                graphics.DrawImageUnscaled(screenshot, 0, 0);
                if (visible) graphics.DrawImageUnscaled(content, 0, 0);
                new DaevanionHighlightRenderer().Paint(graphics, board, detection.Nodes, selected,
                    spacing: detection.Spacing, phase: .16);
                DaevanionToggleRenderer.Paint(graphics, toggle, visible, language);
                DaevanionStatRailRenderer.Paint(graphics, rail);
            }
            // Offline export only. This exercises the actual live painters, never starts the app or capture.
            if (Environment.GetEnvironmentVariable("SOULCREST_DAEVANION_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                var name = $"screenshot-{(visible ? "on" : "off")}-{language}";
                scene.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                var crop = new Rectangle(Math.Max(0, screenshot.Width - 600), 0,
                    Math.Min(600, screenshot.Width) + 230, Math.Min(730, screenshot.Height));
                using var detail = scene.Clone(crop, PixelFormat.Format32bppPArgb);
                detail.Save(Path.Combine(output, name + "-detail.png"), ImageFormat.Png);
            }
        }
    }
}
