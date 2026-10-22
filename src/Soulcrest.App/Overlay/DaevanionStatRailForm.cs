using System.Diagnostics;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Unowned no-activate input surface; only the individual beveled buttons accept mouse input.</summary>
internal sealed class DaevanionStatRailForm : Form
{
    private readonly Func<bool> _canInteract;
    private readonly ToolTip _tooltip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 4000, ShowAlways = true };
    private DaevanionSnapshot? _source;
    private DaevanionStatRailLayout _layout = new(Rectangle.Empty, [], 0, 0);
    private HashSet<string> _selected = new(StringComparer.Ordinal);
    private Rectangle _toggleBounds;
    private string? _hoverId, _pressedId;
    private string _tooltipText = "";
    private bool _ready, _rendering, _disposing, _visibleInRecordings;
    private bool? _appliedCaptureVisibility;
    private long _saveErrorUntil;
    private int _page;

    internal DaevanionStatRailForm(Func<bool> canInteract)
    {
        _canInteract = canInteract;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand; SetStyle(ControlStyles.StandardDoubleClick, false);
    }

    internal event Action<string>? StatToggled;
    internal event Action? ClearRequested;
    internal bool HasSpace { get; private set; } = true;
    internal DaevanionStatRailLayout RailLayout => _layout;
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

    internal void Present(DaevanionSnapshot snapshot, IReadOnlySet<string> selected, Rectangle toggleScreenBounds, bool visibleInRecordings)
    {
        if (_disposing || IsDisposed) return;
        if (!_canInteract()) { Suppress(); return; }
        var sameBoard = _source is { } previous && DaevanionOverlayForm.HasSameBoard(previous, snapshot);
        if (!sameBoard) { CancelPress(); _page = 0; HideTooltip(); _saveErrorUntil = 0; }
        var toggle = toggleScreenBounds;
        if (!toggle.IsEmpty) toggle.Offset(-snapshot.GameBounds.X, -snapshot.GameBounds.Y);
        var layout = DaevanionStatRailLayout.Build(snapshot.Board, snapshot.Detection, snapshot.GameBounds.Size,
            selected, snapshot.Language, toggle, 96, _page);
        if (layout.HasSpace)
        {
            var center = new Point(snapshot.GameBounds.X + layout.Bounds.X + layout.Bounds.Width / 2,
                snapshot.GameBounds.Y + layout.Bounds.Y + layout.Bounds.Height / 2);
            var dpi = DaevanionToggleLayout.DpiFor(center);
            if (dpi != 96) layout = DaevanionStatRailLayout.Build(snapshot.Board, snapshot.Detection,
                snapshot.GameBounds.Size, selected, snapshot.Language, toggle, dpi, _page);
            if (layout.HasSpace)
            {
                center = new(snapshot.GameBounds.X + layout.Bounds.X + layout.Bounds.Width / 2,
                    snapshot.GameBounds.Y + layout.Bounds.Y + layout.Bounds.Height / 2);
                var targetDpi = DaevanionToggleLayout.DpiFor(center);
                if (targetDpi != dpi) layout = DaevanionStatRailLayout.Build(snapshot.Board, snapshot.Detection,
                    snapshot.GameBounds.Size, selected, snapshot.Language, toggle, targetDpi, _page);
            }
        }
        HasSpace = layout.HasSpace;
        if (!HasSpace) { _layout = layout; Suppress(); return; }
        var screenBounds = layout.Bounds; screenBounds.Offset(snapshot.GameBounds.Location);
        var changed = !_ready || !sameBoard || Bounds != screenBounds || !_layout.Items.SequenceEqual(layout.Items);
        if (changed) CancelPress();
        if (!_selected.SetEquals(selected)) { _saveErrorUntil = 0; HideTooltip(); }
        _source = snapshot; _selected = new(selected, StringComparer.Ordinal); _toggleBounds = toggleScreenBounds;
        _layout = layout; _page = layout.Page;
        if (changed)
        {
            _hoverId = null;
            if (Bounds != screenBounds) Bounds = screenBounds;
            var region = new Region(); region.MakeEmpty();
            foreach (var item in layout.Items)
            {
                using var shape = DaevanionStatRailRenderer.Shape(item.Bounds);
                region.Union(shape);
            }
            var previousRegion = Region; Region = region; previousRegion?.Dispose();
        }
        _visibleInRecordings = visibleInRecordings;
        _ = Handle;
        if (_appliedCaptureVisibility != visibleInRecordings)
        {
            NativeMethods.SetCaptureVisibility(Handle, visibleInRecordings);
            _appliedCaptureVisibility = visibleInRecordings;
        }
        UpdateTooltip();
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
        _ready = false; _source = null; _saveErrorUntil = 0; HideTooltip(); CancelPress(); _hoverId = null; Hide();
    }

    internal void NotifySaveFailed()
    {
        if (_disposing || IsDisposed || !Visible || !_canInteract()) return;
        _saveErrorUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4;
        var text = SaveErrorText(_source?.Language ?? "en");
        SetTooltip(text); _tooltip.Show(text, this, ClientSize.Width / 2, Math.Min(ClientSize.Height, 32), 4000);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); NativeMethods.SetCaptureVisibility(Handle, _visibleInRecordings);
        _appliedCaptureVisibility = _visibleInRecordings;
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true; CancelPress(); base.OnDpiChanged(e);
        if (_ready && !RenderSurface()) Suppress();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Visible || !_canInteract() || Hit(e.Location) is not { Enabled: true } item) return;
        _pressedId = item.Id; Capture = true;
        if (!RenderSurface()) Suppress();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = Hit(e.Location);
        var selected = e.Button == MouseButtons.Left && Visible && _canInteract() && hit is { Enabled: true }
            && _pressedId == hit.Id ? hit : null;
        CancelPress();
        if (selected is null) return;
        switch (selected.Kind)
        {
            case DaevanionStatRailItemKind.Stat: StatToggled?.Invoke(selected.Id); break;
            case DaevanionStatRailItemKind.Clear: ClearRequested?.Invoke(); break;
            case DaevanionStatRailItemKind.Previous: ChangePage(-1); break;
            case DaevanionStatRailItemKind.Next: ChangePage(1); break;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_disposing) return;
        var hit = Hit(e.Location);
        var hover = hit is { Enabled: true } ? hit.Id : null;
        if (_hoverId == hover) return;
        _hoverId = hover; UpdateTooltip(); if (_ready && !RenderSurface()) Suppress();
    }
    protected override void OnMouseLeave(EventArgs e)
    { base.OnMouseLeave(e); if (_disposing) return; _hoverId = null; UpdateTooltip(); if (_ready && !RenderSurface()) Suppress(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) CancelPress(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) CancelPress(); }
    protected override void OnHandleDestroyed(EventArgs e)
    { _ready = false; _appliedCaptureVisibility = null; CancelPress(); base.OnHandleDestroyed(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private void ChangePage(int change)
    {
        if (_source is not { } source || !_canInteract()) return;
        _page = Math.Clamp(_page + change, 0, _layout.PageCount - 1);
        Present(source, _selected, _toggleBounds, _visibleInRecordings);
    }
    private DaevanionStatRailItem? Hit(Point point) => ClientRectangle.Contains(point)
        && (Region is null || Region.IsVisible(point)) ? _layout.Items.FirstOrDefault(item => item.Bounds.Contains(point)) : null;
    private void HideTooltip() { if (IsHandleCreated) _tooltip.Hide(this); }
    private void UpdateTooltip()
    {
        var text = Stopwatch.GetTimestamp() < _saveErrorUntil ? SaveErrorText(_source?.Language ?? "en")
            : _layout.Items.FirstOrDefault(item => item.Id == _hoverId)?.Tooltip ?? "";
        SetTooltip(text);
    }
    private void SetTooltip(string text) { if (_tooltipText == text) return; _tooltipText = text; _tooltip.SetToolTip(this, text); }
    internal static string SaveErrorText(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
        ? "Hervorhebungen konnten nicht gespeichert werden." : "Highlights could not be saved.";
    private void CancelPress()
    {
        var changed = _pressedId is not null; _pressedId = null;
        if (Capture) Capture = false;
        if (changed) RenderSurface();
    }
    private bool RenderSurface()
    {
        if (_disposing || IsDisposed || _rendering || !_ready || !IsHandleCreated) return false;
        _rendering = true;
        try
        {
            using var image = DaevanionStatRailRenderer.Render(_layout, _hoverId, _pressedId);
            LayeredOverlayPresenter.Configure(this, true, 1);
            return LayeredOverlayPresenter.Present(this, image);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        { Trace.TraceWarning("Daevanion stat rail presentation: {0}", error.GetType().Name); return false; }
        finally { _rendering = false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposing)
        {
            _disposing = true; _pressedId = null; if (Capture) Capture = false;
            HideTooltip(); _tooltip.Dispose(); var previous = Region; Region = null; previous?.Dispose();
        }
        base.Dispose(disposing);
    }
}
