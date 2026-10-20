namespace Soulcrest.App.Services;

/// <summary>Navigation shared between views (e.g. "show this pet on the map").</summary>
public sealed class UiState
{
    public string Tab { get; private set; } = "map";
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
