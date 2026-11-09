using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

// The kit's selection/geometry calculation, with Soulcrest item keys and no BDO categories.
public sealed record OverlayLootView(string Label, string Detail,
    IReadOnlyList<OverlayLootItem> Items, IReadOnlyList<OverlayLootItem> VisibleItems,
    int HiddenCount, int Columns, double CellWidth, double CellHeight,
    double HeaderHeight, double FooterHeight, double ItemSize, double FontScale, double Gap);

public static class OverlayLootPresentation
{
    public const double PaddingX = 10, PaddingY = 8, Gap = 4;
    public static OverlayLootView Create(OverlayWidget widget, OverlaySnapshot snapshot)
    {
        IEnumerable<OverlayLootItem> selection = snapshot.Drops;
        if (widget.ItemFilter == "selected")
        {
            var actual = snapshot.Drops.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            var catalog = snapshot.ItemCatalog.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            var emptyQuantity = snapshot.SessionActive && snapshot.TrackingAvailable ? "0" : "—";
            selection = widget.ItemKeys.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).Select(key =>
                actual.GetValueOrDefault(key) ?? (catalog.GetValueOrDefault(key) is { } item
                    ? item with { Quantity = 0, QuantityText = emptyQuantity } : new OverlayLootItem(key, key, emptyQuantity)));
        }
        else if (widget.ItemFilter == "rare") selection = selection.Where(item => item.IsRare);
        // Group only the filtered list projection; exact drops remain available to the other views and item selection.
        if (widget.ItemView == "list") selection = OverlayEquipmentDropGroups.Group(selection, snapshot.UiLanguage);
        selection = widget.ItemSort switch
        {
            "quantity" => selection.OrderByDescending(item => item.Quantity).ThenBy(item => item.Key, StringComparer.Ordinal),
            "name" => selection.OrderBy(item => item.Name, StringComparer.Create(CultureInfo.GetCultureInfo(snapshot.UiLanguage == "de" ? "de-DE" : "en-US"), true)),
            _ when widget.ItemView == "list" && widget.ItemFilter != "selected" =>
                LootTrackerPresentation.OrderDrops(selection, item => item.Rarity, item => item.Quantity, item => item.Name, item => item.Key),
            _ => selection,
        };
        var items = Array.AsReadOnly(selection.ToArray());
        var view = widget.ItemView;
        var count = view == "card" ? Math.Min(items.Count, 1) : widget.ItemFilter == "all"
            ? items.Count : Math.Min(items.Count, Math.Clamp(widget.ItemLimit, 1, 24));
        var visible = Array.AsReadOnly(items.Take(count).ToArray());
        var hiddenCount = items.Count - count;
        var width = Math.Max(0, widget.Width - PaddingX * 2);
        var innerHeight = Math.Max(0, widget.Height - PaddingY * 2);
        var itemSize = widget.ItemSize;
        var fontScale = widget.FontScale;
        var header = widget.ShowLabel ? 18 * fontScale : 0;
        var footer = hiddenCount > 0 ? 18 * fontScale : 0;
        var cellHeight = view switch
        {
            "list" => Math.Max(24, Math.Max(itemSize * .55, 20 * fontScale)),
            "card" => Math.Max(widget.ShowIcon ? itemSize : 0, 62 * fontScale),
            _ => itemSize,
        };
        var preferredWidth = view switch
        {
            "list" => (widget.ShowIcon ? itemSize * .55 + 6 : 0) + 80 * fontScale,
            "card" => (widget.ShowIcon ? itemSize + 12 : 0) + 64 * fontScale,
            _ => itemSize,
        };
        // User request 2026-11-06: new drops must never shrink the existing list rows.
        // Only explicit module geometry can scale them; overflow gets a counted footer.
        if (view == "list")
        {
            var listScale = Math.Min(1, width / preferredWidth);
            var rowHeight = cellHeight * listScale;
            var rowGap = Gap * listScale;
            var listHeader = header * listScale;
            int Capacity(double reservedFooter) => rowHeight <= 0 ? 0 : Math.Max(0,
                (int)Math.Floor((innerHeight - listHeader - reservedFooter + rowGap + .000001) / (rowHeight + rowGap)));
            var listCount = Math.Min(count, Capacity(0));
            var listFooter = items.Count > listCount ? 18 * fontScale * listScale : 0;
            if (listFooter > 0) listCount = Math.Min(listCount, Capacity(listFooter));
            return new(widget.ItemFilter == "rare" ? "Seltene Drops · laufende Session" : "Drops · laufende Session",
                "Gesammelte Mengen der laufenden Session.", items, Array.AsReadOnly(items.Take(listCount).ToArray()),
                items.Count - listCount, 1, width, rowHeight, listHeader, listFooter,
                itemSize * listScale, fontScale * listScale, rowGap);
        }
        var layoutCount = Math.Max(1, count);
        var columns = view switch
        {
            "list" or "card" => 1,
            "strip" => layoutCount,
            _ => Math.Clamp((int)Math.Floor((width + Gap) / (itemSize + Gap)), 1, layoutCount),
        };
        double Fit(int candidateColumns)
        {
            var rows = (layoutCount + candidateColumns - 1) / candidateColumns;
            return Math.Min(1, Math.Min(width / (candidateColumns * preferredWidth + (candidateColumns - 1) * Gap),
                innerHeight / (header + footer + rows * cellHeight + (rows - 1) * Gap)));
        }
        var scale = Fit(columns);
        if (view is not ("list" or "card" or "strip") && scale < 1)
            for (var candidate = 1; candidate <= layoutCount; candidate++)
            {
                var candidateScale = Fit(candidate);
                if (candidateScale <= scale) continue;
                columns = candidate;
                scale = candidateScale;
            }
        header *= scale;
        footer *= scale;
        var cellWidth = view is "list" or "card" ? width : itemSize * scale;
        cellHeight = view == "card" ? Math.Max(0, innerHeight - header - footer) : cellHeight * scale;
        return new(widget.ItemFilter == "rare" ? "Seltene Drops · laufende Session" : "Drops · laufende Session",
            "Gesammelte Mengen der laufenden Session.", items, visible, hiddenCount, columns, cellWidth, cellHeight,
            header, footer, itemSize * scale, fontScale * scale, Gap * scale);
    }
}
