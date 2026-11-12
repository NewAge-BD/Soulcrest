using System.Collections.Immutable;

namespace Soulcrest.App.Services;

public sealed record ResourceMapSelection(bool Worldmap, bool Minimap, bool Soulcrest)
{
    public bool Any => Worldmap || Minimap || Soulcrest;
    public bool Includes(MapSurface surface) => surface switch { MapSurface.Worldmap => Worldmap, MapSurface.Minimap => Minimap, _ => Soulcrest };
    public static ResourceMapSelection Default { get; } = new(true, true, true);
}
public sealed record ResourceMapDialogRequest(int? ItemId, string ItemName, ResourceMapSelection Selection,
    ImmutableArray<MapSurface> Mixed, bool DontShowAgain, bool EditingDefaults);
public sealed record ResourceMapFocus(int ItemId, string MapId, ImmutableArray<string> SourceMaps, ImmutableArray<ResourceMapLocation> Locations);

/// <summary>One saved legend state for the resource dialog and the three map surfaces.</summary>
public sealed class ResourceMapService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ResourceMapSources _sources;
    private readonly UiState _ui;
    private readonly Func<string?> _playerMap;
    public ResourceMapDialogRequest? Pending { get; private set; }
    public string? Feedback { get; private set; }
    public ResourceMapService(SettingsService settings, ResourceMapSources sources, UiState ui, MapTrackingService tracking)
        : this(settings, sources, ui, () => ExplorationOverview.FreshPosition(ExplorationOverview.Capture(tracking), DateTimeOffset.UtcNow)?.MapId) { }
    internal ResourceMapService(SettingsService settings, ResourceMapSources sources, UiState ui, Func<string?> playerMap)
    { _settings = settings; _sources = sources; _ui = ui; _playerMap = playerMap; _settings.Changed += SettingsChanged; }
    public event Action? Changed;
    public bool CanShow(int itemId) => _sources.HasItem(itemId);

    public void Open(int itemId, string itemName)
    {
        Feedback = null;
        var locations = _sources.ForItem(itemId);
        if (locations.IsEmpty) { Feedback = "Keine bestätigten Fundorte für diese Ressource verfügbar."; Changed?.Invoke(); return; }
        if (_settings.Current.ResourceMapDontShowAgain) { Apply(itemId, _settings.Current.ResourceMapDefaultSelection); return; }
        var states = Enum.GetValues<MapSurface>().ToDictionary(surface => surface, surface => locations.Select(l =>
            MapLayerVisibility.KindVisible(_settings.Current, l.MapId, surface, l.Category.Split('/')[0], l.Category[(l.Category.IndexOf('/') + 1)..], l.Kind)).Distinct().ToArray());
        bool On(MapSurface surface) => states[surface].Any(on => on);
        Pending = new(itemId, itemName, new(On(MapSurface.Worldmap), On(MapSurface.Minimap), On(MapSurface.Interactive)),
            states.Where(p => p.Value.Length > 1).Select(p => p.Key).ToImmutableArray(), false, false);
        Changed?.Invoke();
    }
    public void EditDefaults()
    { Pending = new(null, "", _settings.Current.ResourceMapDefaultSelection, [], _settings.Current.ResourceMapDontShowAgain, true); Changed?.Invoke(); }
    public void Cancel() { Pending = null; Changed?.Invoke(); }
    public void Confirm(ResourceMapSelection selection, bool dontShowAgain)
    {
        if (!selection.Any || Pending is not { } request) return;
        Pending = null;
        if (request.EditingDefaults || dontShowAgain)
            _settings.Update(s => { s.ResourceMapDefaultSelection = selection; s.ResourceMapDontShowAgain = dontShowAgain; });
        if (request.ItemId is { } item) Apply(item, selection); else Changed?.Invoke();
    }
    private void Apply(int itemId, ResourceMapSelection selection)
    {
        if (!selection.Any) return;
        var locations = _sources.ForItem(itemId);
        if (locations.IsEmpty) return;
        _settings.Update(s => {
            foreach (var group in locations.GroupBy(l => l.MapId))
                foreach (var surface in Enum.GetValues<MapSurface>())
                    foreach (var layer in group.Select(l => (l.Category, l.Kind)).Distinct())
                        MapLayerVisibility.SetKind(s, group.Key, surface, _sources.LayersFor(group.Key), layer.Category, layer.Kind, selection.Includes(surface));
            if (locations.Any(l => l.Category.StartsWith("Monster Drops/", StringComparison.Ordinal)))
                s.TargetedResourceMapItems = [.. s.TargetedResourceMapItems.Append(itemId).Distinct()];
        });
        Feedback = "Ressourcen-Kartenanzeige aktualisiert.";
        if (selection.Soulcrest)
        {
            var maps = locations.Select(l => l.MapId).Distinct(StringComparer.Ordinal).ToImmutableArray();
            var preferred = _playerMap();
            var map = preferred is not null && maps.Contains(preferred) ? preferred : maps.Contains(_settings.Current.LastMap) ? _settings.Current.LastMap : maps[0];
            _ui.ShowResourceOnMap(new(itemId, map, maps, locations.Where(l => l.MapId == map).ToImmutableArray()));
        }
        Changed?.Invoke();
    }
    private void SettingsChanged() => Changed?.Invoke();
    public void Dispose() => _settings.Changed -= SettingsChanged;
}
