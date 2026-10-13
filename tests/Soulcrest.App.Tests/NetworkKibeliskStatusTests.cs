using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkKibeliskStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic Kibelisk profile");
    private static readonly ExplorationPlace[] Places =
    [
        new("pillar", "altgard", "kibelisk", 0, "Synthetic pillar", null, TeleportId: 1052020),
        new("other", "altgard", "kibelisk", 1, "Synthetic second pillar", null, TeleportId: 1052021),
    ];

    [Theory]
    [InlineData("de", "Kibelisk-Bindung über das Netzwerk erfasst. Weitere Erkundungsdaten stehen noch aus.")]
    [InlineData("en", "Kibelisk binding received from the network. Further exploration data is still pending.")]
    public async Task OwnBoundBindingIsPartialInSetupAndExplorationWithoutClaimingABaseline(string language, string text)
    {
        await using var f = new Fixture(language);
        f.BindOwn();
        f.Receive(Binding());
        var sources = f.Network.ProgressSources;
        Assert.True(sources.IdentityIsLive);
        Assert.Equal(At, sources.KibeliskActivityAt);
        Assert.False(sources.KibeliskFormatRejected);
        Assert.False(sources.HasAnyBaseline);
        Assert.False(f.Network.ProgressIsLive);
        Assert.Null(sources.PetBaselineAt);
        Assert.Null(sources.QuestBaselineAt);
        Assert.Null(sources.TeleportBaselineAt);
        foreach (var scope in new[] { "all", "exploration" })
        {
            var html = await f.Render(scope);
            Assert.Contains("data-progress-state=\"partial\"", html);
            Assert.Contains("data-progress-live=\"false\"", html);
            Assert.Contains(text, html);
        }
        var pet = await f.Render("pet");
        Assert.Contains("data-progress-state=\"waiting\"", pet);
        Assert.DoesNotContain(text, pet);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
    }

    [Theory]
    [InlineData("de", "Netzwerkformat für Kibelisk-Bindungen nicht unterstützt. Bereits bestätigte Bindungen bleiben erhalten.")]
    [InlineData("en", "Network format for Kibelisk bindings is not supported. Previously confirmed bindings are retained.")]
    public async Task UnsupportedBindingWarnsOnlyExplorationAndRetainsFullBaselinesAndCurrentPets(string language, string warning)
    {
        await using var f = new Fixture(language);
        f.BindOwn();
        f.Receive(PetSnapshot());
        f.Receive(Quests());
        f.Receive(Teleports());
        f.Receive(Binding());
        f.Receive([.. Binding(), 0], At.AddSeconds(1));
        var sources = f.Network.ProgressSources;
        Assert.True(sources.IdentityIsLive);
        Assert.True(sources.HasAllBaselines);
        Assert.True(sources.KibeliskFormatRejected);
        Assert.False(sources.TeleportFormatRejected);
        Assert.Equal(At, sources.KibeliskActivityAt);
        foreach (var scope in new[] { "all", "exploration" })
        {
            var html = await f.Render(scope);
            Assert.Contains(warning, html);
            Assert.Contains("data-progress-state=\"partial\"", html);
            Assert.Contains("data-progress-live=\"true\"", html);
        }
        var pet = await f.Render("pet");
        Assert.Contains("data-progress-state=\"current\"", pet);
        Assert.DoesNotContain(warning, pet);
        f.Receive(Gain(), At.AddSeconds(2));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(f.Network.Recent);
        f.Receive(Teleports(), At.AddSeconds(3));
        f.Receive(Binding(1052021), At.AddSeconds(4));
        Assert.True(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Equal(At.AddSeconds(3), f.Network.TeleportBaselineAt);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Contains("other", f.Exploration.Completed);
        Assert.Contains(warning, await f.Render("exploration"));
        Assert.Contains("data-progress-state=\"current\"", await f.Render("pet"));
        f.BindOwn(At.AddSeconds(5));
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.DoesNotContain(warning, await f.Render("exploration"));
        Assert.Contains("data-progress-state=\"waiting\"", await f.Render("exploration"));
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Contains("other", f.Exploration.Completed);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task GapReturnsAllScopesToWaitingAndSavedBindingsRemainStale(string language)
    {
        await using var f = new Fixture(language);
        f.BindOwn();
        f.Receive(Binding());
        f.Receive([.. Binding(), 0], At.AddSeconds(1));
        f.Network.ProcessProgressGap("own");
        var sources = f.Network.ProgressSources;
        Assert.False(sources.IdentityIsLive);
        Assert.Null(sources.KibeliskActivityAt);
        Assert.False(sources.KibeliskFormatRejected);
        Assert.False(sources.HasAnyBaseline);
        foreach (var scope in new[] { "all", "exploration", "pet" })
        {
            var html = await f.Render(scope);
            Assert.Contains("data-progress-state=\"waiting\"", html);
            Assert.Contains("data-progress-live=\"false\"", html);
            Assert.DoesNotContain(UiText.T("Netzwerkformat für Kibelisk-Bindungen nicht unterstützt. Bereits bestätigte Bindungen bleiben erhalten."), html);
        }
        var place = f.Exploration.Snapshot().Progress["pillar"];
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, place.State);
        Assert.Equal(ProgressFreshness.Stale, place.Freshness);
        Assert.True(place.TeleportPresent);
        var reloaded = f.ReloadExploration();
        Assert.Contains("pillar", reloaded.Completed);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, reloaded.Snapshot().Progress["pillar"].State);
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["pillar"].Freshness);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MountedStatusRefreshesForBindingPersistentRejectionAndGap(string language)
    {
        await using var f = new Fixture(language);
        f.BindOwn();
        var states = await f.RenderTransitions(
            () => f.Receive(Binding()),
            () => f.Receive([.. Binding(), 0], At.AddSeconds(1)),
            () => { f.Receive(PetSnapshot()); f.Receive(Quests()); f.Receive(Teleports()); },
            () => f.Network.ProcessProgressGap("own"));
        foreach (var (html, state) in states.Zip(new[] { "waiting", "partial", "partial", "partial", "waiting" }))
            Assert.Contains($"data-progress-state=\"{state}\"", html);
        Assert.Contains(UiText.T("Kibelisk-Bindung über das Netzwerk erfasst. Weitere Erkundungsdaten stehen noch aus."), states[1]);
        var warning = UiText.T("Netzwerkformat für Kibelisk-Bindungen nicht unterstützt. Bereits bestätigte Bindungen bleiben erhalten.");
        Assert.Contains(warning, states[2]);
        Assert.Contains(warning, states[3]);
        Assert.DoesNotContain(warning, states[4]);
        Assert.Contains("data-progress-live=\"true\"", states[3]);
    }

    [Fact]
    public async Task UnmappedBindingDoesNotInventPartialStatus()
    {
        await using var f = new Fixture("en");
        f.BindOwn();
        f.Receive(Binding(999999));
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Empty(f.Exploration.Completed);
        Assert.Contains("data-progress-state=\"waiting\"", await f.Render("all"));
        Assert.Contains("data-progress-state=\"waiting\"", await f.Render("exploration"));
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] Binding(int teleportId = 1052020) => Packet(0x9233, writer => writer.Write(teleportId));
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });
    private static byte[] PetSnapshot() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
        writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
    });
    private static byte[] Quests() => Packet(0x8D09, writer => writer.Write((byte)0));
    private static byte[] Teleports() => Packet(0x9232, writer => { writer.Write((byte)1); writer.Write(1052020); });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-status-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private string ExplorationPath => Path.Combine(_folder, "exploration.json");
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture(string language)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(ExplorationPath, Places);
            Network = new(Progress, Exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton(Network);
            _provider = services.BuildServiceProvider();
            _provider.GetRequiredService<SettingsService>().Current.UiLanguage = UiText.Language = language;
        }
        internal void BindOwn(DateTimeOffset? at = null) => Network.ObserveIdentity("own", Own, at ?? At);
        internal void Receive(byte[] packet, DateTimeOffset? at = null) => Network.ProcessProgressMessage("own", packet, at ?? At);
        internal ExplorationService ReloadExploration() => new(ExplorationPath, Places);
        internal async Task<string> Render(string scope)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<NetworkProgressStatus>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(NetworkProgressStatus.Scope)] = scope }))).ToHtmlString()));
        }
        internal async Task<IReadOnlyList<string>> RenderTransitions(params Action[] changes)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<NetworkProgressStatus>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(NetworkProgressStatus.Scope)] = "exploration" })));
            var states = new List<string> { await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString())) };
            foreach (var change in changes)
            {
                await renderer.Dispatcher.InvokeAsync(change);
                states.Add(await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString())));
            }
            return states;
        }
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            Network.Dispose();
            if (_settings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, recursive: true);
        }
    }
}
