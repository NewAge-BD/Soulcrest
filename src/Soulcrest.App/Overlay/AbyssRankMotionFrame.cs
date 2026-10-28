using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>A deterministic presentation frame. It never changes confirmed tracking values.</summary>
public sealed record AbyssRankMotionFrame
{
    public int DisplayCurrentStageIndex { get; init; }
    public int? DisplayNextStageIndex { get; init; }
    public int MotionFromStageIndex { get; init; }
    public int MotionToStageIndex { get; init; }
    /// <summary>Always lower-to-higher, including when time runs through a rank-down.</summary>
    public double MorphProgress { get; init; }
    public double CenterX { get; init; } = 32;
    public double CenterY { get; init; } = 76;
    public double IconScale { get; init; } = 1;
    public double NextOpacity { get; init; } = 1;
    public double TotalOpacity { get; init; } = 1;
    public double RemainingOpacity { get; init; } = 1;
    public double LabelOpacity { get; init; } = 1;
    public double BarProgress { get; init; }
    public double LightStrength { get; init; }
    public long TotalPoints { get; init; }
    public long? PointsToNext { get; init; }
    public double ElapsedMs { get; init; }
    public bool Active { get; init; }
}

/// <summary>Offline, stateless choreography using the catalog's actual points-stage order.</summary>
public static class AbyssRankMotionTimeline
{
    // User feedback 2026-10-27: give the evolution itself more time and a softer landing.
    public const double UpDurationMs = 2640;
    public const double DownDurationMs = 2780;
    public const double ReducedDurationMs = 720;
    public const double MorphStartMs = 560;
    public const double FirstMorphDurationMs = 1400;
    public const double FollowingMorphDurationMs = 280;
    public const double ReturnDurationMs = 460;
    private const double IntermediateHoldMs = 45;

    public static double GetDurationMs(long fromPoints, long toPoints, bool reduced = false) =>
        Steps(fromPoints, toPoints) == 0 ? 0 : GetCommitMs(fromPoints, toPoints, reduced) + (reduced ? 120 : 220);

    public static double GetMorphStartMs(long fromPoints, long toPoints, bool reduced = false) =>
        reduced ? 0 : toPoints < fromPoints ? 600 : MorphStartMs;

    public static double GetMorphCommitMs(long fromPoints, long toPoints, int completedSteps, bool reduced = false)
    {
        var steps = Steps(fromPoints, toPoints);
        if (steps == 0) return 0;
        completedSteps = Math.Clamp(completedSteps, 1, steps);
        return reduced ? (steps == 1 ? 600 : MorphStepDuration(fromPoints, toPoints) * completedSteps)
            : GetMorphStartMs(fromPoints, toPoints) + MorphStepDuration(fromPoints, toPoints) * completedSteps;
    }

    public static double GetReturnStartMs(long fromPoints, long toPoints, bool reduced = false) =>
        GetMorphCommitMs(fromPoints, toPoints, Math.Max(1, Steps(fromPoints, toPoints)), reduced);

    public static double GetCommitMs(long fromPoints, long toPoints, bool reduced = false) =>
        Steps(fromPoints, toPoints) == 0 ? 0 : reduced ? GetReturnStartMs(fromPoints, toPoints, true) : GetReturnStartMs(fromPoints, toPoints) + ReturnDurationMs;

