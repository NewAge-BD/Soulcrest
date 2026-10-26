namespace Soulcrest.App.Services;

/// <summary>Navigation shared between views (e.g. "show this pet on the map").</summary>
public sealed class UiState
{
    public string Tab { get; private set; } = "map";
    public string? OverlaySection { get; private set; }

    private bool _exclusivePetFilterEnabled, _exclusivePetFilterNoticeDismissed;
    public bool ExclusivePetFilterNoticeVisible => _exclusivePetFilterEnabled && !_exclusivePetFilterNoticeDismissed;

    /// <summary>User request 2026-10-24: dismissal lasts across map/tab changes, until reactivation or restart.</summary>
    public bool ObserveExclusivePetFilter(bool enabled)
    {
        var wasVisible = ExclusivePetFilterNoticeVisible;
        if (_exclusivePetFilterEnabled != enabled)
        {
            _exclusivePetFilterEnabled = enabled;
            _exclusivePetFilterNoticeDismissed = false;
        }
        return wasVisible != ExclusivePetFilterNoticeVisible;
    }

    public void DismissExclusivePetFilterNotice() => _exclusivePetFilterNoticeDismissed = true;

    /// <summary>Opens the central overlay settings without changing their enabled state.</summary>
    public void ShowOverlay(string? id = null)
    {
        OverlaySection = id;
        Navigate("overlay");
    }

    public string? PendingPetFocus { get; private set; }

    private MapTarget? _pendingBoss;
    public void ShowBossOnMap(MapTarget target) { _pendingBoss = target; Navigate("map"); }
    public MapTarget? TakePendingBossFocus() { var target = _pendingBoss; _pendingBoss = null; return target; }

    private ExplorationPlace? _pendingExploration;

    /// <summary>Focuses a catalogue place without enabling tracking, changing a boss mode or saving progress.</summary>
    public void ShowExplorationOnMap(ExplorationPlace place)
    {
        _pendingExploration = place;
        Navigate("map");
    }

    public ExplorationPlace? TakePendingExplorationFocus()
    {
        var place = _pendingExploration;
        _pendingExploration = null;
        return place;
    }

    private LootTrackerMapFocus? _pendingLootRoute;

    /// <summary>Opens an ephemeral session route without replacing targets or saving a map route.</summary>
    public bool ShowLootRouteOnMap(LootTrackerSession session, string? mapId = null)
    {
        var focus = LootTrackerMapFocus.Create(session, mapId);
        if (focus is null) return false;
        _pendingLootRoute = focus;
        Navigate("map");
        return true;
    }

    public LootTrackerMapFocus? TakePendingLootRouteFocus()
    {
        var route = _pendingLootRoute;
        _pendingLootRoute = null;
        return route;
    }



    public event Action? Changed;

    public void Navigate(string tab)
    {
        Tab = tab == "petscan" ? "pets" : tab;
        Changed?.Invoke();
    }

    public void ShowPetOnMap(string petId)
    {
        PendingPetFocus = petId;
        Navigate("map");
    }

    /// <summary>The first-start tutorial over everything (minimap and quest log).</summary>
    public bool TutorialOpen { get; private set; }

    public void OpenTutorial()
    {
        TutorialOpen = true;
        Changed?.Invoke();
    }

    public void CloseTutorial()
    {
        TutorialOpen = false;
        Changed?.Invoke();
    }

    public string? TakePendingPetFocus()
    {
        var pending = PendingPetFocus;
        PendingPetFocus = null;
        return pending;
    }
}
