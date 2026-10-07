using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Soulcrest.App.Capture;

/// <summary>
/// Screen capture of the game window (or, without one, a monitor) via Windows Graphics Capture (GPU), adapted from Grindcrest's
/// GraphicsWindowFrameSource (docs/GRINDCREST_REUSE.md): the wanted rectangle is cut out on the GPU and
/// only that is read back. GDI read the whole desktop back for every picture: ~85 ms with the game
/// running, also for a quarter-size StretchBlt (measured 2026-10-03). Passive: screen pixels only.
/// Window capture contains only the game: Soulcrest's overlays never get into the tracking picture,
/// also when they are made visible for recordings (monitor capture fed the overlay back into the flow:
/// symbols lagged and wandered on the world map, user report 2026-10-03).
/// Not thread-safe: callers serialise (one D3D11 context).
/// </summary>
public sealed unsafe class MonitorCapture : IDisposable
{
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private const int WasStillDrawing = unchecked((int)0x887A000A);
    private const uint DoNotWait = 0x100000;

    private readonly object _gate = new();
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDirect3DDevice _projected;
    private nint _device;
    private nint _context;
    // Ring of staging textures: copy into one now, read the one copied a call earlier (already done by
    // the GPU). Waiting for the copy just made took ~30 ms behind the game's GPU work.
    private const int Slots = 3;
    private nint[] _staging = new nint[Slots];
    private readonly Dictionary<Size, nint[]> _stagingSets = new();
    private readonly bool[] _pending = new bool[Slots];
    private readonly long[] _order = new long[Slots];
    private Size _stagingSize;
    private Rectangle _lastCrop;
    private long _copies;
    private readonly long[] _copiedAt = new long[Slots];
    private Direct3D11CaptureFrame? _latest;
    private bool _disposed;

    private readonly nint _window;
    private readonly Rectangle _monitorBounds;
    // HDR (user request 2026-10-07): frames come as FP16 scRGB and are converted with the monitor's SDR white.
    private readonly DirectXPixelFormat _format;
    private readonly byte[]? _hdrLookup;
    private Windows.Graphics.SizeInt32 _poolSize;

