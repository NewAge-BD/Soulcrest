using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayLockWidgetRenderSurfaceTests
{
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    public async Task PreviewDrawsOneLogicalControlWhileLiveLeavesItToTheNativeHost(bool pauseAnimations, bool drawLockWidget, int expectedControls)
    {
        var source = new ForbiddenPresentationSource();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IOverlayEditorPresentationSource>(source).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var content = OverlayCatalog.CreateWidget("duration", 8, 8);
        var window = new OverlayInstance
        {
            Settings = new() { Scale = 1.65, LockWidget = new() { X = 19.5, Y = 31.25 }, Widgets = [content] },
        };
        var model = new OverlayPlacementRenderState(pauseAnimations, drawLockWidget);
        var snapshot = OverlaySnapshot.DemoFor("en");
        model.Publish(window, snapshot);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var surface = await renderer.RenderComponentAsync<OverlayPlacementRenderSurface>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(OverlayPlacementRenderSurface.Model)] = model }));
            return WebUtility.HtmlDecode(surface.ToHtmlString());
        });

        Assert.Equal(expectedControls, System.Text.RegularExpressions.Regex.Matches(html, "data-required-widget=\"true\"").Count);
        Assert.Equal(expectedControls, System.Text.RegularExpressions.Regex.Matches(html, "class=\"oe-lock-glyph\"").Count);
        if (drawLockWidget)
        {
            Assert.Contains("style=\"left:19.5px;top:31.25px;width:28px;height:28px\"", html);
            Assert.True(html.IndexOf("data-required-widget=\"true\"", StringComparison.Ordinal)
                > html.IndexOf($"data-widget-id=\"{content.Id}\"", StringComparison.Ordinal));
            Assert.Contains("data:image/png;base64,", html);
        }
        Assert.Contains($"data-widget-id=\"{content.Id}\"", html);
        Assert.Same(window, model.Window);
        Assert.Same(snapshot, model.Snapshot);
        Assert.Equal(0, source.Captures);
        Assert.Equal(0, source.Acquisitions);
    }

    [Fact]
    public void BitmapAndPreviewModelsDrawTheMandatoryControlByDefault()
    {
        var model = new OverlayPlacementRenderState();
        Assert.True(model.DrawLockWidget);
        Assert.True(model.PauseAnimations);
    }

    private sealed class ForbiddenPresentationSource : IOverlayEditorPresentationSource
    {
        public event Action? Changed { add { } remove { } }
        public int Captures { get; private set; }
        public int Acquisitions { get; private set; }
        public OverlaySnapshot Capture(bool examples = false, string? language = null)
        {
            Captures++;
            throw new InvalidOperationException("A supplied render snapshot must not capture another source.");
        }
        public IDisposable Acquire(bool examples = false, bool market = false)
        {
            Acquisitions++;
            throw new InvalidOperationException("Rendering must not acquire a live data source.");
        }
        public void Dispose() { }
    }
}
