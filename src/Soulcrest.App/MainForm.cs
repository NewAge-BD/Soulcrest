using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Soulcrest.App.Components;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;

namespace Soulcrest.App;

public sealed class MainForm : Form
{
    public const string MapDataHost = "mapdata.soulcrest";
    private readonly OverlayHotkeyService _hotkeys;

    private readonly ServiceProvider _services;
    private readonly PetOverlayForm _overlay;
    private readonly BossOverlayForm _bossOverlay;
    private readonly LevelingOverlayForm _levelingOverlay;
    private readonly RouteOverlayForm _routeOverlay;
    private readonly ScanMarkerOverlayForm _scanMarkers;
    private readonly ExplorationScanOverlayForm _explorationOverlay;
    private readonly SettingsService _settings;
    private readonly TrackerService _tracker;
    private readonly PetScanService _scan;
    private readonly MapTrackingService _map;
    private readonly Capture.GameCaptureService _gameCapture;
    private readonly ExplorationScanService _explorationScan;
    private readonly CancellationTokenSource _watchdog = new();
    private readonly System.Windows.Forms.Timer _overlayInteractionTimer = new() { Interval = 25 };
    private bool _closing;
    private bool _cleanupStarted;
    private bool _trackingStopped;
    private bool _overlayKeyboardStarted;
    private bool _overlaysStarted;
    private bool? _lastAlt, _lastShift;

    public IServiceProvider Services => _services;

    public MainForm()
    {
        Text = $"Soulcrest – Aion 2 Pet-Begleiter (Tester {AppPaths.Version})";
        Size = new Size(1500, 950);
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 17, 23);
        var icon = Path.Combine(AppContext.BaseDirectory, "wwwroot", "soulcrest.ico");
        if (File.Exists(icon))
            Icon = new Icon(icon);

        RouteFile.EnsureDefaultLibrary(AppPaths.RoutesFile, AppPaths.LegacyRoutesFile);

        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<OverlayHotkeyService>();
        services.AddSingleton<OcrLanguageInstaller>();
        services.AddSingleton<Capture.GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<Network.NetworkLootService>();
        services.AddSingleton<Network.INetworkLootSource>(sp => sp.GetRequiredService<Network.NetworkLootService>());
        services.AddSingleton<TrackerService>();
        services.AddSingleton<PetScanService>();
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<ExplorationScanService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<BossRushService>();
        services.AddSingleton<FarmedTargetCleaner>();
        services.AddSingleton<ExplorationArrivalService>();
        services.AddSingleton<CharacterDetectionService>();
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UiState>();
        _services = services.BuildServiceProvider();
        _settings = _services.GetRequiredService<SettingsService>();
        _hotkeys = _services.GetRequiredService<OverlayHotkeyService>();
        _hotkeys.Invoked += ExecuteOverlayHotkey;
        UpdateLanguage();
        _settings.Changed += UpdateLanguage;
        _tracker = _services.GetRequiredService<TrackerService>();
        _scan = _services.GetRequiredService<PetScanService>();
        _map = _services.GetRequiredService<MapTrackingService>();
        _gameCapture = _services.GetRequiredService<Capture.GameCaptureService>();
        _explorationScan = _services.GetRequiredService<ExplorationScanService>();
        var progress = _services.GetRequiredService<ProgressService>();
        RestoreWindowBounds();