    private MonitorCapture(Rectangle bounds, nint monitor, nint window)
    {
        _monitorBounds = bounds;
        _window = window;
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            // D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_BGRA_SUPPORT, SDK version 7.
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, &device, 0, &context));
            // WGC's free-threaded frame pool and our readers share this immediate context.
            // Our C# locks do not protect calls made internally by Windows.
            EnableMultithreadProtection(context);
            Marshal.ThrowExceptionForHR(QueryInterface(device, DxgiDeviceId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable));
            _projected = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            _device = device;
            _context = context;
            device = context = 0;
        }
        finally
        {
            Release(ref inspectable);
            Release(ref dxgi);
            Release(ref context);
            Release(ref device);
        }
        _item = window != 0 ? CreateItem(window, 3) : CreateItem(monitor, 4);
        _poolSize = _item.Size;
        (IsHdr, SdrWhite) = HdrDisplay.Read(window != 0 ? MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */) : monitor);
        _hdrLookup = IsHdr ? HdrDisplay.Lookup(SdrWhite) : null;
        _format = IsHdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_projected, _format, 2, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        // Like Grindcrest: the border can only be dropped before StartCapture; the permission was
        // requested beforehand (GameCaptureService.PrepareBorderlessAsync), never on this thread.
        IsBorderSuppressed = TryDisableBorder(_session);
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    /// <summary>Screen rectangle of the captured picture: the game window's frame, or the monitor.</summary>
    public Rectangle Bounds
    {
        get
        {
            if (_window == 0)
                return _monitorBounds;
            NativeRect rect;
            return DwmGetWindowAttribute(_window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, &rect, sizeof(NativeRect)) >= 0
                ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
                : Rectangle.Empty;
        }
    }

    public bool IsWindow => _window != 0;

    /// <summary>The monitor runs in HDR: frames are FP16 and converted (a change needs a new capture).</summary>
    public bool IsHdr { get; }

    /// <summary>SDR white of the HDR monitor in scRGB units (1.0 = 80 nits); 1 without HDR.</summary>
    public double SdrWhite { get; }

    /// <summary>Windows confirmed this session runs without the yellow border (permission allowed, no other demand).</summary>
    public bool IsBorderSuppressed { get; }

    private long _framesArrived;
    private long _lastFrameTicks;

    /// <summary>Frames delivered by Windows Graphics Capture so far (diagnosis: 0 = the capture gets no pictures).</summary>
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    /// <summary>When the last frame arrived (UTC), null before the first.</summary>
    public DateTime? LastFrameAt => Interlocked.Read(ref _lastFrameTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    /// <summary>Why the game window was not captured the last time (diagnosis), null when it was.</summary>
    public static string? LastProblem { get; private set; }

    /// <summary>
    /// Starts capturing the Aion 2 window (process AION2) when it covers <paramref name="screenArea"/>,
    /// otherwise the monitor; null when WGC is unavailable.
    /// </summary>
    public static MonitorCapture? TryStartForGame(Rectangle screenArea)
    {
        var window = FindGameWindow();
        if (window == 0)
            LastProblem = "Kein sichtbares Fenster des Prozesses AION2 gefunden.";
        if (window != 0)
        {
            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    LastProblem = "Windows Graphics Capture wird auf diesem System nicht unterstützt.";
                }
                else
                {
                    var capture = new MonitorCapture(Rectangle.Empty, 0, window);
                    if (capture.Bounds.IntersectsWith(screenArea))
                    {
                        LastProblem = null;
                        return capture;
                    }
                    LastProblem = $"Spielfenster {capture.Bounds} liegt nicht über dem gewählten Bereich {screenArea}.";
                    capture.Dispose();
                }
            }
            catch (Exception error) when (error is COMException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Typical: the game runs with administrator rights and Soulcrest does not (access denied).
                LastProblem = $"Aufnahme des Spielfensters abgelehnt: {error.GetType().Name}: {error.Message}";
                Trace.TraceWarning("Game window capture unavailable: {0}", error.Message);
            }
        }
        return TryStart(screenArea);
    }

    /// <summary>Main window of the game process (AION2.exe), the largest visible one.</summary>
    internal static Rectangle? GameWindowBounds()
    {
        var window = FindGameWindow();
        NativeRect rect;
        if (window == 0 || IsIconic(window) || DwmGetWindowAttribute(window, 9, &rect, sizeof(NativeRect)) < 0) return null;
        var result = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return result.Width > 0 && result.Height > 0 ? result : null;
    }

    private static nint FindGameWindow()
    {
        var best = (Window: (nint)0, Area: 0L);
        foreach (var process in Process.GetProcessesByName("AION2"))
        {
            using (process)
            {
                var handle = process.MainWindowHandle;
                if (handle == 0 || !IsWindowVisible(handle))
                    continue;
                NativeRect rect;
                if (DwmGetWindowAttribute(handle, 9, &rect, sizeof(NativeRect)) < 0)
                    continue;
                var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > best.Area)
                    best = (handle, area);
            }
        }
        return best.Window;
    }

    /// <summary>Starts capturing the monitor that contains <paramref name="screenArea"/>; null when WGC is unavailable.</summary>
    public static MonitorCapture? TryStart(Rectangle screenArea)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return null;
            var screen = Screen.FromRectangle(screenArea);
            var centre = new NativePoint(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
            var monitor = MonitorFromPoint(centre, 2 /* MONITOR_DEFAULTTONEAREST */);
            return new MonitorCapture(screen.Bounds, monitor, 0);
        }
        catch (Exception error) when (error is COMException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastProblem = (LastProblem is null ? "" : LastProblem + " ") + $"Monitoraufnahme nicht möglich ({error.Message}), Rückfall auf GDI.";
            Trace.TraceWarning("Windows Graphics Capture unavailable, falling back to GDI: {0}", error.Message);
            return null;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        var entered = false;
        try
        {
            if (_disposed) return;
            frame = sender.TryGetNextFrame();
            if (frame is null) return;
            Interlocked.Increment(ref _framesArrived);
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            // WGC runs this on its own worker. Do not wait behind a GPU readback:
            // Windows may need this callback to return before that read can finish.
            entered = Monitor.TryEnter(_gate);
            if (!entered || _disposed) return;
            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // Return ALL outstanding frames before rebuilding the pool. Never publish a
                // surface from the old pool or race a readback with Recreate.
                frame.Dispose();
                frame = null;
                _latest?.Dispose();
                _latest = null;
                Array.Clear(_pending);
                if (size.Width <= 0 || size.Height <= 0) return; // minimized/transitional
                sender.Recreate(_projected, _format, 2, size);
                _poolSize = size;
                return; // wait for a frame from the new pool
            }
            _latest?.Dispose();
            _latest = frame;
            frame = null; // ownership transferred to the reader
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            LastProblem = $"WGC-Bildempfang: {error.GetType().Name} (0x{error.HResult:X8}): {error.Message}";
            Trace.TraceWarning("{0}", LastProblem);
        }
        finally
        {
            if (entered) Monitor.Exit(_gate);
            try { frame?.Dispose(); }
            catch (ExternalException error) { Trace.TraceWarning("WGC frame release: {0}", error.Message); }
        }
    }

    private static void EnableMultithreadProtection(nint context)
    {
        nint protection = 0;
        try
        {
            Marshal.ThrowExceptionForHR(QueryInterface(context,
                new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"), out protection));
            // IUnknown(0..2), Enter(3), Leave(4), SetMultithreadProtected(5). BOOL is 32-bit.
            _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)Method(protection, 5))(protection, 1);
        }
        finally { Release(ref protection); }
    }

    /// <summary>
    /// The newest picture of a screen rectangle (screen coordinates) as BGR image, optionally scaled
    /// (e.g. 0.25 for the world map, OpenCV area filter). Waits up to 100 ms for the first frame; null if
    /// none arrived yet or the rectangle lies outside this monitor.
    /// </summary>
    public OpenCvSharp.Mat? CaptureMat(Rectangle screenArea, double scale = 1.0, Action<string>? stage = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var crop = Rectangle.Intersect(new Rectangle(screenArea.X - Bounds.X, screenArea.Y - Bounds.Y, screenArea.Width, screenArea.Height),
            new Rectangle(Point.Empty, Bounds.Size));
        if (crop.Width <= 0 || crop.Height <= 0)
            return null;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            stage?.Invoke("WGC: Bildübergabe / Sperre");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_latest is { } frame)
                {
                    stage?.Invoke("WGC: Frame.Surface");
                    return CopyCrop(frame.Surface, crop, scale, stage);
                }
            }
            if (waited.ElapsedMilliseconds > 100)
                return null;
            Thread.Yield();
        }
    }

    /// <summary>Like <see cref="CaptureMat"/>, as bitmap.</summary>
    public Bitmap? Capture(Rectangle screenArea, double scale = 1.0)
    {
        using var mat = CaptureMat(screenArea, scale);
        return mat is null ? null : Soulcrest.Ocr.BitmapMat.ToBitmap(mat);
    }

    private OpenCvSharp.Mat CopyCrop(IDirect3DSurface surface, Rectangle crop, double scale, Action<string>? stage)
    {
        nint access = 0, texture = 0;
        stage?.Invoke("WGC: Surface-COM-Zugriff");
        using var surfaceReference = WinRT.MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        try
        {
            Marshal.ThrowExceptionForHR(QueryInterface(surfaceReference.ThisPtr, SurfaceAccessId, out access));
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(access, 3);
            var textureId = TextureId;
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            stage?.Invoke("D3D11: Texture.GetDesc");
            TextureDescription description = default;
            ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)Method(texture, 10))(texture, &description);
            crop = Rectangle.Intersect(crop, new Rectangle(0, 0, (int)description.Width, (int)description.Height));
            if (_stagingSize != crop.Size)
            {
                Array.Clear(_pending);
                if (_stagingSets.TryGetValue(crop.Size, out var cached))
                {
                    _staging = cached;
                    _stagingSize = crop.Size;
                }
                else
                {
                    // Bounded cache: minimap, full frame, loot and scan crops can coexist.
                    if (_stagingSets.Count >= 6)
                    {
                        foreach (var set in _stagingSets.Values)
                            for (var i = 0; i < Slots; i++) Release(ref set[i]);
                        _stagingSets.Clear();
                    }
                    var created = new nint[Slots];
                    description.Width = (uint)crop.Width;
                    description.Height = (uint)crop.Height;
                    description.Usage = 3; // D3D11_USAGE_STAGING
                    description.BindFlags = description.MiscFlags = 0;
                    description.CpuAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
                    var create = (delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)Method(_device, 5);
                    try
                    {
                        for (var i = 0; i < Slots; i++)
                        {
                            nint staging = 0;
                            stage?.Invoke("D3D11: CreateTexture2D");
                            Marshal.ThrowExceptionForHR(create(_device, &description, 0, &staging));
                            created[i] = staging;
                        }
                    }
                    catch
                    {
                        for (var i = 0; i < Slots; i++) Release(ref created[i]);
                        _stagingSize = Size.Empty;
                        throw;
                    }
                    _staging = created;
                    _stagingSets.Add(crop.Size, created);
                    _stagingSize = crop.Size;
                }
            }
            if (_lastCrop != crop) Array.Clear(_pending);
            _lastCrop = crop;

            // Submit this frame's copy into a free slot (or the oldest one).
            var slot = Array.IndexOf(_pending, false);
            if (slot < 0)
                slot = Array.IndexOf(_order, _order.Min());
            var box = new TextureBox { Left = (uint)crop.Left, Top = (uint)crop.Top, Right = (uint)crop.Right, Bottom = (uint)crop.Bottom, Back = 1 };
            // ID3D11DeviceContext::CopySubresourceRegion = 46, Map = 14, Unmap = 15, Flush = 111.
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, TextureBox*, void>)Method(_context, 46);
            stage?.Invoke("D3D11: CopySubresourceRegion");
            copy(_context, _staging[slot], 0, 0, 0, 0, texture, 0, &box);
            stage?.Invoke("D3D11: Flush");
            ((delegate* unmanaged[Stdcall]<nint, void>)Method(_context, 111))(_context);
            _pending[slot] = true;
            _order[slot] = ++_copies;
            _copiedAt[slot] = Stopwatch.GetTimestamp();

            // Read the oldest finished copy if it is fresh (a caller at 60 fps); occasional callers (pet
            // scan every 0.3 s, loot feed) wait for this call's copy instead of getting a picture 0.3 s old.
            var read = -1;
            for (var i = 0; i < Slots; i++)
            {
                if (!_pending[i] || i == slot)
                    continue;
                if (Stopwatch.GetElapsedTime(_copiedAt[i]).TotalMilliseconds > 50)
                {
                    _pending[i] = false; // stale: drop
                    continue;
                }
                if (read < 0 || _order[i] < _order[read])
                    read = i;
            }
            if (read < 0)
                read = slot;
            return Read(read, crop.Size, scale, stage);
        }
        finally
        {
            stage?.Invoke("WGC: Textur freigeben");
            Release(ref texture);
            Release(ref access);
        }
    }

    private OpenCvSharp.Mat Read(int slot, Size size, double scale, Action<string>? stage)
    {
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedTexture*, int>)Method(_context, 14);
        MappedTexture data = default;
        stage?.Invoke("D3D11: Map (DO_NOT_WAIT)");
        var started = Stopwatch.StartNew();
        while (true)
        {
            var result = map(_context, _staging[slot], 0, 1 /* D3D11_MAP_READ */, DoNotWait, &data);
            if (result >= 0)
                break;
            if (result != WasStillDrawing || started.ElapsedMilliseconds > 2000)
            {
                var error = Marshal.GetExceptionForHR(result)!;
                error.Data["CaptureStage"] = "D3D11: Map (DO_NOT_WAIT)";
                throw error;
            }
            // Not Sleep(1): without a raised timer resolution it sleeps 15.6 ms (measured: 31 ms per picture).
            Thread.Yield();
        }
        try
        {
            // Straight out of the mapped texture (its row pitch is wider than the picture): scale first,
            // then drop alpha, so a quarter-size world map never copies the full screen.
            stage?.Invoke("OpenCV: Bild konvertieren");
            if (_hdrLookup is not null)
            {
                var converted = HdrDisplay.ToBgr(data.Data, data.RowPitch, size.Width, size.Height, _hdrLookup);
                if (Math.Abs(scale - 1) < 1e-6)
                    return converted;
                using (converted)
                {
                    var scaled = new OpenCvSharp.Mat();
                    OpenCvSharp.Cv2.Resize(converted, scaled, new OpenCvSharp.Size(), scale, scale, OpenCvSharp.InterpolationFlags.Area);
                    return scaled;
                }
            }
            using var mapped = OpenCvSharp.Mat.FromPixelData(size.Height, size.Width, OpenCvSharp.MatType.CV_8UC4, data.Data, data.RowPitch);
            var bgr = new OpenCvSharp.Mat();
            if (Math.Abs(scale - 1) < 1e-6)
            {
                OpenCvSharp.Cv2.CvtColor(mapped, bgr, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
                return bgr;
            }
            using var small = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Resize(mapped, small, new OpenCvSharp.Size(), scale, scale, OpenCvSharp.InterpolationFlags.Area);
            OpenCvSharp.Cv2.CvtColor(small, bgr, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            stage?.Invoke("D3D11: Unmap");
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(_context, 15))(_context, _staging[slot], 0);
            _pending[slot] = false;
        }
    }

    /// <summary>Asks Windows once for capture without the yellow border (Windows 11; prompt only if needed).</summary>
    public static AppCapabilityAccessStatus? RequestBorderlessAccess()
    {
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
                return null;
            var initialization = RoInitialize(1);
            if (initialization < 0 && initialization != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE: already STA
                Marshal.ThrowExceptionForHR(initialization);
            nint className = 0, factory = 0, operation = 0;
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureAccess";
            var accessId = new Guid("743ed370-06ec-5040-a58a-901f0f757095");
            try
            {
                Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, &className));
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, &accessId, &factory));
                var request = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Method(factory, 6);
                Marshal.ThrowExceptionForHR(request(factory, 0 /* Borderless */, &operation));
                var pending = WinRT.MarshalInterface<Windows.Foundation.IAsyncOperation<AppCapabilityAccessStatus>>.FromAbi(operation);
                try { return pending.AsTask().GetAwaiter().GetResult(); }
                finally { pending.Close(); }
            }
            finally
            {
                Release(ref operation);
                Release(ref factory);
                if (className != 0) WindowsDeleteString(className);
                if (initialization >= 0) RoUninitialize();
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            Trace.TraceWarning("Borderless capture access could not be requested: {0}", error.Message);
            throw;
        }
    }

    public static bool SupportsBorderSuppression =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    /// <summary>Current permission for borderless capture without asking (null = cannot be checked).</summary>
    public static AppCapabilityAccessStatus? CheckBorderlessAccess()
    {
        try { return AppCapability.Create("graphicsCaptureWithoutBorder").CheckAccess(); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning("Windows capture permission could not be checked: {0}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// Grindcrest's TryDisableCaptureBorder: asks for no border on a session that has not started yet and
    /// reads back whether Windows dropped it. Windows keeps the border without permission or while another
    /// capture application requires it; the global OS setting is never changed.
    /// </summary>
    private static bool TryDisableBorder(GraphicsCaptureSession session)
    {
        // UniversalApiContract v12 is newer than the Windows 10 SDK target: documented ABI, no higher minimum OS.
        if (!SupportsBorderSuppression)
            return false;
        nint borderSession = 0;
        try
        {
            using var reference = WinRT.MarshalInspectable<GraphicsCaptureSession>.CreateMarshaler(session);
            Marshal.ThrowExceptionForHR(QueryInterface(reference.ThisPtr, new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be"), out borderSession));
            var setBorder = (delegate* unmanaged[Stdcall]<nint, byte, int>)Method(borderSession, 7);
            Marshal.ThrowExceptionForHR(setBorder(borderSession, 0));
            byte required = 1;
            var getBorder = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Method(borderSession, 6);
            Marshal.ThrowExceptionForHR(getBorder(borderSession, &required));
            return CheckBorderlessAccess() == AppCapabilityAccessStatus.Allowed && required == 0;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            Trace.TraceWarning("Capture border stays visible: {0}", error.Message);
            return false;
        }
        finally
        {
            Release(ref borderSession);
        }
    }

    /// <summary>IGraphicsCaptureItemInterop: slot 3 = CreateForWindow, slot 4 = CreateForMonitor.</summary>
    private static GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        nint className = 0, factory = 0, item = 0;
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
        var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, &className));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, &interopId, &factory));
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Method(factory, slot);
            Marshal.ThrowExceptionForHR(create(factory, handle, &itemId, &item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Release(ref item);
            Release(ref factory);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
        // Do not close the session while holding a lock its callback may need.
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        foreach (var set in _stagingSets.Values)
            for (var i = 0; i < Slots; i++) Release(ref set[i]);
        _stagingSets.Clear();
        _projected.Dispose();
        Release(ref _context);
        Release(ref _device);
    }

    private static nint Method(nint value, int slot) => (*(nint**)value)[slot];

    private static int QueryInterface(nint value, Guid id, out nint result)
    {
        result = 0;
        fixed (nint* pointer = &result)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(value, 0))(value, &id, pointer);
    }

    private static void Release(ref nint value)
    {
        var current = value;
        value = 0;
        if (current != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(current, 2))(current);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        internal uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedTexture
    {
        internal nint Data;
        internal uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureBox
    {
        internal uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint* device, nint selectedFeatureLevel, nint* context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, nint* graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsCreateString(string value, int length, nint* result);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoGetActivationFactory(nint className, Guid* interfaceId, nint* factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoInitialize(uint initializationType);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void RoUninitialize();
}
