using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayLootListLayoutTests
{
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, .7)]
    [InlineData(false, false, 1.25)]
    public void AdditionalDropsKeepTheSameFontIconsAndRowGeometry(bool label, bool icons, double font)
    {
        var widget = OverlayCatalog.CreateWidget("drop-list") with { ShowLabel = label, ShowIcon = icons, FontScale = font };
        var baseline = OverlayLootPresentation.Create(widget, Snapshot(1));
        foreach (var count in new[] { 3, 10, 50, 200 })
        {
            var result = OverlayLootPresentation.Create(widget, Snapshot(count));
            Assert.Equal(baseline.FontScale, result.FontScale);
            Assert.Equal(baseline.ItemSize, result.ItemSize);
            Assert.Equal(baseline.CellHeight, result.CellHeight);
            Assert.Equal(baseline.Gap, result.Gap);
            Assert.Equal(baseline.HeaderHeight, result.HeaderHeight);
            Assert.Equal(count, result.VisibleItems.Count + result.HiddenCount);
            Assert.Equal(count, result.Items.Count);
            AssertFits(widget, result);
        }
    }

    [Fact]
    public void TallerListShowsMoreItemsWithoutChangingTheirTextOrIcons()
    {
        var widget = OverlayCatalog.CreateWidget("drop-list");
        var snapshot = Snapshot(100);
        var shortList = OverlayLootPresentation.Create(widget, snapshot);
        var tallList = OverlayLootPresentation.Create(widget with { Height = 448 }, snapshot);
        Assert.True(tallList.VisibleItems.Count > shortList.VisibleItems.Count);
        Assert.Equal(shortList.FontScale, tallList.FontScale);
        Assert.Equal(shortList.ItemSize, tallList.ItemSize);
        Assert.Equal(shortList.CellHeight, tallList.CellHeight);
        Assert.True(shortList.HiddenCount > tallList.HiddenCount);
    }

    [Theory]
    [InlineData(104, 2)]
    [InlineData(103.99, 1)]
    public void OverflowReservesTheFooterAndOnlyCompleteRows(double height, int visible)
    {
        var widget = OverlayCatalog.CreateWidget("drop-list") with { ItemSize = 40, Height = height };
        var view = OverlayLootPresentation.Create(widget, Snapshot(3));
        Assert.Equal(visible, view.VisibleItems.Count);
        Assert.Equal(3 - visible, view.HiddenCount);
        Assert.Equal(1, view.FontScale);
        Assert.Equal(24, view.CellHeight);
        Assert.Equal(18, view.FooterHeight);
        AssertFits(widget, view);
    }

    [Fact]
    public void AListWithNoRoomForOneRowShowsTheHiddenCountWithoutShrinking()
    {
        var widget = OverlayCatalog.CreateWidget("drop-list") with { Height = 64 };
        var view = OverlayLootPresentation.Create(widget, Snapshot(200));
        Assert.Empty(view.VisibleItems);
        Assert.Equal(200, view.HiddenCount);
        Assert.Equal(1, view.FontScale);
        Assert.Equal(widget.ItemSize, view.ItemSize);
        AssertFits(widget, view);
    }

    [Fact]
    public void AllItemsIgnoreTheOptionalSelectionLimitButStillRespectPhysicalCapacity()
    {
        var widget = OverlayCatalog.CreateWidget("drop-list") with { ItemLimit = 1 };
        var view = OverlayLootPresentation.Create(widget, Snapshot(100));
        Assert.Equal(5, view.VisibleItems.Count);
        Assert.Equal(95, view.HiddenCount);
        Assert.Equal(100, view.Items.Count);
    }

    [Theory]
    [InlineData("rare")]
    [InlineData("selected")]
    public void FilteringSortingAndExplicitLimitsRemainSeparateFromThePhysicalCapacity(string filter)
    {
        var snapshot = Snapshot(20);
        var widget = OverlayCatalog.CreateWidget("drop-list") with
        {
            ItemFilter = filter, ItemSort = "quantity", ItemLimit = 2,
            ItemKeys = snapshot.Drops.Take(10).Select(item => item.Key).ToArray(),
        };
        var view = OverlayLootPresentation.Create(widget, snapshot);
        Assert.Equal(2, view.VisibleItems.Count);
        Assert.Equal(10, view.Items.Count);
        Assert.Equal(8, view.HiddenCount);
        Assert.Equal(view.Items.Take(2), view.VisibleItems);
        Assert.True(view.VisibleItems[0].Quantity > view.VisibleItems[1].Quantity);
        Assert.Equal(1, view.FontScale);
    }

    [Fact]
    public void AnEmptyListHasNeitherRowsNorAnOverflowFooter()
    {
        var view = OverlayLootPresentation.Create(OverlayCatalog.CreateWidget("drop-list"), Snapshot(0));
        Assert.Empty(view.VisibleItems);
        Assert.Equal(0, view.HiddenCount);
        Assert.Equal(0, view.FooterHeight);
    }

    private static void AssertFits(OverlayWidget widget, OverlayLootView view)
    {
        var count = view.VisibleItems.Count;
        var height = view.HeaderHeight + view.FooterHeight + count * view.CellHeight + Math.Max(0, count - 1) * view.Gap;
        Assert.InRange(height, 0, widget.Height - 2 * OverlayLootPresentation.PaddingY + .000001);
    }
    private static OverlaySnapshot Snapshot(int count) => new()
    {
        SessionActive = true, TrackingAvailable = true,
        Drops = Enumerable.Range(1, count).Select(i => new OverlayLootItem("sample:" + i, "Example item " + i,
            i.ToString(System.Globalization.CultureInfo.InvariantCulture), IsRare: i % 2 == 0, Quantity: i)).ToArray(),
    };
}
