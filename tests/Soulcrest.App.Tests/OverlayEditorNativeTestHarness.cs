using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Explicit local WebView tests only; no product host, game sources or desktop capture.</summary>
internal static class OverlayEditorNativeTestHarness
{
    internal static void ValidateIsolation()
    {
        var configured = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_DATA"));
        Assert.Equal(configured, Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        var real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest"));
        Assert.False(string.Equals(real, configured, StringComparison.OrdinalIgnoreCase));
    }

    internal static void EnsureFrameworkAssets()
    {
        // testhost cannot discover Soulcrest's static assets through its own entry-assembly manifest.
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

    internal static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var previousPort = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
            Exception? failure = null;
            try
            {
                Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", null);
                using var context = new ApplicationContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                dispatcher.BeginInvoke(async () =>
                {
                    try { await action(); }
                    catch (Exception error) { failure = error; }
                    finally { context.ExitThread(); }
                });
                Application.Run(context);
            }
            catch (Exception error) { failure = error; }
            finally { Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", previousPort); }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
