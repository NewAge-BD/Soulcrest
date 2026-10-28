namespace Soulcrest.App.Services;

/// <summary>Point-threshold progress only; no leaderboard place or actual rank is inferred.</summary>
public sealed record AbyssRankProgress(AbyssRankStage? Current, AbyssRankStage? Next,
    long? TotalPoints, long PointsInStage, long? PointsToNext, double Progress, bool IsMaximum)
{
    public bool HasPoints => TotalPoints is not null;
}

public static class AbyssRankPresentation
{
    /// <summary>
    /// User decision 2026-10-26: show the points stage, not a competitive rank claim.
    /// Missing/invalid totals remain unknown; crossing a threshold chooses the new interval.
    /// </summary>
    public static AbyssRankProgress Project(long? totalPoints, AbyssRankCatalog? catalog = null)
    {
        if (totalPoints is null or < 0) return new(null, null, null, 0, null, 0, false);
        catalog ??= AbyssRankCatalog.Default;
        var current = catalog.FindStage(totalPoints.Value)!;
        var next = catalog.Stages.FirstOrDefault(stage => stage.Threshold > totalPoints.Value);
        var pointsInStage = totalPoints.Value - current.Threshold;
        return next is null
            ? new(current, null, totalPoints, pointsInStage, null, 1, true)
            : new(current, next, totalPoints, pointsInStage, next.Threshold - totalPoints.Value,
                (double)pointsInStage / (next.Threshold - current.Threshold), false);
    }
}
