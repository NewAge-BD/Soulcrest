using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in native renderer regressions without MainForm, tracking or game/desktop capture.</summary>
[Collection("Settings file")]
public sealed class OverlayEditorRenderCancellationTests
{
    private const int InvalidState = unchecked((int)0x8007139F);

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task CancellationBeforeTheNativeReadinessFailureDoesNotBecomeARenderError() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation(); OverlayEditorNativeTestHarness.EnsureFrameworkAssets();
        var services = new ServiceCollection(); services.AddLogging(); services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var before = OverlayEditorRenderDiagnostics.Current.Capture();
        using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: false, executeScript: (_, _) =>
        {
            attempts++;
            cancellation.Cancel();
            throw new COMException("Synthetic abandoned document state", InvalidState);
        });
        var error = await Record.ExceptionAsync(() => renderer.RenderAsync(Window(), OverlaySnapshot.Demo, cancellation.Token));
        Assert.Equal(1, attempts);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        var after = OverlayEditorRenderDiagnostics.Current.Capture();
        Assert.Equal(before.Failed, after.Failed);
        Assert.Equal(before.Succeeded, after.Succeeded);
    });

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task TheLiveRendererCanRenderAgainAfterAnAbandonedReadinessFailure() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation(); OverlayEditorNativeTestHarness.EnsureFrameworkAssets();
        var services = new ServiceCollection(); services.AddLogging(); services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var before = OverlayEditorRenderDiagnostics.Current.Capture();
        using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: false, executeScript: (core, script) =>
        {
            if (++attempts != 1) return core.ExecuteScriptAsync(script);
            cancellation.Cancel();
            throw new COMException("Synthetic abandoned document state", InvalidState);
        });
        var window = Window(); var snapshot = OverlaySnapshot.Demo;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(window, snapshot, cancellation.Token));
        using var next = await renderer.RenderAsync(window, snapshot, CancellationToken.None);
        Assert.Equal(240, next.Width); Assert.Equal(100, next.Height); Assert.True(attempts > 1);
        var after = OverlayEditorRenderDiagnostics.Current.Capture();
        Assert.Equal(before.Failed, after.Failed);
        Assert.Equal(before.Succeeded + 1, after.Succeeded);
    });

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task RendererDisposalBeforeTheNativeReadinessFailureRemainsLifetimeCancellation() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation(); OverlayEditorNativeTestHarness.EnsureFrameworkAssets();
        var services = new ServiceCollection(); services.AddLogging(); services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        OverlayEditorBitmapRenderer? current = null;
        var attempts = 0;
        var before = OverlayEditorRenderDiagnostics.Current.Capture();
        using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: false, executeScript: (_, _) =>
        {
            attempts++;
            current!.Dispose();
            throw new COMException("Synthetic disposed document state", InvalidState);
        });
        current = renderer;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(Window(), OverlaySnapshot.Demo));
        Assert.Equal(1, attempts);
        var after = OverlayEditorRenderDiagnostics.Current.Capture();
        Assert.Equal(before.Failed, after.Failed);
        Assert.Equal(before.Succeeded, after.Succeeded);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.RenderAsync(Window(), OverlaySnapshot.Demo));
    });

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task TheSameNativeReadinessFailureWithoutCancellationRemainsAnObservableRenderError() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation(); OverlayEditorNativeTestHarness.EnsureFrameworkAssets();
        var services = new ServiceCollection(); services.AddLogging(); services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        var attempts = 0;
        var before = OverlayEditorRenderDiagnostics.Current.Capture();
        using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: false, executeScript: (_, _) =>
        {
            attempts++;
            throw new COMException("Synthetic current document state", InvalidState);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync(Window(), OverlaySnapshot.Demo));
        Assert.Equal(1, attempts);
        Assert.Equal(InvalidState, Assert.IsType<COMException>(error.InnerException).HResult);
        var after = OverlayEditorRenderDiagnostics.Current.Capture();
        Assert.Equal(before.Failed + 1, after.Failed);
        Assert.True(after.LastAttemptFailed);
        Assert.Equal(OverlayEditorRenderStage.SurfaceReady, after.FailureStage);
        Assert.Equal(nameof(COMException), after.ExceptionType);
        Assert.Equal(InvalidState, after.HResult);
    });

    private static OverlayInstance Window() => new()
    {
        Id = "synthetic-render-cancellation", Name = "Synthetic renderer",
        Settings = new OverlaySettings
        {
            Width = 240, Height = 100, BackgroundOpacity = 0, ShowBorder = false,
            Widgets = [OverlayCatalog.CreateWidget("duration")]
        }
    };

}
