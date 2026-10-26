using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>The common passive capture's explicit own wallet balances and qualified combat income.</summary>
public interface INetworkKinaSource
{
    bool Running { get; }
    OwnCharacterContext? OwnCharacter { get; }
    NetworkKinaSnapshot KinaSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
    event Action<NetworkKinaGain>? KinaGained;
}

/// <summary>Missing explicit records are unknown. Balances and login lists never create income.</summary>
public sealed record NetworkKinaSnapshot(long? UnboundBalance, long? BoundBalance,
    DateTimeOffset? BaselineAt, bool FormatRejected, string Status)
{
    public bool IsReady => UnboundBalance is not null;
}

/// <summary>Only the currently qualified unbound combat gain, without native wallet identifiers.</summary>
public sealed record NetworkKinaGain(DateTimeOffset At, long Sequence,
    OwnCharacterContext Character, long UnboundKina)
{
    public bool InInstance { get; init; }
}
