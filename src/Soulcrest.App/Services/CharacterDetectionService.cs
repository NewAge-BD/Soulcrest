using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Chooses the character profile by itself after every loading screen (user request 2026-10-06): the
/// network source reports the own character's name, the exploration profiles follow. Works while the
/// network source runs (loot tracking).
/// </summary>
public sealed class CharacterDetectionService : IDisposable
{
    private readonly NetworkLootService _network;
    public CharacterDetectionService(NetworkLootService network)
    {
        _network = network;
        _network.OwnCharacterEntered += OnCharacterEntered;
    }

    /// <summary>What the last detection did, for the profile picker ("Name erkannt · neues Profil").</summary>
    public string? Status { get; private set; }

    public event Action? Changed;

    private void OnCharacterEntered(OwnCharacterIdentity identity)
    {
        // The source already bound the stable server/DB identity. A second name-only selection
        // could choose the wrong legacy profile when different servers use the same name.
        Status = UiText.F("{0} erkannt · Profil gewählt", identity.Name);
        Changed?.Invoke();
    }

    public void Dispose() => _network.OwnCharacterEntered -= OnCharacterEntered;
}
