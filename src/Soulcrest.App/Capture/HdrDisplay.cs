using System.Runtime.InteropServices;

namespace Soulcrest.App.Capture;

/// <summary>
/// HDR state and SDR white level of a monitor (Windows display configuration), and the conversion of an
/// FP16 scRGB capture to 8-bit sRGB (user request 2026-10-07: HDR capture).
/// <para>
/// With HDR on, Windows Graphics Capture in BGRA8 gives a clipped, washed-out picture. In FP16 (scRGB,
/// 1.0 = 80 nits) the full range arrives. Soulcrest compares the minimap and pet portraits with SDR
/// references, so the picture is divided by the monitor's SDR white level: SDR content (the HUD) comes out
/// as it would on an SDR screen, and only highlights above SDR white are clipped. Grindcrest's fixed
/// Reinhard curve suits text recognition but shifts every tone, which the references would not match.
/// </para>
/// </summary>
internal static unsafe class HdrDisplay
{
    /// <summary>SDR white in scRGB units when Windows does not report it (its default SDR brightness, 200 nits).</summary>
    internal const double DefaultSdrWhite = 2.5;

    /// <summary>HDR on for the monitor, and its SDR white in scRGB units (null when it cannot be read).</summary>
    internal static (bool Hdr, double SdrWhite) Read(nint monitor)
    {
        try
        {
            var info = new MonitorInfoEx { Size = sizeof(MonitorInfoEx) };
            if (monitor == 0 || !GetMonitorInfoW(monitor, &info))
                return (false, 1);
            var device = new string(info.Device);
            if (GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out var pathCount, out var modeCount) != 0)
                return (false, 1);
            var paths = new PathInfo[pathCount];
            var modes = new byte[modeCount * 64];
            fixed (PathInfo* p = paths)
            fixed (byte* m = modes)
            {
                if (QueryDisplayConfig(2, ref pathCount, p, ref modeCount, m, 0) != 0)
                    return (false, 1);
            }
            for (var i = 0; i < pathCount; i++)
            {
                var source = new SourceDeviceName { Header = new Header { Type = 1, Size = (uint)sizeof(SourceDeviceName),
                    AdapterLow = paths[i].SourceAdapterLow, AdapterHigh = paths[i].SourceAdapterHigh, Id = paths[i].SourceId } };
                if (DisplayConfigGetDeviceInfo(&source.Header) != 0 || new string(source.GdiName) != device)
                    continue;
                var color = new AdvancedColorInfo { Header = new Header { Type = 9, Size = (uint)sizeof(AdvancedColorInfo),
                    AdapterLow = paths[i].TargetAdapterLow, AdapterHigh = paths[i].TargetAdapterHigh, Id = paths[i].TargetId } };
                var hdr = DisplayConfigGetDeviceInfo(&color.Header) == 0 && (color.Value & 2) != 0; // advancedColorEnabled
                if (!hdr)
                    return (false, 1);
                var white = new SdrWhiteLevel { Header = new Header { Type = 11, Size = (uint)sizeof(SdrWhiteLevel),
                    AdapterLow = paths[i].TargetAdapterLow, AdapterHigh = paths[i].TargetAdapterHigh, Id = paths[i].TargetId } };
                // SDRWhiteLevel: 1000 = 80 nits = scRGB 1.0.
                var level = DisplayConfigGetDeviceInfo(&white.Header) == 0 && white.Level >= 1000 ? white.Level / 1000.0 : DefaultSdrWhite;
                return (true, level);
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or ArgumentException)
        {
        }
        return (false, 1);
    }

    private static readonly Dictionary<int, byte[]> Lookups = [];

    /// <summary>All 65,536 half-float bit patterns → 8-bit sRGB for the given SDR white (cached).</summary>
    internal static byte[] Lookup(double sdrWhite)
    {
        var key = (int)Math.Round(sdrWhite * 1000);
        lock (Lookups)
        {
            if (Lookups.TryGetValue(key, out var cached))
                return cached;
            var table = new byte[65536];
            for (var bits = 0; bits < table.Length; bits++)
                table[bits] = Encode((float)BitConverter.UInt16BitsToHalf((ushort)bits), sdrWhite);
            Lookups[key] = table;
            return table;
        }
    }

    /// <summary>scRGB channel → sRGB byte: SDR white becomes 255, above it clips; NaN and negatives are black.</summary>
    internal static byte Encode(float value, double sdrWhite)
    {
        if (float.IsNaN(value) || value <= 0)
            return 0;
        var linear = Math.Min(1.0, value / sdrWhite);
        var encoded = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255), 0, 255);
    }

    /// <summary>
    /// One mapped RGBA16F texture (row pitch in bytes) to a new BGR picture; <paramref name="step"/> 2 takes
    /// every second pixel per direction (half size, a quarter of the work: the full-screen world map cost
    /// ~20 ms per picture before it was scaled to a quarter anyway).
    /// </summary>
    internal static OpenCvSharp.Mat ToBgr(nint data, uint rowPitch, int width, int height, byte[] lookup, int step = 1)
    {
        int rows = height / step, cols = width / step;
        var bgr = new OpenCvSharp.Mat(rows, cols, OpenCvSharp.MatType.CV_8UC3);
        for (var y = 0; y < rows; y++)
        {
            var source = (ushort*)((byte*)data + (nuint)(y * step) * rowPitch);
            var target = (byte*)bgr.Ptr(y);
            for (var x = 0; x < cols; x++)
            {
                var pixel = source + x * step * 4;
                target[x * 3] = lookup[pixel[2]];
                target[x * 3 + 1] = lookup[pixel[1]];
                target[x * 3 + 2] = lookup[pixel[0]];
            }
        }
        return bgr;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    // DISPLAYCONFIG_PATH_INFO (72 bytes): source info, target info, flags.
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public uint SourceAdapterLow;
        public int SourceAdapterHigh;
        public uint SourceId, SourceModeIndex, SourceStatus;
        public uint TargetAdapterLow;
        public int TargetAdapterHigh;
        public uint TargetId, TargetModeIndex;
        public int OutputTechnology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator;
        public int ScanLineOrdering, TargetAvailable;
        public uint TargetStatus, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public Header Header;
        public fixed char GdiName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public Header Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel
    {
        public Header Header;
        public uint Level;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int pathCount, PathInfo* paths, ref int modeCount, byte* modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(Header* request);
}
