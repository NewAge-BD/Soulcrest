using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using NativeOverlayLayout = Soulcrest.App.Services.OverlayLayout;

namespace Soulcrest.App.Overlay;

internal interface IOverlayEditorLiveSurface : IDisposable
{
    bool Visible { get; }
    event Action? GeometryInvalidated;
    void HideFrame();
    void ShowFrame(Bitmap image, Rectangle bounds, bool visibleInRecordings);
    void ConfigureInteraction(OverlayInstance window, Size logicalSize, OverlayMonitor monitor, string themeId, double? displayedScale = null) { }
    void CompletePlacement(string? error) { }
    event Action<OverlayLivePlacement>? PlacementCompleted { add { } remove { } }
}

internal sealed record OverlayLivePlacement(Rectangle Bounds, double Scale, string? MonitorId, OverlaySettings StartedSettings);

/// <summary>Own layered surface with a separate mandatory lock hotspot and explicit, temporary mouse editing.</summary>
internal sealed class OverlayEditorLiveWindow : Form, IOverlayEditorLiveSurface
{
    private readonly OverlayCaptureVisibility _captureVisibility = new();
    private readonly OverlayLockButton _lockButton;
    private readonly System.Windows.Forms.Timer _inputTimer = new() { Interval = 100 };
    private OverlayInstance? _window;
    private OverlayMonitor? _monitor;
    private Size _logicalSize;
    private double _scale = 1;
    private Bitmap? _frame;
    private Rectangle _committedBounds;
    private bool _presentationAllowed, _locked = true, _manualInteraction, _blockAltUntilRelease;
    private bool _savingPlacement, _blocked, _endingGesture, _recordings;
    private Gesture? _gesture;
    private sealed record Gesture(Point Pointer, Rectangle Bounds, double Scale, OverlaySettings Settings,
        OverlayMonitor Monitor, OverlayPlacementGrip Grip);
    public event Action? GeometryInvalidated;
    public event Action<OverlayLivePlacement>? PlacementCompleted;

