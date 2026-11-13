using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Explicit own WebView cold-start test; no product host, tracker or game/desktop capture.</summary>
[Collection("Settings file")]
public sealed class OverlayEditorDocumentReadyNativeTests
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task FirstLocalDocumentMustLoadBeforeTheSurfaceCanExecuteScripts() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation();
        OverlayEditorNativeTestHarness.EnsureFrameworkAssets();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        var firstDocumentPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2Deferral? firstDocument = null;
        CoreWebView2? browser = null;
        ulong? localNavigation = null;
        var documentLoaded = false;
        var browserCount = 0;
        var earlyScripts = 0;
        var scripts = 0;
        var before = OverlayEditorRenderDiagnostics.Current.Capture();
        using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: true, drawLockWidget: false,
            executeScript: (core, script) =>
            {
                scripts++;
                if (!documentLoaded)
                {
                    earlyScripts++;
                    // Reproduce the native invalid-state result deterministically while the real
                    // first document is blocked, rather than depending on startup timing luck.
                    throw new COMException("Synthetic document-not-ready state.", unchecked((int)0x8007139F));
                }
                return core.ExecuteScriptAsync(script);
            },
            browserInitialized: core =>
            {
                browserCount++;
                browser = core;
                core.NavigationStarting += (_, args) =>
                {
                    if (OverlayEditorBitmapRenderer.AllowsResource(args.Uri)) localNavigation = args.NavigationId;
                };
                core.DOMContentLoaded += (_, args) =>
                {
                    if (localNavigation == args.NavigationId && OverlayEditorBitmapRenderer.AllowsResource(core.Source))
                        documentLoaded = true;
                };
                core.AddWebResourceRequestedFilter("https://0.0.0.1/*", CoreWebView2WebResourceContext.Document);
                core.WebResourceRequested += (_, args) =>
                {
                    if (firstDocumentPaused.Task.IsCompleted || args.ResourceContext != CoreWebView2WebResourceContext.Document
                        || !OverlayEditorBitmapRenderer.AllowsResource(args.Request.Uri)) return;
                    firstDocument = args.GetDeferral();
                    firstDocumentPaused.TrySetResult();
                };
            });
        var window = new OverlayInstance
        {
            Id = "synthetic-delayed-document", Name = "Abyss",
            Settings = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.Abyss),
        };
        var snapshot = OverlaySnapshot.DemoFor("en");
        Task<Bitmap>? rendering = null;
        try
        {
            rendering = renderer.RenderAsync(window, snapshot);
            var first = await Task.WhenAny(firstDocumentPaused.Task, rendering).WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(0, earlyScripts);
            Assert.Same(firstDocumentPaused.Task, first);
            await Task.Delay(100);
            Assert.False(documentLoaded);
            Assert.Equal(0, earlyScripts);
            Assert.False(rendering.IsCompleted);
            Assert.NotNull(firstDocument);
            firstDocument.Complete();
            firstDocument = null;
            using var image = await rendering.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(documentLoaded);
            Assert.True(scripts > 0);
            Assert.Equal(0, earlyScripts);
            Assert.Equal(1, browserCount);
            Assert.Equal(OverlayEditorBitmapRenderer.Measure(window, snapshot), image.Size);
            Assert.NotNull(browser);
            using var value = JsonDocument.Parse(await browser.ExecuteScriptAsync(
                "document.querySelector('.overlay-widget-bound-kina .overlay-value-text')?.textContent.trim()"));
            Assert.Equal(snapshot.Metrics["bound-kina"].Value, value.RootElement.GetString());
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            Assert.True(png.Length > 1024, "The production render did not produce a populated PNG.");
            Assert.Equal(before.Failed, OverlayEditorRenderDiagnostics.Current.Capture().Failed);
            Assert.Equal(before.Succeeded + 1, OverlayEditorRenderDiagnostics.Current.Capture().Succeeded);
            SaveOptionalImage(image);
        }
        finally
        {
            // A failing pre-fix renderer may already have disposed the blocked native request.
            if (firstDocument is not null)
            {
                try { firstDocument.Complete(); }
                catch (Exception error) when (error is COMException or InvalidOperationException) { }
            }
            if (rendering is not null)
            {
                try { using var abandoned = await rendering; }
                catch (Exception) { /* Observe a fault after an earlier assertion failed. */ }
            }
        }
    });

    private static void SaveOptionalImage(Bitmap image)
    {
        var configured = Environment.GetEnvironmentVariable("SOULCREST_EDITOR_DOCUMENT_READY_PNG");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var target = Path.GetFullPath(configured);
        var allowed = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".png", Path.GetExtension(target), ignoreCase: true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        image.Save(target, ImageFormat.Png);
    }
}
