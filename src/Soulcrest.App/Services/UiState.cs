namespace Soulcrest.App.Services;

/// <summary>Navigation shared between views (e.g. "show this pet on the map").</summary>
public sealed class UiState
{
    public string Tab { get; private set; } = "map";
    public string? OverlaySection { get; private set; }
    public string OverlayEditorSection { get; private set; } = "layout";
    public string CombatScoreSection { get; private set; } = "stat-overview";

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

    /// <summary>Opens an editor category without changing an overlay or its enabled state.</summary>
    public void ShowOverlayEditorSection(string section)
    {
        OverlayEditorSection = section;
        Navigate("overlay-editor");
    }

    /// <summary>Opens a calculator section in RAM; saved routes and preferences remain unchanged.</summary>
    public void ShowCombatScoreSection(string section)
    {
        CombatScoreSection = section is "stat-overview" or "daevanion" or "wings" or "gear" or "pets"
            or "genus-insight" or "pantheon" or "achievements" or "titles" ? section : "stat-overview";
        Tab = "combat-score";
        Changed?.Invoke();
    }

    public string? PendingPetFocus { get; private set; }

    private string? _pendingPetCategory;

    /// <summary>Opens one of the four pet-effect categories using RAM navigation only.</summary>
    public void ShowPetCategory(string genus)
    {
        var category = genus?.Trim().ToLowerInvariant();
        if (category is not ("cogni" or "fera" or "natura" or "varian")) return;
        _pendingPetCategory = category;
        Navigate("pets");
    }

    /// <summary>The destination consumes an explicit category request once, including when already open.</summary>
    public string? TakePendingPetCategory()
    {
        var category = _pendingPetCategory;
        _pendingPetCategory = null;
        return category;
    }

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
        // User request 2026-11-05: preserve old Daevanion links inside the combined calculator.
        if (tab is "daevanion-calculator" or "combat-score")
        {
            ShowCombatScoreSection(tab == "daevanion-calculator" ? "daevanion" : "stat-overview");
            return;
        }
        // Keep existing settings links working after their former top-level tab was removed.
        if (tab == "overlay")
        {
            OverlayEditorSection = OverlaySection is "farm-loot" or "abyss-rank" ? "templates" : "static";
            tab = "overlay-editor";
        }
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
