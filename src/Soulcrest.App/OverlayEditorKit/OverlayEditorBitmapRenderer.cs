using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Web.WebView2.Core;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Services;
using BrowserView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>UI-thread-only, local Blazor-to-bitmap adapter for the existing placement compositor.</summary>
public sealed class OverlayEditorBitmapRenderer : IOverlayEditorBitmapRenderer
{
    private readonly IServiceProvider _services;
    private readonly bool _pauseAnimations;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OverlayPlacementRenderState _model;
    private OverlayInstance? _publishedWindow;
    private OverlaySnapshot? _publishedSnapshot;
    private string? _readyRevision;
    private Task? _pendingCapture;
    private long _lastCaptureAt;
    private readonly List<string> _browserDiagnostics = [];
    private RenderHost? _host;
    private TaskCompletionSource<BrowserView>? _initialized;
    private bool _disposed;
    private int _calls;
    internal static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(15);

    public OverlayEditorBitmapRenderer(IServiceProvider services) : this(services, pauseAnimations: true) { }
    internal OverlayEditorBitmapRenderer(IServiceProvider services, bool pauseAnimations)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _pauseAnimations = pauseAnimations;
        _model = new(pauseAnimations);
    }

    public async Task<Bitmap> RenderAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyAccess();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(RenderTimeout);
        var entered = false;
        _calls++;
        try
        {
            await _gate.WaitAsync(timeout.Token);
            entered = true;
            timeout.Token.ThrowIfCancellationRequested();
            // A cancelled caller may leave a native capture finishing. Drain its completion only:
            // an obsolete frame's failure must not fail the new request or become a false timeout.
            if (_pendingCapture is { } pendingCapture)
            {
                await WaitForPreviousCaptureAsync(pendingCapture, timeout.Token);
                _pendingCapture = null;
            }
            if (!_pauseAnimations && _lastCaptureAt != 0)
            {
                var delay = TimeSpan.FromMilliseconds(100) - Stopwatch.GetElapsedTime(_lastCaptureAt);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, timeout.Token);
            }
            var normalized = window with { Settings = OverlayLayout.Normalize(window.Settings) };
            var size = Measure(normalized, snapshot);
            if (!Equals(_publishedWindow, window) || !ReferenceEquals(_publishedSnapshot, snapshot))
            {
                _model.Publish(normalized, snapshot);
                _publishedWindow = window;
                _publishedSnapshot = snapshot;
                _readyRevision = null;
            }
            var browser = await EnsureHostAsync(size, timeout.Token);
            await CalibratePixelsAsync(browser, timeout.Token);
            if (_readyRevision != _model.Revision)
            {
                await WaitForSurfaceAsync(browser.CoreWebView2, _model.Revision, size, timeout.Token);
                _readyRevision = _model.Revision;
            }
            _lastCaptureAt = Stopwatch.GetTimestamp();
            return await CaptureAsync(browser.CoreWebView2, size, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            if (entered) { await DumpDiagnosticsAsync(); ResetHost(); }
            throw new TimeoutException(UiText.T("Zeitüberschreitung beim Rendern des Editorfensters."));
        }
        catch (OperationCanceledException)
        {
            if (entered && (_pauseAnimations || _disposed)) ResetHost();
            throw;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
            or COMException or ArgumentException or TimeoutException or WebView2RuntimeNotFoundException)
        {
            if (entered) { await DumpDiagnosticsAsync(); ResetHost(); }
            throw new InvalidOperationException(UiText.T("Das Editorfenster konnte nicht gerendert werden."), error);
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
            if (--_calls == 0 && _disposed) _gate.Dispose();
        }
    }

    internal static Size Measure(OverlayInstance window, OverlaySnapshot snapshot)
    {
        var settings = OverlayLayout.Normalize(window.Settings);
        var chrome = OverlayWindowChrome.For(snapshot.ThemeId, settings.ShowBorder);
        // Output density is one physical pixel per CSS pixel. The caller applies Settings.Scale once.
        return new((int)Math.Ceiling(chrome.OuterWidth(settings.Width)), (int)Math.Ceiling(chrome.OuterHeight(settings.Height)));
    }

    internal static bool AllowsResource(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "0.0.0.1" && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private async Task<BrowserView> EnsureHostAsync(Size size, CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            _host.ResizeSurface(size);
            return await _initialized!.Task.WaitAsync(cancellationToken);
        }
        var page = Path.Combine(AppContext.BaseDirectory, "wwwroot", "overlay-placement-render.html");
        if (!File.Exists(page)) throw new FileNotFoundException("The local editor rendering page is missing.", page);
        var initialized = new TaskCompletionSource<BrowserView>(TaskCreationOptions.RunContinuationsAsynchronously);
        _initialized = initialized;
        var host = new RenderHost();
        _host = host;
        var view = new BlazorWebView { Dock = DockStyle.Fill, HostPage = page, Services = _services };
        view.RootComponents.Add<OverlayPlacementRenderSurface>("#app", new Dictionary<string, object?> { [nameof(OverlayPlacementRenderSurface.Model)] = _model });
        view.BlazorWebViewInitializing += (_, args) =>
        {
            Directory.CreateDirectory(AppPaths.WebViewDirectory);
            args.UserDataFolder = AppPaths.WebViewDirectory;
            // A shared profile requires exactly the same environment options as MainForm.
            if (int.TryParse(Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT"), out var port) && port is > 1024 and < 65536)
                args.EnvironmentOptions = new CoreWebView2EnvironmentOptions($"--remote-debugging-port={port} --remote-debugging-address=127.0.0.1");
        };
        void ObserveInitialization(Control? control)
        {
            if (control is not BrowserView browser) return;
            browser.DefaultBackgroundColor = Color.Transparent;
            browser.CoreWebView2InitializationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) initialized.TrySetException(args.InitializationException ?? new InvalidOperationException("WebView2 initialization failed."));
            };
        }
        view.ControlAdded += (_, args) => ObserveInitialization(args.Control);
        foreach (Control control in view.Controls) ObserveInitialization(control);
        view.BlazorWebViewInitialized += (_, args) =>
        {
            try
            {
                ConfigureBrowser(args.WebView);
                initialized.TrySetResult(args.WebView);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or ArgumentException)
            { initialized.TrySetException(error); }
        };
        host.Controls.Add(view);
        host.ResizeSurface(size);
        host.Show();
        return await initialized.Task.WaitAsync(cancellationToken);
    }

    private void ConfigureBrowser(BrowserView browser)
    {
        browser.DefaultBackgroundColor = Color.Transparent;
        browser.ZoomFactor = 1;
        var core = browser.CoreWebView2;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_EDITOR_RENDER_DIAGNOSTICS")))
        {
            foreach (var name in new[] { "Runtime.exceptionThrown", "Log.entryAdded" })
                core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, args) => _browserDiagnostics.Add(name + ": " + args.ParameterObjectAsJson);
            _ = EnableDiagnosticsAsync(core);
        }
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) => args.Cancel = !AllowsResource(args.Uri);
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!AllowsResource(args.Request.Uri))
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Local renderer only", "Content-Type: text/plain");
        };
    }

    private async Task EnableDiagnosticsAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        }
        catch (Exception error) { _browserDiagnostics.Add(error.ToString()); }
    }

    private async Task DumpDiagnosticsAsync()
    {
        var directory = Environment.GetEnvironmentVariable("SOULCREST_EDITOR_RENDER_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(directory) || _initialized?.Task.IsCompletedSuccessfully != true) return;
        try
        {
            var core = _initialized.Task.Result.CoreWebView2;
            var dump = await core.ExecuteScriptAsync("JSON.stringify({url:location.href,ready:document.readyState,body:document.body?.innerHTML,scripts:Array.from(document.scripts).map(s=>s.src),resources:performance.getEntriesByType('resource').map(r=>({name:r.name,size:r.transferSize})),blazor:typeof Blazor,transport:typeof window.external?.sendMessage})")
                .WaitAsync(TimeSpan.FromMilliseconds(700));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "renderer-diagnostics-" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture) + ".json"),
                JsonSerializer.Serialize(new { revision = _model.Revision, browser = JsonSerializer.Deserialize<string>(dump), events = _browserDiagnostics }));
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException or COMException) { }
    }

    private static async Task CalibratePixelsAsync(BrowserView browser, CancellationToken cancellationToken)
    {
        // WinForms exposes ZoomFactor, not its private controller. Measure the effective browser density.
        var encoded = await browser.CoreWebView2.ExecuteScriptAsync("window.devicePixelRatio").WaitAsync(cancellationToken);
        if (!double.TryParse(encoded, NumberStyles.Float, CultureInfo.InvariantCulture, out var density) || !double.IsFinite(density) || density <= 0)
            throw new InvalidOperationException("The browser pixel density is unavailable.");
        if (Math.Abs(density - 1) > .001)
        {
            browser.ZoomFactor = Math.Clamp(browser.ZoomFactor / density, .25, 5);
            await Task.Delay(40, cancellationToken);
        }
    }

    private static async Task WaitForSurfaceAsync(CoreWebView2 core, string revision, Size expected, CancellationToken cancellationToken)
    {
        while (await core.ExecuteScriptAsync("typeof window.soulcrestPlacementRender === 'object'").WaitAsync(cancellationToken) != "true")
            await Task.Delay(25, cancellationToken);
        var token = JsonSerializer.Serialize(revision);
        _ = await core.ExecuteScriptAsync($"window.soulcrestPlacementRender.begin({token})").WaitAsync(cancellationToken);
        while (true)
        {
            var encoded = await core.ExecuteScriptAsync($"window.soulcrestPlacementRender.status({token})").WaitAsync(cancellationToken);
            using var document = JsonDocument.Parse(encoded);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = document.RootElement;
                var state = root.GetProperty("state").GetString();
                if (state == "error") throw new InvalidOperationException(root.GetProperty("error").GetString());
                if (state == "ready")
                {
                    if (Math.Abs(root.GetProperty("density").GetDouble() - 1) > .02
                        || root.GetProperty("viewportWidth").GetInt32() != expected.Width
                        || root.GetProperty("viewportHeight").GetInt32() != expected.Height
                        || Math.Abs(root.GetProperty("left").GetDouble()) > .25 || Math.Abs(root.GetProperty("top").GetDouble()) > .25)
                        throw new InvalidOperationException("The browser did not render in logical 1:1 pixels.");
                    return;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private async Task<Bitmap> CaptureAsync(CoreWebView2 core, Size expected, CancellationToken cancellationToken)
    {
        // CapturePreview can return a stale/partially painted offscreen backbuffer.
        // A Chromium surface snapshot refreshes the raster and preserves per-pixel alpha;
        // fromSurface:false would flatten the transparent overlay onto an opaque view.
        var capture = core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
            "{\"format\":\"png\",\"fromSurface\":true,\"captureBeyondViewport\":false}");
        _pendingCapture = capture;
        try
        {
            var response = await capture.WaitAsync(cancellationToken);
            return OverlayEditorFrameDecoder.Decode(response, expected);
        }
        catch (OperationCanceledException)
        {
            // The native protocol operation itself is not cancellable. The next call
            // still waits for it above; observe a late failure without presenting it.
            _ = ObserveCaptureCompletionAsync(capture);
            throw;
        }
    }

    internal static async Task WaitForPreviousCaptureAsync(Task capture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ObserveCaptureCompletionAsync(capture).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ObserveCaptureCompletionAsync(Task capture)
    {
        try { await capture.ConfigureAwait(false); }
        catch (Exception) { /* This frame belongs to an abandoned request; only completion matters. */ }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The editor bitmap renderer must run on its owning WinForms UI thread.");
    }

    private void ResetHost()
    {
        var host = _host;
        _host = null;
        _readyRevision = null;
        _pendingCapture = null;
        _initialized?.TrySetCanceled();
        _initialized = null;
        host?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess();
        _disposed = true;
        _lifetime.Cancel();
        ResetHost();
        if (_calls == 0) _gate.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class RenderHost : Form
    {
        public RenderHost()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Soulcrest editor bitmap surface";
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */; return value; }
        }
        public void ResizeSurface(Size size)
        {
            ClientSize = size;
            var screen = SystemInformation.VirtualScreen;
            Location = new(screen.Left - size.Width - 64, screen.Top - size.Height - 64);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }
    }
}

/// <summary>Per-render-host data only. It does not reference presentation, settings or tracker services.</summary>
public sealed class OverlayPlacementRenderState(bool pauseAnimations = true)
{
    public bool PauseAnimations { get; } = pauseAnimations;
    public OverlayInstance Window { get; private set; } = new();
    public OverlaySnapshot Snapshot { get; private set; } = new();
    public string Revision { get; private set; } = "0";
    public event Action? Changed;
    private long _revision;
    internal void Publish(OverlayInstance window, OverlaySnapshot snapshot)
    {
        Window = window;
        Snapshot = snapshot;
        Revision = (++_revision).ToString(CultureInfo.InvariantCulture);
        Changed?.Invoke();
    }
}
