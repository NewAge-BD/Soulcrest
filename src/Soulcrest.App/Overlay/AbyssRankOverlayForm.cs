using System.Diagnostics;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Independent points-tier HUD; owns presentation and interaction only, never game-state mutation.</summary>
public sealed class AbyssRankOverlayForm : Form
{
    private readonly IAbyssRankOverlaySource _source;
    private readonly Func<AbyssRankOverlayOptions> _options;
    private readonly Action<Point>? _positionChanged;
    private readonly Action<long, long, bool>? _rankUpStarted;
    private readonly Action? _motionCanceled;
    private readonly Stopwatch _motionClock = Stopwatch.StartNew();
    private readonly Func<double> _milliseconds;
    private readonly System.Windows.Forms.Timer _motionTimer = new() { Interval = 16 };
    private readonly AbyssRankMorphCache _morphs = new();
    private readonly CancellationTokenSource _preloadCancellation = new();
    private readonly object _refreshGate = new();
    private readonly LootOverlayIconCache _icons = new();
    private readonly System.Diagnostics.Stopwatch _killFeedClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _killFeedTimer = new() { Interval = 33 };
    private readonly OverlayLockButton _lockButton;
    private AbyssRankOverlaySnapshot _snapshot;
    private AbyssRankOverlayOptions _current;
    private OverlayThemeDefinition _theme;
    private AbyssRankTransitionController _motion;
    private AbyssRankMotionFrame? _motionFrame;
    private AbyssRankMotionInput? _lastMotionInput;
    private int _handleGeneration;
    private bool _preloadStarted, _rankSoundActive;
    private volatile bool _morphsReady, _motionDisposed;
    private Point? _drag;
    private bool _locked = true, _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _suppressed, _placementHidden, _foregroundSuppressed, _rendering, _refreshQueued;
    private bool PresentationHidden => _suppressed || _placementHidden || _foregroundSuppressed || !_snapshot.WorldContextVisible;

    public AbyssRankOverlayForm(IAbyssRankOverlaySource source, Func<AbyssRankOverlayOptions> options,
        Action<Point>? positionChanged = null, Action<long, long, bool>? rankUpStarted = null, Action? motionCanceled = null)
        : this(source, options, positionChanged, rankUpStarted, motionCanceled, null) { }

    internal AbyssRankOverlayForm(IAbyssRankOverlaySource source, Func<AbyssRankOverlayOptions> options,
        Action<Point>? positionChanged, Action<long, long, bool>? rankUpStarted, Action? motionCanceled, Func<double>? milliseconds)
    {
        _source = source;
        _options = options;
        _positionChanged = positionChanged;
        _rankUpStarted = rankUpStarted;
        _motionCanceled = motionCanceled;
        _milliseconds = milliseconds ?? (() => _motionClock.Elapsed.TotalMilliseconds);
        _snapshot = source.Snapshot;
        _current = options();
        _motion = new(_current.ReducedMotion);
        _theme = OverlayThemeDefinition.Resolve(_current.Theme);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        // Every theme uses the same per-pixel presenter; no frame animation passes through WinForms painting.
        LayeredOverlayPresenter.Configure(this, true, 1);
        _lockButton = new OverlayLockButton(this, LockBounds);
        _lockButton.SetTheme(_theme);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(_current.VisibleInRecordings);
        _motionTimer.Tick += MotionTimerTick;
        _source.Changed += SourceChanged;
        _killFeedTimer.Tick += KillFeedTimerTick;
    }

