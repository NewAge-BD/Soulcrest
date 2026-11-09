using Soulcrest.App.OverlayEditorKit;

namespace Soulcrest.App.Components.OverlayKit;

public partial class OriginalKitOverlayEditor
{
    private IReadOnlyList<OverlayLootItem>? _filteredCatalog;
    private IReadOnlyList<string>? _filteredSelection;
    private string? _filteredSearch, _filteredLanguage;
    private IList<OverlayLootItem> _filteredItems = Array.Empty<OverlayLootItem>();
    private IReadOnlyList<OverlayLootItem> ItemCatalog => PreviewSnapshot.ItemCatalog;
    private OverlayLootItem CatalogItem(string key) => ItemCatalog.FirstOrDefault(item =>
        string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) ?? new(key, key, "0");

    private IList<OverlayLootItem> AvailableItems(OverlayWidget widget)
    {
        var search = _itemSearch.Trim();
        var catalog = ItemCatalog;
        if (ReferenceEquals(_filteredCatalog, catalog) && ReferenceEquals(_filteredSelection, widget.ItemKeys)
            && _filteredSearch == search && _filteredLanguage == UiLanguage) return _filteredItems;
        _filteredCatalog = catalog;
        _filteredSelection = widget.ItemKeys;
        _filteredSearch = search;
        _filteredLanguage = UiLanguage;
        _filteredItems = catalog.Where(item => !widget.ItemKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)
            && (search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Key.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Name, StringComparer.Create(UiCulture, true)).ToList();
        return _filteredItems;
    }
    private Task AddItem(string key) => ChangeWidget(widget => widget.ItemKeys.Count >= 24 ? widget : widget with
    {
        ItemFilter = "selected",
        ItemKeys = widget.ItemKeys.Append(key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
    });
    private Task RemoveItem(string key) => ChangeWidget(widget => widget with
    {
        ItemKeys = widget.ItemKeys.Where(value => !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)).ToArray(),
    });
    private Task MoveItem(string key, int direction) => ChangeWidget(widget =>
    {
        var keys = widget.ItemKeys.ToList();
        var index = keys.FindIndex(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= keys.Count) return widget;
        (keys[index], keys[target]) = (keys[target], keys[index]);
        return widget with { ItemKeys = keys.ToArray(), ItemSort = "default" };
    });
}