    internal OverlayEditorLiveWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Soulcrest Overlay";
        LayeredOverlayPresenter.Configure(this, true, 1);
        _lockButton = new(this, LockBounds);
        _lockButton.Toggled += ToggleManualInteraction;
        _inputTimer.Tick += (_, _) => RefreshInteraction();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            if (_locked) parameters.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            else parameters.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && _presentationAllowed);

    public void HideFrame()
    {
        _presentationAllowed = false;
        CancelGesture();
        _manualInteraction = false;
        SetLocked(true);
        _inputTimer.Stop();
        if (!IsDisposed) Hide();
    }

    public void ConfigureInteraction(OverlayInstance window, Size logicalSize, OverlayMonitor monitor, string themeId,
        double? displayedScale = null)
    {
        if (IsDisposed || Disposing) return;
        var changed = !ReferenceEquals(_window?.Settings, window.Settings);
        if (changed && _gesture is not null)
        {
            CancelGesture();
            _manualInteraction = false;
            SetLocked(true);
        }
        if (changed && !_savingPlacement) { _blocked = false; _lockButton.SetError(null); }
        _window = window; _logicalSize = logicalSize; _monitor = monitor;
        if (_gesture is null && (!_savingPlacement || changed))
        {
            _scale = displayedScale is { } scale && double.IsFinite(scale) ? Math.Clamp(scale, .5, 2) : window.Settings.Scale;
            if (_savingPlacement && changed)
            {
                // An acknowledged save is authoritative before its replacement bitmap arrives.
                var committed = OverlayEditorPlacementGeometry.Read(window.Settings, logicalSize, monitor);
                committed = OverlayPlacementForm.FitLayout(committed, logicalSize, monitor.WorkingArea, .5);
                _scale = committed.Scale;
                Bounds = new(new Point(committed.X, committed.Y), OverlayEditorPlacementGeometry.ScaledSize(logicalSize, _scale));
                _committedBounds = Bounds;
                PresentFrame();
            }
        }
        _lockButton.SetTheme(OverlayThemeDefinition.Resolve(themeId));
        _lockButton.RefreshTarget();
    }

    public void ShowFrame(Bitmap image, Rectangle bounds, bool visibleInRecordings)
    {
        if (IsDisposed || Disposing) return;
        _ = Handle;
        LayeredOverlayPresenter.Configure(this, true, 1);
        _captureVisibility.Apply(Handle, visibleInRecordings);
        if (_recordings != visibleInRecordings) { _recordings = visibleInRecordings; _lockButton.SetVisibleInRecordings(visibleInRecordings); }
        if (_gesture is null && !_savingPlacement) { Bounds = bounds; _committedBounds = bounds; }
        var previous = _frame;
        _frame = new Bitmap(image);
        previous?.Dispose();
        _presentationAllowed = true;
        PresentFrame();
        if (!Visible) Show();
        _lockButton.RefreshTarget();
        if (!_inputTimer.Enabled) _inputTimer.Start();
    }

    private void PresentFrame()
    {
        if (!_presentationAllowed || _frame is null || IsDisposed || Disposing || !IsHandleCreated) return;
        if (_frame.Size == Bounds.Size && _locked && !_blocked)
        {
            if (!LayeredOverlayPresenter.Present(this, _frame)) throw new InvalidOperationException("Das Overlay konnte nicht angezeigt werden.");
            return;
        }
        using var scaled = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(_frame, new Rectangle(Point.Empty, scaled.Size));
            graphics.CompositingMode = CompositingMode.SourceOver;
            if (!_locked)
            {
                // Alpha 1 makes empty canvas pixels receive input only while explicitly unlocked.
                using var hitFill = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
                graphics.FillRectangle(hitFill, new Rectangle(Point.Empty, scaled.Size));
            }
            if (!_locked || _blocked)
            {
                var color = _blocked ? Color.OrangeRed : Color.FromArgb(237, 201, 111);
                using var edge = new Pen(color, 1);
                graphics.DrawRectangle(edge, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                if (!_locked)
                {
                    using var grip = new SolidBrush(color);
                    foreach (var point in new[] { new Point(0, 0), new Point(Width - 6, 0), new Point(0, Height - 6), new Point(Width - 6, Height - 6) })
                        graphics.FillRectangle(grip, point.X, point.Y, 6, 6);
                }
            }
        }
        if (!LayeredOverlayPresenter.Present(this, scaled)) throw new InvalidOperationException("Das Overlay konnte nicht angezeigt werden.");
    }

    private Rectangle LockBounds()
    {
        if (_window is null || _logicalSize.Width <= 0 || _logicalSize.Height <= 0) return Rectangle.Empty;
        var bounds = OverlayLockWidget.Bounds(_window.Settings);
        var scaleX = ClientSize.Width / (double)_logicalSize.Width;
        var scaleY = ClientSize.Height / (double)_logicalSize.Height;
        return Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB((int)Math.Floor(bounds.Left * scaleX),
            (int)Math.Floor(bounds.Top * scaleY), (int)Math.Ceiling(bounds.Right * scaleX), (int)Math.Ceiling(bounds.Bottom * scaleY)));
    }

    private void ToggleManualInteraction()
    {
        if (_blocked || _savingPlacement || !_presentationAllowed || !Visible) return;
        _manualInteraction = _locked;
        _blockAltUntilRelease = !_manualInteraction && OverlayInteraction.IsAltHeld;
        RefreshInteraction();
    }

    private void RefreshInteraction()
    {
        var alt = OverlayInteraction.IsAltHeld;
        if (!alt) _blockAltUntilRelease = false;
        SetLocked(!_presentationAllowed || !Visible || _blocked || _savingPlacement || !(_manualInteraction || alt && !_blockAltUntilRelease));
    }

    private void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        _lockButton.SetLocked(locked);
        if (locked) CancelGesture();
        Cursor = locked ? Cursors.Default : Cursors.SizeAll;
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, locked);
        PresentFrame();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { CancelGesture(); return; }
        if (e.Button != MouseButtons.Left || _locked || _blocked || _savingPlacement || _window is null || _monitor is null || !Visible) return;
        _gesture = new(PointToScreen(e.Location), Bounds, _scale, _window.Settings, _monitor,
            OverlayPlacementForm.GripAt(ClientRectangle, e.Location, false));
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_locked || _blocked || _savingPlacement) return;
        if (_gesture is not { } gesture)
        {
            var grip = OverlayPlacementForm.GripAt(ClientRectangle, e.Location, false);
            Cursor = grip is OverlayPlacementGrip.Left or OverlayPlacementGrip.Right or OverlayPlacementGrip.Top or OverlayPlacementGrip.Bottom
                ? Cursors.SizeAll : grip == OverlayPlacementGrip.None ? Cursors.SizeAll
                : (grip == (OverlayPlacementGrip.Left | OverlayPlacementGrip.Top) || grip == (OverlayPlacementGrip.Right | OverlayPlacementGrip.Bottom)) ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return;
        }
        var pointer = PointToScreen(e.Location);
        var delta = new Point(pointer.X - gesture.Pointer.X, pointer.Y - gesture.Pointer.Y);
        var original = new NativeOverlayLayout(gesture.Bounds.X, gesture.Bounds.Y, gesture.Scale);
        var layout = gesture.Grip == OverlayPlacementGrip.None
            ? OverlayPlacementForm.DragLayout(original, delta, gesture.Monitor.Bounds, false)
            : OverlayPlacementForm.ResizeLayout(original, gesture.Bounds, delta, gesture.Grip,
                new OverlayDescriptor("kit-live", "Overlay", true, .5, 2), gesture.Monitor.Bounds, false);
        if (gesture.Grip != OverlayPlacementGrip.None)
            layout = OverlayPlacementForm.FitLayout(layout, _logicalSize, gesture.Monitor.WorkingArea, .5);
        _scale = layout.Scale;
        Bounds = new(new Point(layout.X, layout.Y), OverlayEditorPlacementGeometry.ScaledSize(_logicalSize, _scale));
        PresentFrame();
        _lockButton.RefreshTarget();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _gesture is not { } gesture || _endingGesture) return;
        _endingGesture = true;
        try
        {
            _gesture = null;
            Capture = false;
            if (Bounds == gesture.Bounds) return;
            _savingPlacement = true;
            SetLocked(true);
            PlacementCompleted?.Invoke(new(Bounds, _scale, gesture.Monitor.Id, gesture.Settings));
        }
        finally { _endingGesture = false; }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && !_endingGesture) CancelGesture();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && _gesture is not null) { CancelGesture(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void CancelGesture()
    {
        if (_gesture is not { } gesture) return;
        _gesture = null; _endingGesture = true;
        try { Capture = false; Bounds = gesture.Bounds; _scale = gesture.Scale; }
        finally { _endingGesture = false; }
        PresentFrame();
    }

    public void CompletePlacement(string? error)
    {
        if (IsDisposed || Disposing) return;
        _savingPlacement = false;
        _blocked = error is not null;
        _lockButton.SetError(error);
        if (error is not null)
        {
            _manualInteraction = false;
            if (!_committedBounds.IsEmpty) Bounds = _committedBounds;
            SetLocked(true);
        }
        else _committedBounds = Bounds;
        RefreshInteraction();
        PresentFrame();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        _manualInteraction = false;
        CancelGesture();
        SetLocked(true);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _captureVisibility.Reset();
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
        if (_gesture is not null || _savingPlacement)
        {
            // Physical gesture bounds stay authoritative across monitors; commit resolves the new monitor once.
            _lockButton.RefreshTarget();
            return;
        }
        HideFrame();
        GeometryInvalidated?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _presentationAllowed = false;
            CancelGesture();
            _inputTimer.Stop(); _inputTimer.Dispose();
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
            _frame?.Dispose(); _frame = null;
        }
        base.Dispose(disposing);
    }
}
