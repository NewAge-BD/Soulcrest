using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.Ocr;

namespace Soulcrest.App.Services;

/// <summary>One finding of the automatic check, with what to do about it.</summary>
public sealed record DiagnosticHint(bool Severe, string Text);

/// <summary>
/// Runs along (every 30 s) and writes what is needed to find capture and tracking problems on another
/// PC (user request 2026-10-04: a colleague saw a yellow border, 0 fps and no map preview) to
/// logs\diagnose.txt, and turns typical causes into hints shown in the app. Everything stays local;
/// "Diagnose-Paket erstellen" puts report, logs, settings and two small pictures of the chosen areas
/// into a ZIP on the desktop for the user to pass on.
/// </summary>
public sealed class DiagnosticsService : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private readonly SettingsService _settings;
    private readonly GameCaptureService _capture;
    private readonly TrackerService _loot;
    private readonly MapTrackingService _map;
    private readonly PetScanService _scan;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private long _framesBefore = -1;
    private long _gdiBefore = -1;
    private int _checking;
    private string? _lastReport;
    private string? _checkError;

    public DiagnosticsService(SettingsService settings, GameCaptureService capture, TrackerService loot, MapTrackingService map, PetScanService scan)
    {
        _settings = settings;
        _capture = capture;
        _loot = loot;
        _map = map;
        _scan = scan;
        _timer = new System.Threading.Timer(_ => Check(), null, TimeSpan.FromSeconds(10), Every);
    }

    public IReadOnlyList<DiagnosticHint> Hints { get; private set; } = [];
    public DateTime? CheckedAt { get; private set; }
    public string ReportPath => Path.Combine(AppPaths.LogsDirectory, "diagnose.txt");

    public event Action? Changed;

    /// <summary>Runs the check now and writes the report (also used by the "Jetzt prüfen" button).</summary>
    public void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            File.WriteAllText(ReportPath, CaptureReport() + "\nErweiterte Pruefung laeuft noch.", Encoding.UTF8);
            var hints = new List<DiagnosticHint>();
            var report = Collect(hints);
            Volatile.Write(ref _lastReport, report);
            Volatile.Write(ref _checkError, null);
            File.WriteAllText(ReportPath, report, Encoding.UTF8);
            lock (_gate)
            {
                Hints = hints;
                CheckedAt = DateTime.Now;
            }
            Changed?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Volatile.Write(ref _checkError, exception.ToString());
            Trace.TraceWarning("Diagnosis failed: {0}", exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>ZIP with report, logs, settings and pictures of the chosen areas on the desktop; returns its path.</summary>
    public string CreatePackage()
    {
        // System/OCR/process queries may be stuck. Export the independent capture snapshot now.
        _ = Task.Run(Check);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, $"Soulcrest-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        AddReport(zip, CaptureReport(), Volatile.Read(ref _lastReport), Volatile.Read(ref _checkError));
        if (File.Exists(AppPaths.SettingsFile))
            zip.CreateEntryFromFile(AppPaths.SettingsFile, "settings.json");
        if (Directory.Exists(AppPaths.LogsDirectory))
        {
            foreach (var file in Directory.GetFiles(AppPaths.LogsDirectory, "*.log"))
                AddTail(zip, file, "logs/" + Path.GetFileName(file), 2 * 1024 * 1024);
        }
        // Use already produced previews: a fresh Grab could hang on the very driver/lock
        // this package is meant to diagnose. Missing previews are useful evidence too.
        AddPreview(zip, "karten-bereich", _map.PreviewDataUrl);
        return path;
    }

    internal string CaptureReport()
    {
        var capture = _capture.Diagnostics;
        return $"Soulcrest-Aufnahmestatus {DateTime.Now:O}\nVersion: {AppPaths.Version}\n" +
            $"Methode: {capture.Method}\nBereich: {capture.Bounds}\n" +
            $"Schritt: {capture.Stage}\nSchritt seit (UTC): {capture.StageSince:O}\n" +
            $"WGC-Bilder: {capture.FramesArrived}\nLetztes Bild (UTC): {capture.LastFrameAt:O}\n" +
            $"Abrufe: {capture.Grabs}\nGDI-Abrufe: {capture.GdiFallbacks}\nGPU-bedingt übersprungene Bilder: {capture.BusyFrames}\n" +
            $"Aufnahmefehler: {capture.LastProblem ?? "keiner gemeldet"}\n" +
            $"Loot: {_loot.Status}\nLoot-Quelle: Netzwerk\n" +
            $"Karte: {_map.Status}\nKarten-FPS: {_map.Fps}\n" +
            $"Pet-Scan: {_scan.Status}\nPet-Scan läuft: {_scan.Running}\nPet-Scan Fehler: {_scan.StatusIsError}\nPet-Scan Bilder: {_scan.Frames}\n" +
            $"Erweiterte Pruefung aktiv: {Volatile.Read(ref _checking) != 0}\n";
    }

    internal static void AddReport(ZipArchive zip, string captureReport, string? fullReport, string? error)
    {
        using var writer = new StreamWriter(zip.CreateEntry("diagnose.txt").Open(), Encoding.UTF8);
        writer.WriteLine(captureReport);
        writer.WriteLine("[Letzter abgeschlossener Gesamtbericht]");
        writer.WriteLine(fullReport ?? "Noch kein Gesamtbericht abgeschlossen. Der Aufnahmestatus oben ist davon unabhaengig.");
        if (error is not null) writer.WriteLine("Fehler der erweiterten Pruefung: " + error);
    }

    internal static void AddPreview(ZipArchive zip, string name, string? dataUrl)
    {
        if (dataUrl is not null && (dataUrl.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)))
        {
            try
            {
                var bytes = Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
                using var entry = zip.CreateEntry($"bilder/{name}.{(dataUrl.StartsWith("data:image/png", StringComparison.Ordinal) ? "png" : "jpg")}").Open();
                entry.Write(bytes);
                return;
            }
            catch (FormatException) { }
        }
        using var note = new StreamWriter(zip.CreateEntry($"bilder/{name}-fehlt.txt").Open());
        note.Write("Noch keine gültige Vorschau vorhanden. Für die Diagnose wurde keine neue Aufnahme gestartet.");
    }

    private static void AddTail(ZipArchive zip, string file, string name, int maxBytes)
    {
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (source.Length > maxBytes)
            source.Seek(-maxBytes, SeekOrigin.End);
        using var entry = zip.CreateEntry(name).Open();
        source.CopyTo(entry);
    }

    private string Collect(List<DiagnosticHint> hints)
    {
        var s = new StringBuilder();
        void Line(string text) => s.AppendLine(text);
        Line($"Soulcrest-Diagnose {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"Version: {AppPaths.Version}");
        Line("");

        // ---- System
        Line("[System]");
        Line($"Windows: {WindowsVersion()}");
        var elevated = IsCurrentProcessElevated();
        Line($"Soulcrest mit Administratorrechten: {(elevated ? "ja" : "nein")}");
        Line($"WebView2: {WebView2Version()}");
        var ocr = WindowsOcrLineReader.AvailableLanguages();
        Line($"Oberflächensprache: {_settings.Current.UiLanguage}; OCR: {_settings.Current.EffectiveOcrLanguage}");
        Line($"Windows-OCR-Sprachen: {(ocr.Count == 0 ? "keine" : string.Join(", ", ocr))}");
        if (!ocr.Any(language => language.StartsWith("en", StringComparison.OrdinalIgnoreCase) || language.StartsWith("de", StringComparison.OrdinalIgnoreCase)))
            hints.Add(new(true, "Kein englisches oder deutsches Windows-OCR-Paket installiert. Bitte unter Windows → Sprache hinzufügen."));
        Line($"Npcap: {(NetworkLootService.NpcapInstalled ? "installiert" : "nicht installiert")}");
        Line("");

        Line("[Monitore]");
        foreach (var screen in Screen.AllScreens)
            Line($"{screen.DeviceName}: {screen.Bounds}{(screen.Primary ? " (Hauptbildschirm)" : "")}, Skalierung {Scaling(screen)} %, {screen.BitsPerPixel} bit");
        Line("");

        // ---- Game
        Line("[Spiel]");
        var games = GameProcesses();
        if (games.Count == 0)
        {
            Line("Kein Prozess AION2 gefunden.");
            var lookalikes = Process.GetProcesses()
                .Select(p => { using (p) { try { return (ProcessName: p.ProcessName, MainWindowTitle: p.MainWindowTitle); } catch (InvalidOperationException) { return (ProcessName: "", MainWindowTitle: ""); } } })
                .Where(p => p.MainWindowTitle.Contains("AION", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains("aion", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var (name, title) in lookalikes)
                Line($"  ähnlich: Prozess {name}, Fenster \"{title}\"");
            hints.Add(new(true, lookalikes.Count > 0
                ? $"Spiel unter einem anderen Prozessnamen gefunden ({string.Join(", ", lookalikes.Select(l => l.ProcessName).Distinct())}); Soulcrest sucht \"AION2\"."
                : "Aion 2 läuft nicht (kein Prozess AION2) – ohne Spielfenster nimmt Soulcrest den Monitor auf."));
        }
        foreach (var game in games)
        {
            Line($"Prozess AION2 PID {game.Pid}: Fenster \"{game.Title}\" {game.Bounds}, {game.Mode}{(game.Minimized ? ", minimiert" : "")}, Adminrechte: {game.Elevated}");
            if (game.Minimized)
                hints.Add(new(true, "Das Spielfenster ist minimiert – minimierte Fenster liefern keine Bilder."));
        }
        Line("");

        // ---- Stalls of the window and the map page
        var stalls = UiResponsiveness.Recent;
        Line("[Hänger]");
        if (stalls.Count == 0)
            Line("keine");
        foreach (var stall in stalls)
            Line($"{stall.At:HH:mm:ss} {stall.Part}: {stall.Duration.TotalMilliseconds:0} ms – {stall.Context}");
        if (stalls.Any(s => s.At > DateTime.Now.AddMinutes(-5) && s.Duration > TimeSpan.FromSeconds(2)))
            hints.Add(new(false, "Soulcrest hat in den letzten Minuten länger als 2 s nicht reagiert. Details stehen im Bericht unter [Hänger]."));
        Line("");

        // ---- Capture
        var capture = _capture.Diagnostics;
        Line("[Aufnahme]");
        Line($"Methode: {capture.Method}, Bereich der Aufnahme: {capture.Bounds?.ToString() ?? "–"}");
        Line($"Bilder von Windows: {capture.FramesArrived}, letztes: {(capture.LastFrameAt is { } at ? $"vor {(DateTime.UtcNow - at).TotalSeconds:0.0} s" : "noch keines")}");
        Line($"Abrufe: {capture.Grabs}, davon per GDI: {capture.GdiFallbacks}, GPU-bedingt übersprungen: {capture.BusyFrames}");
        Line($"Gelber Aufnahmerand: {(_capture.BorderSuppressed switch { true => "entfernt", false => "sichtbar", null => "–" })}, Windows-Freigabe: {(Capture.MonitorCapture.SupportsBorderSuppression ? Capture.MonitorCapture.CheckBorderlessAccess()?.ToString() ?? "unbekannt" : "nicht unterstützt")}");
        Line($"Letztes Problem: {capture.LastProblem ?? "–"}");
        Line($"Aufnahmeschritt: {capture.Stage}, seit {(DateTime.UtcNow - capture.StageSince).TotalSeconds:0.0} s");
        if (capture.Stage != "Bereit" && DateTime.UtcNow - capture.StageSince > TimeSpan.FromSeconds(5))
            hints.Add(new(true, $"Aufnahme wartet seit mehr als 5 Sekunden in: {capture.Stage}. Diagnose-Paket erstellen; die Meldung zeigt, wo die Verarbeitung festhängt."));
        var framesDelta = _framesBefore < 0 ? -1 : capture.FramesArrived - _framesBefore;
        var gdiDelta = _gdiBefore < 0 ? -1 : capture.GdiFallbacks - _gdiBefore;
        _framesBefore = capture.FramesArrived;
        _gdiBefore = capture.GdiFallbacks;
        Line($"Seit der letzten Prüfung: {framesDelta} Bilder, {gdiDelta} GDI-Abrufe");
        var capturing = _map.Running || _scan.Running;
        if (capture.Method != "Spielfenster (WGC)" && games.Count > 0)
        {
            hints.Add(new(true, $"Soulcrest nimmt nicht das Spielfenster auf, sondern {(capture.Method == "GDI" ? "per GDI-Bildschirmkopie" : "den ganzen Monitor (gelber Rahmen um den Bildschirm)")}. Grund: {capture.LastProblem ?? "unbekannt"}"));
            // Elevated game and a refused window capture: running Soulcrest elevated too is worth a try
            // (on the developer's PC an elevated game was captured fine, so this is not always the cause).
            if (games.Any(g => g.Elevated != "nein") && !elevated)
                hints.Add(new(true, "Aion 2 läuft mit Administratorrechten, Soulcrest nicht. Soulcrest testweise ebenfalls als Administrator starten."));
        }
        if (capturing && capture.Method != "GDI" && framesDelta == 0)
            hints.Add(new(true, "Die Aufnahme liefert keine aktuellen Bilder. Aufnahmeschritt und Zeitpunkt des letzten Bildes prüfen; aus diesem Befund allein lässt sich der Fenstermodus nicht als Ursache bestimmen."));
        if (capturing && gdiDelta > 0)
            hints.Add(new(false, "Soulcrest nimmt per GDI-Bildschirmkopie auf (langsamer; bei Vollbild oft schwarz)."));
        Line("");

        // ---- Trackers
        Line("[Loot-Tracker]");
        Line($"Quelle: Netzwerk, aktiviert: {_settings.Current.LootTrackingEnabled}, läuft: {_loot.Running}, Status: {_loot.Status}");
        if (_loot.Network is { } network)
            Line($"Netzwerk: {network.Connection ?? "–"}, {network.Packets} Pakete, {network.Messages} Nachrichten, Status: {network.Status}");
        Line("");

        Line("[Spieler-Tracking]");
        Line($"läuft: {_map.Running}, gefunden: {_map.Found}, {_map.Fps} fps, Suche alle {_map.DetectionInterval} ms, Methode: {_map.CaptureMethod}, Status: {_map.Status}");
        Line($"Kartenbereich: {_map.Region?.ToString() ?? "nicht markiert"}{OnScreen(_map.Region)}");
        if (_map.Region is null)
            hints.Add(new(false, "Kein Kartenbereich markiert (Optionen → Spieler-Tracking)."));
        else if (!Visible(_map.Region.Value))
            hints.Add(new(true, "Der Kartenbereich liegt außerhalb aller Monitore – neu markieren."));
        Line("");

        Line("[Pet-Fenster-Scan]");
        Line($"läuft: {_scan.Running}, Bereich: {_scan.CaptureRegion}, Bilder: {_scan.Frames}, Status: {_scan.Status}");
        if (_scan.StatusIsError) hints.Add(new(true, _scan.Status));
        if (_map.StatusIsError) hints.Add(new(true, _map.Status));
        if (_loot.StatusIsError) hints.Add(new(true, _loot.Status));
        if (_scan.Status == CaptureReadback.Waiting || _map.Status == CaptureReadback.Waiting || _loot.Status == CaptureReadback.Waiting)
            hints.Add(new(false, CaptureReadback.Waiting));
        Line("");

        Line("[Hinweise]");
        if (hints.Count == 0)
            Line("keine Auffälligkeiten");
        foreach (var hint in hints)
            Line((hint.Severe ? "! " : "- ") + hint.Text);
        return s.ToString();
    }

    private static bool Visible(Rectangle area) => Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(area));

    private static string OnScreen(Rectangle? area) =>
        area is { } rect ? (Visible(rect) ? $" auf {Screen.FromRectangle(rect).DeviceName}" : " (außerhalb aller Monitore!)") : "";

    private sealed record GameInfo(int Pid, string Title, Rectangle Bounds, string Mode, bool Minimized, string Elevated);

    private static List<GameInfo> GameProcesses()
    {
        var result = new List<GameInfo>();
        foreach (var process in Process.GetProcessesByName("AION2"))
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (window == 0)
                {
                    result.Add(new GameInfo(process.Id, "(kein Hauptfenster)", Rectangle.Empty, "–", false, Elevation(process.Id)));
                    continue;
                }
                _ = GetWindowRect(window, out var rect);
                var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                var style = GetWindowLongPtr(window, -16).ToInt64();
                const long Caption = 0x00C00000;
                var screen = Screen.FromHandle(window).Bounds;
                var mode = (style & Caption) == Caption ? "Fenster mit Rahmen"
                    : bounds == screen ? "randlos/Vollbild über den ganzen Monitor" : "randloses Fenster";
                result.Add(new GameInfo(process.Id, process.MainWindowTitle, bounds, mode, IsIconic(window), Elevation(process.Id)));
            }
        }
        return result;
    }

    private static string Elevation(int pid)
    {
        const uint QueryLimited = 0x1000, TokenQuery = 0x0008;
        var process = OpenProcess(QueryLimited, false, pid);
        if (process == 0)
            return "unbekannt";
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return "wahrscheinlich ja (Zugriff verweigert)";
            try
            {
                return GetTokenInformation(token, 20 /* TokenElevation */, out var elevation, 4, out _) ? (elevation != 0 ? "ja" : "nein") : "unbekannt";
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var name = key?.GetValue("ProductName") as string ?? "Windows";
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        // Windows 11 still reports "Windows 10" as product name; the build tells.
        if (int.TryParse(build, out var number) && number >= 22000)
            name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return $"{name} {key?.GetValue("DisplayVersion")} (Build {build}.{key?.GetValue("UBR")})";
    }

    private static string WebView2Version()
    {
        try
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "nicht gefunden";
        }
        catch (Exception exception) when (exception is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or COMException or FileNotFoundException)
        {
            return "nicht gefunden";
        }
    }

    private static int Scaling(Screen screen)
    {
        var centre = new NativePoint { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 };
        var monitor = MonitorFromPoint(centre, 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (int)Math.Round(dpi * 100 / 96.0) : 0;
    }

    public void Dispose() => _timer.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);
}
