using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CraftingProficiencyObservationDataTests : IDisposable
{
    private readonly string? _port = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
    private static readonly DateTimeOffset At = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid")] [InlineData("1024")]
    [InlineData("65536")] [InlineData("-1")]
    public async Task DisabledOrInvalidDebugPortNeverResolvesTheProficiencyService(string? port)
    {
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", port);
        var resolutions = 0;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<CraftingProficiencyStateService>(_ =>
        { resolutions++; throw new InvalidOperationException("Disabled diagnostics resolved proficiency state."); });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CraftingProficiencyObservationData>()).ToHtmlString());
        Assert.Empty(html); Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task ValidPortWithNoOptionalProficiencyServiceDoesNotInventAnObservation()
    {
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", "9222");
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CraftingProficiencyObservationData>()).ToHtmlString());
        Assert.Empty(html);
    }

    [Fact]
    public async Task ValidDebugObservationExposesOnlyAnonymousNumericProfessionEvidenceAndUpdates()
    {
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", "9222");
        using var fixture = new Fixture(); fixture.Source.Publish(Evidence());
        await using var provider = new ServiceCollection().AddLogging().AddSingleton(fixture.State).BuildServiceProvider();
        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var rendererDisposed = false;
        try
        {
            var before = Subscribers(fixture.State);
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<CraftingProficiencyObservationData>());
            Assert.Equal(before + 1, Subscribers(fixture.State));
            var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            using var json = Read(html);
            Assert.Equal(new[] { "Generation", "IsFresh", "IsKnown", "ObservedAt", "Sequence", "Source", "Status", "UsingSaved", "categories" },
                json.RootElement.EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal));
            Assert.True(json.RootElement.GetProperty("IsFresh").GetBoolean());
            Assert.Equal(At, json.RootElement.GetProperty("ObservedAt").GetDateTimeOffset());
            Assert.Equal(CraftingProficiencyStateService.LiveSource, json.RootElement.GetProperty("Source").GetString());
            var category = Assert.Single(json.RootElement.GetProperty("categories").EnumerateArray());
            Assert.Equal(new[] { "Category", "Level" }, category.EnumerateObject().Select(row => row.Name).Order());
            Assert.Equal(1, category.GetProperty("Category").GetByte()); Assert.Equal(12, category.GetProperty("Level").GetInt32());
            foreach (var privateValue in new[] { Profile, "PRIVATE_OWNER", "99887766554433", "773311", "1554433221", "456789", "34567" })
                Assert.DoesNotContain(privateValue, html);
            Assert.DoesNotContain("Experience", html); Assert.DoesNotContain("SkillPoints", html);
            Assert.DoesNotContain("ProfileId", html); Assert.DoesNotContain("Identity", html);
            fixture.Source.Publish(Evidence(13, 2));
            using var changed = Read(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Equal(13, changed.RootElement.GetProperty("categories")[0].GetProperty("Level").GetInt32());
            Assert.Equal(2, changed.RootElement.GetProperty("Sequence").GetInt64());
            await renderer.DisposeAsync();
            rendererDisposed = true;
            Assert.Equal(before, Subscribers(fixture.State));
            fixture.Source.Publish(Evidence(14, 3));
        }
        finally { if (!rendererDisposed) await renderer.DisposeAsync(); }
    }

    private static int Subscribers(CraftingProficiencyStateService state) =>
        (typeof(CraftingProficiencyStateService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state) as Action)?.GetInvocationList().Length ?? 0;
    private static JsonDocument Read(string html)
    {
        var match = Regex.Match(html, "<script[^>]*data-crafting-observation[^>]*>([\\s\\S]*?)</script>");
        Assert.True(match.Success); Assert.DoesNotContain("&quot;", match.Groups[1].Value);
        return JsonDocument.Parse(match.Groups[1].Value);
    }
    private static NetworkCharacterCombatSnapshot Evidence(ushort level = 12, long sequence = 1) => new()
    {
        Character = new(new(99887766554433, 773311, 1554433221, "PRIVATE_OWNER"), 7) { ObservedAt = At }, Generation = 7,
        CraftingProficiencies = new(ImmutableDictionary<byte, CharacterCraftingProficiency>.Empty.Add(1,
            new(1, level, 456789, 34567)), NetworkCombatSourceStatus.Complete, true, At, sequence,
            CraftingProficiencyStateService.LiveSource)
    };
    private sealed class Source : INetworkCharacterCombatSource
    {
        public bool Running => true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; private set; } = NetworkCharacterCombatSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        public void Publish(NetworkCharacterCombatSnapshot value) { CombatSnapshot = value; Changed?.Invoke(); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-crafting-observation-" + Guid.NewGuid().ToString("N"));
        public Source Source { get; } = new();
        public CraftingProficiencyStateService State { get; }
        public Fixture() => State = new(Source, _ => new(Profile, "Unused"), () => Profile,
            new CraftingProficiencyStore(Path.Combine(_root, "history.json")));
        public void Dispose() { State.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    public void Dispose() => Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", _port);
}
