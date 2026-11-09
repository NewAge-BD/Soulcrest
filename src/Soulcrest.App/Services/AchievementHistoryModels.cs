using System.Collections.Immutable;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Opaque profile and native-assignment revision. Contains no game identity.</summary>
public sealed record AchievementProfileBinding(string ProfileId, string Revision);

public enum AchievementMilestoneKind { Completed, RewardClaimed, PeriodChanged }

/// <summary>A confirmed fact; FirstObservedAt is never presented as the original completion time.</summary>
public sealed record AchievementMilestone(
    string Id,
    AchievementMilestoneKind Kind,
    int? FamilyId,
    int? StageId,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset? OccurredAt,
    string Origin,
    int MetadataBuild,
    string RuleVersion,
    string? PeriodId,
    CharacterAchievementState RawPatch,
    string? LabelDe = null,
    string? LabelEn = null)
{
    public bool NotificationEligible { get; init; }
}

/// <summary>Persisted last observation; loading it does not establish a live network baseline.</summary>
public sealed record AchievementSavedSnapshot
{
    public AchievementProfileBinding Binding { get; init; } = new("", "");
    public CharacterAchievementState Raw { get; init; } = new(
        ImmutableDictionary<int, CharacterAchievement>.Empty,
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
    public DateTimeOffset ObservedAt { get; init; }
    public int MetadataBuild { get; init; }
    public string RuleVersion { get; init; } = "";
    public long Generation { get; init; }
    public long SourceSequence { get; init; }
    public long JournalWatermark { get; init; }
    public bool HistoryGap { get; init; }
    /// <summary>Describes exactly the received source, not absence in the complete datamine.</summary>
    public string Coverage { get; init; } = "unknown";
}

public sealed record AchievementStoreRead(
    AchievementSavedSnapshot? Snapshot,
    ImmutableArray<AchievementMilestone> Milestones,
    ImmutableArray<string> Issues);
