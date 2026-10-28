using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Qualified presentation input shared by the tracker and its deterministic offline previews.
/// Sequence and observation time belong to the same transient own-character binding.
/// </summary>
public sealed record AbyssRankMotionInput(string? ProfileId, long? BindingGeneration,
    long RankObservationSequence, DateTimeOffset? ObservedAt, long? RankPoints,
    bool Confirmed = true, bool CharacterBound = true, bool TrackingActive = true, bool Visible = true,
    long ContinuityRevision = 0);

/// <summary>
/// Pure rank-motion state machine. New targets replace pending targets; an in-progress form
/// reaches its original neighbour icon before changing direction. All times are monotonic milliseconds.
/// </summary>
public sealed class AbyssRankTransitionController(bool reducedMotion = false)
{
    private AbyssRankMotionInput? _baseline;
    private Transition? _transition;
    private MorphCommit? _lastMorphCommit;
    private long? _settledPoints;
    private long? _departurePoints;
    private double _lastTimeMs;

    public bool ReducedMotion { get; } = reducedMotion;
    public bool IsAnimating => _transition is not null;
    public long? LatestConfirmedPoints { get; private set; }
    public int? CurrentStageIndex { get; private set; }

    public void Observe(AbyssRankMotionInput input, double monotonicMs)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = ReadClock(monotonicMs);
        if (!Qualified(input))
        {
            Cancel();
            return;
        }

        if (_baseline is null || !SameContext(_baseline, input))
        {
            AdoptBaseline(input);
            return;
        }

        // Visibility changes never replay the hidden interval, even if the first visible
        // observation also contains a newer confirmed total.
        if (!input.Visible || !_baseline.Visible)
        {
            StopTransition();
            if ((input.RankObservationSequence > _baseline.RankObservationSequence
                    || input.RankObservationSequence == _baseline.RankObservationSequence && input.RankPoints == _baseline.RankPoints)
                && input.ObservedAt >= _baseline.ObservedAt)
                AdoptBaseline(input);
            else
            {
                _baseline = _baseline with { Visible = input.Visible };
                _settledPoints = LatestConfirmedPoints;
                CurrentStageIndex = StageIndex(LatestConfirmedPoints!.Value);
            }
            return;
        }

        Advance(now);
        if (input.RankObservationSequence <= _baseline.RankObservationSequence) return;
        if (input.ObservedAt < _baseline.ObservedAt)
        {
            // A time regression revokes continuity. The next qualified observation is a baseline.
            Cancel();
            return;
        }

        var previousPoints = LatestConfirmedPoints!.Value;
        _baseline = input;
        LatestConfirmedPoints = input.RankPoints;
        if (_transition is null)
        {
            if (StageIndex(previousPoints) != StageIndex(input.RankPoints!.Value))
                Start(previousPoints, input.RankPoints.Value, now);
            else
            {
                _settledPoints = input.RankPoints;
                CurrentStageIndex = StageIndex(input.RankPoints.Value);
            }
            return;
        }