        var webView = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = _services,
        };
        webView.RootComponents.Add<Main>("#app");
        // Diagnosis only (as in Grindcrest): SOULCREST_WEBVIEW_DEBUG_PORT opens DevTools on 127.0.0.1, so the
        // live page can be inspected. Off unless the variable is set.
        var debugPort = int.TryParse(Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT"), out var port) && port is > 1024 and < 65536 ? port : (int?)null;
        webView.BlazorWebViewInitializing += (_, e) =>
        {
            Directory.CreateDirectory(AppPaths.WebViewDirectory);
            e.UserDataFolder = AppPaths.WebViewDirectory;
            if (debugPort is not null)
                e.EnvironmentOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
                    $"--remote-debugging-port={debugPort} --remote-debugging-address=127.0.0.1");
        };
        webView.BlazorWebViewInitialized += (_, e) =>
        {
            if (_closing || _cleanupStarted) return;
            if (_overlaysStarted) StartOverlayKeyboard();
            // No browser suggestion lists over Soulcrest's own: WebView2 drew its autofill popup over
            // the map search as a box of dots (user report 2026-10-06).
            e.WebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            e.WebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            var mapData = progress.MapDataDirectory;
            if (mapData is not null)
                e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(MapDataHost, mapData, CoreWebView2HostResourceAccessKind.Allow);
        };
        Controls.Add(webView);

        _overlay = new PetOverlayForm(
            _services.GetRequiredService<ProgressService>(),
            _services.GetRequiredService<TrackerService>(),
            _settings,
            _services.GetRequiredService<PetScanService>(),
            _services.GetRequiredService<MapTargetsService>());
        _scanMarkers = new ScanMarkerOverlayForm(_services.GetRequiredService<PetScanService>(), _settings,
            _services.GetRequiredService<Capture.GameCaptureService>());
        _explorationOverlay = new ExplorationScanOverlayForm(_services.GetRequiredService<ExplorationScanService>(), _settings,
            _services.GetRequiredService<Capture.GameCaptureService>());
        _ = _services.GetRequiredService<ExplorationArrivalService>();
        _ = _services.GetRequiredService<FarmedTargetCleaner>(); // removes marks of pets farmed out of view
        _bossOverlay = new BossOverlayForm(_services.GetRequiredService<BossRushService>(), _settings, progress);
        _levelingOverlay = new LevelingOverlayForm(_services.GetRequiredService<MapTargetsService>(),
            _services.GetRequiredService<ExplorationService>(), _settings);
        _ = _services.GetRequiredService<CharacterDetectionService>(); // profile follows the character after loading screens
        // Loading screens end the tracking's rest in an instance at once (and start the short wait in a new one).
        _services.GetRequiredService<Network.NetworkLootService>().CharacterEntered += _ => _map.NoteLoadingScreen();
        _ = _services.GetRequiredService<DiagnosticsService>(); // runs along, writes logs\diagnose.txt
        _routeOverlay = new RouteOverlayForm(
            _services.GetRequiredService<MapTrackingService>(),
            _services.GetRequiredService<MapTargetsService>(),
            _settings,
            _services.GetRequiredService<ProgressService>());
        _overlayInteractionTimer.Tick += (_, _) => UpdateOverlayState();
        OverlayInteraction.Changed += ScheduleOverlayState;
    }

    private void UpdateLanguage()
    {
        if (_closing || _cleanupStarted || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(UpdateLanguage); return; }
        Text = UiText.F("Soulcrest – Aion 2 Pet-Begleiter (Tester {0})", AppPaths.Version);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
        if (_overlaysStarted && !_closing && !_cleanupStarted)
            StartOverlayKeyboard();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopOverlayKeyboard();
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (SmokeTest.Enabled || _closing || _cleanupStarted)
            return; // no overlay, no global hotkeys during the self-test
        _hotkeys.Attach(this);
        _overlaysStarted = true;
        StartOverlayKeyboard();
        UpdateOverlayState();
        if (_settings.Current.OverlayEnabled)
            _overlay.Show();
        _ = _bossOverlay.Handle;
        _ = _levelingOverlay.Handle;
        _ = _routeOverlay.Handle; // shown by itself while the map is tracked and targets are marked
        _ = _explorationOverlay.Handle;
        SetOverlayInteractionEnabled(OverlayInteraction.IsAltHeld);
        _overlayInteractionTimer.Start();
        // Like Grindcrest before tracking: ask Windows for capture without the yellow border (a prompt
        // only the first time), outside the capture lock; a bordered session is then restarted.
        _ = _gameCapture.PrepareBorderlessAsync();
        // Look for a new version on GitHub (user request 2026-10-07); the installer replaces the running files.
        var updates = _services.GetRequiredService<UpdateService>();
        updates.ExitRequested += () => BeginInvoke(() => { if (!IsDisposed) Close(); });
        updates.StartAutomaticCheck();
        var ui = _services.GetRequiredService<UiState>();
        UiResponsiveness.Watch(this, () => $"Tab {ui.Tab}, Tracking {(_map.Running ? (_map.Found ? "gefunden" : "sucht") : "aus")}", _watchdog.Token);
        _ = _scanMarkers.Handle; // shown by itself while a pet scan has cards to click
        _tracker.StartIfEnabled();
        if (_settings.Current.MapTrackingActive && _settings.Current.MapRegion is not null)
            _map.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (!_closing && !_cleanupStarted && OverlayInteraction.ProcessMessage(m.Msg, m.LParam))
            UpdateOverlayState();
        if (m.Msg == NativeMethods.WM_HOTKEY && !_closing && !_cleanupStarted)
        {
            _hotkeys.ProcessHotkey((int)m.WParam);
        }
        base.WndProc(ref m);
    }

    internal void SetOverlayInteractionEnabled(bool enabled)
    {
        if (_cleanupStarted) return;
        _overlay.SetInteractionEnabled(enabled);
        _bossOverlay.SetInteractionEnabled(enabled);
        _levelingOverlay.SetInteractionEnabled(enabled);
    }

    internal void UpdateOverlayState()
    {
        if (_closing || _cleanupStarted) return;
        // Follow the minimap's existing detection/hold rules, including menu transitions.
        // Disabled tracking cannot establish a hidden minimap and must leave the HUD usable.
        var minimapHidden = _map.Running && !_map.Found && !_map.WorldMapOpen;
        var escMenuOpen = _map.Running && _map.EscMenuOpen;
        var suppressed = _map.WorldMapOpen || _map.InInstance || minimapHidden || escMenuOpen;
        _overlay.SetSuppressed(suppressed);
        _levelingOverlay.SetSuppressed(suppressed);
        _bossOverlay.SetSuppressed(minimapHidden || escMenuOpen);
        _routeOverlay.SetSuppressed(escMenuOpen);
        var alt = OverlayInteraction.IsAltHeld;
        var shift = OverlayInteraction.IsShiftHeld;
        SetOverlayInteractionEnabled(alt);
        if (_lastAlt != alt || _lastShift != shift)
        {
            _lastAlt = alt;
            _lastShift = shift;
            LogFile.Append("overlay-input.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} overlay modifiers: alt={alt}, shift={shift}, suppressed={suppressed}; {OverlayInteraction.State}{Environment.NewLine}");
        }
    }

    private void ScheduleOverlayState()
    {
        if (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateOverlayState); }
        catch (InvalidOperationException) when (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) { }
    }

    private void StartOverlayKeyboard() => _overlayKeyboardStarted = OverlayInteraction.StartKeyboard(Handle);

    private void ExecuteOverlayHotkey(string action)
    {
        if (_closing || _cleanupStarted) return;
        switch (action)
        {
            case "loot.toggle": ToggleOverlay(); return;
            case "loot.unlock": if (_overlay.Visible) _overlay.ToggleManualInteraction(); return;
            case "boss.unlock": if (_bossOverlay.Visible) _bossOverlay.ToggleManualInteraction(); return;
            case "leveling.unlock": if (_levelingOverlay.Visible) _levelingOverlay.ToggleManualInteraction(); return;
        }
        _settings.Update(s => OverlayHotkeys.ToggleVisibility(s, action));
        UpdateOverlayState();
    }

    public void ToggleOverlay()
    {
        var enabled = !_settings.Current.OverlayEnabled;
        _settings.Update(s => s.OverlayEnabled = enabled);
        if (enabled) _overlay.Show();
        else _overlay.Hide();
        UpdateOverlayState();
    }

    /// <summary>Restores the saved window position and size (when it is still on a screen).</summary>
    private void RestoreWindowBounds()
    {
        if (_settings.Current.MainWindowBounds is not { } saved)
            return;
        var visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(saved.X + 40, saved.Y + 10, Math.Max(1, saved.Width - 80), 40)));
        if (!visible)
            return;
        StartPosition = FormStartPosition.Manual;
        Bounds = saved;
        if (_settings.Current.MainWindowMaximized)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        // The self-test runs hidden in the default size: never save that as the user's window.
        if (WindowState == FormWindowState.Minimized || SmokeTest.Enabled)
            return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var maximized = WindowState == FormWindowState.Maximized;
        if (_settings.Current.MainWindowBounds == bounds && _settings.Current.MainWindowMaximized == maximized)
            return;
        _settings.Update(s =>
        {
            s.MainWindowBounds = bounds;
            s.MainWindowMaximized = maximized;
        });
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // WebView disposal can pump window messages. A second WM_CLOSE must not resolve
        // services from a provider whose shutdown already started.
        if (_closing || _cleanupStarted) return;
        base.OnFormClosing(e);
        if (e.Cancel || _cleanupStarted) return;
        _closing = true;
        _overlaysStarted = false;
        _overlayInteractionTimer.Stop();
        StopOverlayKeyboard();
        SetOverlayInteractionEnabled(false);
        _watchdog.Cancel();
        SaveWindowBounds();
        _hotkeys.Detach();
        StopTracking();
        _overlay.Close();
        _bossOverlay.Close();
        _levelingOverlay.Close();
        _routeOverlay.Close();
        _scanMarkers.Close();
        _explorationOverlay.Close();
    }

    private void StopTracking()
    {
        if (_trackingStopped) return;
        _trackingStopped = true;
        _tracker?.Dispose();
        _scan?.Stop();
        _explorationScan?.Stop(); // resolved up front: the provider may already be disposed here
        _map?.Shutdown();
    }

    private void StopOverlayKeyboard()
    {
        if (!_overlayKeyboardStarted) return;
        _overlayKeyboardStarted = false;
        OverlayInteraction.StopKeyboard();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (_cleanupStarted) return;
        _cleanupStarted = true;
        _overlaysStarted = false;
        OverlayInteraction.Changed -= ScheduleOverlayState;
        _overlayInteractionTimer.Dispose();
        StopOverlayKeyboard();
        _settings.Changed -= UpdateLanguage;
        _hotkeys.Invoked -= ExecuteOverlayHotkey;
        _hotkeys.Detach();
        _closing = true;
        try
        {
            StopTracking();
            _overlay?.Dispose();
            _bossOverlay?.Dispose();
            _levelingOverlay?.Dispose();
            _routeOverlay?.Dispose();
            _scanMarkers?.Dispose();
            _explorationOverlay?.Dispose();
        }
        finally
        {
            // Blazor components still need their services while they unsubscribe and dispose.
            try { base.Dispose(true); }
            finally { _services?.Dispose(); }
        }
    }
}
