using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>A click-through highlight surface independent of the optional node artwork.</summary>
internal sealed class DaevanionHighlightForm : Form
{
    private readonly Func<DaevanionSnapshot?> _currentSource;
    private readonly Func<IReadOnlySet<string>> _selectedStats;
    private readonly DaevanionHighlightRenderer _renderer = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private DaevanionSnapshot? _shown;
    private string _selection = "";
    private bool _recordings;
    private bool _disposing;
    private bool? _appliedRecordings;
    private Bitmap? _surface;

    internal DaevanionHighlightForm(Func<DaevanionSnapshot?> currentSource, Func<IReadOnlySet<string>> selectedStats)
    {
        _currentSource = currentSource; _selectedStats = selectedStats;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        _animation.Tick += (_, _) => Animate();
    }

    internal void Present(DaevanionSnapshot snapshot, IReadOnlySet<string> selected, bool visibleInRecordings, bool animationFrame = false)
    {
        if (_disposing || IsDisposed) return;
        if (!snapshot.Board.Nodes.Any(node => node.Kind == Soulcrest.Core.Daevanion.DaevanionNodeKind.Stat
            && node.Effects.Any(effect => effect.StatId is { } id && selected.Contains(id))))
        { Suppress(); return; }
        var key = string.Join("\n", selected.Order(StringComparer.Ordinal));
        if (!animationFrame && _shown is { } shown && Visible && _selection == key && HasSameVisibleGeometry(shown, snapshot))
        {
            var current = _currentSource();
            if (current is null || !DaevanionOverlayForm.HasSameBoard(snapshot, current)) { Suppress(); return; }
            if (HasSameVisibleGeometry(snapshot, current))
            { _shown = current; ApplyRecordingVisibility(visibleInRecordings); BringForward(); return; }
            // A new tooltip mask needs a replacement surface, never an empty interval.
            snapshot = current;
        }
        try
        {
            var area = DaevanionOverlayForm.SurfaceBounds(snapshot);
            if (area.Width <= 0 || area.Height <= 0) { Suppress(); return; }
            var phase = StopwatchPhase();
            var image = Raster(snapshot, selected, area, phase);
            var current = _currentSource();
            if (current is null) { Suppress(); return; }
            var artwork = ReconcileHighlight(image, snapshot, current, area.Location);
            if (artwork is null) { Suppress(); return; }
            Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor[] additions = ReferenceEquals(artwork, current) ? [] : current.Detection.Nodes.Where(node =>
                !artwork.Detection.Nodes.Any(drawn => drawn.Row == node.Row && drawn.Column == node.Column
                    && drawn.Bounds == node.Bounds)).ToArray();
            if (additions.Length > 0)
            {
                using var graphics = Graphics.FromImage(image);
                _renderer.Paint(graphics, current.Board, additions, selected, area.Location, current.Detection.Spacing, phase);
            }
            var latest = _currentSource();
            if (latest is null) { Suppress(); return; }
            artwork = ReconcileHighlight(image, current, latest, area.Location);
            if (artwork is null) { Suppress(); return; }
            _recordings = visibleInRecordings;
            var target = new Rectangle(latest.GameBounds.X + area.X, latest.GameBounds.Y + area.Y, area.Width, area.Height);
            if (Bounds != target) Bounds = target;
            _ = Handle;
            ApplyRecordingVisibility(visibleInRecordings);
            LayeredOverlayPresenter.Configure(this, true, 1);
            var final = _currentSource();
            if (final is null) { Suppress(); return; }
            artwork = ReconcileHighlight(image, artwork, final, area.Location);
            if (artwork is null) { Suppress(); return; }
            if (!LayeredOverlayPresenter.Present(this, image)) { Suppress(); return; }
            _selection = key; _shown = artwork;
            if (!Visible) Show();
            if (artwork.Detection.Nodes.Any(anchor => artwork.Board.Nodes.Any(node => node.Row == anchor.Row
                && node.Column == anchor.Column && node.Kind == Soulcrest.Core.Daevanion.DaevanionNodeKind.Stat
                && node.Effects.Any(effect => effect.StatId is { } id && selected.Contains(id))))) _animation.Start();
            else _animation.Stop();
            BringForward();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        {
            Suppress();
            System.Diagnostics.Trace.TraceWarning("Daevanion highlights: {0}", error.GetType().Name);
        }
    }

    private Bitmap Raster(DaevanionSnapshot snapshot, IReadOnlySet<string> selected, Rectangle area, double phase)
    {
        // One working surface, never a retained frame history. Every frame is cleared and freshly painted.
        if (_surface is null || _surface.Size != area.Size)
        {
            _surface?.Dispose();
            _surface = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        using var graphics = Graphics.FromImage(_surface);
        graphics.Clear(Color.Transparent);
        _renderer.Paint(graphics, snapshot.Board, snapshot.Detection.Nodes, selected, area.Location, snapshot.Detection.Spacing, phase);
        return _surface;
    }

    // User request 2026-11-12: animation geometry does not depend on native active/unknown classification.
    internal static bool HasSameVisibleGeometry(DaevanionSnapshot previous, DaevanionSnapshot current)
    {
        if (!DaevanionOverlayForm.HasSameBoard(previous, current)
            || previous.Detection.Nodes.Count != current.Detection.Nodes.Count) return false;
        if (ReferenceEquals(previous, current) || ReferenceEquals(previous.Detection.Nodes, current.Detection.Nodes)) return true;
        Span<int> index = stackalloc int[225];
        if (!IndexNodes(current.Detection.Nodes, index)) return false;
        foreach (var node in previous.Detection.Nodes)
        {
            var key = CellKey(node);
            if (key < 0 || index[key] < 0 || current.Detection.Nodes[index[key]].Bounds != node.Bounds) return false;
            index[key] = -1;
        }
        return true;
    }

    internal static DaevanionSnapshot? ReconcileHighlight(Bitmap image, DaevanionSnapshot rendered,
        DaevanionSnapshot current, Point origin)
    {
        if (!DaevanionOverlayForm.HasSameBoard(rendered, current)) return null;
        if (ReferenceEquals(rendered, current) || HasSameVisibleGeometry(rendered, current)) return current;
        Span<int> index = stackalloc int[225];
        if (!IndexNodes(current.Detection.Nodes, index)) return null;
        var retained = new List<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor>(current.Detection.Nodes.Count);
        using var graphics = Graphics.FromImage(image);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        using var transparent = new SolidBrush(Color.Transparent);
        foreach (var node in rendered.Detection.Nodes)
        {
            var key = CellKey(node);
            if (key >= 0 && index[key] >= 0 && current.Detection.Nodes[index[key]].Bounds == node.Bounds)
            { retained.Add(current.Detection.Nodes[index[key]]); continue; }
            var frame = new Rectangle(node.Bounds.X - origin.X, node.Bounds.Y - origin.Y, node.Bounds.Width, node.Bounds.Height);
            var definition = rendered.Board.Nodes.FirstOrDefault(cell => cell.Row == node.Row && cell.Column == node.Column);
            var area = definition?.Kind == Soulcrest.Core.Daevanion.DaevanionNodeKind.Stat
                ? RectangleF.Union(frame, DaevanionNodeFitting.Bounds(frame, rendered.Detection.Spacing)) : frame;
            graphics.FillRectangle(transparent, Rectangle.FromLTRB((int)Math.Floor(area.Left), (int)Math.Floor(area.Top),
                (int)Math.Ceiling(area.Right), (int)Math.Ceiling(area.Bottom)));
        }
        return retained.Count == current.Detection.Nodes.Count ? current
            : current with { Detection = current.Detection with { Nodes = Array.AsReadOnly(retained.ToArray()) } };
    }

    private static int CellKey(Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor node) =>
        node.Row is >= 1 and <= 15 && node.Column is >= 1 and <= 15 ? (node.Row - 1) * 15 + node.Column - 1 : -1;

    private static bool IndexNodes(IReadOnlyList<Soulcrest.Ocr.Daevanion.DaevanionNodeAnchor> nodes, Span<int> index)
    {
        index.Fill(-1);
        for (var i = 0; i < nodes.Count; i++)
        {
            var key = CellKey(nodes[i]);
            if (key < 0 || index[key] >= 0) return false;
            index[key] = i;
        }
        return true;
    }

    private static double StopwatchPhase() => (System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency / 2.4) % 1;

    private void Animate()
    {
        if (_disposing || IsDisposed) return;
        if (!Visible) { _animation.Stop(); return; }
        // Animation consumes current evidence; its ticks can never extend the frame deadline.
        var current = _currentSource();
        if (current is null) { Suppress(); return; }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Present(current, _selectedStats(), _recordings, animationFrame: true);
        if (_disposing || IsDisposed || !_animation.Enabled) return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Leave the UI time for input on very large boards; wall-clock phase keeps the same rotation speed.
        _animation.Interval = elapsed > 40 ? 66
            : elapsed > 22 || (_animation.Interval > 33 && elapsed > 18) ? 50 : 33;
    }

    internal void Suppress()
    {
        if (_disposing || IsDisposed) return;
        _animation.Stop(); _shown = null; _selection = "";
        Hide(); _surface?.Dispose(); _surface = null;
    }

    private void ApplyRecordingVisibility(bool value)
    {
        _recordings = value;
        if (!IsHandleCreated || _appliedRecordings == value) return;
        NativeMethods.SetCaptureVisibility(Handle, value); _appliedRecordings = value;
    }

    private void BringForward()
    {
        // Newly shown node artwork must not cover the independent highlight layer.
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
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _recordings); _appliedRecordings = _recordings;
    }
    protected override void OnHandleDestroyed(EventArgs e)
    { _animation.Stop(); _shown = null; _appliedRecordings = null; base.OnHandleDestroyed(e); }
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
        { _disposing = true; _animation.Stop(); _animation.Dispose(); _shown = null; _surface?.Dispose(); _surface = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
