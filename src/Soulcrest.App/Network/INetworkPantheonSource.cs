using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public interface INetworkPantheonSource
{
    bool Running { get; }
    NetworkPantheonSnapshot PantheonSnapshot { get; }
    event Action? Changed;
}

/// <summary>Transient own evidence. Identity is never a persisted decoration or preset choice.</summary>
public sealed record NetworkPantheonSnapshot
{
    public OwnCharacterContext? Character { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public NetworkCombatDomain<CharacterPantheonState> Decorations { get; init; } = NetworkCombatDomain<CharacterPantheonState>.Unknown;
    public static NetworkPantheonSnapshot Empty { get; } = new();
    public override string ToString() => $"NetworkPantheonSnapshot {{ Generation = {Generation}, Sequence = {Sequence} }}";
}