        var elapsed = Math.Max(0, now - _transition.StartedAtMs);
        if (!ReducedMotion && !_transition.Centered && _transition.ReturnOnlyFrom is null
            && StageIndex(input.RankPoints!.Value) == StageIndex(_transition.FromPoints)
            && elapsed < AbyssRankMotionTimeline.GetMorphStartMs(_transition.FromPoints, _transition.ToPoints))
        {
            var outgoing = SampleTransition(_transition, now);
            // The symbol has not changed yet. Bring that same original home instead of
            // evolving into a now-obsolete neighbour and immediately evolving back.
            var returnDuration = 120 + 120 * Math.Clamp((outgoing.CenterX - 32) / 208, 0, 1);
            _transition = new(_transition.FromPoints, input.RankPoints.Value, now)
            { ReturnOnlyFrom = outgoing, ReturnOnlyDurationMs = returnDuration };
            _lastMorphCommit = null;
        }
        else if (StageIndex(_transition.ToPoints) == StageIndex(input.RankPoints!.Value))
            _transition.ToPoints = input.RankPoints.Value;
        else if (_lastMorphCommit is { } commit && commit.AtMs == now)
            RedirectAtCommit(commit);
    }

    /// <summary>Null means the caller should draw the newest ordinary static snapshot.</summary>
    public AbyssRankMotionFrame? Sample(double monotonicMs)
    {
        var now = ReadClock(monotonicMs);
        Advance(now);
        if (_transition is not { } transition) return null;
        var elapsed = Math.Max(0, now - transition.StartedAtMs);
        var frame = SampleTransition(transition, now);
        var landed = elapsed >= AbyssRankMotionTimeline.GetCommitMs(transition.FromPoints, transition.ToPoints, ReducedMotion);
        var evolved = elapsed >= AbyssRankMotionTimeline.GetReturnStartMs(transition.FromPoints, transition.ToPoints, ReducedMotion);
        var departure = _departurePoints is { } departurePoints ? StageIndex(departurePoints) : frame.DisplayCurrentStageIndex;
        var display = evolved ? frame.DisplayCurrentStageIndex : departure;
        var rightStage = landed ? frame.DisplayCurrentStageIndex : departure;
        int? next = rightStage + 1 < AbyssRankCatalog.Default.Stages.Count ? rightStage + 1 : null;
        var total = LatestConfirmedPoints!.Value;
        return frame with
        {
            DisplayCurrentStageIndex = display,
            DisplayNextStageIndex = next,
            NextOpacity = transition.Centered && !landed ? 0 : frame.NextOpacity,
            TotalPoints = total,
            PointsToNext = display + 1 < AbyssRankCatalog.Default.Stages.Count
                ? Math.Max(0, AbyssRankCatalog.Default.Stages[display + 1].Threshold - total) : null
        };
    }

    /// <summary>Forget continuity as well as motion, so the next observation cannot replay an old change.</summary>
    public void Cancel()
    {
        StopTransition();
        _baseline = null;
        _settledPoints = null;
        LatestConfirmedPoints = null;
        CurrentStageIndex = null;
    }

    private static bool Qualified(AbyssRankMotionInput input) => input.Confirmed && input.CharacterBound
        && input.TrackingActive && !string.IsNullOrWhiteSpace(input.ProfileId) && input.BindingGeneration is > 0
        && input.RankObservationSequence > 0 && input.ObservedAt is { } at && at != default && input.RankPoints is >= 0;

    private static bool SameContext(AbyssRankMotionInput left, AbyssRankMotionInput right) =>
        StringComparer.Ordinal.Equals(left.ProfileId, right.ProfileId) && left.BindingGeneration == right.BindingGeneration
        && left.ContinuityRevision == right.ContinuityRevision;

    private void AdoptBaseline(AbyssRankMotionInput input)
    {
        StopTransition();
        _baseline = input;
        LatestConfirmedPoints = _settledPoints = input.RankPoints;
        CurrentStageIndex = StageIndex(input.RankPoints!.Value);
    }

    private void Start(long fromPoints, long toPoints, double atMs)
    {
        _departurePoints = fromPoints;
        _lastMorphCommit = null;
        _transition = new(fromPoints, toPoints, atMs);
        CurrentStageIndex = StageIndex(fromPoints);
    }

    private void StopTransition()
    {
        _transition = null;
        _lastMorphCommit = null;
        _departurePoints = null;
    }

    private void Advance(double now)
    {
        while (_transition is { } transition)
        {
            var elapsed = Math.Max(0, now - transition.StartedAtMs);
            if (transition.ReturnOnlyFrom is not null)
            {
                if (elapsed < transition.ReturnOnlyDurationMs) return;
                var returnedAt = transition.StartedAtMs + transition.ReturnOnlyDurationMs;
                var returnedPoints = transition.ToPoints;
                _settledPoints = returnedPoints;
                CurrentStageIndex = StageIndex(returnedPoints);
                StopTransition();
                if (LatestConfirmedPoints is not { } next || StageIndex(next) == CurrentStageIndex)
                {
                    _settledPoints = LatestConfirmedPoints;
                    return;
                }
                Start(returnedPoints, next, returnedAt);
                continue;
            }
            var from = StageIndex(transition.FromPoints);
            var to = StageIndex(transition.ToPoints);
            var steps = Math.Abs(to - from);
            var redirected = false;
            while (transition.NextMorphCommit <= steps)
            {
                var offset = AbyssRankMotionTimeline.GetMorphCommitMs(transition.FromPoints, transition.ToPoints,
                    transition.NextMorphCommit, ReducedMotion);
                if (elapsed < offset) break;
                var direction = Math.Sign(to - from);
                var stage = from + direction * transition.NextMorphCommit;
                var commit = new MorphCommit(transition.StartedAtMs + offset, stage - direction, stage);
                _lastMorphCommit = commit;
                CurrentStageIndex = stage;
                transition.NextMorphCommit++;
                if (LatestConfirmedPoints is { } latest && StageIndex(latest) != to)
                {
                    RedirectAtCommit(commit);
                    redirected = true;
                    break;
                }
            }
            if (redirected) continue;
            var duration = AbyssRankMotionTimeline.GetDurationMs(transition.FromPoints, transition.ToPoints, ReducedMotion);
            if (elapsed < duration) return;
            var completedAt = transition.StartedAtMs + duration;
            _settledPoints = transition.ToPoints;
            CurrentStageIndex = to;
            StopTransition();
            if (LatestConfirmedPoints is not { } target || StageIndex(target) == to)
            {
                _settledPoints = LatestConfirmedPoints;
                return;
            }
            // A new target received during the return begins from a settled original icon.
            Start(_settledPoints.Value, target, completedAt);
        }
    }

    private void RedirectAtCommit(MorphCommit commit)
    {
        if (LatestConfirmedPoints is not { } target) return;
        var outgoing = _transition is { } previousTransition ? SampleTransition(previousTransition, commit.AtMs) : null;
        var stages = AbyssRankCatalog.Default.Stages;
        if (StageIndex(target) == commit.StageIndex)
        {
            var previous = stages[commit.PreviousStageIndex].Threshold;
            var returnStart = AbyssRankMotionTimeline.GetReturnStartMs(previous, target, ReducedMotion);
            _transition = new(previous, target, commit.AtMs - returnStart)
            { NextMorphCommit = 2, Centered = true, BlendFrom = outgoing, BlendStartedAtMs = commit.AtMs };
        }
        else
        {
            var from = stages[commit.StageIndex].Threshold;
            var morphStart = AbyssRankMotionTimeline.GetMorphStartMs(from, target, ReducedMotion);
            _transition = new(from, target, commit.AtMs - morphStart)
            { Centered = true, BlendFrom = outgoing, BlendStartedAtMs = commit.AtMs };
        }
        CurrentStageIndex = commit.StageIndex;
    }

    private AbyssRankMotionFrame SampleTransition(Transition transition, double atMs)
    {
        if (transition.ReturnOnlyFrom is { } returning)
        {
            var elapsed = Math.Max(0, atMs - transition.StartedAtMs);
            var progress = Smooth(Math.Clamp(elapsed / transition.ReturnOnlyDurationMs, 0, 1));
            var source = StageIndex(transition.FromPoints);
            var points = AbyssRankPresentation.Project(transition.ToPoints);
            return returning with
            {
                DisplayCurrentStageIndex = source,
                DisplayNextStageIndex = source + 1 < AbyssRankCatalog.Default.Stages.Count ? source + 1 : null,
                MotionFromStageIndex = source, MotionToStageIndex = source, MorphProgress = 0,
                CenterX = returning.CenterX + (32 - returning.CenterX) * progress,
                CenterY = returning.CenterY + (76 - returning.CenterY) * progress,
                IconScale = returning.IconScale + (1 - returning.IconScale) * progress,
                LightStrength = returning.LightStrength * (1 - progress),
                NextOpacity = returning.NextOpacity + (1 - returning.NextOpacity) * progress,
                TotalOpacity = returning.TotalOpacity + (1 - returning.TotalOpacity) * progress,
                RemainingOpacity = returning.RemainingOpacity + (1 - returning.RemainingOpacity) * progress,
                LabelOpacity = returning.LabelOpacity + (1 - returning.LabelOpacity) * progress,
                BarProgress = returning.BarProgress + (points.Progress - returning.BarProgress) * progress,
                TotalPoints = transition.ToPoints, PointsToNext = points.PointsToNext, ElapsedMs = elapsed, Active = true
            };
        }
        var frame = AbyssRankMotionTimeline.Sample(transition.FromPoints, transition.ToPoints,
            Math.Max(0, atMs - transition.StartedAtMs), ReducedMotion);
        if (transition.BlendFrom is not { } outgoing) return frame;
        var time = Math.Clamp((atMs - transition.BlendStartedAtMs) / 80, 0, 1);
        var ease = Smooth(time);
        return frame with
        {
            IconScale = outgoing.IconScale + (frame.IconScale - outgoing.IconScale) * ease,
            LightStrength = outgoing.LightStrength + (frame.LightStrength - outgoing.LightStrength) * ease,
            BarProgress = outgoing.BarProgress + (frame.BarProgress - outgoing.BarProgress) * ease
        };
    }

    private static double Smooth(double value) => value * value * value * (value * (value * 6 - 15) + 10);

    private double ReadClock(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        _lastTimeMs = Math.Max(_lastTimeMs, milliseconds);
        return _lastTimeMs;
    }

    private static int StageIndex(long points)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        for (var index = stages.Count - 1; index >= 0; index--)
            if (points >= stages[index].Threshold) return index;
        return 0;
    }

    private sealed class Transition(long fromPoints, long toPoints, double startedAtMs)
    {
        internal long FromPoints { get; } = fromPoints;
        internal long ToPoints { get; set; } = toPoints;
        internal double StartedAtMs { get; } = startedAtMs;
        internal int NextMorphCommit { get; set; } = 1;
        internal bool Centered { get; init; }
        internal AbyssRankMotionFrame? BlendFrom { get; init; }
        internal double BlendStartedAtMs { get; init; }
        internal AbyssRankMotionFrame? ReturnOnlyFrom { get; init; }
        internal double ReturnOnlyDurationMs { get; init; }
    }

    private sealed record MorphCommit(double AtMs, int PreviousStageIndex, int StageIndex);
}