    public void ApplySettings()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        var options = _options();
        if (options.ReducedMotion != _current.ReducedMotion)
        {
            StopMotion();
            _motion = new(options.ReducedMotion);
        }
        _current = options;
        var theme = OverlayThemeDefinition.Resolve(_current.Theme);
        if (theme.Id != _theme.Id)
        {
            _theme = theme;
            _lockButton.SetTheme(theme);
        }
        NativeMethods.SetCaptureVisibility(Handle, _current.VisibleInRecordings);
        _lockButton.SetVisibleInRecordings(_current.VisibleInRecordings);
        var snapshot = _source.Snapshot;
        _snapshot = snapshot with
        {
            KillFeedEnabled = _current.KillFeedEnabled && snapshot.KillFeedEnabled,
            ShowKills = _current.ShowKills && snapshot.ShowKills,
            ShowDeaths = _current.ShowDeaths && snapshot.ShowDeaths,
            ShowAssists = _current.ShowAssists && snapshot.ShowAssists
        };
        if (!_current.Enabled || PresentationHidden || SmokeTest.Enabled)
        {
            _killFeedTimer.Stop();
            StopMotion();
            FinishDrag();
            ResetInteraction();
            Hide();
            ObserveMotion();
            _blockAltUntilRelease = true;
            return;
        }
        if (!_preloadStarted)
        {
            _preloadStarted = true;
            _ = PreloadMotionAsync();
        }
        var size = AbyssRankOverlayRenderer.Measure(_snapshot);
        var scale = AbyssRankOverlayRenderer.NormalizedScale(_current.Scale);
        Size = new((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        if (_drag is null) Location = new(_current.X, _current.Y);
        if (!Visible) Show();
        ObserveMotion();
        _lockButton.RefreshTarget();
        RenderPanel();
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        ApplyPresentationVisibility();
    }

    public void SetPlacementHidden(bool hidden)
    {
        if (_placementHidden == hidden) return;
        _placementHidden = hidden;
        ApplyPresentationVisibility();
    }

    public void SetForegroundSuppressed(bool suppressed)
    {
        if (_foregroundSuppressed == suppressed) return;
        _foregroundSuppressed = suppressed;
        ApplyPresentationVisibility();
    }

    private void ApplyPresentationVisibility()
    {
        StopMotion();
        FinishDrag();
        ResetInteraction();
        _blockAltUntilRelease = true;
        ApplySettings();
    }

    protected override bool ShowWithoutActivation => true;
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && _current.Enabled && !PresentationHidden && !SmokeTest.Enabled);
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            if (_locked) parameters.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayeredOverlayPresenter.Configure(this, true, 1);
        NativeMethods.SetClickThrough(Handle, _locked);
        ApplySettings();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _killFeedTimer.Stop();
        StopMotion();
        lock (_refreshGate) { _refreshQueued = false; _handleGeneration++; }
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _killFeedTimer.Stop();
        if (!Visible) { StopMotion(); ResetInteraction(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void SourceChanged()
    {
        if (!IsHandleCreated || IsDisposed || Disposing || _motionDisposed) return;
        int generation;
        lock (_refreshGate)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            generation = _handleGeneration;
        }
        try
        {
            BeginInvoke(() =>
            {
                lock (_refreshGate)
                {
                    if (generation != _handleGeneration) return;
                    _refreshQueued = false;
                }
                if (!IsDisposed && !Disposing && !_motionDisposed) ApplySettings();
            });
        }
        catch (InvalidOperationException)
        {
            lock (_refreshGate) if (generation == _handleGeneration) _refreshQueued = false;
        }
    }

    private async Task PreloadMotionAsync()
    {
        try
        {
            if (!await _morphs.PreloadAllAsync(_preloadCancellation.Token).ConfigureAwait(false) || _motionDisposed) return;
            _morphsReady = true;
            SourceChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException) { }
    }

    private void ObserveMotion()
    {
        var input = new AbyssRankMotionInput(_snapshot.ProfileId, _snapshot.BindingGeneration, _snapshot.RankObservationSequence,
            _snapshot.RankPointsObservedAt, _snapshot.RankPoints, _snapshot.Confirmed, _snapshot.CharacterBound,
            _snapshot.RankTrackingActive, _morphsReady && Visible && !PresentationHidden && _current.Enabled && !SmokeTest.Enabled,
            _snapshot.RankContinuityRevision);
        if (_lastMotionInput is { } previousInput
            && (!StringComparer.Ordinal.Equals(previousInput.ProfileId, input.ProfileId)
                || previousInput.BindingGeneration != input.BindingGeneration
                || previousInput.ContinuityRevision != input.ContinuityRevision
                || QualifiedMotionInput(previousInput) && !QualifiedMotionInput(input)
                || input.RankObservationSequence > previousInput.RankObservationSequence && input.ObservedAt < previousInput.ObservedAt))
            StopSound();
        _lastMotionInput = input;
        var now = _milliseconds();
        _motion.Sample(now); // Finish an elapsed sequence before deciding whether this fact starts a new one.
        var wasAnimating = _motion.IsAnimating;
        var previousPoints = _motion.LatestConfirmedPoints;
        _motion.Observe(input, now);
        // A retarget can move the landing time. End the old composed cue instead of
        // sounding an obsolete landing chord or stacking another fanfare.
        if (wasAnimating && previousPoints is { } oldTarget && _motion.LatestConfirmedPoints is { } newTarget
            && AbyssRankCatalog.Default.FindStage(oldTarget) != AbyssRankCatalog.Default.FindStage(newTarget))
            StopSound();
        if (!wasAnimating && _motion.IsAnimating && previousPoints is { } from
            && _snapshot.RankPoints is { } to && AbyssRankCatalog.Default.FindStage(to)?.Threshold > AbyssRankCatalog.Default.FindStage(from)?.Threshold)
        {
            _rankSoundActive = true;
            _rankUpStarted?.Invoke(from, to, _current.ReducedMotion);
        }
        else if (wasAnimating && !_motion.IsAnimating) StopSound();
    }

    private static bool QualifiedMotionInput(AbyssRankMotionInput input) => input.Confirmed && input.CharacterBound
        && input.TrackingActive && !string.IsNullOrWhiteSpace(input.ProfileId) && input.BindingGeneration is > 0
        && input.RankObservationSequence > 0 && input.ObservedAt is { } at && at != default && input.RankPoints is >= 0;

    private void StopMotion()
    {
        _motionTimer.Stop();
        _motion.Cancel();
        _motionFrame = null;
        StopSound();
    }

    private void StopSound()
    {
        // The sound player is shared with the settings preview; only this overlay's own cue may be stopped.
        if (!_rankSoundActive) return;
        _rankSoundActive = false;
        _motionCanceled?.Invoke();
    }

    private void MotionTimerTick(object? sender, EventArgs e) => RenderAnimationFrame();

    internal void RenderAnimationFrame()
    {
        if (!Visible || PresentationHidden || !_current.Enabled || !IsHandleCreated || IsDisposed || Disposing)
        { StopMotion(); return; }
        RenderPanel();
    }

    internal bool MotionAssetsReady => _morphsReady;
    internal bool MotionTimerEnabled => _motionTimer.Enabled;
    internal bool MotionAnimating => _motion.IsAnimating;
    internal AbyssRankMotionFrame? CurrentMotionFrame => _motionFrame;

    private void KillFeedTimerTick(object? sender, EventArgs e)
    {
        if (!Visible || PresentationHidden || !_current.Enabled || !IsHandleCreated || IsDisposed || Disposing || SmokeTest.Enabled)
        { _killFeedTimer.Stop(); return; }
        RenderPanel();
    }

    private void RenderPanel()
    {
        if (_rendering || !Visible || !IsHandleCreated || IsDisposed || PresentationHidden || !_current.Enabled || SmokeTest.Enabled) return;
        _rendering = true;
        try
        {
            _motionFrame = _motion.Sample(_milliseconds());
            if (_rankSoundActive && _motionFrame is { } soundFrame
                && (soundFrame.MotionToStageIndex < soundFrame.MotionFromStageIndex
                    || soundFrame.MotionToStageIndex == soundFrame.MotionFromStageIndex
                    && AbyssRankCatalog.Default.FindStage(soundFrame.TotalPoints)?.Threshold
                        <= AbyssRankCatalog.Default.Stages[soundFrame.MotionFromStageIndex].Threshold))
                StopSound();
            using var image = AbyssRankOverlayRenderer.RenderBitmap(_snapshot, _theme.Id, _current.Scale,
                _locked, _icons, _current.Language, _motionFrame, _morphs,
                killFeedSeconds: _killFeedClock.Elapsed.TotalSeconds, animateKillFeed: !_current.ReducedMotion);
            LayeredOverlayPresenter.Present(this, image);
            _killFeedTimer.Enabled = AbyssKillFeedRankVisuals.NeedsAnimation(_snapshot, _current.ReducedMotion,
                Visible && !PresentationHidden && _current.Enabled && !SmokeTest.Enabled);
            if (_motionFrame is not null && _motion.IsAnimating) _motionTimer.Start();
            else _motionTimer.Stop();
        }
        finally { _rendering = false; }
    }

    private Rectangle LockBounds()
    {
        var scale = AbyssRankOverlayRenderer.NormalizedScale(_current.Scale);
        var bounds = AbyssRankOverlayRenderer.LockBounds;
        return Rectangle.Round(new RectangleF((float)(bounds.X * scale), (float)(bounds.Y * scale),
            (float)(bounds.Width * scale), (float)(bounds.Height * scale)));
    }

    internal bool ManualInteractionEnabled => _manualInteraction;
    internal void ToggleManualInteraction() => SetManualInteraction(_locked);
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);

