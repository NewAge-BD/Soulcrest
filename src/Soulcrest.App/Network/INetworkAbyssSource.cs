using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>Qualified own Abyss facts from the existing passive capture. Missing facts remain unknown.</summary>
public interface INetworkAbyssSource
{
    bool Running { get; }
    OwnCharacterContext? OwnCharacter { get; }
    NetworkAbyssSnapshot AbyssSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
    event Action<NetworkAbyssPointGain>? AbyssPointsGained;
    event Action<NetworkAbyssRankChange>? RankPointsChanged;
}

/// <summary>
/// Wallet, cumulative earned AP and rank points are independent absolute values. A baseline is
/// never a gain. Sequence orders absolute observations within the own character generation.
/// </summary>
public sealed record NetworkAbyssSnapshot(OwnCharacterContext? Character, long? AbyssPointBalance,
    long? FarmedAbyssPoints, long? RankPoints, DateTimeOffset? At, long Sequence)
{
    /// <summary>Per-fact evidence times; absent metadata falls back to At for existing sources.</summary>
    public DateTimeOffset? AbyssPointBalanceObservedAt { get; init; }
    public DateTimeOffset? FarmedAbyssPointsObservedAt { get; init; }
    public DateTimeOffset? RankPointsObservedAt { get; init; }
    /// <summary>Own observation time of the independently qualified Abyss combat-Kina comparison.</summary>
    public DateTimeOffset? KinaGainsObservedAt { get; init; }
    public bool FormatRejected { get; init; }
    public string Status { get; init; } = "Abyss-Punktequelle ist noch nicht qualifiziert.";
    /// <summary>Explicitly qualified gain semantics, independently of a readable wallet baseline.</summary>
    public bool AbyssPointGainsQualified { get; init; }
    public bool RankPointChangesQualified { get; init; }
    /// <summary>The wallet combat reason must be independently qualified for the Abyss context.</summary>
    public bool KinaGainsQualified { get; init; }
    public static NetworkAbyssSnapshot Empty { get; } = new(null, null, null, null, null, 0);
}

/// <summary>Positive canonical earned AP, already deduplicated and bound to the current own context.</summary>
public sealed record NetworkAbyssPointGain(DateTimeOffset At, long Sequence, OwnCharacterContext Character, long Amount);

/// <summary>Signed difference between successive qualified own totals; cause remains unknown. Baselines never emit a change.</summary>
public sealed record NetworkAbyssRankChange(DateTimeOffset At, long Sequence, OwnCharacterContext Character, long Delta);
