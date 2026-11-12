using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Independent passive crafting HUD. Hiding never changes confirmed progress or event deadlines.</summary>
public sealed class CraftingProgressOverlayForm : Form
{
    private readonly Func<CraftingProgressSnapshot> _read;
    private readonly Action _tick;
    private readonly SettingsService _settings;
    private readonly CraftingProgressService? _tracker;
    private readonly OverlayLockButton _lockButton;
    private readonly LootOverlayIconCache _icons = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 33 };
    private CraftingProgressSnapshot _snapshot = CraftingProgressSnapshot.Empty;
    private OverlayThemeDefinition _theme;
    private bool _suppressed, _placementHidden, _foregroundSuppressed, _showRequested;
    private bool _locked = true, _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _refreshing, _rendering, _ticking;
    private bool? _inRecordings;
    private int _refreshQueued;
    private double _scale;
    private Point? _dragStart;
    private Corner _resizing;
    private Point _resizeFrom;
    private Rectangle _resizeBounds;
    private double _resizeScale;
    [Flags] private enum Corner { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private bool PresentationHidden => _placementHidden || _foregroundSuppressed;

    public CraftingProgressOverlayForm(CraftingProgressService tracker, SettingsService settings)
        : this(() => tracker.Snapshot, tracker.Tick, settings)
    {
        _tracker = tracker;
        tracker.Changed += QueueRefresh;
    }

    internal CraftingProgressOverlayForm(Func<CraftingProgressSnapshot> read, Action tick, SettingsService settings)
    {
        _read = read; _tick = tick; _settings = settings;
        _theme = OverlayThemeDefinition.Resolve(settings.Current.OverlayTheme);
        _scale = CraftingProgressOverlayRenderer.NormalizeScale(settings.Current.CraftingProgressOverlayScale);
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(_theme.Surface.R, _theme.Surface.G, _theme.Surface.B);
        DoubleBuffered = true;
        LayeredOverlayPresenter.Configure(this, true, .94);
        _lockButton = new(this, LockBounds);
        _lockButton.SetTheme(_theme);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(settings.Current.OverlaysInRecordings);
        _settings.Changed += QueueRefresh;
        _timer.Tick += RefreshTick;
        _animationTimer.Tick += RefreshTick;
        _timer.Start();
        _tick();
        ApplySettings();
    }

    public bool Locked => _locked;
    internal bool ManualInteractionEnabled => _manualInteraction;
        internal bool AnimationRunning => _animationTimer.Enabled;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            if (_locked) parameters.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            else parameters.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
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
        _dragStart = null; _resizing = Corner.None; _manualInteraction = false;
        SetInteractionEnabled(false);
        _blockAltUntilRelease = true;
        if (IsHandleCreated && Capture) Capture = false;
        ApplySettings();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = (_manualInteraction || enabled && !_blockAltUntilRelease) && !_suppressed && !PresentationHidden;
        if (_lockButton.Locked != !enabled) _lockButton.SetLocked(!enabled);
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) FinishDrag();
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, _locked);
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        RenderPanel();
    }
    internal void ToggleManualInteraction() => SetManualInteraction(_locked);
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);
    private void SetManualInteraction(bool enabled)
    {
        _manualInteraction = enabled && !_suppressed && !PresentationHidden;
        var alt = _altInteractionRequested || OverlayInteraction.IsAltHeld;
        _blockAltUntilRelease = !enabled && alt;
        SetInteractionEnabled(alt);
    }
    private bool InteractionAllowed => !_suppressed && !PresentationHidden
        && (_manualInteraction || !_blockAltUntilRelease && OverlayInteraction.IsAltHeld);

    protected override void SetVisibleCore(bool value)
    {
        if (value) _showRequested = true;
        base.SetVisibleCore(value && _settings is not null && _settings.Current.CraftingProgressOverlayEnabled
            && !_suppressed && !PresentationHidden && _snapshot.HasContent);
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (IsDisposed || Disposing) return;
        if (!Visible)
        {
            _manualInteraction = false;
            _animationTimer.Stop();
            if (_lockButton is not null) SetInteractionEnabled(false);
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inRecordings = null;
        ApplyCaptureVisibility();
        LayeredOverlayPresenter.Configure(this, true, .94);
        NativeMethods.SetClickThrough(Handle, _locked);
        RenderPanel();
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }

    private void QueueRefresh()
    {
        if (_ticking || !IsHandleCreated || IsDisposed || Disposing || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        try
        {
            BeginInvoke(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!IsDisposed && !Disposing) ApplySettings(); });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _refreshQueued, 0); }
    }
    private void RefreshTick(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || _ticking) return;
        _ticking = true;
        try { _tick(); }
        finally { _ticking = false; }
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (_refreshing || IsDisposed || Disposing) return;
        _refreshing = true;
        try
        {
            var next = _read();
            _snapshot = next;
            var theme = OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme);
            if (theme != _theme) { _theme = theme; _lockButton.SetTheme(theme); }
            if (_dragStart is null && _resizing == Corner.None)
            {
                _scale = CraftingProgressOverlayRenderer.NormalizeScale(_settings.Current.CraftingProgressOverlayScale);
                ApplySize();
                var working = WorkingArea();
                Location = new(_settings.Current.CraftingProgressOverlayX ?? working.Right - Width - 24,
                    _settings.Current.CraftingProgressOverlayY ?? working.Top + 220);
            }
            ApplyCaptureVisibility();
            if (IsHandleCreated || _showRequested) SetVisibleCore(_settings.Current.CraftingProgressOverlayEnabled);
            _animationTimer.Enabled = Visible && !_suppressed && !PresentationHidden && AnimationsEnabled
                && (_snapshot.Rows.Any(row => row.EventAgeSeconds < 1) || _snapshot.IsCompletionVisible && _snapshot.CompletionAgeSeconds < 1);
            RenderPanel();
        }
        finally { _refreshing = false; }
    }
    private Rectangle WorkingArea() => _settings.Current.CraftingProgressOverlayX is { } x && _settings.Current.CraftingProgressOverlayY is { } y
        ? Screen.FromPoint(new(x, y)).WorkingArea : (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
    private void ApplySize() => Size = new((int)Math.Round(CraftingProgressOverlayRenderer.BaseWidth * _scale),
        (int)Math.Round(CraftingProgressOverlayRenderer.HeightFor(_snapshot.Rows.Length) * _scale));
    private Rectangle LockBounds() => Rectangle.Round(new RectangleF((float)(328 * _scale), (float)(8 * _scale), (float)(18 * _scale), (float)(18 * _scale)));
    private void ApplyCaptureVisibility()
    {
        var visible = _settings.Current.OverlaysInRecordings;
        if (!IsHandleCreated || _inRecordings == visible) return;
        NativeMethods.SetCaptureVisibility(Handle, visible);
        _lockButton.SetVisibleInRecordings(visible);
        _inRecordings = visible;
    }
    internal void RenderPanel()
    {
        if (_rendering || !IsHandleCreated || IsDisposed || Disposing || _suppressed || PresentationHidden || !_snapshot.HasContent) return;
        _rendering = true;
        try
        {
            using var image = CraftingProgressOverlayRenderer.Render(_snapshot, _theme, _scale,
                _settings.Current.LootNameLanguage, AnimationsEnabled, _icons);
            LayeredOverlayPresenter.Present(this, image);
            _lockButton.RefreshTarget();
        }
        finally { _rendering = false; }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private bool AnimationsEnabled => !_settings.Current.CraftingProgressOverlayReducedMotion
        && _settings.Current.LootDropAnimationsEnabled && ReadSystemAnimationsEnabled();
    private static bool ReadSystemAnimationsEnabled() => QuerySystemAnimations(0x1042, 0, out var enabled, 0) && enabled;
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QuerySystemAnimations(uint action, uint parameter,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool enabled, uint flags);

    private Corner CornerAt(Point point)
    {
        var corner = Corner.None;
        if (point.X < 16) corner |= Corner.Left; else if (point.X >= Width - 16) corner |= Corner.Right;
        if (point.Y < 16) corner |= Corner.Top; else if (point.Y >= Height - 16) corner |= Corner.Bottom;
        return (corner & (Corner.Left | Corner.Right)) != 0 && (corner & (Corner.Top | Corner.Bottom)) != 0 ? corner : Corner.None;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed || _locked || e.Button != MouseButtons.Left) return;
        _resizing = CornerAt(e.Location);
        if (_resizing == Corner.None) _dragStart = e.Location;
        else { _resizeFrom = Cursor.Position; _resizeBounds = Bounds; _resizeScale = _scale; }
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_resizing != Corner.None)
        {
            var dx = Cursor.Position.X - _resizeFrom.X;
            var dy = Cursor.Position.Y - _resizeFrom.Y;
            if (_resizing.HasFlag(Corner.Left)) dx = -dx;
            if (_resizing.HasFlag(Corner.Top)) dy = -dy;
            var byWidth = (_resizeBounds.Width + dx) / (double)_resizeBounds.Width;
            var byHeight = (_resizeBounds.Height + dy) / (double)_resizeBounds.Height;
            _scale = CraftingProgressOverlayRenderer.NormalizeScale(_resizeScale * (Math.Abs(byWidth - 1) >= Math.Abs(byHeight - 1) ? byWidth : byHeight));
            ApplySize();
            Location = new(_resizing.HasFlag(Corner.Left) ? _resizeBounds.Right - Width : _resizeBounds.Left,
                _resizing.HasFlag(Corner.Top) ? _resizeBounds.Bottom - Height : _resizeBounds.Top);
            RenderPanel();
        }
        else if (_dragStart is { } start) Location = new(Left + e.X - start.X, Top + e.Y - start.Y);
        else if (!_locked) Cursor = CornerAt(e.Location) switch
        {
            Corner.Top | Corner.Left or Corner.Bottom | Corner.Right => Cursors.SizeNWSE,
            Corner.Top | Corner.Right or Corner.Bottom | Corner.Left => Cursors.SizeNESW, _ => Cursors.SizeAll,
        };
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); FinishDrag(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) FinishDrag(); }
    private void FinishDrag()
    {
        if (_dragStart is null && _resizing == Corner.None) return;
        _dragStart = null; _resizing = Corner.None;
        if (Capture) Capture = false;
        _settings.Update(settings =>
        {
            settings.CraftingProgressOverlayX = Left; settings.CraftingProgressOverlayY = Top;
            settings.CraftingProgressOverlayScale = Math.Round(_scale, 3);
        });
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            if (_tracker is not null) _tracker.Changed -= QueueRefresh;
            _settings.Changed -= QueueRefresh;
            _timer.Stop(); _timer.Dispose(); _animationTimer.Stop(); _animationTimer.Dispose();
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose(); _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
