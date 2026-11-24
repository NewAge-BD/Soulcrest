using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>Opt-in reusable native buffer for one animated layered window; never owns its HWND or visibility.</summary>
internal sealed class LayeredOverlaySurface : IDisposable
{
    private nint _dib, _bits, _memory, _previous;
    private Size _size;
    private bool _disposed;
    internal int AllocationCount { get; private set; }
    internal Size Size => _size;

    internal bool Present(Form target, Bitmap image)
    {
        if (!target.IsHandleCreated || target.IsDisposed || !TryCopyFrom(image)) return false;
        var screen = NativeMethods.GetDC(0);
        try
        {
            var destination = new NativeMethods.NativePoint(target.Left, target.Top);
            var source = new NativeMethods.NativePoint(0, 0);
            var size = new NativeMethods.NativeSize(_size.Width, _size.Height);
            var blend = new NativeMethods.BlendFunction
                { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
            var success = NativeMethods.UpdateLayeredWindow(target.Handle, screen, ref destination, ref size,
                _memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
            if (!success) Trace.TraceWarning("Overlay presentation failed (Win32 {0}).", Marshal.GetLastWin32Error());
            return success;
        }
        finally { NativeMethods.ReleaseDC(0, screen); }
    }

    internal unsafe bool TryCopyFrom(Bitmap image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.PixelFormat != PixelFormat.Format32bppPArgb)
            throw new ArgumentException("An animated surface requires premultiplied BGRA pixels.", nameof(image));
        if (!EnsureSize(image.Size)) return false;
        var rowBytes = checked(image.Width * 4);
        var data = image.LockBits(new(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            if (data.Stride == rowBytes)
                Buffer.MemoryCopy((void*)data.Scan0, (void*)_bits, (long)rowBytes * image.Height, (long)rowBytes * image.Height);
            else for (var row = 0; row < image.Height; row++)
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)row * data.Stride,
                    (byte*)_bits + (long)row * rowBytes, rowBytes, rowBytes);
        }
        finally { image.UnlockBits(data); }
        return true;
    }

    private bool EnsureSize(Size size)
    {
        if (_dib != 0 && _size == size) return true;
        Reset();
        if (size.Width <= 0 || size.Height <= 0) return false;
        var header = new NativeMethods.BitmapInfoHeader
            { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
        _dib = NativeMethods.CreateDIBSection(0, ref header, 0, out _bits, 0, 0);
        if (_dib == 0 || _bits == 0) { Reset(); return false; }
        var screen = NativeMethods.GetDC(0);
        try { _memory = NativeMethods.CreateCompatibleDC(screen); }
        finally { NativeMethods.ReleaseDC(0, screen); }
        if (_memory == 0) { Reset(); return false; }
        _previous = NativeMethods.SelectObject(_memory, _dib);
        if (_previous == 0 || _previous == new nint(-1)) { Reset(); return false; }
        _size = size; AllocationCount++;
        return true;
    }

    internal void Reset()
    {
        if (_memory != 0 && _previous != 0 && _previous != new nint(-1)) NativeMethods.SelectObject(_memory, _previous);
        if (_dib != 0) NativeMethods.DeleteObject(_dib);
        if (_memory != 0) NativeMethods.DeleteDC(_memory);
        _dib = _bits = _memory = _previous = 0; _size = Size.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Reset(); GC.SuppressFinalize(this);
    }

    ~LayeredOverlaySurface() => Reset();
}
