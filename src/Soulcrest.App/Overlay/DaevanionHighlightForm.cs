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
        if (!animationFrame && _shown is { } shown && Visible && _selection == key && DaevanionOverlayForm.HasSameContent(shown, snapshot))
        {
            var current = _currentSource();
            if (current is null || !DaevanionOverlayForm.HasSameBoard(snapshot, current)) { Suppress(); return; }
            if (DaevanionOverlayForm.HasSameContent(snapshot, current))
            { _shown = current; ApplyRecordingVisibility(visibleInRecordings); BringForward(); return; }
            // A new tooltip mask needs a replacement surface, never an empty interval.
            snapshot = current;
        }
        try
        {
            var area = DaevanionOverlayForm.SurfaceBounds(snapshot);
            if (area.Width <= 0 || area.Height <= 0) { Suppress(); return; }
            var phase = StopwatchPhase();
            using var image = _renderer.Render(area.Size, snapshot.Board, snapshot.Detection.Nodes, selected,
                area.Location, snapshot.Detection.Spacing, phase);
            var current = _currentSource();
            if (current is null) { Suppress(); return; }
            var artwork = DaevanionOverlayForm.ReconcileRendered(image, snapshot, current, area.Location, includeStatEnvelope: true);
            if (artwork is null) { Suppress(); return; }
            var additions = current.Detection.Nodes.Where(node => !artwork.Detection.Nodes.Any(drawn =>
                drawn.Row == node.Row && drawn.Column == node.Column && drawn.Bounds == node.Bounds
                && drawn.Appearance == node.Appearance)).ToArray();
            if (additions.Length > 0)
            {
                using var graphics = Graphics.FromImage(image);
                _renderer.Paint(graphics, current.Board, additions, selected, area.Location, current.Detection.Spacing, phase);
            }
            var latest = _currentSource();
            if (latest is null) { Suppress(); return; }
            artwork = DaevanionOverlayForm.ReconcileRendered(image, current, latest, area.Location, includeStatEnvelope: true);
            if (artwork is null) { Suppress(); return; }
            _recordings = visibleInRecordings;
            var target = new Rectangle(latest.GameBounds.X + area.X, latest.GameBounds.Y + area.Y, area.Width, area.Height);
            if (Bounds != target) Bounds = target;
            _ = Handle;
            ApplyRecordingVisibility(visibleInRecordings);
            LayeredOverlayPresenter.Configure(this, true, 1);
            var final = _currentSource();
            if (final is null) { Suppress(); return; }
            artwork = DaevanionOverlayForm.ReconcileRendered(image, artwork, final, area.Location, includeStatEnvelope: true);
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
        Hide();
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
        { _disposing = true; _animation.Stop(); _animation.Dispose(); _shown = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
