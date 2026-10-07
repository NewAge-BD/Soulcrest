namespace Soulcrest.App.Services;

/// <summary>Navigation shared between views (e.g. "show this pet on the map").</summary>
public sealed class UiState
{
    public string Tab { get; private set; } = "map";
    public string? PendingPetFocus { get; private set; }

    public event Action? Changed;

    public void Navigate(string tab)
    {
        Tab = tab;
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
