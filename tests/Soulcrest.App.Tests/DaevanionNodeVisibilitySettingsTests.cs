using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionNodeVisibilitySettingsTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"SettingsRevision\":5,\"DaevanionOverlayEnabled\":false}", true)]
    [InlineData("{\"SettingsRevision\":5,\"DaevanionNodeContentVisible\":false}", false)]
    public void MissingPreferenceDefaultsOnAndExplicitOffSurvivesReloadWithoutChangingTheMaster(string json, bool visible)
    {
        Assert.True(new AppSettings().DaevanionNodeContentVisible);
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, json);
        var original = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        var master = settings.Current.DaevanionOverlayEnabled;
        Assert.Equal(visible, settings.Current.DaevanionNodeContentVisible);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SettingsFile));
        settings.Update(s => s.DaevanionNodeContentVisible = false);
        var reloaded = new SettingsService().Current;
        Assert.False(reloaded.DaevanionNodeContentVisible);
        Assert.Equal(master, reloaded.DaevanionOverlayEnabled);
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.False(saved.RootElement.GetProperty(nameof(AppSettings.DaevanionNodeContentVisible)).GetBoolean());
    }

    [Theory]
    [InlineData("de", "Node-Anzeige: an", "Node-Anzeige: aus")]
    [InlineData("en", "Node display: on", "Node display: off")]
    public async Task OptionsStatusFollowsTheSavedPreferenceWithoutAServicePublication(string language, string on, string off)
    {
        var settings = new SettingsService();
        settings.Current.UiLanguage = language;
        settings.Current.DaevanionNodeContentVisible = true;
        UiText.Language = language;
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(settings);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var before = SubscriberCount(settings);
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DaevanionOverlayOptions>());
        Assert.Equal(before + 1, SubscriberCount(settings));
        async Task<string> Html() => await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains(on, await Html());
        Assert.Contains("data-daevanion-node-content=\"on\"", await Html());
        await renderer.Dispatcher.InvokeAsync(() => settings.Update(s => s.DaevanionNodeContentVisible = false));
        Assert.Contains(off, await Html());
        Assert.Contains("data-daevanion-node-content=\"off\"", await Html());
        Assert.False(new SettingsService().Current.DaevanionNodeContentVisible);
        await renderer.Dispatcher.InvokeAsync(() => settings.Update(s => s.DaevanionNodeContentVisible = true));
        Assert.Contains(on, await Html());
        await renderer.DisposeAsync();
        Assert.Equal(before, SubscriberCount(settings));
    }

    private static int SubscriberCount(SettingsService settings) =>
        (typeof(SettingsService).GetField(nameof(SettingsService.Changed), BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(settings) as Delegate)?.GetInvocationList().Length ?? 0;

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
