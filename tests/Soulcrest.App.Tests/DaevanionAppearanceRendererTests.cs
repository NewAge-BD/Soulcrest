using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionAppearanceRendererTests
{
    [Theory]
    [InlineData(DaevanionNodeQuality.Common, 76)]
    [InlineData(DaevanionNodeQuality.Rare, 76)]
    [InlineData(DaevanionNodeQuality.Legend, 76)]
    [InlineData(DaevanionNodeQuality.Unique, 76)]
    [InlineData(DaevanionNodeQuality.Common, 62)]
    public void ActivationBrightensOnlyTheRoundStoneAndUnknownRemainsConservative(DaevanionNodeQuality quality, int spacing)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat && n.Quality == quality);
        using var renderer = new DaevanionOverlayRenderer();
        using var inactive = Render(DaevanionNodeAppearance.Inactive);
        using var active = Render(DaevanionNodeAppearance.Active);
        using var unknown = Render(DaevanionNodeAppearance.Unknown);
        using var repeated = Render(DaevanionNodeAppearance.Inactive);
        var changed = 0; double gain = 0;
        for (var y = 0; y < inactive.Height; y++)
        for (var x = 0; x < inactive.Width; x++)
        {
            var dark = inactive.GetPixel(x, y); var light = active.GetPixel(x, y);
            Assert.Equal(dark.ToArgb(), unknown.GetPixel(x, y).ToArgb());
            Assert.Equal(dark.ToArgb(), repeated.GetPixel(x, y).ToArgb());
            if (dark.ToArgb() == light.ToArgb()) continue;
            var distance = Math.Sqrt(Math.Pow(x + .5 - 50, 2) + Math.Pow(y + .5 - 50, 2));
            Assert.True(distance < 60 * .36, "Activation must not change native frames, the fitting or footer.");
            gain += light.GetBrightness() - dark.GetBrightness(); changed++;
        }
        Assert.True(changed > 300);
        Assert.True(gain / changed > .08, "The activated background must be visibly brighter, including tight grids.");

        Bitmap Render(DaevanionNodeAppearance appearance) => renderer.Render(new(110, 110), board,
            [new(node.Row, node.Column, new(20, 20, 60, 60), 1) { Appearance = appearance }], "de", spacing: spacing);
    }

    [Fact]
    public void CachedArtworkTracksAppearanceLanguageAndTooltipMaskWithoutRepaintingUnchangedNodes()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "azphel")!;
        var nodes = board.Nodes.Where(n => n.Kind == DaevanionNodeKind.Stat).Take(2).ToArray();
        var first = new DaevanionNodeAnchor(nodes[0].Row, nodes[0].Column, new(20, 20, 60, 60), 1)
            { Appearance = DaevanionNodeAppearance.Active };
        var second = new DaevanionNodeAnchor(nodes[1].Row, nodes[1].Column, new(120, 20, 60, 60), 1)
            { Appearance = DaevanionNodeAppearance.Inactive };
        using var renderer = new DaevanionOverlayRenderer();
        using var both = renderer.Render(new(210, 110), board, [first, second], "de", spacing: 100);
        using var tooltip = renderer.Render(new(210, 110), board, [first], "de", spacing: 100);
        using var changed = renderer.Render(new(210, 110), board,
            [first with { Appearance = DaevanionNodeAppearance.Inactive }], "en", spacing: 100);
        using var restored = renderer.Render(new(210, 110), board, [first, second], "de", spacing: 100);
        var different = false;
        for (var y = 0; y < both.Height; y++)
        for (var x = 0; x < both.Width; x++)
        {
            Assert.Equal(both.GetPixel(x, y).ToArgb(), restored.GetPixel(x, y).ToArgb());
            if (x < 100)
            {
                Assert.Equal(both.GetPixel(x, y).ToArgb(), tooltip.GetPixel(x, y).ToArgb());
                different |= tooltip.GetPixel(x, y).ToArgb() != changed.GetPixel(x, y).ToArgb();
            }
            else Assert.Equal(0, tooltip.GetPixel(x, y).A);
        }
        Assert.True(different);
    }
}