    private void SetManualInteraction(bool enabled)
    {
        _manualInteraction = enabled && !PresentationHidden;
        var alt = _altInteractionRequested || OverlayInteraction.IsAltHeld;
        _blockAltUntilRelease = !enabled && alt;
        SetInteractionEnabled(alt);
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = (_manualInteraction || (enabled && !_blockAltUntilRelease)) && Visible && !PresentationHidden;
        if (_lockButton.Locked != !enabled) _lockButton.SetLocked(!enabled);
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) FinishDrag();
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, _locked);
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        RenderPanel();
    }

    private void ResetInteraction()
    {
        FinishDrag();
        _manualInteraction = false;
        SetInteractionEnabled(false);
    }

    private bool InteractionAllowed => Visible && !PresentationHidden && (_manualInteraction
        || (!_blockAltUntilRelease && (_altInteractionRequested || OverlayInteraction.IsAltHeld)));

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_locked || e.Button != MouseButtons.Left) return;
        _drag = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_drag is { } origin) Location = new(Location.X + e.X - origin.X, Location.Y + e.Y - origin.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); FinishDrag(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) FinishDrag(); }
    private void FinishDrag()
    {
        if (_drag is null) return;
        _drag = null;
        Capture = false;
        _positionChanged?.Invoke(Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_motionDisposed)
        {
            _motionDisposed = true;
            _source.Changed -= SourceChanged;
            _killFeedTimer.Stop();
            _killFeedTimer.Tick -= KillFeedTimerTick;
            _killFeedTimer.Dispose();
            StopMotion();
            _motionTimer.Tick -= MotionTimerTick;
            _motionTimer.Dispose();
            _preloadCancellation.Cancel();
            _preloadCancellation.Dispose();
            _morphs.Dispose();
            _icons.Dispose();
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
        }
        base.Dispose(disposing);
    }
}
