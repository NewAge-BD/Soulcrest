using System.Diagnostics;
using System.Drawing;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

/// <summary>Two distinct window frames must agree. Repeated reads cannot confirm or refresh a board.</summary>
internal sealed class DaevanionLayoutStability
{
    private string? _identity;
    private Rectangle _bounds;
    private DaevanionBoardDetection? _reference;
    private long _frameAt;
    private bool _ready;

    internal DaevanionBoardDetection? Confirmed { get; private set; }
    internal long ConfirmedFrameAt => _ready ? _frameAt : 0;
    internal static bool Fresh(long at) => Fresh(at, Stopwatch.GetTimestamp());
    internal static bool Fresh(long at, long now) => at > 0 && at <= now
        && Stopwatch.GetElapsedTime(at, now) <= TimeSpan.FromSeconds(1);

    /// <summary>Frozen geometry is a hypothesis only; the locator must verify today's pixels independently.</summary>
    internal DaevanionBoardDetection? Preferred(string identity, Rectangle bounds) =>
        _ready && _identity == identity && _bounds == bounds && Fresh(_frameAt) ? _reference : null;

    /// <summary>A busy/missing read cannot prove closure or renew the last confirmed source frame.</summary>
    internal bool RetainAfterMissingCapture(string identity, Rectangle bounds, bool frameIsFresh)
    {
        if (_ready && _identity == identity && _bounds == bounds && frameIsFresh) return true;
        Reset();
        return false;
    }

    internal bool Update(string identity, Rectangle bounds, long at, DaevanionBoardDetection? found)
    {
        Confirmed = null;
        if (string.IsNullOrWhiteSpace(identity) || bounds.Width <= 0 || bounds.Height <= 0
            || found is null || string.IsNullOrWhiteSpace(found.Family) || !Fresh(at) || !double.IsFinite(found.Spacing) || found.Spacing <= 0
            || !double.IsFinite(found.NodeSize) || found.NodeSize <= 0 || found.Nodes.Count == 0
            || !double.IsFinite(found.Origin.X) || !double.IsFinite(found.Origin.Y)
            || !double.IsFinite(found.Confidence) || found.Confidence <= 0 || found.Confidence > 1
            || found.Nodes.Any(n => !double.IsFinite(n.Confidence) || n.Confidence <= 0 || n.Confidence > 1)
            || found.Nodes.Select(n => (n.Row, n.Column)).Distinct().Count() != found.Nodes.Count
            || found.Nodes.Any(n => n.Row is < 1 or > 15 || n.Column is < 1 or > 15 || n.Bounds.Width <= 0 || n.Bounds.Height <= 0 || n.Bounds.X < 0 || n.Bounds.Y < 0
                || (long)n.Bounds.X + n.Bounds.Width > bounds.Width || (long)n.Bounds.Y + n.Bounds.Height > bounds.Height)
            || at < _frameAt)
        { Reset(); return false; }

        var tolerance = Math.Max(2, found.NodeSize * .04);
        if (_reference is null || identity != _identity || bounds != _bounds || _reference.Family != found.Family
            || Math.Abs(_reference.Origin.X - found.Origin.X) > tolerance
            || Math.Abs(_reference.Origin.Y - found.Origin.Y) > tolerance
            || Math.Abs(_reference.Spacing - found.Spacing) * 14 > tolerance
            || Math.Abs(_reference.NodeSize - found.NodeSize) > tolerance)
        {
            _reference = found with { Nodes = Array.AsReadOnly(found.Nodes.Select(n => n with { }).ToArray()) }; _identity = identity; _bounds = bounds; _frameAt = at; _ready = false;
            return false;
        }
        if (at > _frameAt) { _frameAt = at; _ready = true; }
        if (!_ready) return false;
        // Keep confirmed geometry steady, but use only nodes independently visible in THIS frame.
        var nodes = found.Nodes.Select(n =>
        {
            var previous = _reference.Nodes.FirstOrDefault(r => r.Row == n.Row && r.Column == n.Column);
            if (previous is not null) return n with { Bounds = previous.Bounds };
            // A node that reappears after an occlusion still belongs to the frozen lattice.
            return n with { Bounds = new OpenCvSharp.Rect(
                (int)Math.Round(_reference.Origin.X + (n.Column - 1) * _reference.Spacing - _reference.NodeSize / 2),
                (int)Math.Round(_reference.Origin.Y + (n.Row - 1) * _reference.Spacing - _reference.NodeSize / 2),
                Math.Max(1, (int)Math.Round(_reference.NodeSize)), Math.Max(1, (int)Math.Round(_reference.NodeSize))) };
        }).ToArray();
        Confirmed = found with { Origin = _reference.Origin, Spacing = _reference.Spacing,
            NodeSize = _reference.NodeSize, Nodes = Array.AsReadOnly(nodes.OrderBy(n => n.Row).ThenBy(n => n.Column).ToArray()) };
        return true;
    }

    internal void Reset() { _identity = null; _reference = null; _frameAt = 0; _ready = false; Confirmed = null; }
}
