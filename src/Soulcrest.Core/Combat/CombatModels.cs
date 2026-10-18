using System.Text.Json.Serialization;

namespace Soulcrest.Core.Combat;

/// <summary>Local primary records. No network addresses or raw protocol payloads belong in this schema.</summary>
public sealed record CombatRunDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; }
    public string DungeonKey { get; init; } = "";
    public string? InstanceKey { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public bool Favorite { get; init; }
    public CombatAttemptDocument[] Attempts { get; init; } = [];
}

public sealed record CombatAttemptDocument
{
    public Guid Id { get; init; }
    public string BossKey { get; init; } = "";
    public string? EncounterVariantKey { get; init; }
    public CombatEntityReference[] BossTargets { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; }
    public long? DurationTicks { get; init; }
    public CombatEndReason? EndReason { get; init; }
    public CombatAttemptKind Kind { get; init; }
    public CombatCapabilities Capabilities { get; init; }
    public CombatMeasurementProfile MeasurementProfile { get; init; } = new("boss-damage-v1");
    /// <summary>A storage read detected missing journal data; this must propagate to every later projection.</summary>
    public bool HasIncompleteJournal { get; init; }
    /// <summary>Confirmed roster at pull. Subsequent snapshots are events; former members remain in history.</summary>
    public CombatParticipant[] Participants { get; init; } = [];
    public CombatEventEnvelope[] Events { get; init; } = [];
}

public enum CombatEndReason { Victory, Wipe, Reset, ManualStop, Interrupted }
public enum CombatAttemptKind { Boss, TrainingDummy }
public sealed record CombatMeasurementProfile(string RulesetVersion, string? GameBuild = null,
    string? DecoderVersion = null, string? CatalogVersion = null);

[Flags]
public enum CombatCapabilities { None = 0, Damage = 1, SkillCasts = 2, Healing = 4, Deaths = 8, Buffs = 16 }

/// <summary>ParticipantId is a scoped stable character identity, never a roster rank or an entity key.</summary>
public sealed record CombatParticipant(string ParticipantId, string? Name = null, string? ClassKey = null,
    long? CombatPower = null, bool IsSelf = false);

/// <summary>An opaque local stream token, not an IP or endpoint; generation invalidates transient entity bindings.</summary>
public sealed record CombatSourceContext(string StreamId, long Generation);
public sealed record CombatEntityReference(string EntityId, long Generation, string StreamId);

public sealed record CombatEventEnvelope(long Sequence, long OffsetTicks, DateTimeOffset CapturedAtUtc,
    CombatSourceContext Source, CombatEvent Event)
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>False for lifecycle boundaries whose UTC is only a monotone projection of an earlier anchor.</summary>
    public bool IsCaptureTimestampObserved { get; init; } = true;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CombatRosterEvent), "roster")]
[JsonDerivedType(typeof(CombatDamageEvent), "damage")]
[JsonDerivedType(typeof(CombatCastEvent), "cast")]
[JsonDerivedType(typeof(CombatHealEvent), "heal")]
[JsonDerivedType(typeof(CombatBuffEvent), "buff")]
[JsonDerivedType(typeof(CombatHpEvent), "hp")]
[JsonDerivedType(typeof(CombatDeathEvent), "death")]
[JsonDerivedType(typeof(CombatGapEvent), "gap")]
[JsonDerivedType(typeof(CombatUnclassifiedAmountEvent), "unclassified-amount")]
public abstract record CombatEvent;

public sealed record CombatRosterEvent(CombatParticipant[] Members) : CombatEvent;
/// <summary>A supplied owner is already confirmed by the adapter; the projector never guesses summon ownership.</summary>
public sealed record CombatDamageEvent(string? SourceParticipantId, CombatEntityReference Target, string SkillId,
    long Amount, CombatHitFlags? Flags = null, bool IsPeriodic = false,
    string? ConfirmedOwnerParticipantId = null) : CombatEvent;
public sealed record CombatCastEvent(string SourceParticipantId, string SkillId) : CombatEvent;
public sealed record CombatHealEvent(string SourceParticipantId, string TargetParticipantId, string SkillId,
    long Amount, bool IsPeriodic = false) : CombatEvent;
public sealed record CombatHitFlags(bool? Critical = null, bool? Front = null, bool? Back = null,
    bool? Perfect = null, bool? Crushing = null, bool? MultiHit = null, bool? Parried = null);

public enum CombatBuffAction { Apply, Refresh, Remove, ObservedActive }
/// <summary>Exactly one target must be supplied. ObservedActive is not an application.
/// A null source identifies only an unattributed effect; removing all sources requires an explicit adapter proof.</summary>
public sealed record CombatBuffEvent(string BuffId, CombatBuffAction Action, string? TargetParticipantId = null,
    CombatEntityReference? TargetEntity = null, string? SourceParticipantId = null, int? StackCount = null,
    int? Level = null, bool InitialStateKnown = false, bool RemoveAllSources = false) : CombatEvent;
public sealed record CombatHpEvent(CombatEntityReference Target, long Current, long? Maximum = null) : CombatEvent;
public sealed record CombatDeathEvent(string ParticipantId) : CombatEvent;
public enum CombatGapReason { Transport, QueueOverflow, LateStart, UnknownAttribution, InterruptedJournal }
public sealed record CombatGapEvent(CombatGapReason Reason) : CombatEvent;
/// <summary>A neutral observed amount. It cannot increase damage/healing totals or remove roster authority.</summary>
public sealed record CombatUnclassifiedAmountEvent(string SourceParticipantId, string? TargetParticipantId,
    CombatEntityReference? TargetEntity, string? SkillId, string EffectId, long? Amount, bool IsPeriodic) : CombatEvent;

public sealed record CombatQualityIssue(string Code, long? Sequence = null);
public sealed record CombatRate(long Numerator, long Denominator)
{
    public decimal? Fraction => Denominator > 0 ? (decimal)Numerator / Denominator : null;
}
public sealed record CombatSkillSummary(string SkillId, long Damage, long Hits, long PeriodicTicks,
    long? Casts, long? Healing, long? MaximumHit, decimal? AverageHit,
    IReadOnlyDictionary<string, CombatRate> Rates);
public sealed record CombatPlayerSummary(CombatParticipant Participant, long Damage, decimal? Dps,
    decimal? DamageShare, long? Healing, decimal? Hps, long? Deaths,
    IReadOnlyList<CombatSkillSummary> Skills);
public sealed record CombatBuffInterval(long StartTicks, long EndTicks, string? SourceParticipantId,
    int? StackCount, int? Level);
public sealed record CombatBuffSummary(string BuffId, string? TargetParticipantId,
    CombatEntityReference? TargetEntity, long Applications, long ActiveTicks, decimal? Uptime,
    bool InitialStateKnown, IReadOnlyList<CombatBuffInterval> Intervals);
public sealed record CombatHpPoint(long OffsetTicks, CombatEntityReference Target, long Current, long? Maximum)
{
    public decimal? Fraction => Maximum is > 0 ? (decimal)Current / Maximum.Value : null;
}
public sealed record CombatAttemptSummary(Guid Id, string BossKey, long? DurationTicks, CombatEndReason? EndReason,
    long Damage, decimal? Dps, bool IsComplete, IReadOnlyList<CombatQualityIssue> QualityIssues,
    IReadOnlyList<CombatPlayerSummary> Players, IReadOnlyList<CombatBuffSummary> Buffs,
    IReadOnlyList<CombatHpPoint> HpPoints, IReadOnlyList<CombatEventEnvelope> Timeline);
