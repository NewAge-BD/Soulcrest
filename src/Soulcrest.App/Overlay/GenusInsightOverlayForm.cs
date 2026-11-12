using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Passive, freely placed Genus sidebar. Saved rolls never grant authority to this live window.</summary>
public sealed class GenusInsightOverlayForm : Form
{
    private readonly Func<GenusInsightSnapshot> _read;
    private readonly Func<IDisposable> _observe;
    private readonly SettingsService _settings;
    private readonly GenusInsightStateService? _state;
    private readonly OverlayLockButton _lockButton;
    private readonly LootOverlayIconCache _icons = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private GenusInsightSnapshot _snapshot = GenusInsightSnapshot.Empty;
    private OverlayThemeDefinition _theme;
    private IDisposable? _lease;
    private bool _started, _suppressed, _placementHidden, _foregroundSuppressed, _presentationAllowed;
    private bool _locked = true, _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _refreshing, _rendering, _disposed;
    private bool? _inRecordings;
    private int _refreshQueued;
    private long _sourceRevision, _presentedRevision;
    private double _scale;
    private Point? _dragStart;
    private Corner _resizing;
    private Point _resizeFrom;
    private Rectangle _resizeBounds;
    private double _resizeScale;
    [Flags] private enum Corner { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private bool PresentationHidden => _suppressed || _placementHidden || _foregroundSuppressed;
    private bool Eligible => _started && !_disposed && _settings.Current.GenusInsightOverlayEnabled && !PresentationHidden;

    public GenusInsightOverlayForm(GenusInsightStateService state, SettingsService settings)
        : this(() => state.Snapshot, state.ObserveMenu, settings)
    {
        _state = state;
        state.Changed += QueueRefresh;
    }

    internal GenusInsightOverlayForm(Func<GenusInsightSnapshot> read, Func<IDisposable> observe, SettingsService settings)
    {
        _read = read; _observe = observe; _settings = settings;
        _theme = OverlayThemeDefinition.Resolve(settings.Current.OverlayTheme);
        _scale = GenusInsightOverlayRenderer.NormalizeScale(settings.Current.GenusInsightOverlayScale);
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Soulcrest Genus Insight";
        DoubleBuffered = true;
        LayeredOverlayPresenter.Configure(this, true, .94);
        _lockButton = new(this, LockBounds);
        _lockButton.SetTheme(_theme);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(settings.Current.OverlaysInRecordings);
        _settings.Changed += QueueRefresh;
        _timer.Tick += RefreshTick;
        ApplyGeometry();
    }

    public bool Locked => _locked;
    internal bool ManualInteractionEnabled => _manualInteraction;
    internal bool RuntimeAvailable => Eligible && GenusInsightOverlayContent.Available(_read());
    public void Start()
    {
        if (_started || _disposed || IsDisposed) return;
        _started = true;
        _ = Handle;
        _timer.Start();
        ApplySettings();
    }
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
        _suppressed = suppressed; ApplyPresentationVisibility();
    }
    public void SetPlacementHidden(bool hidden)
    {
        if (_placementHidden == hidden) return;
        _placementHidden = hidden; ApplyPresentationVisibility();
    }
    public void SetForegroundSuppressed(bool suppressed)
    {
        if (_foregroundSuppressed == suppressed) return;
        _foregroundSuppressed = suppressed; ApplyPresentationVisibility();
    }
    private void ApplyPresentationVisibility()
    {
        HideFrame();
        _blockAltUntilRelease = true;
        ApplySettings();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = Eligible && _presentationAllowed && (_manualInteraction || enabled && !_blockAltUntilRelease);
        if (_lockButton.Locked != !enabled) _lockButton.SetLocked(!enabled);
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) FinishDrag();
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, _locked);
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
    }
    internal void ToggleManualInteraction() => SetManualInteraction(_locked);
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);
    private void SetManualInteraction(bool enabled)
    {
        _manualInteraction = enabled && Eligible && _presentationAllowed;
        var alt = _altInteractionRequested || OverlayInteraction.IsAltHeld;
        _blockAltUntilRelease = !enabled && alt;
        SetInteractionEnabled(alt);
    }
    private bool InteractionAllowed => Eligible && _presentationAllowed
        && (_manualInteraction || !_blockAltUntilRelease && OverlayInteraction.IsAltHeld);

    protected override void SetVisibleCore(bool value)
    {
        var allowed = value && Eligible && _presentationAllowed
            && _presentedRevision == Volatile.Read(ref _sourceRevision) && SameFrame(_snapshot, _read());
        base.SetVisibleCore(allowed);
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible && !_disposed)
        {
            CancelGesture();
            _manualInteraction = false;
            if (_lockButton is not null) SetInteractionEnabled(false);
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inRecordings = null;
        LayeredOverlayPresenter.Configure(this, true, .94);
        NativeMethods.SetClickThrough(Handle, _locked);
        ApplyCaptureVisibility();
        ApplySettings();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _presentationAllowed = false;
        ReleaseLease();
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }
    private void QueueRefresh()
    {
        Interlocked.Increment(ref _sourceRevision);
        if (!_started || !IsHandleCreated || _disposed || IsDisposed || Disposing
            || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        try
        {
            BeginInvoke(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_disposed && !IsDisposed) ApplySettings(); });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _refreshQueued, 0); }
    }
    private void RefreshTick(object? sender, EventArgs e) => ApplySettings();

    public void ApplySettings()
    {
        if (_disposed || IsDisposed || Disposing) return;
        if (InvokeRequired) { QueueRefresh(); return; }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (Eligible && IsHandleCreated) _lease ??= _observe();
            else ReleaseLease();
            var theme = OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme);
            if (theme != _theme) { _theme = theme; _lockButton.SetTheme(theme); }
            if (_dragStart is null && _resizing == Corner.None) ApplyGeometry();
            ApplyCaptureVisibility();
            if (!Eligible || !IsHandleCreated) { HideFrame(); return; }
            RenderPanel();
        }
        finally { _refreshing = false; }
    }
    private void ApplyGeometry()
    {
        var layout = OverlayRegistry.ReadLayout(_settings.Current, "genus-insight");
        _scale = GenusInsightOverlayRenderer.NormalizeScale(layout.Scale);
        ApplySize();
        Location = new(layout.X, layout.Y);
    }
    private void ApplySize() => Size = new((int)Math.Round(GenusInsightOverlayRenderer.BaseWidth * _scale),
        (int)Math.Round(GenusInsightOverlayRenderer.BaseHeight * _scale));
    private Rectangle LockBounds() => Rectangle.Round(new RectangleF((float)(305 * _scale), (float)(47 * _scale),
        (float)(18 * _scale), (float)(18 * _scale)));
    private void ApplyCaptureVisibility()
    {
        var visible = _settings.Current.OverlaysInRecordings;
        if (!IsHandleCreated || _inRecordings == visible) return;
        NativeMethods.SetCaptureVisibility(Handle, visible);
        _lockButton.SetVisibleInRecordings(visible);
        _inRecordings = visible;
    }
    private void ReleaseLease()
    {
        var lease = _lease; _lease = null;
        lease?.Dispose();
    }
    private void HideFrame()
    {
        _presentationAllowed = false;
        CancelGesture();
        _manualInteraction = false;
        SetInteractionEnabled(false);
        if (Visible) base.SetVisibleCore(false);
        _lockButton.RefreshTarget();
    }
    internal void RenderPanel()
    {
        if (_rendering || _disposed || !IsHandleCreated || IsDisposed || Disposing) return;
        _rendering = true;
        try
        {
            if (!Eligible) { HideFrame(); return; }
            var revision = Volatile.Read(ref _sourceRevision);
            var snapshot = _read();
            if (!GenusInsightOverlayContent.Available(snapshot)) { HideFrame(); return; }
            using var image = GenusInsightOverlayRenderer.Render(snapshot, _theme, _scale, _settings.Current.LootNameLanguage, _icons);
            if (!StillCurrent(snapshot, revision)) { HideFrame(); return; }
            if (!LayeredOverlayPresenter.Present(this, image)) { HideFrame(); return; }
            if (!StillCurrent(snapshot, revision)) { HideFrame(); return; }
            _snapshot = snapshot; _presentedRevision = revision; _presentationAllowed = true;
            if (!Visible) Show();
            if (!StillCurrent(snapshot, revision)) { HideFrame(); return; }
            _lockButton.RefreshTarget();
        }
        finally { _rendering = false; }
    }
    private bool StillCurrent(GenusInsightSnapshot snapshot, long revision) => Eligible
        && revision == Volatile.Read(ref _sourceRevision) && SameFrame(snapshot, _read());
    private static bool SameFrame(GenusInsightSnapshot expected, GenusInsightSnapshot current) =>
        GenusInsightOverlayContent.Available(current) && expected.ProfileId == current.ProfileId
        && expected.Generation == current.Generation && expected.Sequence == current.Sequence
        && expected.MenuSourceGeneration == current.MenuSourceGeneration && expected.SelectedGenus == current.SelectedGenus
        && expected.ViewedPreset == current.ViewedPreset && expected.ObservedAt == current.ObservedAt
        && expected.ViewedBoard?.Genus == current.ViewedBoard?.Genus && expected.ViewedBoard?.Level == current.ViewedBoard?.Level
        && expected.ViewedBoard is { } first && current.ViewedBoard is { } second && first.Slots.SequenceEqual(second.Slots);
    protected override void OnPaintBackground(PaintEventArgs e) { }

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
            _scale = GenusInsightOverlayRenderer.NormalizeScale(_resizeScale * (Math.Abs(byWidth - 1) >= Math.Abs(byHeight - 1) ? byWidth : byHeight));
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
    private void CancelGesture()
    {
        _dragStart = null; _resizing = Corner.None;
        if (IsHandleCreated && Capture) Capture = false;
    }
    private void FinishDrag()
    {
        if (_dragStart is null && _resizing == Corner.None) return;
        CancelGesture();
        _settings.Update(settings =>
        {
            settings.GenusInsightOverlayX = Left; settings.GenusInsightOverlayY = Top;
            settings.GenusInsightOverlayScale = Math.Round(_scale, 3);
        });
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _started = false;
            if (_state is not null) _state.Changed -= QueueRefresh;
            _settings.Changed -= QueueRefresh;
            CancelGesture();
            _timer.Stop(); _timer.Dispose();
            ReleaseLease();
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose(); _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
