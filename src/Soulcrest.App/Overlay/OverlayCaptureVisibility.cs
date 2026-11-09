namespace Soulcrest.App.Overlay;

/// <summary>UI-thread state for one live window's successfully applied capture affinity.</summary>
internal sealed class OverlayCaptureVisibility
{
    private readonly Func<nint, bool, bool> _apply;
    private nint _handle;
    private bool _visibleInRecordings;
    private bool _applied;

    internal OverlayCaptureVisibility(Func<nint, bool, bool>? apply = null) =>
        _apply = apply ?? ((handle, visible) => NativeMethods.SetWindowDisplayAffinity(handle,
            visible ? NativeMethods.WDA_NONE : NativeMethods.WDA_EXCLUDEFROMCAPTURE));

    internal bool Apply(nint handle, bool visibleInRecordings)
    {
        if (_applied && _handle == handle && _visibleInRecordings == visibleInRecordings) return true;
        // A failed or throwing attempt never establishes a cached native state.
        _applied = false;
        if (!_apply(handle, visibleInRecordings)) return false;
        _handle = handle;
        _visibleInRecordings = visibleInRecordings;
        _applied = true;
        return true;
    }

    internal void Reset()
    {
        _applied = false;
        _handle = 0;
    }
}
