using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingProgressOverlayRendererTests
{
    [Fact]
    public void CompletedProgressUsesExactUniqueGoldAcrossAllFrameThemes()
    {
        var row = Example(1).Rows[0] with { RequiredQuantity = 2, KnownQuantity = 2,
            InventoryFresh = true, InventoryComplete = true, IsManualStock = false, EventAgeSeconds = 2 };
        var example = Example(1) with { Rows = [row] };
        Assert.Equal(LootRarityVisuals.Accent(LootItemRarity.Unique), CraftingProgressOverlayRenderer.ProgressGold);
        foreach (var theme in OverlayThemeDefinition.All)
        {
            using var image = CraftingProgressOverlayRenderer.Render(example, theme, animationsEnabled: false);
            // The covered amount rail is a solid shared state accent; frame/headings still use the selected theme.
            Assert.Equal(CraftingProgressOverlayRenderer.ProgressGold.ToArgb(), image.GetPixel(100,
                CraftingProgressOverlayRenderer.HeaderHeight + CraftingProgressOverlayRenderer.GoalHeight
                + CraftingProgressOverlayRenderer.SectionHeight + CraftingProgressOverlayRenderer.RowHeight - 3).ToArgb());
        }
    }
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void NativeProductionLayoutSupportsSharedThemesAndOneToThreeRows(string language)
    {
        foreach (var theme in OverlayThemeDefinition.All)
        foreach (var rowCount in new[] { 0, 1, 3 })
        foreach (var scale in new[] { .8, 1, 1.5, 2.5 })
        {
            var snapshot = Example(rowCount);
            using var bitmap = CraftingProgressOverlayRenderer.Render(snapshot, theme, scale, language, animationsEnabled: false);
            Assert.Equal((int)Math.Round(360 * scale), bitmap.Width);
            Assert.Equal((int)Math.Round(CraftingProgressOverlayRenderer.HeightFor(rowCount) * scale), bitmap.Height);
            Assert.Equal(PixelFormat.Format32bppPArgb, bitmap.PixelFormat);
            Assert.True(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A > 0);
        }
        Assert.Equal(224, CraftingProgressOverlayRenderer.HeightFor(1));
        Assert.Equal(360, CraftingProgressOverlayRenderer.HeightFor(3));
        Assert.Equal(.8, CraftingProgressOverlayRenderer.NormalizeScale(.2));
        Assert.Equal(1, CraftingProgressOverlayRenderer.NormalizeScale(double.NaN));
    }

    [Fact]
    public void PartialHistoricalOrManualStockCannotAcquireFullCoverageGold()
    {
        var row = Example(1).Rows[0] with { RequiredQuantity = 2, KnownQuantity = 2,
            InventoryFresh = true, InventoryComplete = true, IsManualStock = false };
        Assert.True(row.IsComplete);
        Assert.Equal(1, row.CoverageFraction);
        Assert.False((row with { InventoryComplete = false }).IsComplete);
        Assert.True((row with { InventoryComplete = false }).CoverageFraction < 1);
        Assert.False((row with { InventoryFresh = false }).IsComplete);
        Assert.False((row with { IsManualStock = true }).IsComplete);
        Assert.False((row with { HasOpenDemand = true }).IsComplete);
        Assert.False((row with { DemandIsFresh = false }).IsComplete);
        Assert.Null((row with { RequiredQuantity = null }).CoverageFraction);
        Assert.Null((row with { KnownQuantity = null }).CoverageFraction);
        Assert.Equal("Known minimum: 2 / ~2.0", CraftingProgressOverlayRenderer.Coverage(row with { InventoryComplete = false }, "en"));
        Assert.Equal("Bestand unbekannt", CraftingProgressOverlayRenderer.Coverage(row with { KnownQuantity = null }, "de"));
    }

    [Fact]
    public void ReducedMotionRetainsTheStateWithoutTimeDependentPaint()
    {
        var snapshot = Example(1);
        var row = snapshot.Rows[0] with { InventoryComplete = true, KnownQuantity = 2, RequiredQuantity = 2, InventoryFresh = true };
        snapshot = snapshot with { Rows = [row with { EventAgeSeconds = .1 }], IsCompletionVisible = true, CompletionAgeSeconds = .1 };
        using var first = CraftingProgressOverlayRenderer.Render(snapshot, animationsEnabled: false);
        using var second = CraftingProgressOverlayRenderer.Render(snapshot with { Rows = [row with { EventAgeSeconds = .7 }], CompletionAgeSeconds = .7 }, animationsEnabled: false);
        Assert.Equal(Hash(first), Hash(second));
        using var animated = CraftingProgressOverlayRenderer.Render(snapshot, animationsEnabled: true);
        Assert.NotEqual(Hash(first), Hash(animated));
    }

    [Fact]
    public void SourceLabelsKeepOtherSourcesUndetailedAndNeverInferCraftOrMarket()
    {
        var row = Example(1).Rows[0] with { SourceQuantities = ImmutableDictionary<CraftingProgressSource, long>.Empty
            .Add(CraftingProgressSource.Gathering, 2).Add(CraftingProgressSource.Other, 1) };
        Assert.Equal("Sonstiges +1 · Sammeln +2", CraftingProgressOverlayRenderer.Sources(row, "de"));
        Assert.DoesNotContain("Craft", CraftingProgressOverlayRenderer.Sources(row, "en"));
        Assert.DoesNotContain("Market", CraftingProgressOverlayRenderer.Sources(row, "en"));
    }

    private static CraftingProgressSnapshot Example(int rows) => new()
    {
        Pinned = new() { ProfileId = "11111111111111111111111111111111", PinId = "22222222222222222222222222222222",
            TargetItemId = 110730019, TargetQuantity = 1, PinnedAt = DateTimeOffset.UnixEpoch.AddDays(20000) },
        TargetNameDe = "Streitkolben des Sternendrachenkönigs", TargetNameEn = "Star Dragon Lord Mace",
        IsFresh = true,
        Rows = Enumerable.Range(0, rows).Select(index => new CraftingProgressRow
        {
            ItemId = 100 + index, NameDe = "Vorprodukt mit langem vollständigem Namen " + index,
            NameEn = "Intermediate product with a long complete name " + index, Quantity = 2,
            KnownQuantity = 2, RequiredQuantity = 4, InventoryFresh = true, InventoryComplete = false,
            SourceQuantities = ImmutableDictionary<CraftingProgressSource, long>.Empty.Add(CraftingProgressSource.Gathering, 2)
        }).ToImmutableArray()
    };
    private static long Hash(Bitmap bitmap)
    {
        long hash = 17;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++) hash = unchecked(hash * 31 + bitmap.GetPixel(x, y).ToArgb());
        return hash;
    }
}
