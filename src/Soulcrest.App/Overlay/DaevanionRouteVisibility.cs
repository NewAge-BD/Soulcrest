using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Hover invalidates the edge evidence until a complete frame captured after pointer exit.</summary>
internal sealed class DaevanionRouteVisibility
{
    private DaevanionSnapshot? _board;
    private bool _hovered;
    private long _requiresFrameAfter;

    internal bool ConnectionsVisible { get; private set; }

    internal bool Observe(DaevanionSnapshot snapshot, bool pointerInsideBoard, long now)
    {
        if (_board is not null && !DaevanionOverlayForm.HasSameBoard(_board, snapshot)) Reset();
        _board = snapshot;
        if (pointerInsideBoard)
        {
            _hovered = true;
            _requiresFrameAfter = Math.Max(_requiresFrameAfter, now);
        }
        else if (_hovered)
        {
            _hovered = false;
            _requiresFrameAfter = Math.Max(_requiresFrameAfter, now);
        }
        ConnectionsVisible = !pointerInsideBoard && DaevanionLayoutStability.Fresh(snapshot.FrameAt, now)
            && snapshot.FrameAt > _requiresFrameAfter
            && DaevanionRouteRenderer.HasCompleteFrame(snapshot.Board, snapshot.Detection);
        return ConnectionsVisible;
    }

    internal void Reset()
    { _board = null; _hovered = false; _requiresFrameAfter = 0; ConnectionsVisible = false; }
}
