using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Actual own binding, independent of the profile selected in the calculator.</summary>
public sealed record AchievementOwnSnapshot(AchievementProfileBinding? Binding, AchievementViewState State,
    CharacterAchievementState? Raw, long ReceivedTimestamp)
{
    public Guid SourceInstanceId { get; init; }
    public bool IsLive => Binding is not null && State.IsLive;
    public bool IsHistorical => Binding is not null && State.IsSaved;
    public bool HasProgressEvidence => Binding is not null && Raw is not null && (State.IsLive || State.IsSaved);
    public AchievementOwnSnapshot? History { get; init; }

    internal AchievementOwnSnapshot ForProgressFamily(int familyId)
    {
        if (!IsLive || Raw is null || Raw.Achievements.ContainsKey(familyId)) return this;
        return History is { IsHistorical: true, HasProgressEvidence: true } history && history.Binding == Binding
            && history.State.MetadataBuild == State.MetadataBuild && history.Raw!.Achievements.ContainsKey(familyId)
                ? history : this;
    }

    public static AchievementOwnSnapshot Empty { get; } = new(null, AchievementViewState.Empty, null, 0);
}

/// <summary>One ordered observation before UI coalescing; subscribers only enqueue immutable presentation data.</summary>
public sealed record AchievementChangeBatch(AchievementOwnSnapshot Snapshot, NetworkAchievementObservationKind Kind,
    ImmutableArray<AchievementActivity> Activities, ImmutableArray<AchievementMilestone> Milestones,
    bool HistoryGap, Guid SourceInstanceId, long Sequence, long Generation, long ReceivedTimestamp);

public interface IAchievementTrackerSource
{
    AchievementOwnSnapshot CurrentOwnSnapshot { get; }
    event Action<AchievementChangeBatch>? AchievementObserved;
}
