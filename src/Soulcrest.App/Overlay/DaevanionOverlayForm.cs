using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Always click-through: the game's board owns all input and native tooltips.</summary>
public sealed class DaevanionOverlayForm : Form
{
    private readonly DaevanionService _service;
    private readonly SettingsService _settings;
    private readonly DaevanionOverlayRenderer _renderer = new();
    private readonly System.Windows.Forms.Timer _visibility = new() { Interval = 100 };
    private bool _closed, _placementHidden, _foregroundSuppressed;
    private int _queued;
    private DaevanionSnapshot? _shown;

    public DaevanionOverlayForm(DaevanionService service, SettingsService settings)
    {
        _service = service; _settings = settings;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        _visibility.Tick += (_, _) => CheckVisibility();
        _service.Changed += SchedulePresentation;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST; return cp; }
    }
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !_placementHidden && !_foregroundSuppressed && !_closed);
    public void Start() { if (_closed) return; _ = Handle; _visibility.Start(); _service.Start(); }
    public void SetPlacementHidden(bool value) { if (_placementHidden == value) return; _placementHidden = value; Present(); }
    public void SetForegroundSuppressed(bool value) { if (_foregroundSuppressed == value) return; _foregroundSuppressed = value; Present(); }
    private void SchedulePresentation()
    {
        if (_closed || !IsHandleCreated || Interlocked.Exchange(ref _queued, 1) != 0) return;
        try { BeginInvoke(() => { Volatile.Write(ref _queued, 0); if (!_closed && !IsDisposed) Present(); }); }
        catch (InvalidOperationException) { Volatile.Write(ref _queued, 0); }
    }
    private void CheckVisibility()
    {
        if (_shown is null) return;
        if (_closed || _placementHidden || _foregroundSuppressed) { Hide(); _shown = null; return; }
        var current = CurrentForPresentation();
        // A fresh equivalent capture may arrive just before the queued presentation callback.
        // Retain its identical artwork rather than briefly hiding the previous capture's surface.
        if (current is not null)
        {
            if (HasSameContent(_shown, current)) { _shown = current; return; }
            // A fresh changed node mask needs another surface, not a blank interval before
            // the queued callback. Present the current picture directly on this UI tick.
            Present();
            return;
        }
        Hide(); _shown = null;
    }
    private void Present()
    {
        var snapshot = CurrentForPresentation();
        if (_closed || _placementHidden || _foregroundSuppressed || snapshot is null)
        { Hide(); _shown = null; return; }
        try
        {
            NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
            // A publication during painting can change the visible node subset. Render that
            // newest mask once directly; never leave obsolete labels over a fresh tooltip.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_shown is { } oldBoard && !HasSameBoard(oldBoard, snapshot)) { Hide(); _shown = null; }
                if (Visible && _shown is { } previous && HasSameContent(previous, snapshot))
                {
                    _shown = snapshot;
                    return;
                }
                // A tooltip at an outer node must not resize the live layered window.
                var area = SurfaceBounds(snapshot);
                if (area.Width <= 0 || area.Height <= 0) { Hide(); _shown = null; return; }
                using var image = _renderer.Render(area.Size, snapshot.Board, snapshot.Detection.Nodes, snapshot.Language, area.Location,
                    snapshot.Detection.Spacing);
                // A newer equivalent source can reuse these exact pixels without blanking.
                var current = CurrentForPresentation();
                if (current is null) { Hide(); _shown = null; return; }
                var accepted = EquivalentCurrent(snapshot, current);
                if (accepted is null)
                {
                    snapshot = current;
                    if (attempt == 0) continue;
                    Hide(); _shown = null; SchedulePresentation();
                    return;
                }
                snapshot = accepted;
                var target = new Rectangle(new Point(snapshot.GameBounds.X + area.X, snapshot.GameBounds.Y + area.Y), area.Size);
                if (Bounds != target) Bounds = target;
                LayeredOverlayPresenter.Configure(this, true, 1);
                if (LayeredOverlayPresenter.Present(this, image)) { _shown = snapshot; if (!Visible) Show(); }
                else { Hide(); _shown = null; }
                return;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        { Hide(); _shown = null; System.Diagnostics.Trace.TraceWarning("Daevanion presentation: {0}", error.GetType().Name); }
    }
    private DaevanionSnapshot? CurrentForPresentation()
    {
        // Publications are deliberately coalesced. Retry once if a new source replaced the
        // object between reading it and checking it; never accept missing/stale board evidence.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = _service.Snapshot;
            if (current is null) return null;
            if (_service.CanPresent(current)) return current;
        }
        return null;
    }

    internal static DaevanionSnapshot? EquivalentCurrent(DaevanionSnapshot rendered, DaevanionSnapshot current) =>
        HasSameContent(rendered, current) ? current : null;

    internal static bool HasSameBoard(DaevanionSnapshot previous, DaevanionSnapshot current) =>
        ReferenceEquals(previous.Context, current.Context) && ReferenceEquals(previous.Board, current.Board)
        && previous.Language == current.Language && previous.GameBounds == current.GameBounds
        && previous.Detection.Origin == current.Detection.Origin
        && previous.Detection.Spacing == current.Detection.Spacing && previous.Detection.NodeSize == current.Detection.NodeSize;

    internal static bool HasSameContent(DaevanionSnapshot previous, DaevanionSnapshot current) =>
        HasSameBoard(previous, current) && previous.Detection.Nodes.Count == current.Detection.Nodes.Count
        && previous.Detection.Nodes.OrderBy(n => n.Row).ThenBy(n => n.Column)
            .Zip(current.Detection.Nodes.OrderBy(n => n.Row).ThenBy(n => n.Column))
            .All(pair => pair.First.Row == pair.Second.Row && pair.First.Column == pair.Second.Column && pair.First.Bounds == pair.Second.Bounds);

    internal static Rectangle SurfaceBounds(DaevanionSnapshot snapshot)
    {
        var cells = snapshot.Board.Nodes.Where(n => n.Kind != Soulcrest.Core.Daevanion.DaevanionNodeKind.Start).ToArray();
        var geometry = snapshot.Detection;
        if (cells.Length == 0 || snapshot.GameBounds.Width <= 0 || snapshot.GameBounds.Height <= 0
            || !double.IsFinite(geometry.Origin.X) || !double.IsFinite(geometry.Origin.Y)
            || !double.IsFinite(geometry.Spacing) || geometry.Spacing <= 0
            || !double.IsFinite(geometry.NodeSize) || geometry.NodeSize <= 0) return Rectangle.Empty;
        var half = geometry.NodeSize / 2;
        // Keep the complete fitting/footer inside one stable window, including outermost nodes.
        var padding = Math.Max(2, Math.Ceiling(geometry.NodeSize * .20));
        var bounds = Rectangle.FromLTRB(
            (int)Math.Floor(geometry.Origin.X + (cells.Min(n => n.Column) - 1) * geometry.Spacing - half - padding),
            (int)Math.Floor(geometry.Origin.Y + (cells.Min(n => n.Row) - 1) * geometry.Spacing - half - padding),
            (int)Math.Ceiling(geometry.Origin.X + (cells.Max(n => n.Column) - 1) * geometry.Spacing + half + padding),
            (int)Math.Ceiling(geometry.Origin.Y + (cells.Max(n => n.Row) - 1) * geometry.Spacing + half + padding));
        return Rectangle.Intersect(bounds, new Rectangle(Point.Empty, snapshot.GameBounds.Size));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed) { _closed = true; _service.Changed -= SchedulePresentation; _visibility.Dispose(); _renderer.Dispose(); }
        base.Dispose(disposing);
    }
}
