using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNativeEngravingTests
{
    [Theory]
    [InlineData("ariel", "de", 48)]
    [InlineData("ariel", "en", 60)]
    [InlineData("ariel", "de", 90)]
    [InlineData("azphel", "en", 48)]
    [InlineData("azphel", "de", 60)]
    [InlineData("azphel", "en", 90)]
    public void ChangingOneDoubleBonusNeverChangesOrHidesTheOtherRow(string family, string language, int size)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = board.Nodes.First(candidate => candidate.Effects.Count == 2);
        using var renderer = new DaevanionOverlayRenderer();
        using var original = Draw(node);
        using var mask = new DaevanionNodeMask();
        var interior = mask.ContentBounds(new(size, size));
        interior.Offset(12, 12);
        foreach (var index in new[] { 0, 1 })
        {
            var effects = node.Effects.ToArray();
            effects[index] = effects[index] with { DisplayAmount = new("+987,65 %", "+987.65%") };
            using var changed = Draw(node with { Effects = effects });
            var differences = 0;
            for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
            {
                if (original.GetPixel(x, y).ToArgb() == changed.GetPixel(x, y).ToArgb()) continue;
                differences++;
                Assert.True(interior.Contains(x, y));
                if (index == 0) Assert.True(y < interior.Top + interior.Height * .51);
                else Assert.True(y > interior.Top + interior.Height * .50);
            }
            Assert.True(differences > 10);
        }

        Bitmap Draw(DaevanionNode displayed) => renderer.Render(new(size + 24, size + 24),
            board with { Nodes = [displayed] },
            [new(displayed.Row, displayed.Column, new(12, 12, size, size), 1)], language, spacing: size + 16);
    }

    [Fact]
    public void NativeStatMaterialIsBundledAndNeedsNoExternalAssetDirectory()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Stat);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(10, 10, 90, 90), 1);
        using var normal = new DaevanionOverlayRenderer();
        using var missingAssetDirectory = new DaevanionOverlayRenderer(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var expected = normal.Render(new(110, 110), board, [anchor], "en");
        using var actual = missingAssetDirectory.Render(new(110, 110), board, [anchor], "en");
        Assert.True(actual.GetPixel(55, 55).A > 0);
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
            Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }

    [Fact]
    public void NativeSkillPictureAndBadgeDoNotChangeWithStatActivationLighting()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(candidate => candidate.Kind == DaevanionNodeKind.Skill);
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(10, 10, 90, 90), 1);
        using var renderer = new DaevanionOverlayRenderer();
        using var dark = renderer.Render(new(110, 110), board, [anchor with { Appearance = DaevanionNodeAppearance.Inactive }], "en");
        using var light = renderer.Render(new(110, 110), board, [anchor with { Appearance = DaevanionNodeAppearance.Active }], "en");
        Assert.True(dark.GetPixel(55, 55).A > 0);
        for (var y = 0; y < dark.Height; y++)
        for (var x = 0; x < dark.Width; x++)
            Assert.Equal(dark.GetPixel(x, y).ToArgb(), light.GetPixel(x, y).ToArgb());
    }
}
