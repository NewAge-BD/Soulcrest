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
public sealed class NetworkDungeonStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic dungeon profile");
    private static readonly ExplorationPlace[] Places =
    [
        new("cave", "altgard", "dungeon", 0, "Synthetic cave", null, QuestId: 2810027, DungeonId: 320027),
        new("pillar", "altgard", "kibelisk", 1, "Synthetic pillar", null, TeleportId: 1052026),
    ];

    [Theory]
    [InlineData("de", "Instanzabschluss über das Netzwerk erfasst. Weitere Erkundungsdaten stehen noch aus.")]
    [InlineData("en", "Instance completion received from the network. Further exploration data is still pending.")]
    public async Task MappedCompletionIsPartialExplorationWithoutInventingABaseline(string language, string text)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        fixture.Receive(Completion());

        var sources = fixture.Network.ProgressSources;
        Assert.True(sources.IdentityIsLive);
        Assert.Equal(At, sources.DungeonActivityAt);
        Assert.False(sources.DungeonFormatRejected);
        Assert.False(sources.HasAnyBaseline);
        Assert.False(fixture.Network.ProgressIsLive);
        Assert.Null(sources.PetBaselineAt);
        Assert.Null(sources.QuestBaselineAt);
        Assert.Null(sources.TeleportBaselineAt);
        foreach (var scope in new[] { "all", "exploration" })
        {
            var html = await fixture.Render(scope);
            Assert.Contains("data-progress-state=\"partial\"", html);
            Assert.Contains("data-progress-live=\"false\"", html);
            Assert.Contains(text, html);
        }
        var pet = await fixture.Render("pet");
        Assert.Contains("data-progress-state=\"waiting\"", pet);
        Assert.DoesNotContain(text, pet);
        var place = fixture.Exploration.Snapshot().Progress["cave"];
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, place.State);
        Assert.Equal(ProgressFreshness.Fresh, place.Freshness);
        Assert.Equal("network-dungeon-completion", place.Source);
        Assert.Null(place.QuestStatus);
        Assert.Contains("cave", fixture.Exploration.Completed);
        Assert.Null(fixture.Exploration.Snapshot().Progress["pillar"].State);
    }

    [Theory]
    [InlineData("de", "Netzwerkformat für Instanzabschlüsse nicht unterstützt. Bereits bestätigte Abschlüsse bleiben erhalten.")]
    [InlineData("en", "Network format for instance completions is not supported. Previously confirmed completions are retained.")]
    public async Task RejectedDungeonFormatRetainsCompletionAndDoesNotBlockCurrentPets(string language, string warning)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        fixture.Receive(PetSnapshot());
        fixture.Receive(Completion());
        fixture.Receive([.. Completion(), 0], At.AddSeconds(1));

        Assert.True(fixture.Network.ProgressSources.DungeonFormatRejected);
        Assert.True(fixture.Network.ProgressSources.IdentityIsLive);
        Assert.NotNull(fixture.Network.PetBaselineAt);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, fixture.Exploration.Snapshot().Progress["cave"].State);
        foreach (var scope in new[] { "all", "exploration" })
        {
            var html = await fixture.Render(scope);
            Assert.Contains(warning, html);
            Assert.Contains("data-progress-state=\"partial\"", html);
        }
        var pet = await fixture.Render("pet");
        Assert.Contains("data-progress-state=\"current\"", pet);
        Assert.DoesNotContain(warning, pet);

        fixture.Receive(Gain(), At.AddSeconds(2));
        Assert.Equal(10, fixture.Progress.Souls("fossa"));
        fixture.Receive(Completion(), At.AddSeconds(3));
        fixture.Receive(Quests(), At.AddSeconds(4));
        fixture.Receive(Teleports(), At.AddSeconds(5));
        var sources = fixture.Network.ProgressSources;
        Assert.True(sources.HasAllBaselines);
        Assert.True(sources.DungeonFormatRejected); // a valid delta cannot recover another missed completion
        foreach (var scope in new[] { "all", "exploration" })
        {
            var html = await fixture.Render(scope);
            Assert.Contains(warning, html);
            Assert.Contains("data-progress-state=\"partial\"", html);
        }
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("pet"));
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, fixture.Exploration.Snapshot().Progress["cave"].State);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task GapReturnsStatusToWaitingAndRetainsTheSavedOneTimeCompletion(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        fixture.Receive(Completion());
        fixture.Receive([.. Completion(), 0], At.AddSeconds(1));
        fixture.Network.ProcessProgressGap("own");

        var sources = fixture.Network.ProgressSources;
        Assert.False(sources.IdentityIsLive);
        Assert.Null(sources.DungeonActivityAt);
        Assert.False(sources.DungeonFormatRejected);
        Assert.False(sources.HasAnyBaseline);
        foreach (var scope in new[] { "all", "exploration", "pet" })
        {
            var html = await fixture.Render(scope);
            Assert.Contains("data-progress-state=\"waiting\"", html);
            Assert.Contains("data-progress-live=\"false\"", html);
            Assert.DoesNotContain(UiText.T("Netzwerkformat für Instanzabschlüsse nicht unterstützt. Bereits bestätigte Abschlüsse bleiben erhalten."), html);
        }
        var place = fixture.Exploration.Snapshot().Progress["cave"];
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, place.State);
        Assert.Equal(ProgressFreshness.Stale, place.Freshness);
        var reloaded = fixture.ReloadExploration();
        Assert.Contains("cave", reloaded.Completed);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, reloaded.Snapshot().Progress["cave"].State);
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["cave"].Freshness);
    }

    [Theory]
    [InlineData(320027, false)]
    [InlineData(320028, true)]
    public async Task FailedOrUnmappedResultCannotConfirmExplorationActivity(int dungeonId, bool succeeded)
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        fixture.Receive(Completion(dungeonId, succeeded));
        Assert.Null(fixture.Network.ProgressSources.DungeonActivityAt);
        Assert.False(fixture.Network.ProgressSources.DungeonFormatRejected);
        Assert.Null(fixture.Exploration.Snapshot().Progress["cave"].State);
        Assert.Empty(fixture.Exploration.Completed);
        Assert.Contains("data-progress-state=\"waiting\"", await fixture.Render("exploration"));
    }

    [Fact]
    public async Task AnotherStreamsResultCannotUpdateTheBoundCharacterOrItsStatus()
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        fixture.Network.ProcessProgressMessage("another", Completion(), At);
        Assert.Null(fixture.Network.ProgressSources.DungeonActivityAt);
        Assert.Null(fixture.Exploration.Snapshot().Progress["cave"].State);
        Assert.Empty(fixture.Exploration.Completed);
        Assert.Contains("data-progress-state=\"waiting\"", await fixture.Render("exploration"));
    }

    [Fact]
    public async Task MountedStatusRefreshesForCompletionRejectionAndGap()
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        var states = await fixture.RenderTransitions(
            () => fixture.Receive(Completion()),
            () => fixture.Receive([.. Completion(), 0], At.AddSeconds(1)),
            () => fixture.Network.ProcessProgressGap("own"));
        foreach (var (html, state) in states.Zip(new[] { "waiting", "partial", "partial", "waiting" }))
            Assert.Contains($"data-progress-state=\"{state}\"", html);
        Assert.Contains("Instance completion received from the network", states[1]);
        Assert.Contains("Network format for instance completions is not supported", states[2]);
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode);
        write(writer);
        return bytes.ToArray();
    }
    private static byte[] Completion(int dungeonId = 320027, bool succeeded = true) => Packet(0x6101, writer =>
    {
        writer.Write((byte)0); writer.Write(dungeonId); writer.Write((byte)(succeeded ? 1 : 0)); writer.Write((byte)0);
    });
    private static byte[] PetSnapshot() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
        writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
    });
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });
    private static byte[] Quests() => Packet(0x8D09, writer =>
    {
        writer.Write((byte)1); writer.Write(2810027); writer.Write((byte)4); writer.Write((byte)1);
        writer.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); writer.Write(1L); writer.Write(new byte[2]);
    });
    private static byte[] Teleports() => Packet(0x9232, writer => { writer.Write((byte)1); writer.Write(1052026); });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-dungeon-status-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private string ExplorationPath => Path.Combine(_folder, "exploration.json");
        public ProgressService Progress { get; }
        public ExplorationService Exploration { get; }
        public NetworkLootService Network { get; }
        public Fixture(string language)
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
        public void BindOwn() => Network.ObserveIdentity("own", Own, At);
        public void Receive(byte[] packet, DateTimeOffset? at = null) => Network.ProcessProgressMessage("own", packet, at ?? At);
        public ExplorationService ReloadExploration() => new(ExplorationPath, Places);
        public async Task<string> Render(string scope)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<NetworkProgressStatus>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(NetworkProgressStatus.Scope)] = scope }))).ToHtmlString()));
        }
        public async Task<IReadOnlyList<string>> RenderTransitions(params Action[] changes)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<NetworkProgressStatus>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(NetworkProgressStatus.Scope)] = "exploration" })));
            var states = new List<string> { await renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString()) };
            foreach (var change in changes)
            {
                await renderer.Dispatcher.InvokeAsync(change);
                states.Add(await renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString()));
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
