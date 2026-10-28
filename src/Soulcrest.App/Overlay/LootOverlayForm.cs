using System.Diagnostics;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Independent loot HUD; owns presentation and interaction only, never the tracking source.</summary>
public sealed class LootOverlayForm : Form
{
    private readonly ILootOverlaySource _source;
    private readonly Func<LootOverlayOptions> _options;
    private readonly Func<bool?> _systemAnimationsEnabled;
    private readonly Action<Point>? _positionChanged;
    private readonly RareDropAlertService? _alerts;
    private readonly object _alertPresentationGate = new();
    // Only the UI thread publishes context permission. The capture/session worker reads this flag without inspecting controls.
    private bool _alertContextAllowed;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LootOverlayAnimation _animation = new();
    private readonly LootOverlayIconCache _icons = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly OverlayLockButton _lockButton;
    private LootOverlaySnapshot _snapshot;
    private LootOverlayOptions _current;
    private OverlayThemeDefinition _theme;
    private Point? _drag;
    private bool _locked = true, _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _suppressed, _placementHidden, _foregroundSuppressed, _rendering, _refreshQueued;
    private bool PresentationHidden => _suppressed || _placementHidden || _foregroundSuppressed;
    private bool SessionAllowed => !_current.OnlyDuringSession || _snapshot.SessionActive;

    public LootOverlayForm(ILootOverlaySource source, Func<LootOverlayOptions> options, Action<Point>? positionChanged = null,
        RareDropAlertService? alerts = null)
        : this(source, options, positionChanged, alerts, ReadSystemAnimationsEnabled) { }

    internal LootOverlayForm(ILootOverlaySource source, Func<LootOverlayOptions> options, Action<Point>? positionChanged,
        RareDropAlertService? alerts, Func<bool?> systemAnimationsEnabled)
    {
        _source = source;
        _options = options;
        _systemAnimationsEnabled = systemAnimationsEnabled;
        _positionChanged = positionChanged;
        _alerts = alerts;
        _current = options();
        _snapshot = ApplyMotionPreference(source.Snapshot, _current.AnimationsEnabled, _systemAnimationsEnabled());
        _animation.Observe(_snapshot, _clock.Elapsed.TotalSeconds);
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
        _timer.Tick += TimerTick;
        _source.Changed += SourceChanged;
        if (_alerts is not null) _alerts.Changed += SourceChanged;
    }

