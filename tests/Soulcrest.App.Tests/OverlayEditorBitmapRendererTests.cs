using System.Drawing;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayEditorBitmapRendererTests
{
    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void PlacementDensityRemainsLogicalWhenTheSavedWindowScaleChanges(double scale)
    {
        var window = new OverlayInstance { Settings = new OverlaySettings { Width = 321.5, Height = 180.25, Scale = scale } };
        Assert.Equal(new Size(322, 181), OverlayEditorBitmapRenderer.Measure(window, OverlaySnapshot.Demo));
    }

    [Theory]
    [InlineData("https://0.0.0.1/overlay-placement-render.html", true)]
    [InlineData("https://0.0.0.1/_framework/blazor.webview.js", true)]
    [InlineData("https://0.0.0.1/loot-icons/example.png", true)]
    [InlineData("https://0.0.0.0/loot-icons/example.png", false)]
    [InlineData("https://example.com/icon.png", false)]
    [InlineData("https://0.0.0.1.example.com/icon.png", false)]
    [InlineData("https://0.0.0.1:9443/icon.png", false)]
    [InlineData("https://name@0.0.0.1/icon.png", false)]
    [InlineData("http://0.0.0.1/icon.png", false)]
    [InlineData("file:///C:/outside.png", false)]
    public void BrowserResourcesAreRestrictedToTheExistingLocalBlazorOrigin(string address, bool allowed) =>
        Assert.Equal(allowed, OverlayEditorBitmapRenderer.AllowsResource(address));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenderSurfaceUsesTheProductionWidgetsWithoutDialogControlsOrApplyingWindowScale(bool pauseAnimations)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var widget = OverlayCatalog.CreateWidget("duration", 24, 32);
        var window = new OverlayInstance { Name = "Placement example", Settings = new OverlaySettings
        {
            Width = 640, Height = 360, Scale = 1.75, BackgroundOpacity = 0, ShowBorder = false, Widgets = [widget],
        }};
        var snapshot = OverlaySnapshot.DemoFor("en");
        var model = new OverlayPlacementRenderState(pauseAnimations);
        model.Publish(window, snapshot);
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OverlayPlacementRenderSurface>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(OverlayPlacementRenderSurface.Model)] = model })));
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("width:640px;height:360px", html);
        Assert.Contains($"data-widget-id=\"{widget.Id}\"", html);
        Assert.Contains("overlay-widget-duration", html);
        Assert.Contains(snapshot.Metrics["duration"].Value, html);
        Assert.Equal(pauseAnimations, html.Contains("is-static-render", StringComparison.Ordinal));
        Assert.DoesNotContain("transform:scale(1.75)", html);
        Assert.DoesNotContain("<dialog", html);
        Assert.DoesNotContain("oe-widget-grip", html);
        Assert.DoesNotContain("oe-widget-resize", html);
        Assert.DoesNotContain("browser-overlay-toolbar", html);
        var metrics = snapshot.Metrics.ToDictionary(entry => entry.Key, entry => entry.Value);
        metrics["duration"] = metrics["duration"] with { Value = "UPDATED-TIME" };
        await renderer.Dispatcher.InvokeAsync(() => model.Publish(window, snapshot with { Metrics = metrics }));
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains("UPDATED-TIME", html);
    }

    [Fact]
    public Task CancellationBeforeRenderingDoesNotCreateAWebView() => RunSta(() =>
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var renderer = new OverlayEditorBitmapRenderer(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => renderer.RenderAsync(new OverlayInstance(), OverlaySnapshot.Demo,
            cancellation.Token).GetAwaiter().GetResult());
    });

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
