namespace Soulcrest.App.Services;

/// <summary>One Count-domain stage choice shared by calculator and tracker projections.</summary>
public static class AchievementProgressStageResolver
{
    public static int? Select(IReadOnlyList<AchievementProgressThresholdStage> stages, long? reportedValue,
        IReadOnlySet<int>? confirmedClaimedStageIds = null, bool chainComplete = true)
    {
        if (reportedValue is null or < 0 || stages.Count == 0) return null;
        var ids = new HashSet<int>();
        var progress = -1;
        var claimMinimum = 0;
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            if (stage is null || stage.Id <= 0 || !ids.Add(stage.Id) || stage.Target <= 0
                || index > 0 && stage.Target <= stages[index - 1].Target) return null;
            if (progress < 0 && reportedValue.Value < stage.Target) progress = index;
            if (confirmedClaimedStageIds?.Contains(stage.Id) == true) claimMinimum = index + 1;
        }
        // Reaching a goal advances progress immediately, regardless of an outstanding reward.
        // A complete terminal chain keeps its last stage; a truncated chain cannot invent its next.
        if (progress < 0)
        {
            if (!chainComplete) return null;
            progress = stages.Count - 1;
        }
        if (claimMinimum >= stages.Count)
        {
            if (!chainComplete) return null;
            claimMinimum = stages.Count - 1;
        }
        return stages[Math.Max(progress, claimMinimum)].Id;
    }
}
