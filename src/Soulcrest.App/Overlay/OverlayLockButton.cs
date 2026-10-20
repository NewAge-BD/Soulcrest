using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

internal enum OverlayLockButtonStyle { Default, TimerStrip }

/// <summary>
/// A small, separate mouse surface above an overlay. It remains clickable while its target is
/// click-through, and follows the target's position, visibility, handle and disposal lifecycle.
/// </summary>
internal sealed class OverlayLockButton : Form
{
    private const int LogicalSize = 28, LogicalInset = 8, MaximumGlyphExtent = 1024;
    private readonly Form _target;
    private readonly OverlayLockButtonStyle _style;
    private readonly Func<Rectangle>? _boundsProvider;
    private readonly ToolTip _tooltip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000, ShowAlways = true };
    private OverlayThemeDefinition _theme = OverlayThemeDefinition.Legacy;
    private bool _locked = true, _hover, _pressed, _targetHandleAvailable, _visibleInRecordings, _presenting, _changingTheme;
    private bool _creatingHandle, _updatingShape, _recoveringRender;

    /// <param name="boundsProvider">Optional bounds in physical pixels relative to the target's client area.</param>
    internal OverlayLockButton(Form target, Func<Rectangle>? boundsProvider = null,
        OverlayLockButtonStyle style = OverlayLockButtonStyle.Default)
    {
        _target = target;
        _style = style;
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

    internal void SetTheme(OverlayThemeDefinition theme)
    {
        if (_theme == theme || IsDisposed) return;
        _changingTheme = true;
        try
        {
            CancelPress();
            _theme = theme;
            var previous = Region; Region = null; previous?.Dispose();
            // Finish WinForms handle recreation before submitting a per-pixel surface to the new HWND.
            if (IsHandleCreated) RecreateHandle();
            UpdateWindowShape();
        }
        finally { _changingTheme = false; }
        RefreshAppearance();
        RefreshTarget();
    }

    internal bool Locked => _locked;
    internal OverlayLockButtonStyle Style => _style;
    internal event Action? Toggled;

    internal void SetLocked(bool locked)
    {
        if (IsDisposed) return;
        _locked = locked;
        _tooltip.SetToolTip(this, TooltipText(locked));
        RefreshAppearance();
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
        if (IsDisposed || Disposing || _changingTheme || _creatingHandle || _recoveringRender) return;
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
        if (!_theme.IsLegacy || _style == OverlayLockButtonStyle.TimerStrip)
        {
            _ = Handle;
            if (!PresentGlyph()) { HideUnavailableGlyph(); return; }
        }
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
            if (!_theme.IsLegacy || _style == OverlayLockButtonStyle.TimerStrip) parameters.ExStyle |= NativeMethods.WS_EX_LAYERED;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _visibleInRecordings);
    }

    protected override void CreateHandle()
    {
        _creatingHandle = true;
        try { base.CreateHandle(); }
        finally { _creatingHandle = false; }
        // WM_CREATE can expose temporary client dimensions. Never allocate/present until it is complete.
        if (IsDisposed || Disposing || _changingTheme) return;
        UpdateWindowShape();
        RefreshAppearance();
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
        if (_creatingHandle || IsDisposed || Disposing) return;
        UpdateWindowShape();
        if (!_theme.IsLegacy || _style == OverlayLockButtonStyle.TimerStrip) RefreshAppearance();
    }

    private void UpdateWindowShape()
    {
        if (_creatingHandle || _updatingShape || Width <= 0 || Height <= 0) return;
        _updatingShape = true;
        try
        {
            if (!_theme.IsLegacy || _style == OverlayLockButtonStyle.TimerStrip)
            {
                var previousRegion = Region; Region = null; previousRegion?.Dispose();
                return;
            }
            using var outline = Rounded(new RectangleF(0, 0, Width, Height), 6f * Math.Min(Width, Height) / LogicalSize);
            var previous = Region;
            Region = new Region(outline);
            previous?.Dispose();
        }
        catch (Exception error) when (IsDrawingFailure(error)) { LogFile.Error("Overlay lock shape", error); HideUnavailableGlyph(); }
        finally { _updatingShape = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_theme.IsLegacy && _style != OverlayLockButtonStyle.TimerStrip) base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_creatingHandle || !_theme.IsLegacy || _style == OverlayLockButtonStyle.TimerStrip) return;
        var dimensions = ClientSize;
        if (!IsRenderableGlyphSize(dimensions)) return;
        try { PaintLegacy(e, dimensions); }
        catch (Exception error) when (IsDrawingFailure(error))
        {
            LogFile.Error($"Overlay lock paint {dimensions.Width}x{dimensions.Height}", error);
            HideUnavailableGlyph();
        }
    }

    private void PaintLegacy(PaintEventArgs e, Size dimensions)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(dimensions.Width / (float)LogicalSize, dimensions.Height / (float)LogicalSize);
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

    private void RefreshAppearance()
    {
        if (IsDisposed || Disposing || _changingTheme || _creatingHandle || _recoveringRender) return;
        if (_theme.IsLegacy && _style != OverlayLockButtonStyle.TimerStrip) { Invalidate(); return; }
        if (IsHandleCreated && !PresentGlyph()) HideUnavailableGlyph();
    }

    private void HideUnavailableGlyph()
    {
        if (_recoveringRender || IsDisposed || Disposing || _creatingHandle) return;
        _recoveringRender = true;
        try { CancelPress(); Hide(); }
        finally { _recoveringRender = false; }
    }

    private static bool IsDrawingFailure(Exception error) => error is ArgumentException or ExternalException
        or InvalidOperationException or OutOfMemoryException;

    // A lock is normally 20–84 physical pixels. WM_CREATE can briefly report invalid/huge client bounds.
    internal static bool IsRenderableGlyphSize(Size size) => size.Width is > 0 and <= MaximumGlyphExtent
        && size.Height is > 0 and <= MaximumGlyphExtent;

    private bool PresentGlyph()
    {
        if (_theme.IsLegacy) return PresentTimerGlyph();
        if (_presenting) return true;
        if (_creatingHandle || !IsHandleCreated || IsDisposed || Disposing) return false;
        var size = ClientSize;
        if (!IsRenderableGlyphSize(size)) return false;
        _presenting = true;
        try
        {
            using var image = RenderThemedGlyph(size, _theme, _locked, _hover, _pressed);
            return LayeredOverlayPresenter.Present(this, image);
        }
        catch (Exception error) when (IsDrawingFailure(error))
        {
            LogFile.Error($"Overlay lock rendering {size.Width}x{size.Height} ({_theme.Id})", error);
            return false;
        }
        finally { _presenting = false; }
    }

    internal static Bitmap RenderThemedGlyph(Size size, OverlayThemeDefinition theme, bool locked, bool hover, bool pressed)
    {
        if (!IsRenderableGlyphSize(size)) throw new ArgumentOutOfRangeException(nameof(size), size, "Invalid overlay lock surface size.");
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size.Width / 28f, size.Height / 28f);
            theme.Panel(graphics, new Rectangle(0, 0, 28, 28));
            if (hover || pressed)
            {
                using var fill = new SolidBrush(Color.FromArgb(pressed ? 80 : 35, theme.Gold));
                graphics.FillRectangle(fill, 1, 1, 26, 26);
            }
            using var ink = new Pen(locked ? theme.Primary : theme.Gold, 1.5f);
            graphics.DrawRectangle(ink, 8, 12, 12, 10);
            graphics.DrawArc(ink, locked ? 10 : 14, 4, 8, 12, 180, 180);
            graphics.DrawLine(ink, locked ? 10 : 14, 10, locked ? 10 : 14, 12);
            if (locked) graphics.DrawLine(ink, 18, 10, 18, 12);
            graphics.DrawLine(ink, 14, 16, 14, 19);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    /// <summary>Uses real per-pixel alpha, including a fully transparent area around the lock glyph.</summary>
    private bool PresentTimerGlyph()
    {
        if (_presenting) return true;
        if (_creatingHandle || !IsHandleCreated || IsDisposed || Disposing) return false;
        var dimensions = ClientSize;
        if (!IsRenderableGlyphSize(dimensions)) return false;
        _presenting = true;
        try
        {
            var header = new NativeMethods.BitmapInfoHeader
            { Size = 40, Width = dimensions.Width, Height = -dimensions.Height, Planes = 1, BitCount = 32 };
            var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
            if (dib == 0) return false;
            try
            {
                using (var surface = new Bitmap(dimensions.Width, dimensions.Height, dimensions.Width * 4, PixelFormat.Format32bppPArgb, bits))
                using (var graphics = Graphics.FromImage(surface))
                {
                    graphics.Clear(Color.Transparent);
                    TimerStripLockRenderer.Paint(graphics, new Rectangle(Point.Empty, dimensions), _locked, _hover, _pressed);
                }
                var screen = NativeMethods.GetDC(0);
                var memory = NativeMethods.CreateCompatibleDC(screen);
                if (memory == 0) { NativeMethods.ReleaseDC(0, screen); return false; }
                var old = NativeMethods.SelectObject(memory, dib);
                try
                {
                    var target = new NativeMethods.NativePoint(Left, Top);
                    var source = new NativeMethods.NativePoint(0, 0);
                    var size = new NativeMethods.NativeSize(dimensions.Width, dimensions.Height);
                    var blend = new NativeMethods.BlendFunction
                    { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
                    var shown = NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory,
                        ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
                    if (!shown) Trace.TraceWarning("Timer lock presentation failed (Win32 {0}).", Marshal.GetLastWin32Error());
                    return shown;
                }
                finally
                {
                    NativeMethods.SelectObject(memory, old);
                    NativeMethods.DeleteDC(memory);
                    NativeMethods.ReleaseDC(0, screen);
                }
            }
            finally { NativeMethods.DeleteObject(dib); }
        }
        catch (Exception error) when (IsDrawingFailure(error))
        {
            LogFile.Error($"Timer lock rendering {dimensions.Width}x{dimensions.Height}", error);
            return false;
        }
        finally { _presenting = false; }
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
        RefreshAppearance();
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
        RefreshAppearance();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        RefreshAppearance();
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
        RefreshAppearance();
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
