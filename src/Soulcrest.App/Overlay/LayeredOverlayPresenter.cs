using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>Shared per-pixel alpha presenter. It never owns content, geometry, visibility or input state.</summary>
internal static class LayeredOverlayPresenter
{
    private sealed record Mode(nint Handle, bool PerPixel);
    private static readonly ConditionalWeakTable<Form, Mode> Modes = new();

    internal static void Configure(Form target, bool perPixel, double legacyOpacity)
    {
        if (target.IsDisposed) return;
        if (!target.IsHandleCreated)
        {
            // WinForms Opacity < 1 -> 1 queries ExtendedWindowStyle and creates the HWND.
            // Clearing AllowTransparency instead resets its cached opacity without a native style read.
            if (perPixel) target.AllowTransparency = false;
            else target.Opacity = legacyOpacity;
            return;
        }
        if (Modes.TryGetValue(target, out var previous) && previous.Handle == target.Handle && previous.PerPixel == perPixel) return;
        // SetLayeredWindowAttributes (Form.Opacity) prevents UpdateLayeredWindow until the layered bit is reset.
        // Reset only that bit: the HWND, capture affinity, owner and click-through bits remain intact.
        var style = NativeMethods.GetWindowLongPtr(target.Handle, NativeMethods.GWL_EXSTYLE);
        target.Opacity = 1;
        NativeMethods.SetWindowLongPtr(target.Handle, NativeMethods.GWL_EXSTYLE, style & ~NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetWindowLongPtr(target.Handle, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_LAYERED);
        if (!perPixel) target.Opacity = legacyOpacity;
        // WinForms UpdateStyles can rewrite externally configured click-through when opacity changes.
        NativeMethods.SetWindowLongPtr(target.Handle, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_LAYERED);
        Modes.Remove(target);
        Modes.Add(target, new(target.Handle, perPixel));
    }

    internal static bool Present(Form target, Bitmap image)
    {
        if (!target.IsHandleCreated || target.IsDisposed || image.Width <= 0 || image.Height <= 0) return false;
        var header = new NativeMethods.BitmapInfoHeader
        { Size = 40, Width = image.Width, Height = -image.Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (dib == 0) return false;
        try
        {
            using (var surface = new Bitmap(image.Width, image.Height, image.Width * 4, PixelFormat.Format32bppPArgb, bits))
            using (var graphics = Graphics.FromImage(surface))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(image, 0, 0);
            }
            var screen = NativeMethods.GetDC(0);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) { NativeMethods.ReleaseDC(0, screen); return false; }
            var old = NativeMethods.SelectObject(memory, dib);
            try
            {
                var destination = new NativeMethods.NativePoint(target.Left, target.Top);
                var source = new NativeMethods.NativePoint(0, 0);
                var size = new NativeMethods.NativeSize(image.Width, image.Height);
                var blend = new NativeMethods.BlendFunction
                { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
                var success = NativeMethods.UpdateLayeredWindow(target.Handle, screen, ref destination, ref size, memory,
                    ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
                if (!success) Trace.TraceWarning("Overlay presentation failed (Win32 {0}).", Marshal.GetLastWin32Error());
                return success;
            }
            finally { NativeMethods.SelectObject(memory, old); NativeMethods.DeleteDC(memory); NativeMethods.ReleaseDC(0, screen); }
        }
        finally { NativeMethods.DeleteObject(dib); }
    }
}
