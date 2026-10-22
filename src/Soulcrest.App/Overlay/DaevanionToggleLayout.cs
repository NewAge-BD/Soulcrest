using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>Client-relative placement outside the complete board, independent of the visible node subset.</summary>
internal static class DaevanionToggleLayout
{
    internal static int DpiFor(Point screenPoint)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = screenPoint.X, Y = screenPoint.Y }, 2);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi is > 0 and <= 960 ? (int)dpi : 96;
    }

    internal static Rectangle Bounds(DaevanionSnapshot snapshot, int dpi = 96) =>
        Bounds(snapshot.Board, snapshot.Detection, snapshot.GameBounds.Size, dpi);

    internal static Rectangle Bounds(DaevanionBoard boardData, DaevanionBoardDetection geometry, Size clientSize, int dpi = 96)
    {
        var cells = boardData.Nodes;
        if (dpi <= 0 || dpi > 960 || clientSize.Width <= 0 || clientSize.Height <= 0 || cells.Count == 0
            || !double.IsFinite(geometry.Origin.X) || !double.IsFinite(geometry.Origin.Y)
            || !double.IsFinite(geometry.NodeSize) || geometry.NodeSize <= 0
            || !double.IsFinite(geometry.Spacing) || geometry.Spacing <= 0
            || cells.Any(n => n.Row is < 1 or > 15 || n.Column is < 1 or > 15)) return Rectangle.Empty;

        RectangleF? complete = null;
        foreach (var node in cells)
        {
            var x = geometry.Origin.X + (node.Column - 1) * geometry.Spacing - geometry.NodeSize / 2;
            var y = geometry.Origin.Y + (node.Row - 1) * geometry.Spacing - geometry.NodeSize / 2;
            if (Math.Abs(x) > int.MaxValue / 4d || Math.Abs(y) > int.MaxValue / 4d || geometry.NodeSize > int.MaxValue / 4d)
                return Rectangle.Empty;
            var frame = new RectangleF((float)x, (float)y, (float)geometry.NodeSize, (float)geometry.NodeSize);
            var area = node.Kind == DaevanionNodeKind.Stat && DaevanionNodeFitting.CanExtend(frame, geometry.Spacing)
                ? RectangleF.Union(frame, DaevanionNodeFitting.Bounds(frame, geometry.Spacing)) : frame;
            complete = complete is { } previous ? RectangleF.Union(previous, area) : area;
        }
        var board = complete!.Value;
        var width = (int)Math.Round(DaevanionToggleRenderer.LogicalWidth * dpi / 96d);
        var height = (int)Math.Round(DaevanionToggleRenderer.LogicalHeight * dpi / 96d);
        var gap = Math.Max(1, (int)Math.Round(8 * dpi / 96d));
        var client = new Rectangle(Point.Empty, clientSize);
        if (width > client.Width || height > client.Height) return Rectangle.Empty;
        var above = new Rectangle(Math.Clamp((int)Math.Floor(board.Right) - width, 0, client.Width - width),
            (int)Math.Floor(board.Top) - gap - height, width, height);
        if (client.Contains(above)) return above;
        var right = new Rectangle((int)Math.Ceiling(board.Right) + gap,
            Math.Clamp((int)Math.Floor(board.Top), 0, client.Height - height), width, height);
        return client.Contains(right) ? right : Rectangle.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
