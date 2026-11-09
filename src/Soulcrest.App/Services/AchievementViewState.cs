using System.Collections.Immutable;
using Soulcrest.App.Components;

namespace Soulcrest.App.Services;

public enum AchievementViewSource { Unknown, Live, Saved }

/// <summary>An own progress value received in an update, without interpreting its unit or cause.</summary>
public sealed record AchievementActivity(int FamilyId, int? PreviousValue, int CurrentValue, DateTimeOffset ObservedAt);

/// <summary>Presentation facts, never a replacement for a fresh network domain.</summary>
public sealed record AchievementViewState
{
    public string? ProfileId { get; init; }
    public AchievementProfileBinding? Binding { get; init; }
    public AchievementViewSource Source { get; init; }
    public bool IsLive => Source == AchievementViewSource.Live;
    public bool IsSaved => Source == AchievementViewSource.Saved;
    public DateTimeOffset? ObservedAt { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public int MetadataBuild { get; init; }
    public string RuleVersion { get; init; } = AchievementRuleEvaluator.Version;
    public CombatAchievementProgressSnapshot Progress { get; init; } = new(0, 0,
        ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty);
    public ImmutableHashSet<int> ObservedFamilyIds { get; init; } = [];
    /// <summary>Exact signed own counters keyed by the received ID. No unit, goal or completion is implied.</summary>
    public ImmutableDictionary<int, int> ObservedValues { get; init; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableDictionary<int, AchievementActivity> Activities { get; init; } = ImmutableDictionary<int, AchievementActivity>.Empty;
    public ImmutableArray<AchievementMilestone> Milestones { get; init; } = [];
    public ImmutableArray<string> Issues { get; init; } = [];
    public bool HistoryGap { get; init; }
    public bool Saving { get; init; }
    public static AchievementViewState Empty { get; } = new();
}
