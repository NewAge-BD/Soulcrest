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
public sealed class NetworkProgressSourceStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic network profile");

    [Theory]
    [InlineData("pet", "all", "partial", true)]
    [InlineData("pet", "pet", "current", true)]
    [InlineData("pet", "exploration", "waiting", false)]
    [InlineData("quest", "exploration", "partial", true)]
    [InlineData("teleport", "exploration", "partial", true)]
    [InlineData("quest+teleport", "exploration", "current", true)]
    [InlineData("quest+teleport", "all", "partial", true)]
    [InlineData("pet+quest+teleport", "all", "current", true)]
    [InlineData("delta", "all", "partial", false)]
    [InlineData("delta", "pet", "partial", false)]
    [InlineData("delta", "exploration", "waiting", false)]
    public async Task StatusReflectsOnlyTheRequestedSources(string received, string scope, string state, bool hasBaseline)
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        foreach (var source in received.Split('+')) fixture.Receive(source);
        var html = await fixture.Render(scope);
        Assert.Contains($"data-progress-state=\"{state}\"", html);
        Assert.Contains($"data-progress-live=\"{hasBaseline.ToString().ToLowerInvariant()}\"", html);
        if (scope == "pet") Assert.DoesNotContain("Detected character profile", html);
        else Assert.Contains("Detected character profile: Synthetic network profile", html);
        if (state != "waiting") Assert.DoesNotContain(">Awaiting confirmation<", html);
        if (received == "quest+teleport")
        {
            Assert.True(fixture.Network.ProgressIsLive); // exploration must not require a pet baseline
            Assert.Null(fixture.Network.PetBaselineAt);
        }
        if (received == "delta")
        {
            Assert.Null(fixture.Network.PetBaselineAt);
            Assert.Equal(2, fixture.Progress.Souls("fossa"));
            Assert.Contains("complete pet baseline is still pending", await fixture.Render("pet"));
        }
    }

    [Theory]
    [InlineData("de", "Teilweise empfangen", "Erkanntes Charakterprofil")]
    [InlineData("en", "Partially received", "Detected character profile")]
    public async Task SetupPartialStateIsLocalizedAndDoesNotHideAnAcceptedPetBaseline(string language, string partial, string profile)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        fixture.Receive("pet");
        var html = await fixture.Render("all");
        Assert.Contains(partial, html);
        Assert.Contains(profile, html);
        Assert.Contains("data-progress-state=\"partial\"", html);
        Assert.Contains("data-progress-live=\"true\"", html);
        Assert.Contains(language == "de" ? "Pet-Baseline" : "Pet baseline", html);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)4)]
    public async Task OnlyTheLiveVerifiedQuestPatternConfirmsCompletion(byte status)
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        fixture.Network.ProcessProgressMessage("own", Quests(status), At);
        fixture.Receive("teleport");
        var html = await fixture.Render("exploration");
        Assert.Contains("data-progress-state=\"current\"", html);
        Assert.Contains("Confirmed completions and Kibelisk bindings are shown", html);
        var place = fixture.Exploration.Snapshot().Progress["cave"];
        Assert.Equal(status, place.QuestStatus);
        Assert.Equal(ProgressFreshness.Fresh, place.RawFreshness);
        Assert.Equal(status == 4 ? ExplorationProgressState.DiscoveredCompleted : (ExplorationProgressState?)null, place.State);
        Assert.Equal(status == 4 ? new[] { "cave", "pillar" } : new[] { "pillar" }, fixture.Exploration.Completed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GlobalPetBaselineAppliesBeforeIdentityWhileForeignExplorationSourcesAreIgnored()
    {
        await using var fixture = new Fixture("en");
        fixture.Receive("pet");
        Assert.False(fixture.Network.ProgressSources.IdentityIsLive);
        Assert.NotNull(fixture.Network.PetBaselineAt);
        Assert.Contains("data-progress-state=\"partial\"", await fixture.Render("all"));
        fixture.BindOwn();
        Assert.Contains("data-progress-state=\"partial\"", await fixture.Render("all"));
        Assert.NotNull(fixture.Network.PetBaselineAt);
        fixture.Receive("pet");
        Assert.Contains("data-progress-state=\"partial\"", await fixture.Render("all"));
        fixture.Network.ProcessProgressMessage("another", Quests(2), At);
        fixture.Network.ProcessProgressMessage("another", Teleports(), At);
        Assert.Null(fixture.Network.QuestBaselineAt);
        Assert.Null(fixture.Network.TeleportBaselineAt);
        Assert.Contains("data-progress-state=\"waiting\"", await fixture.Render("exploration"));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("switch")]
    [InlineData("stop")]
    [InlineData("unsupported-own")]
    public async Task TransportInvalidationClearsSourcesWhileOwnChangesKeepGlobalPetProgress(string invalidation)
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        foreach (var source in new[] { "pet", "quest", "teleport" }) fixture.Receive(source);
        var before = fixture.Progress.Souls("fossa");
        switch (invalidation)
        {
            case "gap": fixture.Network.ProcessProgressGap("own"); break;
            case "switch": fixture.Network.ObserveIdentity("new-own", Own with { CharacterId = 202, Name = "Synthetic second profile" }, At); break;
            case "stop": fixture.Network.Stop(); break;
            case "unsupported-own": fixture.Network.ProcessProgressMessage("own", [0x33, 0x36, 0], At); break;
        }
        var sources = fixture.Network.ProgressSources;
        var transportEnded = invalidation is "gap" or "stop";
        Assert.Equal(!transportEnded, sources.PetBaselineAt is not null);
        Assert.Null(sources.QuestBaselineAt);
        Assert.Null(sources.TeleportBaselineAt);
        Assert.Equal(before, fixture.Progress.Souls("fossa"));
        var html = await fixture.Render("all");
        Assert.Contains($"data-progress-state=\"{(transportEnded ? "waiting" : "partial")}\"", html);
        Assert.Contains($"data-progress-live=\"{(!transportEnded).ToString().ToLowerInvariant()}\"", html);
        if (invalidation == "unsupported-own") Assert.Contains("Network format is not supported. Saved progress is retained.", html);
        if (invalidation == "switch") Assert.Contains("Detected character profile: Synthetic second profile", html);
    }

    [Theory]
    [InlineData((ushort)0x8D09, "quest")]
    [InlineData((ushort)0x9232, "teleport")]
    [InlineData((ushort)0x9000, "pet")]
    [InlineData((ushort)0x9009, "pet")]
    [InlineData((ushort)0x900D, "pet")]
    [InlineData((ushort)0x900E, "pet")]
    public async Task UnsupportedSourceFormatRetainsIdentityOtherBaselinesAndSubsequentSoulGains(ushort opcode, string source)
    {
        await using var fixture = new Fixture("de");
        fixture.BindOwn();
        foreach (var received in new[] { "pet", "quest", "teleport" }) fixture.Receive(received);
        var soulEvents = new List<NetworkSoul>();
        fixture.Network.SoulGained += soulEvents.Add;
        var valid = opcode switch
        {
            0x8D09 => Quests(2),
            0x9232 => Teleports(),
            0x9000 => PetSnapshot(),
            0x9009 => Packet(0x9009, writer =>
            { writer.Write((byte)Own.EntityKey); writer.Write(1004); writer.Write(2); writer.Write((byte)0); writer.Write(0); writer.Write(0L); }),
            _ => Packet(opcode, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); }),
        };
        fixture.Network.ProcessProgressMessage("own", [.. valid, 0], At.AddSeconds(1));

        var sources = fixture.Network.ProgressSources;
        Assert.True(sources.IdentityIsLive);
        Assert.Equal(Own.Name, sources.Character);
        Assert.Equal(source != "pet", sources.PetBaselineAt is not null);
        Assert.Equal(source != "quest", sources.QuestBaselineAt is not null);
        Assert.Equal(source != "teleport", sources.TeleportBaselineAt is not null);
        Assert.Equal(8, fixture.Progress.Souls("fossa"));
        Assert.Empty(soulEvents);
        var warning = source switch
        {
            "quest" => "Quest-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.",
            "teleport" => "Teleport-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.",
            _ => "Pet-Netzwerkformat nicht unterstützt. Neue Soul-Gewinne werden weiter erfasst; der vollständige Pet-Stand muss erneut empfangen werden.",
        };
        Assert.Equal(warning, fixture.Network.ProgressStatus);
        Assert.Contains(warning, await fixture.Render("all"));

        fixture.Receive("delta");
        Assert.Equal(10, fixture.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(soulEvents).Souls);
        Assert.Equal(warning, fixture.Network.ProgressStatus);
        Assert.Contains(warning, await fixture.Render("all"));
        Assert.True(fixture.Network.ProgressSources.IdentityIsLive);
        Assert.Contains("data-progress-state=\"partial\"", await fixture.Render("all"));
        Assert.Contains($"data-progress-state=\"{(source == "pet" ? "partial" : "current")}\"", await fixture.Render("pet"));
        Assert.Contains($"data-progress-state=\"{(source == "pet" ? "current" : "partial")}\"", await fixture.Render("exploration"));
        Assert.Null(fixture.Exploration.Snapshot().Progress["cave"].State);
        Assert.Equal(new[] { "pillar" }, fixture.Exploration.Completed);

        fixture.Receive(source);
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("all"));
        Assert.Single(soulEvents); // restoring a baseline never creates an extra pickup
    }

    [Theory]
    [InlineData("de", "pet", "Pet-Netzwerkformat nicht unterstützt. Neue Soul-Gewinne werden weiter erfasst; der vollständige Pet-Stand muss erneut empfangen werden.")]
    [InlineData("en", "pet", "Pet network format is not supported. New soul gains are still recorded; the complete pet baseline must be received again.")]
    [InlineData("de", "quest", "Quest-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.")]
    [InlineData("en", "quest", "Quest network format is not supported. Pet tracking continues.")]
    [InlineData("de", "teleport", "Teleport-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.")]
    [InlineData("en", "teleport", "Teleport network format is not supported. Pet tracking continues.")]
    public async Task SourceWarningIsLocalizedInItsOwnPanelAndDoesNotReplaceAnotherPanelsCurrentStatus(string language, string source, string warning)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        foreach (var received in new[] { "pet", "quest", "teleport" }) fixture.Receive(received);
        RejectSource(fixture, source);
        var scope = source == "pet" ? "pet" : "exploration";
        var otherScope = source == "pet" ? "exploration" : "pet";
        Assert.Contains(warning, await fixture.Render(scope));
        var other = await fixture.Render(otherScope);
        Assert.DoesNotContain(warning, other);
        Assert.Contains("data-progress-state=\"current\"", other);
        fixture.Receive("delta");
        Assert.Contains(warning, await fixture.Render(scope));
        fixture.Receive(source);
        var restored = await fixture.Render(scope);
        Assert.DoesNotContain(warning, restored);
        Assert.Contains("data-progress-state=\"current\"", restored);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConcurrentPetAndQuestWarningsAreScopedAndOnlyTheirOwnBaselineClearsThem(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        foreach (var received in new[] { "pet", "quest", "teleport" }) fixture.Receive(received);
        RejectSource(fixture, "pet");
        RejectSource(fixture, "quest");
        var petWarning = UiText.T("Pet-Netzwerkformat nicht unterstützt. Neue Soul-Gewinne werden weiter erfasst; der vollständige Pet-Stand muss erneut empfangen werden.");
        var questWarning = UiText.T("Quest-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.");
        Assert.Contains(petWarning, await fixture.Render("pet"));
        Assert.DoesNotContain(questWarning, await fixture.Render("pet"));
        Assert.Contains(questWarning, await fixture.Render("exploration"));
        Assert.DoesNotContain(petWarning, await fixture.Render("exploration"));

        fixture.Receive("quest");
        Assert.True(fixture.Network.ProgressSources.PetFormatRejected);
        Assert.False(fixture.Network.ProgressSources.QuestFormatRejected);
        Assert.Contains(petWarning, await fixture.Render("pet"));
        Assert.DoesNotContain(questWarning, await fixture.Render("exploration"));
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("exploration"));
        fixture.Receive("delta");
        Assert.Contains(petWarning, await fixture.Render("pet"));
        fixture.Receive("pet");
        Assert.False(fixture.Network.ProgressSources.PetFormatRejected);
        Assert.DoesNotContain(petWarning, await fixture.Render("pet"));
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("all"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConcurrentQuestAndTeleportWarningsSurvivePetBaselinesAndClearIndependently(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.BindOwn();
        foreach (var received in new[] { "pet", "quest", "teleport" }) fixture.Receive(received);
        RejectSource(fixture, "quest");
        RejectSource(fixture, "teleport");
        var questWarning = UiText.T("Quest-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.");
        var teleportWarning = UiText.T("Teleport-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter.");
        Assert.Contains(questWarning, await fixture.Render("exploration"));
        fixture.Receive("pet");
        Assert.True(fixture.Network.ProgressSources.QuestFormatRejected);
        Assert.True(fixture.Network.ProgressSources.TeleportFormatRejected);
        Assert.Contains(questWarning, await fixture.Render("exploration"));
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("pet"));

        fixture.Receive("quest");
        Assert.False(fixture.Network.ProgressSources.QuestFormatRejected);
        Assert.True(fixture.Network.ProgressSources.TeleportFormatRejected);
        var partial = await fixture.Render("exploration");
        Assert.DoesNotContain(questWarning, partial);
        Assert.Contains(teleportWarning, partial);
        fixture.Receive("delta");
        Assert.Contains(teleportWarning, await fixture.Render("exploration"));
        fixture.Receive("teleport");
        Assert.False(fixture.Network.ProgressSources.TeleportFormatRejected);
        Assert.DoesNotContain(teleportWarning, await fixture.Render("exploration"));
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("all"));
    }

    private static void RejectSource(Fixture fixture, string source)
    {
        var packet = source switch { "pet" => PetSnapshot(), "quest" => Quests(2), "teleport" => Teleports(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)) };
        fixture.Network.ProcessProgressMessage("own", [.. packet, 0], At.AddSeconds(1));
    }

    [Fact]
    public async Task AnUnrelatedStreamGapKeepsTheBoundSourcesCurrent()
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        fixture.Receive("quest");
        fixture.Receive("teleport");
        fixture.Network.ProcessProgressGap("another");
        Assert.True(fixture.Network.ProgressIsLive);
        Assert.Contains("data-progress-state=\"current\"", await fixture.Render("exploration"));
    }

    [Fact]
    public async Task SourceEventsRefreshTheAlreadyMountedSetupStatus()
    {
        await using var fixture = new Fixture("en");
        fixture.BindOwn();
        var states = await fixture.RenderTransitions(
            () => fixture.Receive("pet"), () => fixture.Receive("quest"), () => fixture.Receive("teleport"),
            () => fixture.Network.ProcessProgressGap("own"));
        foreach (var (html, state) in states.Zip(new[] { "waiting", "partial", "partial", "current", "waiting" }))
            Assert.Contains($"data-progress-state=\"{state}\"", html);
    }

    [Fact]
    public async Task RetainedExplorationBaselinesCannotConfirmAnUnboundRunningCapture()
    {
        await using var fixture = new Fixture("en");
        foreach (var property in new[] { nameof(NetworkLootService.QuestBaselineAt), nameof(NetworkLootService.TeleportBaselineAt) })
            typeof(NetworkLootService).GetProperty(property)!.SetValue(fixture.Network, At);
        Assert.True(fixture.Network.Running);
        Assert.False(fixture.Network.ProgressIsLive);
        Assert.Contains("data-progress-state=\"waiting\"", await fixture.Render("all"));
        Assert.Contains("data-progress-live=\"false\"", await fixture.Render("all"));
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode);
        write(writer);
        return bytes.ToArray();
    }
    private static byte[] PetSnapshot() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
        writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
    });
    private static byte[] Quests(byte status) => Packet(0x8D09, writer =>
    {
        writer.Write((byte)1); writer.Write(2810001); writer.Write(status); writer.Write((byte)1);
        writer.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); writer.Write(1L); writer.Write(new byte[2]);
    });
    private static byte[] Teleports() => Packet(0x9232, writer => { writer.Write((byte)1); writer.Write(1052026); });
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-source-status-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        public ProgressService Progress { get; }
        public ExplorationService Exploration { get; }
        public NetworkLootService Network { get; }
        public Fixture(string language)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic cave", null, QuestId: 2810001),
                new("pillar", "altgard", "kibelisk", 1, "Synthetic pillar", null, TeleportId: 1052026)]);
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
        public void Receive(string source) => Network.ProcessProgressMessage("own", source switch
        {
            "pet" => PetSnapshot(), "quest" => Quests(2), "teleport" => Teleports(), "delta" => Gain(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        }, At);
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
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<NetworkProgressStatus>());
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
