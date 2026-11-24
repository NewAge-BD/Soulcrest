using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Click-through node surface with a separate, independently visible display switch.</summary>
public sealed class DaevanionOverlayForm : Form
{
    private readonly DaevanionService _service;
    private readonly SettingsService _settings;
    private readonly DaevanionOverlayRenderer _renderer = new();
    private readonly DaevanionToggleForm _toggle;
    private readonly DaevanionStatRailForm _statRail;
    private readonly DaevanionHighlightForm _highlights;
    private readonly IDisposable _animationObservation;
    private readonly DaevanionPlanService? _plans;
    private readonly DaevanionBoardEditService? _edits;
    private readonly DaevanionRouteForm _route;
    private readonly System.Windows.Forms.Timer _visibility = new() { Interval = 100 };
    private bool _closed, _placementHidden, _foregroundSuppressed;
    private int _queued;
    private DaevanionSnapshot? _shown, _controlSource;
    internal DaevanionToggleForm ToggleWindow => _toggle;
    internal DaevanionStatRailForm StatRailWindow => _statRail;
    internal DaevanionHighlightForm HighlightWindow => _highlights;
    internal DaevanionRouteForm RouteWindow => _route;

    public DaevanionOverlayForm(DaevanionService service, SettingsService settings, DaevanionPlanService? plans = null,
        DaevanionBoardEditService? edits = null)
    {
        _service = service; _settings = settings; _plans = plans; _edits = edits;
        _toggle = new DaevanionToggleForm(CanToggleNodeContent);
        _toggle.Toggled += ToggleNodeContent;
        _statRail = new DaevanionStatRailForm(CanToggleNodeContent);
        _statRail.StatToggled += ToggleHighlightedStat;
        _statRail.ClearRequested += ClearHighlightedStats;
        _highlights = new DaevanionHighlightForm(CurrentHighlightSource, SelectedStats);
        _animationObservation = _service.RegisterAnimationDiagnostics(() => _highlights.Diagnostics);
        _route = new DaevanionRouteForm(CurrentHighlightSource, classId => _plans?.GetActiveRoute(classId),
            edit: edits is null ? null : snapshot => edits.ForOverlay(snapshot.Context, snapshot.Board));
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        _visibility.Tick += (_, _) => CheckVisibility();
        _service.Changed += SchedulePresentation;
        if (_plans is not null) _plans.Changed += SchedulePresentation;
        if (_edits is not null) _edits.Changed += SchedulePresentation;
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
    internal void CheckVisibility()
    {
        // The switch must still expire/reposition when the node surface has deliberately been hidden.
        if (_closed || _placementHidden || _foregroundSuppressed) { HideAll(); return; }
        var current = CurrentForPresentation();
        if (current is null) { HideAll(); return; }
        try
        {
            RefreshControls(current);
            if (!_settings.Current.DaevanionNodeContentVisible) { HideNodes(); return; }
            if (_shown is not null && HasSameContent(_shown, current)) { _shown = current; return; }
            Present();
        }
        finally { RefreshHighlights(); RefreshRoute(); }
    }

    private void HideNodes() { Hide(); _shown = null; }
    private void HideAll()
    {
        HideNodes(); _controlSource = null; _toggle.Suppress(); _statRail.Suppress(); _highlights.Suppress(); _route.Suppress();
        _service.SetHighlightPlacementStatus(null);
    }

    private bool CanToggleNodeContent() => !_closed && !_placementHidden && !_foregroundSuppressed
        && _controlSource is { } shown && CurrentForPresentation() is { } current && HasSameBoard(shown, current);

    private void ToggleNodeContent()
    {
        if (!CanToggleNodeContent()) return;
        var previous = _settings.Current.DaevanionNodeContentVisible;
        var failed = false;
        try { _settings.Update(settings => settings.DaevanionNodeContentVisible = !settings.DaevanionNodeContentVisible); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Update mutates Current before its atomic write; do not pretend an unsaved preference stuck.
            _settings.Current.DaevanionNodeContentVisible = previous;
            failed = true;
            System.Diagnostics.Trace.TraceWarning("Daevanion display preference: {0}", error.GetType().Name);
        }
        Present();
        if (failed) _toggle.NotifySaveFailed();
    }

    private void ToggleHighlightedStat(string statId) => ChangeHighlightedStats(statId);
    private void ClearHighlightedStats() => ChangeHighlightedStats(null);
    private void ChangeHighlightedStats(string? statId)
    {
        if (!CanToggleNodeContent()) return;
        if (statId is not null && !_controlSource!.Board.Nodes.Any(node =>
            node.Kind == Soulcrest.Core.Daevanion.DaevanionNodeKind.Stat && node.Effects.Any(effect => effect.StatId == statId))) return;
        var previous = _settings.Current.DaevanionHighlightedStatIds.ToList();
        var selected = previous.ToHashSet(StringComparer.Ordinal);
        if (statId is null) selected.Clear(); else if (!selected.Remove(statId)) selected.Add(statId);
        var failed = false;
        try { _settings.Update(settings => settings.DaevanionHighlightedStatIds = selected.ToList()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _settings.Current.DaevanionHighlightedStatIds = previous; failed = true;
            System.Diagnostics.Trace.TraceWarning("Daevanion highlight preference: {0}", error.GetType().Name);
        }
        Present();
        if (failed) _statRail.NotifySaveFailed();
    }

    private HashSet<string> SelectedStats() => _settings.Current.DaevanionHighlightedStatIds.ToHashSet(StringComparer.Ordinal);
    private DaevanionSnapshot? CurrentHighlightSource() => _closed || _placementHidden || _foregroundSuppressed
        ? null : CurrentForPresentation();
    private void RefreshHighlights()
    {
        if (CurrentHighlightSource() is not { } current) { _highlights.Suppress(); return; }
        _highlights.Present(current, SelectedStats(), _settings.Current.OverlaysInRecordings);
    }
    private void RefreshRoute() => _route.RefreshRoute(_settings.Current.OverlaysInRecordings);
    private void RefreshControls(DaevanionSnapshot snapshot)
    {
        RefreshToggle(snapshot);
        _statRail.Present(snapshot, SelectedStats(), _toggle.Visible ? _toggle.Bounds : Rectangle.Empty,
            _settings.Current.OverlaysInRecordings);
        _service.SetHighlightPlacementStatus(_statRail.HasSpace ? null
            : "Rechts neben dem Board ist zu wenig Platz für die Stat-Schalter.");
    }

    private void RefreshToggle(DaevanionSnapshot snapshot)
    {
        if (_controlSource is { } previous && !HasSameBoard(previous, snapshot)) { _toggle.Suppress(); _statRail.Suppress(); }
        _controlSource = snapshot;
        var boardArea = SurfaceBounds(snapshot);
        var dpiPoint = new Point(snapshot.GameBounds.X + boardArea.Right, snapshot.GameBounds.Y + boardArea.Top);
        var dpi = DaevanionToggleLayout.DpiFor(dpiPoint);
        var relative = DaevanionToggleLayout.Bounds(snapshot, dpi);
        if (relative.IsEmpty) { _toggle.Suppress(); return; }
        var screen = new Rectangle(snapshot.GameBounds.X + relative.X, snapshot.GameBounds.Y + relative.Y, relative.Width, relative.Height);
        var targetDpi = DaevanionToggleLayout.DpiFor(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2));
        if (targetDpi != dpi)
        {
            relative = DaevanionToggleLayout.Bounds(snapshot, targetDpi);
            if (relative.IsEmpty) { _toggle.Suppress(); return; }
            screen = new(snapshot.GameBounds.X + relative.X, snapshot.GameBounds.Y + relative.Y, relative.Width, relative.Height);
        }
        _controlSource = snapshot;
        _toggle.Present(screen, _settings.Current.DaevanionNodeContentVisible, snapshot.Language, _settings.Current.OverlaysInRecordings);
    }

    internal void Present()
    {
        var snapshot = CurrentForPresentation();
        if (_closed || _placementHidden || _foregroundSuppressed || snapshot is null)
        { HideAll(); return; }
        try
        {
            RefreshControls(snapshot);
            if (!_settings.Current.DaevanionNodeContentVisible) { HideNodes(); return; }
            NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
            // A board/context change needs a new surface. Visibility-only publications can
            // update this surface in place, without dropping the unobscured board between frames.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                RefreshToggle(snapshot);
                if (_shown is { } oldBoard && !HasSameBoard(oldBoard, snapshot)) { HideNodes(); }
                if (Visible && _shown is { } previous && HasSameContent(previous, snapshot))
                {
                    _shown = snapshot;
                    return;
                }
                // A tooltip at an outer node must not resize the live layered window.
                var area = SurfaceBounds(snapshot);
                if (area.Width <= 0 || area.Height <= 0) { HideAll(); return; }
                using var image = _renderer.Render(area.Size, snapshot.Board, snapshot.Detection.Nodes, snapshot.Language, area.Location,
                    snapshot.Detection.Spacing);
                // A newer equivalent source can reuse these exact pixels without blanking.
                var current = CurrentForPresentation();
                if (current is null) { HideAll(); return; }
                var artwork = ReconcileRendered(image, snapshot, current, area.Location);
                if (artwork is null)
                {
                    snapshot = current;
                    if (attempt == 0) continue;
                    HideAll(); SchedulePresentation();
                    return;
                }
                // A tooltip can also disappear during rendering. Paint only newly visible or
                // changed nodes; matching nodes already have the exact required cached artwork.
                var additions = MissingCurrentNodes(artwork, current);
                if (additions.Length > 0)
                {
                    using var graphics = Graphics.FromImage(image);
                    _renderer.Paint(graphics, current.Board, additions, current.Language, area.Location, current.Detection.Spacing);
                }
                // The delta paint can race another fresh tooltip mask too. Prune it once more;
                // defer additions from that newest mask instead of repeatedly repainting/hiding all.
                var latest = CurrentForPresentation();
                if (latest is null) { HideAll(); return; }
                artwork = ReconcileRendered(image, current, latest, area.Location);
                if (artwork is null)
                {
                    snapshot = latest;
                    if (attempt == 0) continue;
                    HideAll(); SchedulePresentation();
                    return;
                }
                snapshot = latest;
                RefreshToggle(snapshot);
                if (!_settings.Current.DaevanionNodeContentVisible) { HideNodes(); return; }
                var target = new Rectangle(new Point(snapshot.GameBounds.X + area.X, snapshot.GameBounds.Y + area.Y), area.Size);
                if (Bounds != target) Bounds = target;
                LayeredOverlayPresenter.Configure(this, true, 1);
                if (LayeredOverlayPresenter.Present(this, image))
                {
                    _shown = artwork;
                    if (!Visible) Show();
                    if (!HasSameContent(artwork, latest)) SchedulePresentation();
                }
                else { HideAll(); }
                return;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        { HideAll(); System.Diagnostics.Trace.TraceWarning("Daevanion presentation: {0}", error.GetType().Name); }
        finally { RefreshHighlights(); RefreshRoute(); }
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

    /// <summary>
    /// Keeps only artwork independently supported by the newest confirmed frame. Its source
    /// timestamp comes from that current evidence, never from a timeout or an unconfirmed picture.
    /// </summary>
    internal static DaevanionSnapshot? ReconcileRendered(Bitmap image, DaevanionSnapshot rendered,
        DaevanionSnapshot current, Point origin, bool includeStatEnvelope = false)
    {
        if (!HasSameBoard(rendered, current)) return null;
        if (ReferenceEquals(rendered, current) || HasSameContent(rendered, current)) return current;
        Span<int> index = stackalloc int[225];
        if (!IndexNodes(current.Detection.Nodes, index)) return null;
        Span<bool> statCells = stackalloc bool[225];
        IndexStatCells(current, statCells);
        Span<bool> seen = stackalloc bool[225];
        seen.Clear();
        var retained = new List<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor>(rendered.Detection.Nodes.Count);
        List<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor>? removedNodes = null;
        foreach (var drawn in rendered.Detection.Nodes)
        {
            var key = CellKey(drawn);
            if (key < 0 || seen[key]) return null;
            seen[key] = true;
            if (index[key] >= 0 && SameNode(drawn, current.Detection.Nodes[index[key]], statCells[key]))
                // Retain the pixels, but always carry the newest anchor evidence and source time.
                retained.Add(current.Detection.Nodes[index[key]]);
            else (removedNodes ??= []).Add(drawn);
        }
        if (removedNodes is not null)
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var transparent = new SolidBrush(Color.Transparent);
            foreach (var removed in removedNodes)
            {
                var frame = new Rectangle(removed.Bounds.X - origin.X, removed.Bounds.Y - origin.Y, removed.Bounds.Width, removed.Bounds.Height);
                var area = statCells[CellKey(removed)]
                    && (includeStatEnvelope || DaevanionNodeFitting.CanExtend(frame, rendered.Detection.Spacing))
                    ? RectangleF.Union(frame, DaevanionNodeFitting.Bounds(frame, rendered.Detection.Spacing)) : frame;
                graphics.FillRectangle(transparent, Rectangle.FromLTRB((int)Math.Floor(area.Left), (int)Math.Floor(area.Top),
                    (int)Math.Ceiling(area.Right), (int)Math.Ceiling(area.Bottom)));
            }
        }
        return current with { Detection = current.Detection with { Nodes = Array.AsReadOnly(retained.ToArray()) } };
    }

    private static bool SameNode(Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor previous,
        Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor current, bool isStat) =>
        previous.Row == current.Row && previous.Column == current.Column && previous.Bounds == current.Bounds
        // The renderer deliberately gives Unknown and Inactive the identical dark material.
        // Native skill pictures never depend on this stat-only lighting signal.
        && (!isStat || (previous.Appearance == Soulcrest.Ocr.Daevanion.DaevanionNodeAppearance.Active)
            == (current.Appearance == Soulcrest.Ocr.Daevanion.DaevanionNodeAppearance.Active));

    private static int CellKey(Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor node) =>
        node.Row is >= 1 and <= 15 && node.Column is >= 1 and <= 15 ? (node.Row - 1) * 15 + node.Column - 1 : -1;

    private static bool IndexNodes(IReadOnlyList<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor> nodes, Span<int> index)
    {
        index.Fill(-1);
        for (var position = 0; position < nodes.Count; position++)
        {
            var node = nodes[position];
            var key = CellKey(node);
            if (key < 0 || index[key] >= 0 || node.Bounds.Width <= 0 || node.Bounds.Height <= 0) return false;
            index[key] = position;
        }
        return true;
    }

    private static void IndexStatCells(DaevanionSnapshot snapshot, Span<bool> cells)
    {
        cells.Clear();
        foreach (var node in snapshot.Board.Nodes)
            if (node.Kind == Soulcrest.Core.Daevanion.DaevanionNodeKind.Stat
                && node.Row is >= 1 and <= 15 && node.Column is >= 1 and <= 15)
                cells[(node.Row - 1) * 15 + node.Column - 1] = true;
    }

    // ReconcileRendered has already proved every retained anchor against current. Additions
    // therefore need only a bounded cell-membership check, not a second quadratic node search.
    internal static Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor[] MissingCurrentNodes(DaevanionSnapshot artwork,
        DaevanionSnapshot current)
    {
        if (!HasSameBoard(artwork, current)) return [];
        Span<int> index = stackalloc int[225];
        if (!IndexNodes(artwork.Detection.Nodes, index)) return [];
        List<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor>? additions = null;
        foreach (var node in current.Detection.Nodes)
        {
            var key = CellKey(node);
            if (key >= 0 && index[key] < 0) (additions ??= []).Add(node);
        }
        return additions?.ToArray() ?? [];
    }

    internal static bool HasSameBoard(DaevanionSnapshot previous, DaevanionSnapshot current) =>
        ReferenceEquals(previous.Context, current.Context) && ReferenceEquals(previous.Board, current.Board)
        && previous.Language == current.Language && previous.GameBounds == current.GameBounds
        && previous.Detection.Origin == current.Detection.Origin
        && previous.Detection.Spacing == current.Detection.Spacing && previous.Detection.NodeSize == current.Detection.NodeSize;

    internal static bool HasSameContent(DaevanionSnapshot previous, DaevanionSnapshot current)
    {
        if (!HasSameBoard(previous, current) || previous.Detection.Nodes.Count != current.Detection.Nodes.Count) return false;
        if (ReferenceEquals(previous, current) || ReferenceEquals(previous.Detection.Nodes, current.Detection.Nodes)) return true;
        Span<int> index = stackalloc int[225];
        if (!IndexNodes(current.Detection.Nodes, index)) return false;
        Span<bool> statCells = stackalloc bool[225];
        IndexStatCells(current, statCells);
        foreach (var previousNode in previous.Detection.Nodes)
        {
            var key = CellKey(previousNode);
            if (key < 0 || index[key] < 0 || !SameNode(previousNode, current.Detection.Nodes[index[key]], statCells[key])) return false;
            index[key] = -1;
        }
        return true;
    }

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
        if (disposing && !_closed)
        {
            _closed = true; _service.Changed -= SchedulePresentation; _visibility.Dispose();
            _animationObservation.Dispose();
            if (_plans is not null) _plans.Changed -= SchedulePresentation;
            if (_edits is not null) _edits.Changed -= SchedulePresentation;
            _toggle.Toggled -= ToggleNodeContent; _controlSource = null; _toggle.Dispose();
            _statRail.StatToggled -= ToggleHighlightedStat; _statRail.ClearRequested -= ClearHighlightedStats;
            _statRail.Dispose(); _highlights.Dispose(); _route.Dispose(); _renderer.Dispose();
            _service.SetHighlightPlacementStatus(null);
        }
        base.Dispose(disposing);
    }
}
