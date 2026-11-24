using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OriginalKitOverlayPreviewTests
{
    [Fact]
    public async Task ClosedPreviewNeedsNoLivePresentationServiceAndReusesItsParentsSnapshots()
    {
        await using var fixture = await Fixture.Create();
        Assert.False(fixture.Service.State.Previewing);
        Assert.Empty(fixture.Js.Calls);
        for (var index = 0; index < 10; index++) await fixture.Snapshot(Snapshot("LIVE-LATEST", "obsidian"));
        Assert.Contains("LIVE-LATEST", await fixture.Html());
        Assert.Empty(fixture.Js.Calls);
    }

    [Fact]
    public async Task OpenCloseLanguageThemeAndDisposalUseOnlyTheSuppliedSnapshot()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SetPreviewAsync(true);
        await fixture.AfterRender();
        Assert.Equal(["soulcrestOverlayDialogs.showDialog", "soulcrestOverlayEditor.mount"], fixture.Js.Calls);
        await fixture.AfterRender();
        Assert.Equal(2, fixture.Js.Calls.Count);
        await fixture.Snapshot(Snapshot("AP-LATEST", "obsidian") with { UiLanguage = "de" });
        var html = await fixture.Html();
        Assert.Contains("AP-LATEST", html);
        Assert.Contains("data-theme=\"obsidian\"", html);
        await fixture.Close();
        Assert.False(fixture.Service.State.Previewing);
        await fixture.AfterRender();
        Assert.Equal(["soulcrestOverlayDialogs.showDialog", "soulcrestOverlayEditor.mount", "soulcrestOverlayEditor.unmount", "soulcrestOverlayDialogs.closeDialog"], fixture.Js.Calls);
        await fixture.Service.SetPreviewAsync(true);
        await fixture.AfterRender();
        await fixture.DisposePreview();
        var calls = fixture.Js.Calls.Count;
        await fixture.AfterRender();
        Assert.Equal(calls, fixture.Js.Calls.Count);
    }

    private static OverlaySnapshot Snapshot(string points, string theme) => new()
    {
        ThemeId = theme,
        Metrics = new Dictionary<string, OverlayMetric> { ["abyss-points"] = new("AP", points) },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "SoulcrestPreviewTests", Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private CapturedKitPreview _preview = null!;
        internal OverlayService Service { get; }
        internal JsRuntime Js { get; } = new();
        private Fixture()
        {
            Service = new(_directory, new OverlayCollectionSettings());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOverlayService>(Service);
            services.AddSingleton<IJSRuntime>(Js);
            // Deliberately no presentation/capture service: the dialog must be presentation-only.
            _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Service.SaveAsync(new() { Widgets = [OverlayCatalog.CreateWidget("abyss-points")] });
            await fixture._renderer.Dispatcher.InvokeAsync(async () => fixture._root = await fixture._renderer.RenderComponentAsync<CapturedKitPreview>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CapturedKitPreview.Captured)] = (Action<CapturedKitPreview>)(view => fixture._preview = view) })));
            return fixture;
        }
        internal Task Snapshot(OverlaySnapshot snapshot) => _renderer.Dispatcher.InvokeAsync(() => _preview.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(OriginalKitOverlayPreview.Snapshot)] = snapshot })));
        internal Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString());
        // HtmlRenderer deliberately omits interactive post-render callbacks; invoke the actual
        // component callback here against a JS stub to verify dialog transitions without WebView.
        internal Task AfterRender() => _renderer.Dispatcher.InvokeAsync(_preview.AfterRender);
        internal Task Close() => _renderer.Dispatcher.InvokeAsync(() => (Task)typeof(OriginalKitOverlayPreview)
            .GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_preview, null)!);
        internal Task DisposePreview() => _renderer.Dispatcher.InvokeAsync(() => _preview.DisposeAsync().AsTask());
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
    private sealed class JsRuntime : IJSRuntime
    {
        internal List<string> Calls { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult(default(T)!);
        }
    }
}

public sealed class CapturedKitPreview : OriginalKitOverlayPreview
{
    [Parameter] public Action<CapturedKitPreview>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task AfterRender() => base.OnAfterRenderAsync(false);
}
