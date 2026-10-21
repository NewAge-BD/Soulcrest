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
    private readonly AlertsOverlayForm _alertsOverlay;
    private readonly EventTimelineOverlayForm _eventTimelineOverlay;
    private readonly AlertNotificationsForm _alertNotifications;
    private readonly AlertSoundPlayer _alertSounds;
    private readonly AlertsService _alerts;
    private readonly LevelingOverlayForm _levelingOverlay;
    private readonly RouteOverlayForm _routeOverlay;
    private readonly MerchantOverlayForm _merchantOverlay;
    private readonly DaevanionOverlayForm _daevanionOverlay;
    private readonly SettingsService _settings;
    private readonly OverlayPlacementService _placement;
    private OverlayPlacementForm? _placementWindow;
    private bool _placementPresented;
    private readonly TrackerService _tracker;
    private readonly MapTrackingService _map;
    private readonly WorldContextService _worldContext;
    private readonly Capture.GameCaptureService _gameCapture;
    private IDisposable? _watchdog;
    private readonly System.Windows.Forms.Timer _overlayInteractionTimer = new() { Interval = 25 };
    private bool _closing;
    private bool _cleanupStarted;
    private bool _trackingStopped;
    private bool _overlayKeyboardStarted;
    private bool _overlaysStarted;
    private bool? _lastAlt, _lastShift;
    private bool _lootContextSuppressed;
    private int _contextUpdatePending;
    private long _foregroundCheckedAt;
    private bool _gameInForeground;

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
        services.AddSingleton<OverlayPlacementService>();
        services.AddSingleton<OverlayPreviewRenderer>();
        services.AddSingleton<Capture.GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<WorldContextService>();
        services.AddSingleton<Network.NetworkLootService>();
        services.AddSingleton<Network.INetworkLootSource>(sp => sp.GetRequiredService<Network.NetworkLootService>());
        services.AddSingleton<Network.INetworkMerchantSource>(sp => sp.GetRequiredService<Network.NetworkLootService>());
        services.AddSingleton<MerchantService>();
        services.AddSingleton<DaevanionService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<ExplorationService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<BossRushService>();
        services.AddSingleton<AlertsService>();
        services.AddSingleton<AlertSoundPlayer>();
        services.AddSingleton<FarmedTargetCleaner>();
        services.AddSingleton<NavigationArrivalService>();
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
        _worldContext = _services.GetRequiredService<WorldContextService>();
        _worldContext.Changed += ScheduleOverlayContext;
        _map = _services.GetRequiredService<MapTrackingService>();
        _gameCapture = _services.GetRequiredService<Capture.GameCaptureService>();
        var progress = _services.GetRequiredService<ProgressService>();
        _merchantOverlay = new MerchantOverlayForm(_services.GetRequiredService<MerchantService>(), _settings, _gameCapture);
        _daevanionOverlay = new DaevanionOverlayForm(_services.GetRequiredService<DaevanionService>(), _settings);
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
            _services.GetRequiredService<MapTargetsService>());
        _ = _services.GetRequiredService<NavigationArrivalService>();
        _ = _services.GetRequiredService<FarmedTargetCleaner>(); // removes marks of pets farmed out of view
        _alerts = _services.GetRequiredService<AlertsService>();
        _alertSounds = _services.GetRequiredService<AlertSoundPlayer>();
        _alerts.NoticeRaised += DeliverAlertSound;
        _alertsOverlay = new AlertsOverlayForm(_alerts, _settings, progress.MapDataDirectory);
        _eventTimelineOverlay = new EventTimelineOverlayForm(_alerts, _services.GetRequiredService<BossRushService>(), _settings, _services.GetRequiredService<ProgressService>());
        _alertNotifications = new AlertNotificationsForm(_alerts, _settings);
        _bossOverlay = new BossOverlayForm(_services.GetRequiredService<BossRushService>(), _settings, progress, externalAlerts: true);
        _levelingOverlay = new LevelingOverlayForm(_services.GetRequiredService<MapTargetsService>(),
            _services.GetRequiredService<ExplorationService>(), _settings);
        _ = _services.GetRequiredService<CharacterDetectionService>(); // profile follows the character after loading screens
        _ = _services.GetRequiredService<DiagnosticsService>(); // runs along, writes logs\diagnose.txt
        _routeOverlay = new RouteOverlayForm(
            _services.GetRequiredService<MapTrackingService>(),
            _services.GetRequiredService<MapTargetsService>(),
            _settings,
            _services.GetRequiredService<ProgressService>());
        _overlayInteractionTimer.Tick += (_, _) => UpdateOverlayInteractionState();
        OverlayInteraction.Changed += ScheduleOverlayInteraction;
        _placement = _services.GetRequiredService<OverlayPlacementService>();
        _placement.RefreshMonitors(Bounds);
        _placement.Changed += PlacementStateChanged;
        _map.Changed += ScheduleOverlayContext;
        _settings.Changed += ScheduleOverlayContext;
        ApplyOverlayContext();
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
        {
            StartOverlayKeyboard();
            ApplyOverlayContext();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopOverlayKeyboard();
        Interlocked.Exchange(ref _contextUpdatePending, 0);
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
        _ = _alertsOverlay.Handle;
        _ = _eventTimelineOverlay.Handle;
        _ = _alertNotifications.Handle;
        _ = _levelingOverlay.Handle;
        _ = _routeOverlay.Handle; // shown by itself while the map is tracked and targets are marked
        _merchantOverlay.Start();
        _daevanionOverlay.Start();
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
        _watchdog = UiResponsiveness.Watch(this, () => $"Tab {ui.Tab}, Tracking {(_map.Running ? (_map.Found ? "gefunden" : "sucht") : "aus")}");
        _tracker.StartIfEnabled();
        if (_settings.Current.MapTrackingActive && _settings.Current.MapRegion is not null)
            _map.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (!_closing && !_cleanupStarted && OverlayInteraction.ProcessMessage(m.Msg, m.LParam))
            UpdateOverlayInteractionState();
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
        _alertsOverlay.SetInteractionEnabled(enabled);
        _eventTimelineOverlay.SetInteractionEnabled(enabled);
        _alertNotifications.SetInteractionEnabled(enabled);
        _levelingOverlay.SetInteractionEnabled(enabled);
    }

    internal void UpdateOverlayState()
    {
        ApplyOverlayContext();
        UpdateOverlayInteractionState();
    }

    private void ApplyOverlayContext()
    {
        if (_closing || _cleanupStarted || _placement.Active) return;
        var networkEnabled = _settings.Current.LootTrackingEnabled && _worldContext.HasLocationFacts;
        var source = _worldContext.Snapshot;
        var networkInInstance = networkEnabled ? EffectiveFeaturePolicy.ConfirmedInstance(source) : null;
        var mode = EffectiveFeaturePolicy.TrackingMode(networkEnabled, source);
        var context = new OverlayContext(_map.Running, _map.Found, _map.WorldMapOpen, _map.EscMenuOpen,
            networkInInstance ?? (!networkEnabled && _map.InInstance), networkInInstance)
            { VisualObservationSuspended = mode == MapTrackingMode.NetworkSuspended };
        bool Suppressed(string id) => OverlayVisibilityPolicy.IsSuppressed(_settings.Current, id, context);
        _lootContextSuppressed = Suppressed("loot");
        _overlay.SetSuppressed(_lootContextSuppressed);
        _levelingOverlay.SetSuppressed(Suppressed("leveling"));
        _bossOverlay.SetSuppressed(Suppressed("boss"));
        _alertsOverlay.SetSuppressed(Suppressed("alerts"));
        _alertNotifications.SetSuppressed(Suppressed("notifications"));
        _eventTimelineOverlay.SetSuppressed(Suppressed("event-timeline"));
        _routeOverlay.SetSuppressed(context.VisualObservationSuspended || _map.Running && _map.EscMenuOpen);
    }

    internal void UpdateOverlayInteractionState()
    {
        if (_closing || _cleanupStarted) return;
        if (_placement.Active) { SetOverlayInteractionEnabled(false); return; }
        if (_overlaysStarted)
        {
            var now = Environment.TickCount64;
            if (_foregroundCheckedAt == 0 || now - _foregroundCheckedAt >= 100)
            {
                _gameInForeground = Soulcrest.App.Capture.MonitorCapture.ForegroundGameWindowBounds() is not null;
                _foregroundCheckedAt = now;
            }
            UpdateForegroundVisibility(_gameInForeground);
        }
        var alt = OverlayInteraction.IsAltHeld;
        var shift = OverlayInteraction.IsShiftHeld;
        SetOverlayInteractionEnabled(alt);
        if (_lastAlt != alt || _lastShift != shift)
        {
            _lastAlt = alt;
            _lastShift = shift;
            LogFile.Append("overlay-input.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} overlay modifiers: alt={alt}, shift={shift}, suppressed={_lootContextSuppressed}; {OverlayInteraction.State}{Environment.NewLine}");
        }
    }

    internal void UpdateForegroundVisibility(bool gameInForeground)
    {
        var suppressed = OverlayVisibilityPolicy.IsForegroundSuppressed(_settings.Current, gameInForeground);
        _overlay.SetForegroundSuppressed(suppressed);
        _bossOverlay.SetForegroundSuppressed(suppressed);
        _levelingOverlay.SetForegroundSuppressed(suppressed);
        _alertsOverlay.SetForegroundSuppressed(suppressed);
        _alertNotifications.SetForegroundSuppressed(suppressed);
        _eventTimelineOverlay.SetForegroundSuppressed(suppressed);
        _routeOverlay.SetForegroundSuppressed(suppressed);
        _merchantOverlay.SetForegroundSuppressed(suppressed);
        _daevanionOverlay.SetForegroundSuppressed(suppressed);
    }

    private void ScheduleOverlayContext()
    {
        if (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) return;
        // Queue UI-thread events too: visibility callbacks may publish a newer world/settings state
        // while the current context is still being applied to the remaining HUDs.
        if (Interlocked.Exchange(ref _contextUpdatePending, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _contextUpdatePending, 0);
                ApplyOverlayContext();
            });
        }
        catch (InvalidOperationException) when (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated)
        {
            Interlocked.Exchange(ref _contextUpdatePending, 0);
        }
    }

    private void ScheduleOverlayInteraction()
    {
        if (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateOverlayInteractionState); }
        catch (InvalidOperationException) when (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) { }
    }

    private void PlacementStateChanged()
    {
        if (_closing || _cleanupStarted || !IsHandleCreated || !_overlaysStarted || SmokeTest.Enabled) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(PlacementStateChanged); }
            catch (InvalidOperationException) when (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) { }
            return;
        }
        if (_placementPresented == _placement.Active) return;
        _placementPresented = _placement.Active;
        if (_placementPresented)
        {
            try
            {
                SetOverlayPlacementHidden(true);
                _placementWindow = new OverlayPlacementForm(_settings, _placement, _services.GetRequiredService<OverlayPreviewRenderer>());
                _placementWindow.FormClosed += (_, _) => _placement.Stop();
                _placementWindow.Show(this);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException
                or System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                LogFile.Append("overlay-placement.log", $"{DateTime.Now:O} placement startup: {error.GetType().Name}{Environment.NewLine}");
                _placement.Fail("Place Mode konnte nicht geöffnet werden.");
            }
        }
        else
        {
            var window = _placementWindow;
            _placementWindow = null;
            if (window is { IsDisposed: false, PlacementClosing: false }) { window.Close(); window.Dispose(); }
            UpdateOverlayState();
            SetOverlayPlacementHidden(false);
        }
    }

    private void SetOverlayPlacementHidden(bool hidden)
    {
        _overlay.SetPlacementHidden(hidden);
        _bossOverlay.SetPlacementHidden(hidden);
        _levelingOverlay.SetPlacementHidden(hidden);
        _alertsOverlay.SetPlacementHidden(hidden);
        _alertNotifications.SetPlacementHidden(hidden);
        _eventTimelineOverlay.SetPlacementHidden(hidden);
        _routeOverlay.SetPlacementHidden(hidden);
        _merchantOverlay.SetPlacementHidden(hidden);
        _daevanionOverlay.SetPlacementHidden(hidden);
    }

    private void StartOverlayKeyboard() => _overlayKeyboardStarted = OverlayInteraction.StartKeyboard(Handle);

    private void ExecuteOverlayHotkey(string action)
    {
        if (_closing || _cleanupStarted || _placement.Active) return;
        switch (action)
        {
            case "loot.toggle": ToggleOverlay(); return;
            case "loot.unlock": if (_overlay.Visible) _overlay.ToggleManualInteractionFromHotkey(); return;
            case "alerts.unlock": if (_alertsOverlay.Visible) _alertsOverlay.ToggleManualInteractionFromHotkey(); return;
            case "notifications.unlock": if (_alertNotifications.Visible) _alertNotifications.ToggleManualInteractionFromHotkey(); return;
            case "event-timeline.unlock": if (_eventTimelineOverlay.Visible) _eventTimelineOverlay.ToggleManualInteractionFromHotkey(); return;
            case "boss.unlock": if (_bossOverlay.Visible) _bossOverlay.ToggleManualInteractionFromHotkey(); return;
            case "leveling.unlock": if (_levelingOverlay.Visible) _levelingOverlay.ToggleManualInteractionFromHotkey(); return;
        }
        _settings.Update(s => OverlayHotkeys.ToggleVisibility(s, action));
        UpdateOverlayState();
    }

    private void DeliverAlertSound(AlertNotice notice)
    {
        if (!notice.PlaySound || SmokeTest.Enabled || _closing || _cleanupStarted || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_closing || _cleanupStarted || IsDisposed || notice.Expires <= _alerts.Now) return;
                _ = _alertSounds.Play(notice.Sound, notice.SoundFile);
            });
        }
        catch (InvalidOperationException) when (_closing || _cleanupStarted || IsDisposed || !IsHandleCreated) { }
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
        _placement.RefreshMonitors(Bounds);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // WebView disposal can pump window messages. A second WM_CLOSE must not resolve
        // services from a provider whose shutdown already started.
        if (_closing || _cleanupStarted)
        {
            return;
        }
        base.OnFormClosing(e);
        if (e.Cancel || _cleanupStarted) return;
        _closing = true;
        _placementWindow?.Close();
        _placement.Stop();
        _overlaysStarted = false;
        _overlayInteractionTimer.Stop();
        StopOverlayKeyboard();
        SetOverlayInteractionEnabled(false);
        StopWatchdog();
        SaveWindowBounds();
        _hotkeys.Detach();
        StopTracking();
        _overlay.Close();
        _bossOverlay.Close();
        _alertsOverlay.Close();
        _eventTimelineOverlay.Close();
        _alertNotifications.Close();
        _levelingOverlay.Close();
        _routeOverlay.Close();
        _merchantOverlay.Close();
        _daevanionOverlay.Close();
    }

    private void StopWatchdog() => Interlocked.Exchange(ref _watchdog, null)?.Dispose();

    private void StopTracking()
    {
        if (_trackingStopped) return;
        _trackingStopped = true;
        _tracker?.Dispose();
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
        StopWatchdog();
        _overlaysStarted = false;
        OverlayInteraction.Changed -= ScheduleOverlayInteraction;
        _placement.Changed -= PlacementStateChanged;
        _placementWindow?.Dispose();
        _placement.Stop();
        _overlayInteractionTimer.Dispose();
        StopOverlayKeyboard();
        _settings.Changed -= UpdateLanguage;
        _settings.Changed -= ScheduleOverlayContext;
        _map.Changed -= ScheduleOverlayContext;
        _alerts.NoticeRaised -= DeliverAlertSound;
        _worldContext.Changed -= ScheduleOverlayContext;
        _hotkeys.Invoked -= ExecuteOverlayHotkey;
        _hotkeys.Detach();
        _closing = true;
        try
        {
            StopTracking();
            _overlay?.Dispose();
            _bossOverlay?.Dispose();
            _alertsOverlay?.Dispose();
            _eventTimelineOverlay?.Dispose();
            _alertNotifications?.Dispose();
            _levelingOverlay?.Dispose();
            _routeOverlay?.Dispose();
            _merchantOverlay?.Dispose();
            _daevanionOverlay?.Dispose();
        }
        finally
        {
            // Blazor components still need their services while they unsubscribe and dispose.
            try { base.Dispose(true); }
            finally { _services?.Dispose(); }
        }
    }
}
