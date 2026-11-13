using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;
using BrowserView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in production rasterization of synthetic values; no MainForm, tracker or game capture.</summary>
[Collection("Settings file")]
public sealed class OverlayEditorBoundKinaRenderDiagnostic
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task RenderUpdatedAbyssPresetInBothLanguages() => RunSta(async () =>
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_OVERLAY_BOUND_KINA_RENDER_DIRECTORY"));
        var permitted = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(permitted + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DATA")));
        Assert.False(string.Equals(Path.GetFullPath(AppPaths.DataDirectory), Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest")), StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(target);
        EnsureTestHostFrameworkAssets();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        var window = new OverlayInstance
        {
            Id = "synthetic-bound-kina", Name = "Abyss",
            Settings = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.Abyss),
        };
        Assert.DoesNotContain(window.Settings.Widgets, widget => widget.Kind is "dropped-kina" or "dropped-kina-hour");
        var originalLanguage = UiText.Language;
        var originalDebugPort = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", null);
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                var snapshot = OverlaySnapshot.DemoFor(language);
                Assert.NotEqual(snapshot.Metrics["dropped-kina"].Value, snapshot.Metrics["bound-kina"].Value);
                using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: true, drawLockWidget: false);
                using var image = await renderer.RenderAsync(window, snapshot);
                Assert.Equal(360, image.Width);
                Assert.Equal(400, image.Height);
                await AssertBrowserValues(renderer, snapshot);
                image.Save(Path.Combine(target, "abyss-bound-kina-" + language + ".png"), ImageFormat.Png);
            }
        }
        finally
        {
            UiText.Language = originalLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", originalDebugPort);
        }
    });

    private static async Task AssertBrowserValues(OverlayEditorBitmapRenderer renderer, OverlaySnapshot snapshot)
    {
        var field = typeof(OverlayEditorBitmapRenderer).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var initialized = Assert.IsType<TaskCompletionSource<BrowserView>>(field.GetValue(renderer));
        var browser = await initialized.Task;
        const string script = """
            ['bound-kina', 'bound-kina-hour'].map(kind => {
                const node = document.querySelector('.overlay-widget-' + kind);
                const value = node?.querySelector('.overlay-value-text');
                const bounds = value?.getBoundingClientRect();
                return { kind, value: value?.textContent.trim(), width: bounds?.width, height: bounds?.height,
                    icon: node?.querySelector('img')?.getAttribute('src'),
                    suffix: node?.querySelector('.overlay-hourly-suffix')?.textContent.trim() };
            })
            """;
        using var values = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync(script));
        foreach (var entry in values.RootElement.EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString()!;
            Assert.Equal(snapshot.Metrics[kind].Value, entry.GetProperty("value").GetString());
            Assert.True(entry.GetProperty("width").GetDouble() > 0);
            Assert.True(entry.GetProperty("height").GetDouble() > 0);
            Assert.EndsWith(OverlayCatalog.GameIconAsset(kind)!, entry.GetProperty("icon").GetString());
            if (kind.EndsWith("-hour", StringComparison.Ordinal)) Assert.Equal("/h", entry.GetProperty("suffix").GetString());
        }
    }

    private static void EnsureTestHostFrameworkAssets()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory,
            typeof(OverlayEditorBitmapRenderer).Assembly.GetName().Name + ".staticwebassets.runtime.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var roots = manifest.RootElement.GetProperty("ContentRoots");
        var framework = manifest.RootElement.GetProperty("Root").GetProperty("Children")
            .GetProperty("_framework").GetProperty("Children");
        foreach (var name in new[] { "blazor.webview.js", "blazor.modules.json" })
        {
            var destination = Path.Combine(AppContext.BaseDirectory, "wwwroot", "_framework", name);
            if (File.Exists(destination)) continue;
            var asset = framework.GetProperty(name).GetProperty("Asset");
            var source = Path.Combine(roots[asset.GetProperty("ContentRootIndex").GetInt32()].GetString()!,
                asset.GetProperty("SubPath").GetString()!);
            Assert.True(File.Exists(source), "The local build's WebView framework asset is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                Exception? failure = null;
                dispatcher.BeginInvoke(async () =>
                {
                    try { await action(); }
                    catch (Exception error) { failure = error; }
                    finally { context.ExitThread(); }
                });
                Application.Run(context);
                if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