    public static AbyssRankMotionFrame Sample(long fromPoints, long toPoints, double elapsedMs, bool reduced = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(toPoints);
        if (!double.IsFinite(elapsedMs)) throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        var catalog = AbyssRankCatalog.Default.Stages;
        var from = StageIndex(fromPoints);
        var to = StageIndex(toPoints);
        var steps = Math.Abs(to - from);
        var duration = GetDurationMs(fromPoints, toPoints, reduced);
        var t = Math.Max(0, elapsedMs);
        if (steps == 0 || t >= duration)
            return Static(toPoints, to, elapsedMs);
        if (elapsedMs <= 0)
            return Static(fromPoints, from, elapsedMs) with { Active = true };

        var ascending = to > from;
        var direction = ascending ? 1 : -1;
        var returnStart = GetReturnStartMs(fromPoints, toPoints, reduced);
        var commit = GetCommitMs(fromPoints, toPoints, reduced);
        var morphStart = GetMorphStartMs(fromPoints, toPoints, reduced);
        var step = 0;
        while (step < steps - 1 && t >= GetMorphCommitMs(fromPoints, toPoints, step + 1, reduced)) step++;
        var stepStart = step == 0 ? morphStart : GetMorphCommitMs(fromPoints, toPoints, step, reduced);
        var stepEnd = GetMorphCommitMs(fromPoints, toPoints, step + 1, reduced);
        var localMorph = Smooth(Unit(t, stepStart, stepEnd - (steps > 1 ? IntermediateHoldMs : 0)));
        var motionFrom = from + step * direction;
        var motionTo = motionFrom + direction;
        var landed = t >= commit;
        var evolved = t >= returnStart;
        var displayed = evolved ? to : from;
        var target = AbyssRankPresentation.Project(toPoints);
        var source = AbyssRankPresentation.Project(fromPoints);
        var outProgress = Smooth(Unit(t, ascending ? 140 : 120, morphStart));
        var returnProgress = Smooth(Unit(t, returnStart, commit));
        var x = reduced ? 32 : t < morphStart ? Lerp(32, 240, outProgress)
            : t < returnStart ? 240 : Lerp(240, 32, returnProgress);
        var travelScale = ascending ? 1.08 : 1.06;
        var detachEnd = ascending ? 140 : 120;
        var detachScale = ascending ? 1.02 : 1.01;
        var scale = reduced ? 1 : t < detachEnd ? Lerp(1, detachScale, Smooth(Unit(t, 0, detachEnd)))
            : t < morphStart ? Lerp(detachScale, travelScale, outProgress)
            : t < returnStart ? travelScale + (ascending ? .02 : 0) * Math.Sin(Math.PI * Unit(t, morphStart, returnStart))
            : Lerp(travelScale, 1, returnProgress);
        var beforeSlotSwap = reduced ? 1 - Smooth(Unit(t, 0, 100)) : 1 - Smooth(Unit(t, 0, morphStart));
        var nextOpacity = landed ? Smooth(Unit(t, commit, duration)) : beforeSlotSwap;
        var totalOpacity = reduced ? 1 : 1 - .90 * Smooth(Unit(t, 220, morphStart))
            * (1 - Smooth(Unit(t, returnStart + 150, commit)));
        var labelOpacity = reduced ? 1 : evolved ? Smooth(Unit(t, returnStart, returnStart + 160))
            : 1 - Smooth(Unit(t, returnStart - 160, returnStart));
        var barProgress = evolved ? Lerp(ascending ? 0 : 1, target.Progress, Smooth(Unit(t, returnStart, returnStart + (reduced ? 120 : 240))))
            : Lerp(source.Progress, ascending ? 1 : 0, Smooth(Unit(t, 0, morphStart)));
        var rightStage = landed ? to : from;
        var next = rightStage + 1 < catalog.Count ? rightStage + 1 : (int?)null;
        return new()
        {
            DisplayCurrentStageIndex = displayed, DisplayNextStageIndex = next,
            MotionFromStageIndex = motionFrom, MotionToStageIndex = motionTo,
            MorphProgress = ascending ? localMorph : 1 - localMorph,
            CenterX = x, CenterY = 76, IconScale = scale,
            NextOpacity = nextOpacity, TotalOpacity = totalOpacity,
            RemainingOpacity = totalOpacity, LabelOpacity = labelOpacity,
            BarProgress = barProgress,
            LightStrength = reduced ? 0 : (ascending ? .72 : .30) * Math.Sin(Math.PI * Unit(t, morphStart, returnStart)),
            TotalPoints = toPoints,
            PointsToNext = evolved ? target.PointsToNext : next is { } n ? Math.Max(0, catalog[n].Threshold - toPoints) : null,
            ElapsedMs = elapsedMs, Active = true
        };
    }

    private static AbyssRankMotionFrame Static(long points, int stage, double elapsedMs)
    {
        var progress = AbyssRankPresentation.Project(points);
        return new()
        {
            DisplayCurrentStageIndex = stage,
            DisplayNextStageIndex = stage + 1 < AbyssRankCatalog.Default.Stages.Count ? stage + 1 : null,
            MotionFromStageIndex = stage, MotionToStageIndex = stage,
            BarProgress = progress.Progress, TotalPoints = points, PointsToNext = progress.PointsToNext,
            ElapsedMs = elapsedMs
        };
    }

    private static int StageIndex(long points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(points);
        var stages = AbyssRankCatalog.Default.Stages;
        for (var index = stages.Count - 1; index >= 0; index--) if (points >= stages[index].Threshold) return index;
        return 0;
    }
    private static int Steps(long fromPoints, long toPoints) => Math.Abs(StageIndex(toPoints) - StageIndex(fromPoints));
    private static double MorphStepDuration(long fromPoints, long toPoints) => Steps(fromPoints, toPoints) switch
    {
        0 => 0,
        1 => toPoints < fromPoints ? 1500 : FirstMorphDurationMs,
        2 => 650,
        3 => 500,
        _ => FollowingMorphDurationMs
    };
    private static double Unit(double value, double start, double end) => end <= start ? value >= end ? 1 : 0 : Math.Clamp((value - start) / (end - start), 0, 1);
    private static double Smooth(double value) => value * value * value * (value * (value * 6 - 15) + 10);
    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;
}
