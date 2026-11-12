using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData(null)] [InlineData("invalid")] [InlineData("1024")] [InlineData("65536")]
    public async Task LootDebugObservationDoesNotResolveAnyRecorderWhenDisabled(string? port)
    {
        var previous = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        try
        {
            Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", port);
            var resolutions = 0;
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<UnifiedLootTrackerService>(_ =>
            { resolutions++; throw new InvalidOperationException("Recorder must stay lazy."); }).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<LootRecordingObservationData>()).ToHtmlString());
            Assert.Empty(html); Assert.Equal(0, resolutions);
        }
        finally { Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", previous); }
    }

    [Fact]
    public async Task EvenEnabledLootDebugObservationNeverResolvesALazyRecorderOutsideTheRegistry()
    {
        var previous = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        try
        {
            Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", "9222");
            var resolutions = 0;
            await using var provider = new ServiceCollection().AddLogging().AddSingleton(new DiagnosticServiceRegistry())
                .AddSingleton<UnifiedLootTrackerService>(_ =>
                { resolutions++; throw new InvalidOperationException("Recorder must stay lazy."); }).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<LootRecordingObservationData>()).ToHtmlString());
            Assert.Empty(html); Assert.Equal(0, resolutions);
        }
        finally { Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", previous); }
    }

    [Fact]
    public async Task ExistingRecorderObservationUpdatesItsAnonymousFaultAndDetachesOnDisposal()
    {
        var previous = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        try
        {
            Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", "9222");
            await using var f = new Fixture(); await f.Ready();
            await using var provider = new ServiceCollection().AddLogging().AddSingleton(new DiagnosticServiceRegistry(f.Service)).BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var disposed = false;
            try
            {
                var subscribers = LootSubscribers(f.Service);
                var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<LootRecordingObservationData>());
                Assert.Equal(subscribers + 1, LootSubscribers(f.Service));
                using var healthy = LootObservation(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
                Assert.Equal(JsonValueKind.Null, healthy.RootElement.GetProperty("fault").ValueKind);
                f.FailAppend = true; f.Source.Harvest(1, new NetworkGatheringItem(610620013, 1)); await f.Ready();
                var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
                using var observed = LootObservation(html);
                Assert.Equal(new[] { "fault", "sessionActive", "statusIsError", "trackingAvailable" },
                    observed.RootElement.EnumerateObject().Select(property => property.Name).Order());
                var fault = observed.RootElement.GetProperty("fault");
                Assert.Equal(new[] { "At", "ExceptionType", "HResult", "Kind", "Stage" },
                    fault.EnumerateObject().Select(property => property.Name).Order());
                Assert.Equal("JournalAppend", fault.GetProperty("Stage").GetString());
                Assert.Equal("Io", fault.GetProperty("Kind").GetString());
                Assert.Equal("IOException", fault.GetProperty("ExceptionType").GetString());
                Assert.True(observed.RootElement.GetProperty("statusIsError").GetBoolean());
                Assert.False(observed.RootElement.GetProperty("trackingAvailable").GetBoolean());
                foreach (var value in new[] { f.Root, "Synthetic character", "Synthetic append failure", "ProfileId", "SessionId", "Message", "Path" })
                    Assert.DoesNotContain(value, html);
                await renderer.DisposeAsync(); disposed = true;
                Assert.Equal(subscribers, LootSubscribers(f.Service));
            }
            finally { if (!disposed) await renderer.DisposeAsync(); }
        }
        finally { Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", previous); }
    }

    [Fact]
    public async Task AllLootRecordingContributorsExposeTheSameAnonymousFirstFaultWithoutStartingAnything()
    {
        await using var f = new Fixture(); await f.Ready(); f.FailAppend = true;
        f.Source.Harvest(1, new NetworkGatheringItem(610620013, 1)); await f.Ready();
        var contributors = DiagnosticContributors.Create(new DiagnosticServiceRegistry(f.Service, f.Settings));
        foreach (var id in new[] { "loot-sessions", "loot-pve", "loot-lifeskill" })
        {
            var result = contributors.Single(contributor => contributor.FeatureIds.Contains(id)).Collect(id);
            Assert.Equal(DiagnosticStatus.Error, result.Status);
            Assert.Equal(true, result.Facts["recordingFaultPresent"]);
            Assert.Equal("JournalAppend", result.Facts["recordingFaultStage"]);
            Assert.Equal("Io", result.Facts["recordingFaultKind"]);
            Assert.Equal("IOException", result.Facts["recordingFaultExceptionType"]);
            Assert.Equal(f.Service.RecordingFault!.HResult, result.Facts["recordingFaultHResult"]);
            var json = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(f.Root, json); Assert.DoesNotContain("Synthetic character", json);
            Assert.DoesNotContain("Synthetic append failure", json);
        }
    }

    private static int LootSubscribers(UnifiedLootTrackerService service) =>
        (typeof(UnifiedLootTrackerService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) as Action)?.GetInvocationList().Length ?? 0;
    private static JsonDocument LootObservation(string html)
    {
        var match = Regex.Match(html, "<script[^>]*data-loot-recording-observation[^>]*>([\\s\\S]*?)</script>");
        Assert.True(match.Success); return JsonDocument.Parse(match.Groups[1].Value);
    }
}
