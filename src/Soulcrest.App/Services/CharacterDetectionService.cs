using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Chooses the character profile by itself after every loading screen (user request 2026-10-06): the
/// network source reports the own character's name, the exploration profiles follow. Works while the
/// network source runs (loot tracking).
/// </summary>
public sealed class CharacterDetectionService : IDisposable
{
    private readonly NetworkLootService _network;
    private readonly ExplorationService _exploration;

    public CharacterDetectionService(NetworkLootService network, ExplorationService exploration)
    {
        _network = network;
        _exploration = exploration;
        _network.CharacterEntered += OnCharacterEntered;
    }

    /// <summary>What the last detection did, for the profile picker ("Name erkannt · neues Profil").</summary>
    public string? Status { get; private set; }

    public event Action? Changed;

    private void OnCharacterEntered(string name)
    {
        var choice = _exploration.UseCharacter(name);
        Status = choice switch
        {
            CharacterChoice.Selected => UiText.F("{0} erkannt · Profil gewählt", name),
            CharacterChoice.Adopted => UiText.F("{0} erkannt · Standardprofil übernommen", name),
            CharacterChoice.Created => UiText.F("{0} erkannt · neues Profil angelegt", name),
            _ => UiText.F("{0} erkannt", name),
        };
        Changed?.Invoke();
    }

    public void Dispose() => _network.CharacterEntered -= OnCharacterEntered;
}
