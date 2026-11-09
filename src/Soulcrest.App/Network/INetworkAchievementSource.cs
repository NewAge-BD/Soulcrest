using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public enum NetworkAchievementObservationKind
{
    Baseline, Update, Reward, Invalidated, RecoveryBaseline
}

/// <summary>
/// Ordered, immutable own evidence before UI coalescing. Character contains transient native binding
/// data and must not be persisted. Recovery baselines never imply live milestone transitions.
/// </summary>
public sealed record NetworkAchievementObservation(long Sequence, long Generation, DateTimeOffset ObservedAt,
    OwnCharacterContext? Character, NetworkCombatDomain<CharacterAchievementState> Domain,
    NetworkAchievementObservationKind Kind)
{
    public Guid SourceInstanceId { get; init; }
    public bool HistoryGap { get; init; }

    public override string ToString() => $"Achievement observation {Sequence}, {Kind}, {Domain.Status}";
}

/// <summary>Fixed anonymous ingress counters; no payload, identity, address or transport keys.</summary>
public sealed record AchievementIngressDiagnostics
{
    public long Entry3620Framed { get; init; }
    public long Entry3620Parsed { get; init; }
    public long Own3621Framed { get; init; }
    public long Own3621Parsed { get; init; }
    public long Own3633Framed { get; init; }
    public long Own3633Parsed { get; init; }
    public long OwnBindings { get; init; }
    public long Lists5683Framed { get; init; }
    public long Updates5684Framed { get; init; }
    public long Rewards5686Framed { get; init; }
    public long AcceptedBaselines { get; init; }
    public long AcceptedUpdates { get; init; }
    public long AcceptedRewards { get; init; }
    public long Invalidations { get; init; }
    public long QueueOverflows { get; init; }
    public long SubscriberFailures { get; init; }
    public int PendingNotifications { get; init; }
    public int FramingResyncs { get; init; }
    public bool CharacterBound { get; init; }
    public bool SourceFresh { get; init; }
    public NetworkCombatSourceStatus SourceStatus { get; init; }
}

public interface INetworkAchievementSource
{
    NetworkAchievementObservation AchievementSnapshot { get; }
    event Action<NetworkAchievementObservation>? AchievementObserved;
    AchievementIngressDiagnostics AchievementDiagnostics { get; }
}
