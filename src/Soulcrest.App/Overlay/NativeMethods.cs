using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

internal static partial class NativeMethods
{
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int GWL_EXSTYLE = -20;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    internal const uint WDA_NONE = 0x0;

    /// <summary>Shows a window in screen captures or keeps it on the monitor only.</summary>
    internal static void SetCaptureVisibility(nint hwnd, bool visibleInRecordings) =>
        SetWindowDisplayAffinity(hwnd, visibleInRecordings ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x1;
    internal const uint MOD_CONTROL = 0x2;
    internal const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    internal const int ULW_ALPHA = 0x2;
    internal const byte AC_SRC_OVER = 0;
    internal const byte AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref NativePoint pptDst, ref NativeSize psize, nint hdcSrc,
        ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    /// <summary>BITMAPINFOHEADER; a negative height gives a top-down DIB.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateDIBSection(nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
}
