using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkInitialSetupStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic setup profile");
    private const string Confirmation = "Die gemeinsame Pet-Sammlung und die verfügbaren Erkundungsdaten dieses Charakters wurden eingelesen.";

    [Theory]
    [InlineData("de", "Fortschritt einmal einlesen", "Charakterauswahl", "logge dich mit deinem Charakter wieder ein", "Warte auf die Bestätigung unten")]
    [InlineData("en", "Read progress once", "character selection", "log back in with your character", "Wait for the confirmation below")]
    public async Task SetupExplainsReloginAndNeverConfirmsCaptureIdentityOrDeltasAlone(string language, string heading, string selection, string login, string wait)
    {
        await using var f = new Fixture(language);
        var html = await f.Render<TutorialView>();
        Assert.Contains(heading, html);
        Assert.Contains(selection, html);
        Assert.Contains(login, html);
        Assert.Contains(wait, html);
        Assert.Contains("Exploration Progress", html);
        Assert.DoesNotContain("Quest baseline", html);
        Assert.DoesNotContain("Quest-Baseline", html);
        AssertUnconfirmed(html);
        f.Running();
        AssertUnconfirmed(await f.Render<TutorialView>());
        f.Network.ObserveIdentity("own", Own, At);
        AssertUnconfirmed(await f.Render<TutorialView>());
        foreach (var delta in new[] { Gain(), Binding(), Completion() }) f.Receive(delta);
        html = await f.Render<TutorialView>();
        AssertUnconfirmed(html);
        Assert.Contains("data-progress-state=\"partial\"", html);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(2, f.Progress.Souls("fossa"));
    }

    [Theory]
    [InlineData("pet,quest,teleport")]
    [InlineData("pet,teleport,quest")]
    [InlineData("quest,pet,teleport")]
    [InlineData("quest,teleport,pet")]
    [InlineData("teleport,pet,quest")]
    [InlineData("teleport,quest,pet")]
    public async Task ConfirmationRequiresEveryFullSourceRegardlessOfTheirArrivalOrder(string order)
    {
        await using var f = new Fixture("en");
        f.Running();
        f.Network.ObserveIdentity("own", Own, At);
        var sources = order.Split(',');
        for (var i = 0; i < sources.Length; i++)
        {
            f.Receive(Source(sources[i]), At.AddSeconds(i));
            var html = await f.Render<NetworkProgressStatus>();
            Assert.Equal(i == 2, f.Network.ProgressSources.InitialProgressConfirmed);
            if (i < 2) AssertUnconfirmed(html);
            else
            {
                Assert.Contains("data-progress-initialized=\"true\"", html);
                Assert.Contains("data-progress-state=\"current\"", html);
                Assert.Contains(UiText.T(Confirmation), html);
            }
        }
        // A received complete source is not a claim that every catalogued place was completed.
        Assert.Null(f.Exploration.Snapshot().Progress["missing-stronghold"].State);
        Assert.DoesNotContain("missing-stronghold", f.Exploration.Completed);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExplorationOwnBindingGuardsSetupWhileTheGlobalPetBaselineAlreadyApplies(string language)
    {
        await using var f = new Fixture(language);
        f.Running();
        foreach (var source in new[] { "pet", "quest", "teleport" }) f.Receive(Source(source));
        AssertUnconfirmed(await f.Render<TutorialView>());
        f.Network.ObserveIdentity("other", Own, At);
        AssertUnconfirmed(await f.Render<TutorialView>());
        foreach (var source in new[] { "pet", "quest", "teleport" }) f.Receive(Source(source));
        AssertUnconfirmed(await f.Render<TutorialView>());
        f.Network.ResetProgressContext();
        f.Receive(EntryCandidate());
        foreach (var source in new[] { "pet", "quest", "teleport" }) f.Receive(Source(source));
        AssertUnconfirmed(await f.Render<TutorialView>());
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Exploration.Completed);
        f.Receive(Preparation(), At.AddSeconds(1));
        AssertUnconfirmed(await f.Render<TutorialView>());
        Assert.Equal(8, f.Progress.Souls("fossa"));
        f.Network.ObserveIdentity("own", Own, At.AddSeconds(2));
        Assert.Contains(UiText.T(Confirmation), await f.Render<TutorialView>());
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        var savedSouls = f.Progress.Souls("fossa");
        f.Network.ProcessProgressGap("own");
        AssertUnconfirmed(await f.Render<TutorialView>());
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(savedSouls, f.Progress.Souls("fossa"));
    }

    [Theory]
    [InlineData("de", "unanchored")]
    [InlineData("en", "unanchored")]
    [InlineData("de", "no-prepare")]
    [InlineData("en", "no-prepare")]
    [InlineData("de", "wrong-character")]
    [InlineData("en", "wrong-character")]
    [InlineData("de", "wrong-entity")]
    [InlineData("en", "wrong-entity")]
    [InlineData("de", "wrong-stream")]
    [InlineData("en", "wrong-stream")]
    [InlineData("de", "second-prepare")]
    [InlineData("en", "second-prepare")]
    [InlineData("de", "gap")]
    [InlineData("en", "gap")]
    [InlineData("de", "expired")]
    [InlineData("en", "expired")]
    [InlineData("de", "duplicate-source")]
    [InlineData("en", "duplicate-source")]
    public async Task UnsafeExplorationSequencesNeverConfirmSetupOrContaminateCharacterProgress(string language, string boundary)
    {
        await using var f = new Fixture(language);
        f.Running();
        if (boundary != "unanchored") f.Receive(EntryCandidate());
        foreach (var source in new[] { "pet", "quest", "teleport" }) f.Receive(Source(source));
        AssertUnconfirmed(await f.Render<TutorialView>());
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Exploration.Completed);
        if (boundary == "duplicate-source") f.Receive(Source("quest"));
        if (boundary != "no-prepare") f.Receive(Preparation(), At.AddSeconds(1));
        if (boundary == "second-prepare") f.Receive(Preparation(), At.AddSeconds(2));
        if (boundary == "gap") f.Network.ProcessProgressGap("own");
        var identity = boundary switch
        {
            "wrong-character" => Own with { CharacterId = 202 },
            "wrong-entity" => Own with { EntityKey = 101 },
            _ => Own,
        };
        var stream = boundary == "wrong-stream" ? "other" : "own";
        var ownAt = boundary == "expired" ? At.AddMinutes(2).AddTicks(1) : At.AddSeconds(3);
        f.Network.ObserveIdentity(stream, identity, ownAt);
        AssertUnconfirmed(await f.Render<TutorialView>());
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        if (boundary == "gap") Assert.Null(f.Network.PetBaselineAt);
        else Assert.NotNull(f.Network.PetBaselineAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Empty(f.Exploration.Completed);
        Assert.Empty(f.Progress.Recent());
    }

    [Theory]
    [InlineData("de", "kibelisk")]
    [InlineData("en", "kibelisk")]
    [InlineData("de", "instance")]
    [InlineData("en", "instance")]
    public async Task UnsupportedDeltaPreventsSuccessEvenWhenCompleteBaselinesRemainAvailable(string language, string source)
    {
        await using var f = new Fixture(language);
        f.Running();
        f.Network.ObserveIdentity("own", Own, At);
        foreach (var name in new[] { "pet", "quest", "teleport" }) f.Receive(Source(name));
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        var delta = source == "kibelisk" ? Binding() : Completion();
        f.Receive([.. delta, 0]);
        Assert.True(f.Network.ProgressSources.HasAllBaselines);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertUnconfirmed(await f.Render<TutorialView>());
        foreach (var name in new[] { "pet", "quest", "teleport" }) f.Receive(Source(name));
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        AssertUnconfirmed(await f.Render<TutorialView>());
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AlreadyMountedSetupShowsSuccessOnlyAfterTheThirdSourceAndWithdrawsItOnGap(string language)
    {
        await using var f = new Fixture(language);
        f.Running();
        f.Network.ObserveIdentity("own", Own, At);
        var states = await f.RenderTransitions(
            () => f.Receive(Gain()),
            () => f.Receive(Source("pet")),
            () => f.Receive(Source("quest")),
            () => f.Receive(Source("teleport")),
            () => f.Network.ProcessProgressGap("own"));
        for (var i = 0; i < states.Count; i++)
        {
            if (i == 4)
            {
                Assert.Contains("data-progress-initialized=\"true\"", states[i]);
                Assert.Contains(UiText.T(Confirmation), states[i]);
            }
            else AssertUnconfirmed(states[i]);
        }
    }

    private static void AssertUnconfirmed(string html)
    {
        Assert.Contains("data-progress-initialized=\"false\"", html);
        Assert.DoesNotContain(UiText.T(Confirmation), html);
    }
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] EntryCandidate() => Packet(0x3620, writer =>
    {
        var bodyStart = writer.BaseStream.Position;
        writer.Write((ushort)0);
        var entity = (uint)Own.EntityKey;
        while (entity >= 0x80)
        {
            writer.Write((byte)(entity | 0x80));
            entity >>= 7;
        }
        writer.Write((byte)entity);
        writer.Write(new byte[4]);
        writer.Write(Own.CharacterId);
        writer.Write(new byte[2279 - (int)(writer.BaseStream.Position - bodyStart)]);
    });
    private static byte[] Preparation() => Packet(0x3621, writer => writer.Write(new byte[46]));
    private static byte[] Source(string source) => source switch
    {
        "pet" => Packet(0x9000, writer =>
        {
            writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
            writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
        }),
        "quest" => Packet(0x8D09, writer => writer.Write((byte)0)),
        "teleport" => Packet(0x9232, writer => { writer.Write((byte)1); writer.Write(1052020); }),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });
    private static byte[] Binding() => Packet(0x9233, writer => writer.Write(1052020));
    private static byte[] Completion() => Packet(0x6101, writer =>
    { writer.Write((byte)0); writer.Write(320034); writer.Write((byte)1); writer.Write((byte)0); });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-initial-setup-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture(string language)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic setup cave", null, QuestId: 2810034, DungeonId: 320034),
                new("missing-stronghold", "altgard", "stronghold", 1, "Synthetic missing Stronghold", null, QuestId: 2820006),
                new("pillar", "altgard", "kibelisk", 2, "Synthetic setup pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton(Progress);
            services.AddSingleton(Exploration);
            services.AddSingleton(Network);
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime, NoJs>();
            _provider = services.BuildServiceProvider();
            _provider.GetRequiredService<SettingsService>().Current.UiLanguage = UiText.Language = language;
        }
        internal void Running() => typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        internal void Receive(byte[] message, DateTimeOffset? at = null) => Network.ProcessProgressMessage("own", message, at ?? At);
        internal async Task<string> Render<T>() where T : IComponent
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<T>()).ToHtmlString()));
        }
        internal async Task<IReadOnlyList<string>> RenderTransitions(params Action[] changes)
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<TutorialView>());
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
            if (_savedSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _savedSettings);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, recursive: true);
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
