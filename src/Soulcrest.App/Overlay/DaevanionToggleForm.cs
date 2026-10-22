using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Soulcrest.App.Overlay;

/// <summary>An independent mouse surface: hiding the node overlay must never hide its content toggle.</summary>
internal sealed class DaevanionToggleForm : Form
{
    private readonly Func<bool> _canInteract;
    private readonly ToolTip _tooltip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000, ShowAlways = true };
    private bool _contentVisible = true, _hover, _pressed, _ready, _rendering, _disposing, _visibleInRecordings;
    private string _language = "en";
    private string _tooltipText = "";
    private bool? _appliedCaptureVisibility;
    private long _saveErrorUntil;

    internal DaevanionToggleForm(Func<bool> canInteract)
    {
        _canInteract = canInteract;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.StandardDoubleClick, false);
    }

    internal event Action? Toggled;
    internal bool ContentVisible => _contentVisible;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            parameters.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }

    internal void Present(Rectangle screenBounds, bool contentVisible, string language, bool visibleInRecordings)
    {
        if (_disposing || IsDisposed) return;
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0 || screenBounds.Width > 4096 || screenBounds.Height > 4096 || !_canInteract())
        { Suppress(); return; }
        language = language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        var changed = !_ready || Bounds != screenBounds || _contentVisible != contentVisible || _language != language;
        if (changed) CancelPress();
        if (_contentVisible != contentVisible) { _saveErrorUntil = 0; HideTooltip(); }
        _contentVisible = contentVisible; _language = language;
        if (Bounds != screenBounds)
        {
            Bounds = screenBounds;
            using var shape = DaevanionToggleRenderer.Shape(new(0, 0, Width, Height));
            var previous = Region; Region = new Region(shape); previous?.Dispose();
        }
        _visibleInRecordings = visibleInRecordings;
        _ = Handle;
        if (_appliedCaptureVisibility != visibleInRecordings)
        {
            NativeMethods.SetCaptureVisibility(Handle, visibleInRecordings);
            _appliedCaptureVisibility = visibleInRecordings;
        }
        SetTooltip(Stopwatch.GetTimestamp() < _saveErrorUntil ? SaveErrorText(language) : TooltipText(contentVisible, language));
        if (changed || !Visible)
        {
            _ready = true;
            if (!RenderSurface()) { Suppress(); return; }
        }
        if (!_canInteract()) { Suppress(); return; }
        if (!Visible) Show();
    }

    internal void Suppress()
    {
        if (_disposing || IsDisposed) return;
        _ready = false; _saveErrorUntil = 0; HideTooltip(); CancelPress(); _hover = false; Hide();
    }

    internal void NotifySaveFailed()
    {
        if (_disposing || IsDisposed || !Visible || !_canInteract()) return;
        _saveErrorUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4;
        var text = SaveErrorText(_language);
        SetTooltip(text);
        _tooltip.Show(text, this, ClientSize.Width / 2, ClientSize.Height, 4000);
    }

    internal static string TooltipText(bool contentVisible, string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
        ? contentVisible ? "Node-Ergänzungen ausblenden" : "Node-Ergänzungen anzeigen"
        : contentVisible ? "Hide node additions" : "Show node additions";

    internal static string SaveErrorText(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
        ? "Anzeige konnte nicht gespeichert werden." : "Display preference could not be saved.";

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _visibleInRecordings);
        _appliedCaptureVisibility = _visibleInRecordings;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Layout supplies physical pixels from the target monitor; WinForms must not apply a second scale.
        e.Cancel = true; CancelPress(); base.OnDpiChanged(e);
        if (_ready && !RenderSurface()) Suppress();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Hit(e.Location) || !Visible || !_canInteract()) return;
        _pressed = true; Capture = true;
        if (!RenderSurface()) Suppress();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var toggle = _pressed && e.Button == MouseButtons.Left && Hit(e.Location) && Visible && _canInteract();
        CancelPress();
        if (toggle) Toggled?.Invoke();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; if (_ready && !RenderSurface()) Suppress(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; if (_ready && !RenderSurface()) Suppress(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) CancelPress(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) CancelPress(); }
    protected override void OnHandleDestroyed(EventArgs e) { _ready = false; _appliedCaptureVisibility = null; CancelPress(); base.OnHandleDestroyed(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private bool Hit(Point point) => ClientRectangle.Contains(point) && (Region is null || Region.IsVisible(point));

    private void HideTooltip() { if (IsHandleCreated) _tooltip.Hide(this); }

    private void SetTooltip(string text)
    {
        if (_tooltipText == text) return;
        _tooltipText = text; _tooltip.SetToolTip(this, text);
    }

    private void CancelPress()
    {
        var changed = _pressed;
        _pressed = false;
        if (Capture) Capture = false;
        if (changed) RenderSurface();
    }

    private bool RenderSurface()
    {
        if (_disposing || IsDisposed || _rendering || !_ready || !IsHandleCreated) return false;
        _rendering = true;
        try
        {
            using var image = DaevanionToggleRenderer.Render(ClientSize, _contentVisible, _language, _hover, _pressed);
            LayeredOverlayPresenter.Configure(this, true, 1);
            return LayeredOverlayPresenter.Present(this, image);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        {
            System.Diagnostics.Trace.TraceWarning("Daevanion toggle presentation: {0}", error.GetType().Name);
            return false;
        }
        finally { _rendering = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposing)
        {
            _disposing = true; _pressed = false;
            if (Capture) Capture = false;
            HideTooltip(); _tooltip.Dispose();
            var previous = Region; Region = null; previous?.Dispose();
        }
        base.Dispose(disposing);
    }
}
