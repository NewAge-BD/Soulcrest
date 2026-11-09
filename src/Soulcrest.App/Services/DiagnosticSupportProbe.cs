using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Soulcrest.App.Capture;

namespace Soulcrest.App.Services;

/// <summary>Anonymous support metadata only. No images, process titles, identifiers, paths or permission requests.</summary>
internal static class DiagnosticSupportProbe
{
    internal static IReadOnlyList<(string Key, object? Value)> SystemFacts()
    {
        var facts = new List<(string, object?)>();
        AddMonitors(facts);
        AddWebView(facts);
        AddGame(facts);
        return facts;
    }

    private static void AddMonitors(List<(string, object?)> facts)
    {
        try
        {
            var observations = new List<(string, object?)>();
            var monitors = Screen.AllScreens;
            observations.Add(("monitorProbeAvailable", true)); observations.Add(("monitorCount", monitors.Length));
            // Bound the scalar export even with a large virtual-display installation.
            observations.Add(("monitorsOmitted", Math.Max(0, monitors.Length - 16)));
            for (var index = 0; index < Math.Min(monitors.Length, 16); index++)
            {
                var monitor = monitors[index];
                var prefix = "monitor" + (index + 1).ToString(CultureInfo.InvariantCulture);
                observations.Add((prefix + "Width", monitor.Bounds.Width)); observations.Add((prefix + "Height", monitor.Bounds.Height));
                observations.Add((prefix + "BitsPerPixel", monitor.BitsPerPixel));
                observations.Add((prefix + "ScalePercent", MonitorScale(monitor.Bounds)));
            }
            facts.AddRange(observations);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or COMException)
        {
            facts.Add(("monitorProbeAvailable", false));
        }
    }

    private static int? MonitorScale(System.Drawing.Rectangle bounds)
    {
        var centre = new NativePoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        var monitor = MonitorFromPoint(centre, 2);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX is > 0 and < 10000
            ? (int)Math.Round(dpiX * 100d / 96) : null;
    }

    private static void AddWebView(List<(string, object?)> facts)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            facts.Add(("webView2RuntimeInstalled", !string.IsNullOrWhiteSpace(version)));
            facts.Add(("webView2RuntimeVersion", NormalizeWebViewVersion(version)));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            facts.Add(("webView2RuntimeInstalled", false)); facts.Add(("webView2RuntimeVersion", null));
        }
        catch (Exception error) when (error is COMException or FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            facts.Add(("webView2RuntimeInstalled", null)); facts.Add(("webView2RuntimeVersion", null));
        }
    }

    internal static string? NormalizeWebViewVersion(string? value)
    {
        if (value is null || value.Length > 80) return null;
        var first = value.Split(' ', 2)[0];
        return first.Length <= 32 && Version.TryParse(first, out var version) ? version.ToString() : null;
    }

    private static void AddGame(List<(string, object?)> facts)
    {
        var elevated = CurrentProcessElevated();
        facts.Add(("soulcrestElevated", elevated));
        try
        {
            var games = Process.GetProcessesByName("AION2");
            var windows = 0; var minimized = 0; var windowQueries = 0; var elevationQueries = 0; var elevatedGames = 0;
            foreach (var game in games)
            {
                using (game)
                {
                    try
                    {
                        var window = game.MainWindowHandle;
                        windowQueries++;
                        if (window != 0) { windows++; if (IsIconic(window)) minimized++; }
                        var gameElevated = ProcessElevated(game.Id);
                        if (gameElevated is not null) { elevationQueries++; if (gameElevated == true) elevatedGames++; }
                    }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
                }
            }
            facts.Add(("gameProcessCount", games.Length)); facts.Add(("gameWindowQueryCount", windowQueries));
            facts.Add(("gameWindowAvailable", windows > 0 ? true : windowQueries == games.Length ? false : null));
            facts.Add(("gameWindowCount", windows)); facts.Add(("gameMinimizedCount", minimized));
            facts.Add(("gameElevationQueryCount", elevationQueries)); facts.Add(("gameElevatedCount", elevatedGames));
            facts.Add(("elevationMismatch", games.Length == 0 || elevated is null || elevated == false && elevationQueries != games.Length && elevatedGames == 0
                ? null : elevated == false && elevatedGames > 0));
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            facts.Add(("gameProcessCount", null)); facts.Add(("gameWindowAvailable", null)); facts.Add(("elevationMismatch", null));
        }
    }

    private static bool? CurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool? ProcessElevated(int processId)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
            try
            {
                return GetTokenInformation(token, 20 /* TokenElevation */, out var elevation, sizeof(int), out _)
                    ? elevation != 0 : null;
            }
            finally { _ = CloseHandle(token); }
        }
        finally { _ = CloseHandle(process); }
    }

    internal static string CaptureStage(string? stage) => stage switch
    {
        "Bereit" => "ready", "WGC schließen" => "wgc-close", "WGC starten / Fenster suchen" => "wgc-start",
        "WGC-Bild / GPU-Lesekopie" => "wgc-frame-copy", "GDI-Bildschirmkopie" => "gdi-copy",
        "WGC: Bildübergabe / Sperre" => "wgc-frame-lock", "WGC: Frame.Surface" => "wgc-frame-surface",
        "WGC: Surface-COM-Zugriff" => "wgc-surface-com", "D3D11: Texture.GetDesc" => "d3d-texture-description",
        "D3D11: CreateTexture2D" => "d3d-create-texture", "D3D11: CopySubresourceRegion" => "d3d-copy",
        "D3D11: Flush" => "d3d-flush", "WGC: Textur freigeben" => "wgc-release-texture",
        "D3D11: Map (DO_NOT_WAIT)" => "d3d-map", "OpenCV: Bild konvertieren" => "opencv-convert",
        "D3D11: Unmap" => "d3d-unmap", _ => "unknown"
    };

    internal static string CaptureMethod(string? method) => method switch
    {
        "Spielfenster (WGC)" => "wgc-window", "Monitor (WGC)" => "wgc-monitor", "GDI" => "gdi", _ => "unknown"
    };

    internal static (bool? Supported, string? Permission) BorderPermission()
    {
        try
        {
            if (!MonitorCapture.SupportsBorderSuppression) return (false, "unsupported");
            var access = MonitorCapture.CheckBorderlessAccess();
            return (true, access is { } status && Enum.IsDefined(status) ? status.ToString() : null);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or NotSupportedException) { return (null, null); }
    }

    private static readonly Regex HdrDescription = new(@"\AHDR, SDR-Weiß ([0-9]{1,5}) nits\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static (string Mode, int? SdrWhiteNits) CaptureColor(GameCaptureService.CaptureDiagnostics capture)
    {
        if (capture.Hdr is null) return capture.Bounds is not null || capture.FramesArrived > 0 ? ("sdr", null) : ("unknown", null);
        if (capture.Hdr.Length > 64) return ("unknown", null);
        var match = HdrDescription.Match(capture.Hdr);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var nits) && nits is > 0 and <= 10000
            ? ("hdr", nits) : ("unknown", null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