    public void ApplySettings()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        _current = _options();
        var theme = OverlayThemeDefinition.Resolve(_current.Theme);
        if (theme.Id != _theme.Id)
        {
            _theme = theme;
            _lockButton.SetTheme(theme);
        }
        NativeMethods.SetCaptureVisibility(Handle, _current.VisibleInRecordings);
        _lockButton.SetVisibleInRecordings(_current.VisibleInRecordings);
        UpdateSnapshot();
        PublishAlertPresentation();
        _alerts?.ApplySettings();
        if (!_current.Enabled || !SessionAllowed || PresentationHidden || SmokeTest.Enabled)
        {
            FinishDrag();
            ResetInteraction();
            Hide();
            _timer.Stop();
            return;
        }
        var size = LootOverlayRenderer.Measure(_snapshot);
        var scale = LootOverlayRenderer.NormalizedScale(_current.Scale);
        Size = new((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        if (_drag is null) Location = new(_current.X, _current.Y);
        if (!Visible) Show();
        _lockButton.RefreshTarget();
        UpdateTimer();
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
        PublishAlertPresentation();
        FinishDrag();
        ResetInteraction();
        _blockAltUntilRelease = true;
        ApplySettings();
    }

    /// <summary>
    /// Makes the first committed receipt eligible after the host has applied auto-show for a new session.
    /// Called from the session worker: no control properties, handles, Invoke or UI waits are used here.
    /// </summary>
    public void PrepareAlertsForSessionStart()
    {
        lock (_alertPresentationGate)
            _alerts?.SetPresentationVisible(Volatile.Read(ref _alertContextAllowed) && AlertPolicyAllows());
    }

    private void PublishAlertPresentation(bool revokeContext = false)
    {
        var allowed = !revokeContext && !PresentationHidden && !SmokeTest.Enabled
            && IsHandleCreated && !IsDisposed && !Disposing;
        lock (_alertPresentationGate)
        {
            Volatile.Write(ref _alertContextAllowed, allowed);
            _alerts?.SetPresentationVisible(allowed && AlertPolicyAllows());
        }
    }

    // Read the policy under the same gate as session-start preparation. A queued UI
    // refresh may still hold old options while the worker has enabled a new session;
    // that old refresh must not clear its already accepted first receipt.
    private bool AlertPolicyAllows()
    {
        var options = _options();
        return options.Enabled && (!options.OnlyDuringSession || _source.Snapshot.SessionActive);
    }

    protected override bool ShowWithoutActivation => true;
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && _current.Enabled && SessionAllowed && !PresentationHidden && !SmokeTest.Enabled);
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
        PublishAlertPresentation(revokeContext: true);
        _timer.Stop();
        _refreshQueued = false;
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _timer.Stop();
            PublishAlertPresentation();
            ResetInteraction();
        }
        else { PublishAlertPresentation(); UpdateTimer(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void SourceChanged()
    {
        if (!IsHandleCreated || IsDisposed || Disposing || _refreshQueued) return;
        _refreshQueued = true;
        try
        {
            BeginInvoke(() =>
            {
                _refreshQueued = false;
                if (!IsDisposed && !Disposing) ApplySettings();
            });
        }
        catch (InvalidOperationException) { _refreshQueued = false; }
    }

    private void UpdateSnapshot()
    {
        _snapshot = ApplyMotionPreference(_source.Snapshot with { RareDrops = _alerts?.CurrentNotices ?? [] },
            _current.AnimationsEnabled, _systemAnimationsEnabled());
        _animation.Observe(_snapshot, _clock.Elapsed.TotalSeconds);
    }

    internal static LootOverlaySnapshot ApplyMotionPreference(LootOverlaySnapshot snapshot, bool appAnimationsEnabled,
        bool? systemAnimationsEnabled) => snapshot with
        { AnimationsEnabled = snapshot.AnimationsEnabled && appAnimationsEnabled && systemAnimationsEnabled == true };

    // Windows' accessibility preference covers client-area flashes, blinking and moving content.
    // https://learn.microsoft.com/windows/win32/winauto/client-area-animation
    // An unavailable preference never opts the user into motion.
    private static bool? ReadSystemAnimationsEnabled() =>
        QuerySystemAnimations(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var enabled, 0) ? enabled : null;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QuerySystemAnimations(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);

    private void UpdateTimer()
    {
        if (!Visible || PresentationHidden || !_current.Enabled || !SessionAllowed) { _timer.Stop(); return; }
        _timer.Interval = _animation.IsAnimating(_snapshot, _clock.Elapsed.TotalSeconds) ? 33 : 1000;
        _timer.Start();
    }

    private void TimerTick(object? sender, EventArgs e)
    {
        if (!Visible || PresentationHidden || !_current.Enabled || !SessionAllowed) { _timer.Stop(); return; }
        var previousSize = LootOverlayRenderer.Measure(_snapshot);
        UpdateSnapshot();
        if (!SessionAllowed) { ApplySettings(); return; }
        var size = LootOverlayRenderer.Measure(_snapshot);
        if (previousSize != size)
        {
            var scale = LootOverlayRenderer.NormalizedScale(_current.Scale);
            Size = new((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        }
        // Static mode still refreshes duration/rates once per second. Hidden overlays have no timer.
        RenderPanel();
        UpdateTimer();
    }

    private void RenderPanel()
    {
        if (_rendering || !Visible || !IsHandleCreated || IsDisposed || PresentationHidden || !SessionAllowed) return;
        _rendering = true;
        try
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            using var image = LootOverlayRenderer.RenderBitmap(_snapshot, _theme.Id, _current.Scale, seconds,
                _animation.CompletionSeconds(seconds), _locked, _icons, _current.BackgroundOpacity);
            LayeredOverlayPresenter.Present(this, image);
        }
        finally { _rendering = false; }
    }

    private Rectangle LockBounds()
    {
        var scale = LootOverlayRenderer.NormalizedScale(_current.Scale);
        var bounds = LootOverlayRenderer.LockBounds;
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
        if (message.Msg == 0x001A /* WM_SETTINGCHANGE */) ApplySettings();
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
        if (disposing)
        {
            PublishAlertPresentation(revokeContext: true);
            _source.Changed -= SourceChanged;
            if (_alerts is not null) _alerts.Changed -= SourceChanged;
            _timer.Stop();
            _timer.Tick -= TimerTick;
            _timer.Dispose();
            _icons.Dispose();
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
        }
        base.Dispose(disposing);
    }
}
