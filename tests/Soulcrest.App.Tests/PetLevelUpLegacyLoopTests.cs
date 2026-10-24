using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Checks the Legacy production painter without windows, services or captures.</summary>
[Collection("Settings file")]
public sealed class PetLevelUpLegacyLoopTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SweepAndPortraitPulseContinueInLateCyclesWithoutMovingLegacyContent(int level)
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = "en";
            var maximum = level == 3;
            PetOverlayForm.LootRow[] focus = [Pet("Focus pet", maximum ? "next" : "active", maximum ? 2 : level)];
            PetOverlayForm.LootRow[] drops = [Pet("Other pet", "other", 1)];
            var frame = new PetOverlayForm.LevelUpRow("Completed pet", level, maximum, null, 0,
                maximum ? "completed" : "active");
            var layout = PetOverlayForm.Measure(1, 1, maximum, OverlayThemeDefinition.Legacy);
            var top = layout.ContentTop + (maximum ? 0 : layout.SectionHeight);
            var height = maximum ? layout.CelebrationHeight : layout.RowHeight;

            // Opacity stays constant in both late cycles. The sweep is right of the portrait
            // in all four frames, keeping the halo probe independent of its moving light.
            foreach (var (firstSeconds, secondSeconds) in new[] { (14.4, 15.0), (27.6, 28.5) })
            {
                using var first = Render(focus, drops, frame with
                {
                    Progress = firstSeconds / PetLevelUpAnimation.DurationSeconds
                });
                using var second = Render(focus, drops, frame with
                {
                    Progress = secondSeconds / PetLevelUpAnimation.DurationSeconds
                });

                Assert.Equal(layout.Size, first.Size);
                Assert.Equal(first.Size, second.Size);
                Assert.True(DifferentPixels(first, second, new(230, top + 30, 30, 10)) > 10,
                    $"The Legacy level {level} surface must still sweep at {firstSeconds} seconds.");
                var halo = maximum ? new Rectangle(16, top + 35, 4, 12) : new Rectangle(14, top + 26, 4, 12);
                Assert.True(DifferentPixels(first, second, halo) > 2,
                    $"The Legacy level {level} portrait must still pulse at {firstSeconds} seconds.");
                AssertOnlyChangesInside(first, second, new(8, top, 344, height));
            }

            Assert.All(focus.Concat(drops), pet => Assert.Null(pet.LevelUp));
            Assert.Equal(4, focus[0].Current);
        }
        finally { UiText.Language = previousLanguage; }
    }

    private static PetOverlayForm.LootRow Pet(string name, string id, int level) =>
        new(name, "fera", level, false, 4, level == 1 ? 25 : 75,
            SoulName: "Preview soul", PetId: id);

    private static Bitmap Render(IReadOnlyList<PetOverlayForm.LootRow> focus,
        IReadOnlyList<PetOverlayForm.LootRow> drops, PetOverlayForm.LevelUpRow frame)
    {
        var placed = PetOverlayForm.PlaceLevelUpRows(focus, drops, frame);
        return PetOverlayForm.RenderLootBitmap(placed.Focus, placed.Drops, true, true, frame,
            OverlayThemeDefinition.Legacy);
    }

    private static int DifferentPixels(Bitmap first, Bitmap second, Rectangle area)
    {
        Assert.Equal(first.Size, second.Size);
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
    }

    private static void AssertOnlyChangesInside(Bitmap first, Bitmap second, Rectangle area)
    {
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
            if (!area.Contains(x, y)) Assert.Equal(first.GetPixel(x, y).ToArgb(), second.GetPixel(x, y).ToArgb());
    }
}
