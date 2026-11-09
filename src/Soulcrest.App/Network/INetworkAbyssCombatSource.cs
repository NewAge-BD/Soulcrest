using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public enum NetworkAbyssCombatKind { Kill = 0, Death = 1, Assist = 2 }

/// <summary>
/// Independently established source capabilities, with estimates separate from server-qualified results.
/// Qualifying one kind never enables another; an unknown kind is always unsupported.
/// </summary>
public sealed record NetworkAbyssCombatCapabilities(bool KillQualified, bool DeathQualified, bool AssistQualified)
{
    public static NetworkAbyssCombatCapabilities None { get; } = new(false, false, false);
    /// <summary>Own damage participation may produce separately labelled estimates, never server credit.</summary>
    public bool EstimatedAssistSupported { get; init; }

    public bool Supports(NetworkAbyssCombatKind kind) => kind switch
    {
        NetworkAbyssCombatKind.Kill => KillQualified,
        NetworkAbyssCombatKind.Death => DeathQualified,
        NetworkAbyssCombatKind.Assist => AssistQualified,
        _ => false
    };

    public bool Supports(NetworkAbyssCombatKind kind, bool isEstimated) => isEstimated
        ? kind == NetworkAbyssCombatKind.Assist && EstimatedAssistSupported
        : Supports(kind);
}

/// <summary>
/// An own PvP result. AP changes, rank deltas and nearby deaths do not by themselves establish it.
/// Server-qualified assists need an explicit result. Separately labelled estimates may use own positive
/// damage and a subsequent other-caster PC death (user authorization 2026-10-29).
/// Opponent names and native identities are live-only data and must never
/// be persisted. Anonymous validated class and actual rank grade may be frozen in session statistics
/// (user request 2026-10-27). Optional point amounts require an explicit association with this result, never
/// independent wallet/total deltas. The grade is the native ordinal, never a computed points tier.
/// </summary>
public sealed record NetworkAbyssCombatEvent(DateTimeOffset At, long Sequence, OwnCharacterContext Character,
    NetworkAbyssCombatKind Kind, string? OpponentName = null, long? RankPointDelta = null)
{
    /// <summary>Own positive-damage participation estimate, explicitly authorized on 2026-10-29.</summary>
    public bool IsEstimated { get; init; }
    /// <summary>Explicitly attributed earned AP for this result; wallet changes alone never supply it.</summary>
    public long? AbyssPointGain { get; init; }
    public string? OpponentClassId { get; init; }
    public int? OpponentAbyssRankGrade { get; init; }
    public override string ToString() => nameof(NetworkAbyssCombatEvent);
}

/// <summary>The existing passive capture emits only independently qualified own combat results.</summary>
public interface INetworkAbyssCombatSource
{
    bool Running { get; }
    OwnCharacterContext? OwnCharacter { get; }
    NetworkAbyssCombatCapabilities CombatCapabilities => NetworkAbyssCombatCapabilities.None;
    event Action<NetworkAbyssCombatEvent>? CombatEvent;
    event Action? OwnCharacterChanged;
    event Action? Changed;
}
