using System.Drawing;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Capture;

/// <summary>Only native metadata of an existing window; no discovery or pixel reads.</summary>
internal interface IGameWindowMetadata
{
    bool IsWindow(nint window);
    bool IsVisible(nint window);
    bool IsMinimized(nint window);
    uint ProcessId(nint window);
    nint ForegroundWindow { get; }
    Rectangle? Bounds(nint window);
}

internal static class CapturedGameWindowMetadata
{
    internal static Rectangle? Read(nint window, uint processId, bool requireForeground, IGameWindowMetadata metadata)
    {
        bool Matches() => window != 0 && processId != 0 && metadata.IsWindow(window)
            && metadata.IsVisible(window) && !metadata.IsMinimized(window)
            && metadata.ProcessId(window) == processId && (!requireForeground || metadata.ForegroundWindow == window);
        if (!Matches()) return null;
        var bounds = metadata.Bounds(window);
        // A close, replacement by another process or foreground transition rejects this observation.
        return bounds is { Width: > 0, Height: > 0 } && Matches() ? bounds : null;
    }
}

internal interface IGameWindowBoundsSource
{
    Rectangle? TryGetCurrentGameWindowBounds(bool requireForeground);
}

/// <summary>Reads one existing source without taking the capture/readback lock.</summary>
internal sealed class CurrentGameWindowBounds(Func<IGameWindowBoundsSource?> currentSource, Func<bool> disposed)
{
    internal Rectangle? Read(bool requireForeground)
    {
        if (disposed()) return null;
        var source = currentSource();
        if (source is null) return null;
        var bounds = source.TryGetCurrentGameWindowBounds(requireForeground);
        return !disposed() && ReferenceEquals(source, currentSource()) ? bounds : null;
    }
}

internal sealed class NativeGameWindowMetadata : IGameWindowMetadata
{
    internal static readonly NativeGameWindowMetadata Instance = new();
    private NativeGameWindowMetadata() { }
    public bool IsWindow(nint window) => NativeIsWindow(window);
    public bool IsVisible(nint window) => IsWindowVisible(window);
    public bool IsMinimized(nint window) => IsIconic(window);
    public uint ProcessId(nint window) { _ = GetWindowThreadProcessId(window, out var processId); return processId; }
    public nint ForegroundWindow => GetForegroundWindow();
    public Rectangle? Bounds(nint window) => DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<NativeRect>()) >= 0
        ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
}
