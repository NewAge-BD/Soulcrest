using System.Drawing.Drawing2D;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// A small, separate mouse surface above an overlay. It remains clickable while its target is
/// click-through, and follows the target's position, visibility, handle and disposal lifecycle.
/// </summary>
internal sealed class OverlayLockButton : Form
{
    private const int LogicalSize = 28, LogicalInset = 8;
    private readonly Form _target;
    private readonly Func<Rectangle>? _boundsProvider;
    private readonly ToolTip _tooltip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000, ShowAlways = true };
    private bool _locked = true, _hover, _pressed, _targetHandleAvailable, _visibleInRecordings;

    /// <param name="boundsProvider">Optional bounds in physical pixels relative to the target's client area.</param>
    internal OverlayLockButton(Form target, Func<Rectangle>? boundsProvider = null)
    {
        _target = target;
        _boundsProvider = boundsProvider;
        _targetHandleAvailable = target.IsHandleCreated;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(20, 29, 40);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.StandardDoubleClick, false);

        _target.LocationChanged += TargetChanged;
        _target.SizeChanged += TargetChanged;
        _target.VisibleChanged += TargetChanged;
        _target.HandleCreated += TargetHandleCreated;
        _target.HandleDestroyed += TargetHandleDestroyed;
        _target.DpiChanged += TargetDpiChanged;
        _target.Disposed += TargetDisposed;
        RefreshTarget();
    }

    internal bool Locked => _locked;
    internal event Action? Toggled;

    internal void SetLocked(bool locked)
    {
        if (IsDisposed) return;
        _locked = locked;
        _tooltip.SetToolTip(this, TooltipText(locked));
        Invalidate();
        RefreshTarget();
    }

    internal void SetVisibleInRecordings(bool visible)
    {
        _visibleInRecordings = visible;
        if (IsHandleCreated && !IsDisposed)
            NativeMethods.SetCaptureVisibility(Handle, visible);
    }

    internal static string TooltipText(bool locked) => UiText.T(locked ? "Overlay entsperren" : "Overlay sperren");

    /// <summary>Repositions the button and shows it only while the target is visible and usable.</summary>
    internal void RefreshTarget()
    {
        if (IsDisposed || Disposing) return;
        if (_target.IsDisposed || !_targetHandleAvailable || !_target.IsHandleCreated || !_target.Visible)
        {
            CancelPress();
            Hide();
            return;
        }

        var size = Math.Max(1, (int)Math.Round(LogicalSize * _target.DeviceDpi / 96d));
        var inset = Math.Max(0, (int)Math.Round(LogicalInset * _target.DeviceDpi / 96d));
        var relative = _boundsProvider?.Invoke()
            ?? new Rectangle(Math.Max(0, _target.ClientSize.Width - size - inset), inset, size, size);
        if (relative.Width <= 0 || relative.Height <= 0)
        {
            CancelPress();
            Hide();
            return;
        }

        Bounds = new Rectangle(_target.PointToScreen(relative.Location), relative.Size);
        _tooltip.SetToolTip(this, TooltipText(_locked));
        if (Owner != _target) Owner = _target;
        if (!Visible) Show(_target);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            parameters.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _visibleInRecordings);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            message.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var outline = Rounded(new RectangleF(0, 0, Width, Height), 6f * Math.Min(Width, Height) / LogicalSize);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(ClientSize.Width / (float)LogicalSize, ClientSize.Height / (float)LogicalSize);
        var surface = _pressed ? Color.FromArgb(55, 45, 77)
            : _hover ? Color.FromArgb(39, 48, 64) : BackColor;
        using var fill = new SolidBrush(surface);
        using var outline = Rounded(new RectangleF(.5f, .5f, LogicalSize - 1, LogicalSize - 1), 6);
        graphics.FillPath(fill, outline);
        using var border = new Pen(_locked ? Color.FromArgb(68, 83, 103) : Color.FromArgb(167, 139, 250));
        graphics.DrawPath(border, outline);
        using var ink = new Pen(_locked ? Color.FromArgb(200, 211, 226) : Color.FromArgb(196, 177, 255), 1.7f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        graphics.DrawRectangle(ink, 8, 12, 12, 10);
        if (_locked)
        {
            graphics.DrawArc(ink, 10, 4, 8, 12, 180, 180);
            graphics.DrawLine(ink, 10, 10, 10, 12);
            graphics.DrawLine(ink, 18, 10, 18, 12);
        }
        else
        {
            graphics.DrawArc(ink, 14, 4, 8, 12, 180, 180);
            graphics.DrawLine(ink, 14, 10, 14, 12);
        }
        graphics.DrawLine(ink, 14, 16, 14, 19);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !ClientRectangle.Contains(e.Location) || !Visible) return;
        _pressed = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var toggle = _pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location) && Visible;
        CancelPress();
        if (toggle) Toggled?.Invoke();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) CancelPress();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) CancelPress();
    }

    private void CancelPress()
    {
        _pressed = false;
        if (Capture) Capture = false;
        Invalidate();
    }

    private void TargetChanged(object? sender, EventArgs e) => RefreshTarget();
    private void TargetDpiChanged(object? sender, DpiChangedEventArgs e) => RefreshTarget();
    private void TargetHandleCreated(object? sender, EventArgs e)
    {
        _targetHandleAvailable = true;
        RefreshTarget();
    }

    private void TargetHandleDestroyed(object? sender, EventArgs e)
    {
        _targetHandleAvailable = false;
        CancelPress();
        Hide();
        Owner = null;
    }

    private void TargetDisposed(object? sender, EventArgs e) => Dispose();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _target.LocationChanged -= TargetChanged;
            _target.SizeChanged -= TargetChanged;
            _target.VisibleChanged -= TargetChanged;
            _target.HandleCreated -= TargetHandleCreated;
            _target.HandleDestroyed -= TargetHandleDestroyed;
            _target.DpiChanged -= TargetDpiChanged;
            _target.Disposed -= TargetDisposed;
            CancelPress();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
