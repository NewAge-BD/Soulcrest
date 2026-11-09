using System.Diagnostics;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>Independent passive route layer; pointer observations never change game input or capture state.</summary>
internal sealed class DaevanionRouteForm : Form
{
    private sealed record Selection(string Key, IReadOnlySet<int> Nodes, IReadOnlyList<(int FromNodeId, int ToNodeId)> Edges,
        IReadOnlySet<int>? RemovedNodes = null)
    {
        internal bool IsEdit => RemovedNodes is not null;
    }
    private readonly Func<DaevanionSnapshot?> _currentSource;
    private readonly Func<string, DaevanionSavedRoute?> _activeRoute;
    private readonly Func<DaevanionSnapshot, DaevanionBoardEditOverlay?>? _edit;
    private readonly Func<Point> _pointer;
    private readonly DaevanionRouteRenderer _renderer = new();
    private readonly DaevanionRouteVisibility _visibility = new();
    private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 16 };
    private DaevanionSnapshot? _shown;
    private string _selection = "";
    private bool _shownConnections, _recordings, _disposing;
    private bool? _appliedRecordings;

    internal DaevanionSnapshot? ShownSource => _shown;
    internal bool ConnectionsVisible => Visible && _shownConnections;

    internal DaevanionRouteForm(Func<DaevanionSnapshot?> currentSource,
        Func<string, DaevanionSavedRoute?> activeRoute, Func<Point>? pointer = null,
        Func<DaevanionSnapshot, DaevanionBoardEditOverlay?>? edit = null)
    {
        _currentSource = currentSource; _activeRoute = activeRoute; _edit = edit; _pointer = pointer ?? (() => Cursor.Position);
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        _pointerTimer.Tick += (_, _) => RefreshRoute(_recordings);
    }

    internal void RefreshRoute(bool visibleInRecordings)
    {
        if (_disposing || IsDisposed) return;
        _recordings = visibleInRecordings;
        var snapshot = _currentSource();
        var selection = snapshot is null ? null : Select(snapshot);
        if (snapshot is null || selection is null) { Suppress(); return; }
        _pointerTimer.Start();
        try
        {
            var area = DaevanionOverlayForm.SurfaceBounds(snapshot);
            if (area.IsEmpty) { Suppress(); return; }
            var connections = ConnectionsFor(snapshot, selection);
            if (Visible && _shown is { } shown && _selection == selection.Key && _shownConnections == connections
                && SameFrame(shown, snapshot))
            {
                ApplyRecordingVisibility(visibleInRecordings);
                var current = _currentSource(); var currentSelection = current is null ? null : Select(current);
                if (current is null || currentSelection is null || currentSelection.Key != selection.Key
                    || !SameFrame(snapshot, current) || connections != ConnectionsFor(current, currentSelection))
                { HideArtwork(); return; }
                BringForward(); return;
            }

            // Always replace the complete surface. Node-only reconciliation cannot clear an edge
            // through a newly covered gap and is deliberately never used for route artwork.
            using var image = selection.IsEdit
                ? _renderer.RenderChanges(area.Size, snapshot.Board, snapshot.Detection,
                    selection.Nodes, selection.RemovedNodes!, area.Location)
                : _renderer.Render(area.Size, snapshot.Board, snapshot.Detection,
                    selection.Nodes, selection.Edges, connections, area.Location);
            var latest = _currentSource();
            if (latest is null) { Suppress(); return; }
            var latestSelection = Select(latest);
            if (latestSelection is null || selection.Key != latestSelection.Key || !SameFrame(snapshot, latest))
            { HideArtwork(); return; }
            if (connections != ConnectionsFor(latest, latestSelection))
            { HideArtwork(); return; }
            var target = new Rectangle(latest.GameBounds.X + area.X, latest.GameBounds.Y + area.Y, area.Width, area.Height);
            if (Bounds != target) Bounds = target;
            _ = Handle;
            ApplyRecordingVisibility(visibleInRecordings);
            LayeredOverlayPresenter.Configure(this, true, 1);

            // Recheck source, route and pointer after all handle/placement callbacks as well.
            var final = _currentSource();
            var finalSelection = final is null ? null : Select(final);
            if (final is null || finalSelection is null || finalSelection.Key != selection.Key || !SameFrame(snapshot, final)
                || connections != ConnectionsFor(final, finalSelection))
            { HideArtwork(); return; }
            if (!LayeredOverlayPresenter.Present(this, image)) { HideArtwork(); return; }
            _shown = final; _selection = selection.Key; _shownConnections = connections;
            if (!Visible) Show();
            BringForward();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        {
            Suppress();
            Trace.TraceWarning("Daevanion route presentation: {0}", error.GetType().Name);
        }
    }

    private Selection? Select(DaevanionSnapshot snapshot)
    {
        if (_edit?.Invoke(snapshot) is { } edit)
        {
            // A matching active edit always owns this layer, including an empty or invalid edit.
            // Never replace it with a saved route merely because there is no change to draw.
            if (!edit.CanPresent || edit.AddedNodeIds.Count == 0 && edit.RemovedNodeIds.Count == 0) return null;
            var added = edit.AddedNodeIds.ToHashSet(); var removed = edit.RemovedNodeIds.ToHashSet();
            var editKey = $"edit\n{snapshot.Board.ClassId}\n{snapshot.Board.Id}\n{snapshot.Board.FamilyId}\n{edit.Revision}\n"
                + string.Join(',', added.Order()) + "\n" + string.Join(',', removed.Order());
            return new(editKey, added, [], removed);
        }
        var route = _activeRoute(snapshot.Board.ClassId);
        if (route is null || route.ClassId != snapshot.Board.ClassId) return null;
        var board = route.Result.Boards.SingleOrDefault(board => board.BoardId == snapshot.Board.Id
            && board.FamilyId == snapshot.Board.FamilyId);
        if (board is null || board.NodeIds.Count == 0) return null;
        var nodes = board.NodeIds.ToHashSet();
        var edges = board.Edges.Select(edge => (edge.FromNodeId, edge.ToNodeId)).ToArray();
        var key = $"route\n{route.Id}\n{route.ClassId}\n{route.CatalogFingerprint}\n{route.RulesVersion}\n{board.BoardId}\n"
            + string.Join(',', nodes.Order()) + "\n" + string.Join(',', edges.OrderBy(edge => edge.FromNodeId)
                .ThenBy(edge => edge.ToNodeId).Select(edge => $"{edge.FromNodeId}:{edge.ToNodeId}"));
        return new(key, nodes, edges);
    }

    private bool ConnectionsFor(DaevanionSnapshot snapshot, Selection selection)
    {
        var visible = _visibility.Observe(snapshot, PointerInside(snapshot), Stopwatch.GetTimestamp());
        return !selection.IsEdit && visible;
    }

    private bool PointerInside(DaevanionSnapshot snapshot)
    {
        var board = DaevanionOverlayForm.SurfaceBounds(snapshot);
        board.Offset(snapshot.GameBounds.Location);
        return board.Contains(_pointer());
    }

    internal static bool SameFrame(DaevanionSnapshot previous, DaevanionSnapshot current) =>
        previous.FrameAt == current.FrameAt && previous.Detection.StartVisible == current.Detection.StartVisible
        && DaevanionOverlayForm.HasSameContent(previous, current);

    internal void Suppress()
    {
        if (_disposing || IsDisposed) return;
        _pointerTimer.Stop(); _visibility.Reset(); HideArtwork();
    }

    private void HideArtwork() { _shown = null; _selection = ""; _shownConnections = false; Hide(); }
    private void ApplyRecordingVisibility(bool value)
    {
        if (!IsHandleCreated || _appliedRecordings == value) return;
        NativeMethods.SetCaptureVisibility(Handle, value); _appliedRecordings = value;
    }
    private void BringForward()
    {
        if (Visible && IsHandleCreated) SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); NativeMethods.SetCaptureVisibility(Handle, _recordings); _appliedRecordings = _recordings; }
    protected override void OnHandleDestroyed(EventArgs e)
    { _shown = null; _appliedRecordings = null; base.OnHandleDestroyed(e); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposing)
        { _disposing = true; _pointerTimer.Stop(); _pointerTimer.Dispose(); _visibility.Reset(); _shown = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
