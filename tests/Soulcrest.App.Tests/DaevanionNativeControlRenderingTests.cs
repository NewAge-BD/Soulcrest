using System.Drawing;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNativeControlRenderingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectionAndPointerFeedbackStayInsideTheirOwnButtonWithClearGaps(double scale)
    {
        var layout = Layout(scale);
        var first = layout.Items[0];
        using var normal = DaevanionStatRailRenderer.Render(layout);
        using var selected = DaevanionStatRailRenderer.Render(layout with
        {
            Items = layout.Items.Select(item => item.Id == first.Id ? item with { Selected = true } : item).ToArray()
        });
        using var hover = DaevanionStatRailRenderer.Render(layout, first.Id);
        using var pressed = DaevanionStatRailRenderer.Render(layout, first.Id, first.Id);
        foreach (var changed in new[] { selected, hover, pressed })
        {
            var changedPixels = 0;
            for (var y = 0; y < normal.Height; y++)
            for (var x = 0; x < normal.Width; x++)
            {
                var expected = normal.GetPixel(x, y);
                var actual = changed.GetPixel(x, y);
                if (!layout.Items.Any(item => item.Bounds.Contains(x, y))) Assert.Equal(0, actual.A);
                if (actual.ToArgb() == expected.ToArgb()) continue;
                Assert.True(first.Bounds.Contains(x, y), "A stat state changed another button or a click-through gap.");
                changedPixels++;
            }
            Assert.True(changedPixels > 20);
        }
        Assert.Equal(0, normal.GetPixel(0, 0).A);
        Assert.Equal(0, selected.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData(DaevanionStatRailItemKind.Clear)]
    [InlineData(DaevanionStatRailItemKind.Previous)]
    [InlineData(DaevanionStatRailItemKind.Next)]
    public void DisabledControlsDoNotImplyClickableHoverOrPress(DaevanionStatRailItemKind kind)
    {
        var layout = new DaevanionStatRailLayout(new(35, 80, 176, 28),
            [new("disabled", kind, new(0, 0, 176, 28), "Alle Highlights aus", "", Enabled: false)], 0, 1);
        using var normal = DaevanionStatRailRenderer.Render(layout);
        using var hover = DaevanionStatRailRenderer.Render(layout, "disabled");
        using var pressed = DaevanionStatRailRenderer.Render(layout, "disabled", "disabled");
        for (var y = 0; y < normal.Height; y++)
        for (var x = 0; x < normal.Width; x++)
        {
            Assert.Equal(normal.GetPixel(x, y), hover.GetPixel(x, y));
            Assert.Equal(normal.GetPixel(x, y), pressed.GetPixel(x, y));
        }
    }

    private static DaevanionStatRailLayout Layout(double scale)
    {
        int Scaled(int value) => (int)Math.Round(value * scale);
        var width = Scaled(176); var row = Scaled(28); var step = Scaled(32);
        return new(new(50, 80, width, step * 2 + row),
        [
            new("Critical", DaevanionStatRailItemKind.Stat, new(0, 0, width, row), "Crit", "Critical Hit"),
            new("PvEAccuracy", DaevanionStatRailItemKind.Stat, new(0, step, width, row), "PvE ACC", "PvE Accuracy"),
            new(DaevanionStatRailLayout.ClearId, DaevanionStatRailItemKind.Clear, new(0, step * 2, width, row), "Alle Highlights aus", "")
        ], 0, 1);
    }
}
