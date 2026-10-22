using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionHighlightSettingsTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"DaevanionHighlightedStatIds\":null}")]
    [InlineData("{\"DaevanionHighlightedStatIds\":[\"unknown\",null,\"\"]}")]
    public void MissingOrInvalidSelectionIsEmptyAndLoadingDoesNotRewriteTheFile(string json)
    {
        Assert.Empty(new AppSettings().DaevanionHighlightedStatIds);
        WriteSettings(json);
        var original = File.ReadAllBytes(AppPaths.SettingsFile);
        var settings = new SettingsService();
        Assert.Empty(settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Fact]
    public void LoadingAndUpdatingKeepOnlyKnownDistinctIdsInCatalogOrderAndPersistIndependently()
    {
        WriteSettings("{\"SettingsRevision\":5,\"DaevanionOverlayEnabled\":false,\"DaevanionNodeContentVisible\":false,"
            + "\"DaevanionHighlightedStatIds\":[\"PvPAddDamage\",\"unknown\",\"Critical\",\"PvPAddDamage\",null]}");
        var settings = new SettingsService();
        Assert.Equal(new[] { "Critical", "PvPAddDamage" }, settings.Current.DaevanionHighlightedStatIds);
        var changes = 0; settings.Changed += () => changes++;
        var selected = new[] { "PvPAccuracy", "Critical", "PvEAccuracy" };
        settings.Update(s => s.DaevanionHighlightedStatIds = ["PvPAccuracy", "unknown", "Critical", "PvEAccuracy", "Critical", null!]);
        var expected = DaevanionCatalog.Default.Stats.Where(s => selected.Contains(s.Id, StringComparer.Ordinal)).Select(s => s.Id).ToArray();
        Assert.Equal(expected, settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(1, changes);
        var reloaded = new SettingsService().Current;
        Assert.Equal(expected, reloaded.DaevanionHighlightedStatIds);
        Assert.False(reloaded.DaevanionOverlayEnabled);
        Assert.False(reloaded.DaevanionNodeContentVisible);
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Equal(expected, saved.RootElement.GetProperty(nameof(AppSettings.DaevanionHighlightedStatIds)).EnumerateArray().Select(v => v.GetString()));
        settings.Update(s => s.DaevanionHighlightedStatIds = null!);
        Assert.Empty(settings.Current.DaevanionHighlightedStatIds);
        Assert.Empty(new SettingsService().Current.DaevanionHighlightedStatIds);
    }

    [Fact]
    public void AtomicWriteFailuresAreReportedToTheCallerAndDoNotPublishChanged()
    {
        WriteSettings("{}");
        var settings = new SettingsService(); var changes = 0; settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var error = Record.Exception(() => settings.Update(s => s.DaevanionHighlightedStatIds = ["Critical"]));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Theory]
    [InlineData("de", "Stat-Hervorhebung", "Alle Hervorhebungen ausgeschaltet.")]
    [InlineData("en", "Stat highlighting", "All highlights turned off.")]
    public async Task OptionsShowOnlyTheCurrentBoardsSelectedStatsAndResetAllSavedSelections(string language, string heading, string success)
    {
        WriteSettings("{}");
        var settings = new SettingsService();
        settings.Update(s => { s.UiLanguage = language; s.OverlayLanguage = language; s.DaevanionNodeContentVisible = false;
            s.DaevanionHighlightedStatIds = ["PvEAddDamage", "PvPAddDamage"]; });
        using var source = new Source(settings);
        var activator = new CapturingActivator();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(settings);
        services.AddSingleton(source.Service); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var settingsSubscribers = SubscriberCount(settings); var serviceSubscribers = SubscriberCount(source.Service);
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DaevanionOverlayOptions>());
        async Task<string> Html() => await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Equal(settingsSubscribers + 1, SubscriberCount(settings));
        Assert.Equal(serviceSubscribers + 1, SubscriberCount(source.Service));
        var initial = await Html();
        Assert.Contains(heading, initial);
        Assert.Contains("data-daevanion-highlight-stat=\"PvEAddDamage\"", initial);
        Assert.DoesNotContain("data-daevanion-highlight-stat=\"PvPAddDamage\"", initial);
        Assert.Contains(DaevanionCatalog.Default.GetStat("PvEAddDamage")!.ShortLabel.For(language), initial);
        await renderer.Dispatcher.InvokeAsync(() => { source.Publish(); settings.Update(s => s.DaevanionHighlightedStatIds = ["BossNpcDefense", "PvPAddDamage"]); });
        var updated = await Html();
        Assert.DoesNotContain("data-daevanion-highlight-stat=\"PvEAddDamage\"", updated);
        Assert.Contains("data-daevanion-highlight-stat=\"BossNpcDefense\"", updated);
        await renderer.Dispatcher.InvokeAsync(() => { source.Publish(); InvokeAction(activator.Options!, "ClearHighlights"); });
        var cleared = await Html();
        Assert.DoesNotContain("data-daevanion-highlight-stat=", cleared);
        Assert.Contains(success, cleared);
        Assert.Empty(settings.Current.DaevanionHighlightedStatIds);
        Assert.Empty(new SettingsService().Current.DaevanionHighlightedStatIds);
        Assert.False(settings.Current.DaevanionNodeContentVisible);
        Assert.Equal(0, source.Capture.Diagnostics.Grabs);
        await renderer.DisposeAsync();
        Assert.Equal(settingsSubscribers, SubscriberCount(settings));
        Assert.Equal(serviceSubscribers, SubscriberCount(source.Service));
    }

    [Theory]
    [InlineData("de", "Hervorhebungen konnten nicht gespeichert werden.")]
    [InlineData("en", "Highlights could not be saved.")]
    public async Task OptionsKeepThePreviousSelectionAndShowAnErrorWhenResetCannotBeSaved(string language, string errorText)
    {
        WriteSettings("{}");
        var settings = new SettingsService(); settings.Update(s => { s.UiLanguage = language; s.DaevanionHighlightedStatIds = ["Critical"]; });
        var activator = new CapturingActivator();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(settings); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DaevanionOverlayOptions>());
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            await renderer.Dispatcher.InvokeAsync(() => InvokeAction(activator.Options!, "ClearHighlights"));
            var html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
            Assert.Equal(new[] { "Critical" }, settings.Current.DaevanionHighlightedStatIds);
            Assert.Contains(errorText, html);
            Assert.Contains("role=\"alert\"", html);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Theory]
    [InlineData("de", "Warte auf ein bestätigtes Board.", "Rechts neben dem Board ist zu wenig Platz für die Stat-Schalter.")]
    [InlineData("en", "Waiting for a confirmed board.", "There is not enough room to the right of the board for the stat switches.")]
    public async Task OptionsShowPlacementFeedbackAndStopListingStatsForAnExpiredSnapshot(string language, string waiting, string placement)
    {
        WriteSettings("{}");
        var settings = new SettingsService(); settings.Update(s => { s.UiLanguage = language; s.OverlayLanguage = language;
            s.DaevanionHighlightedStatIds = ["PvEAddDamage"]; });
        using var source = new Source(settings);
        var activator = new CapturingActivator();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(settings);
        services.AddSingleton(source.Service); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DaevanionOverlayOptions>());
        await renderer.Dispatcher.InvokeAsync(() => { source.Publish(); source.Service.SetHighlightPlacementStatus("Rechts neben dem Board ist zu wenig Platz für die Stat-Schalter."); });
        var shown = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains(placement, shown);
        Assert.Contains("data-daevanion-highlight-stat=\"PvEAddDamage\"", shown);
        await Task.Delay(1100);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Options, null));
        var expired = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.DoesNotContain("data-daevanion-highlight-stat=", expired);
        Assert.Contains(waiting, expired);
        Assert.Equal(new[] { "PvEAddDamage" }, settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(0, source.Capture.Diagnostics.Grabs);
    }

    private static void InvokeAction(DaevanionOverlayOptions options, string method)
    {
        typeof(DaevanionOverlayOptions).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(options, null);
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(options, null);
    }
    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
    private static int SubscriberCount(object service) =>
        (service.GetType().GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service) as Delegate)?.GetInvocationList().Length ?? 0;
    private sealed class CapturingActivator : IComponentActivator
    {
        internal DaevanionOverlayOptions? Options;
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is DaevanionOverlayOptions options) Options = options;
            return component;
        }
    }
    private sealed class Source : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-highlight-settings-" + Guid.NewGuid().ToString("N"));
        internal readonly GameCaptureService Capture = new();
        internal readonly NetworkLootService Network;
        internal readonly DaevanionService Service;
        private readonly SettingsService _settings;
        private readonly Rectangle _bounds = new(100, 100, 1600, 1200);
        internal Source(SettingsService settings)
        {
            Directory.CreateDirectory(_root);
            _settings = settings;
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Service = new(Network, settings, Capture, () => _bounds);
            Network.ObserveIdentity("own", new OwnCharacterIdentity(101, 501, 300, "SyntheticHighlight") { ClassId = "Cleric" }, DateTimeOffset.UnixEpoch);
            Publish();
        }
        internal void Publish()
        {
            var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
            var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
            var anchors = new[] { new DaevanionNodeAnchor(node.Row, node.Column, new(50, 100, 60, 60), .95) };
            var snapshot = new DaevanionSnapshot(Network.OwnCharacter!, board, _bounds, Stopwatch.GetTimestamp(),
                new("ariel", new(50, 100), 76, 60, anchors, .95), _settings.Current.LootNameLanguage);
            Assert.True(Service.TryPublishObservation(snapshot, Service.ObservationRevision));
        }
        public void Dispose() { Service.Dispose(); Network.Dispose(); Capture.Dispose(); Directory.Delete(_root, recursive: true); }
    }
    public void Dispose()
    {
        if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
