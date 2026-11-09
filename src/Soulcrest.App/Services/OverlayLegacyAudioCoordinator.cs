using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>
/// Owns the retired HUDs' audio permission and rank-cue timeline without creating a window.
/// Only accepted drop receipts and fresh qualified rank observations can start a cue.
/// </summary>
public sealed class OverlayLegacyAudioCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly RareDropAlertService _drops;
    private readonly IAbyssRankOverlaySource _rankSource;
    private readonly Func<bool> _reducedMotion;
    private readonly Action<long, long, bool> _startRank;
    private readonly Action _stopRank, _applyRankSettings;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly ITimer _timer;
    private AbyssRankTransitionController _motion;
    private AbyssRankMotionInput? _lastInput;
    private bool _contextAllowed, _ownRankCue, _disposed;

    public OverlayLegacyAudioCoordinator(RareDropAlertService drops, IAbyssRankOverlaySource rankSource,
        AbyssRankSoundPlayer rankSounds, Func<bool> reducedMotion)
        : this(drops, rankSource, reducedMotion, rankSounds.Start, rankSounds.Stop,
            rankSounds.ApplySettings, TimeProvider.System) { }

    internal OverlayLegacyAudioCoordinator(RareDropAlertService drops, IAbyssRankOverlaySource rankSource,
        Func<bool> reducedMotion, Action<long, long, bool> startRank, Action stopRank,
        Action applyRankSettings, TimeProvider time)
    {
        _drops = drops;
        _rankSource = rankSource;
        _reducedMotion = reducedMotion;
        _startRank = startRank;
        _stopRank = stopRank;
        _applyRankSettings = applyRankSettings;
        _time = time;
        _started = time.GetTimestamp();
        _motion = new(reducedMotion());
        _timer = time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _drops.SetPresentationVisible(false);
        ObserveRank();
        _rankSource.Changed += SourceChanged;
    }

    /// <summary>Published by Main's existing foreground/placement/smoke policy, before tracking starts.</summary>
    public void SetContextAllowed(bool allowed)
    {
        lock (_gate)
        {
            if (_disposed || _contextAllowed == allowed) return;
            _contextAllowed = allowed;
            _drops.SetPresentationVisible(allowed);
            ApplySettingsCore();
        }
    }

    public void ApplySettings()
    {
        lock (_gate)
        {
            if (!_disposed) ApplySettingsCore();
        }
    }

    private void ApplySettingsCore()
    {
        _drops.ApplySettings();
        _applyRankSettings();
        var reduced = _reducedMotion();
        if (reduced != _motion.ReducedMotion)
        {
            StopOwnRankCue();
            _motion = new(reduced);
            _lastInput = null;
        }
        ObserveRank();
    }

    private void SourceChanged()
    {
        lock (_gate)
        {
            if (!_disposed) ObserveRank();
        }
    }

    private double Milliseconds => _time.GetElapsedTime(_started, _time.GetTimestamp()).TotalMilliseconds;

    private void ObserveRank()
    {
        var snapshot = _rankSource.Snapshot;
        var input = new AbyssRankMotionInput(snapshot.ProfileId, snapshot.BindingGeneration,
            snapshot.RankObservationSequence, snapshot.RankPointsObservedAt, snapshot.RankPoints,
            snapshot.Confirmed, snapshot.CharacterBound, snapshot.RankTrackingActive,
            _contextAllowed && snapshot.WorldContextVisible, snapshot.RankContinuityRevision);
        if (!input.Visible || _lastInput is { } previous &&
            (!StringComparer.Ordinal.Equals(previous.ProfileId, input.ProfileId)
                || previous.BindingGeneration != input.BindingGeneration
                || previous.ContinuityRevision != input.ContinuityRevision
                || Qualified(previous) && !Qualified(input)
                || input.RankObservationSequence > previous.RankObservationSequence && input.ObservedAt < previous.ObservedAt))
            StopOwnRankCue();
        _lastInput = input;
        var now = Milliseconds;
        var completing = _motion.IsAnimating;
        _motion.Sample(now);
        if (completing && !_motion.IsAnimating) StopOwnRankCue();
        var wasAnimating = _motion.IsAnimating;
        var previousPoints = _motion.LatestConfirmedPoints;
        _motion.Observe(input, now);
        // Match the former HUD: a retarget cancels its obsolete composed landing cue;
        // it does not stack another fanfare over the in-progress sequence.
        if (wasAnimating && previousPoints is { } oldTarget && _motion.LatestConfirmedPoints is { } newTarget
            && AbyssRankCatalog.Default.FindStage(oldTarget) != AbyssRankCatalog.Default.FindStage(newTarget))
            StopOwnRankCue();
        if (!wasAnimating && _motion.IsAnimating && previousPoints is { } from
            && snapshot.RankPoints is { } to
            && AbyssRankCatalog.Default.FindStage(to)?.Threshold > AbyssRankCatalog.Default.FindStage(from)?.Threshold)
        {
            _ownRankCue = true;
            _startRank(from, to, _motion.ReducedMotion);
        }
        else if (wasAnimating && !_motion.IsAnimating) StopOwnRankCue();
        UpdateTimer();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var frame = _motion.Sample(Milliseconds);
            if (_ownRankCue && (frame is null || frame.MotionToStageIndex < frame.MotionFromStageIndex
                || frame.MotionToStageIndex == frame.MotionFromStageIndex
                    && AbyssRankCatalog.Default.FindStage(frame.TotalPoints)?.Threshold
                        <= AbyssRankCatalog.Default.Stages[frame.MotionFromStageIndex].Threshold))
                StopOwnRankCue();
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        var interval = _motion.IsAnimating ? TimeSpan.FromMilliseconds(33) : Timeout.InfiniteTimeSpan;
        _timer.Change(interval, interval);
    }

    private static bool Qualified(AbyssRankMotionInput input) => input.Confirmed && input.CharacterBound
        && input.TrackingActive && !string.IsNullOrWhiteSpace(input.ProfileId) && input.BindingGeneration is > 0
        && input.RankObservationSequence > 0 && input.ObservedAt is { } at && at != default && input.RankPoints is >= 0;

    private void StopOwnRankCue()
    {
        // An unrelated settings-preview cue must survive if this owner never started a rank cue.
        if (!_ownRankCue) return;
        _ownRankCue = false;
        _stopRank();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rankSource.Changed -= SourceChanged;
            _timer.Dispose();
            _drops.SetPresentationVisible(false);
            StopOwnRankCue();
            _motion.Cancel();
        }
    }
}
