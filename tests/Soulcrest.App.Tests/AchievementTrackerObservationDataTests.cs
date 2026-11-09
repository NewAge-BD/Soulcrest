using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
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
public sealed class AchievementTrackerObservationDataTests : IDisposable
{
    private readonly string? _port = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
    private static readonly Guid SourceId = Guid.Parse("9a176df1-8a09-47ad-a724-926c464339ef");
    private static readonly AchievementProfileBinding Binding = new(new string('a', 32), new string('b', 32));
    public AchievementTrackerObservationDataTests() => Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", "9222");

    [Fact]
    public void ExplicitAnonymousShapePreservesGreyRowsAndExposesOnlyEqualityForPrivateBindings()
    {
        const int family = 433101200;
        var raw = Raw(family, 11);
        var binding = new AchievementProfileBinding("PRIVATE_PROFILE</script>", "PRIVATE_REVISION");
        var view = new AchievementViewState { Binding = binding, ProfileId = binding.ProfileId, Source = AchievementViewSource.Live,
            Generation = 7, Sequence = 12, MetadataBuild = 26425393, RuleVersion = "PRIVATE_RULE", Issues = ["PRIVATE_ISSUE"] };
        var own = new AchievementOwnSnapshot(binding, view, raw, 12345) { SourceInstanceId = SourceId };
        var source = new NetworkAchievementObservation(14, 7, DateTimeOffset.UnixEpoch,
            new(new(99887766554433, 773311, 1554433221, "PRIVATE_NAME"), 7),
            new(raw, NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch, 14, "PRIVATE_SOURCE"), NetworkAchievementObservationKind.Baseline)
            { SourceInstanceId = SourceId };
        var tracker = new AchievementTrackerSnapshot(binding, [new() { FamilyId = family, TrackedTitleId = 23040023,
            StageId = family + 1, State = AchievementTrackerRowState.Unknown, NameDe = "PRIVATE_NAME", ConditionEn = "PRIVATE_CONDITION",
            Current = null, Target = null, RawValue = null, IsFavorite = true }]) { IsLive = false };
        var dto = AchievementTrackerObservationPresentation.Create(source, own, view with { Binding = Binding }, tracker, true);
        var json = AchievementTrackerObservationPresentation.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "ownState", "schema", "source", "tracker" }, document.RootElement.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(new[] { "fresh", "gen", "kind", "rowCount", "seq", "status" },
            document.RootElement.GetProperty("source").EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(new[] { "gen", "isLive", "metadataBuild", "rowCount", "selectedBindingMatchesOwn", "seq", "source", "sourceInstanceMatchesSource" },
            document.RootElement.GetProperty("ownState").EnumerateObject().Select(item => item.Name).Order());
        Assert.DoesNotContain("PRIVATE_", json); Assert.DoesNotContain(SourceId.ToString(), json);
        Assert.DoesNotContain("99887766554433", json); Assert.DoesNotContain("773311", json); Assert.DoesNotContain("1554433221", json);
        Assert.False(dto.OwnState!.SelectedBindingMatchesOwn); Assert.True(dto.OwnState.SourceInstanceMatchesSource);
        Assert.Equal(26425393, dto.OwnState.MetadataBuild); Assert.Equal(1, dto.OwnState.RowCount);
        Assert.True(dto.Tracker!.BindingMatchesOwn); Assert.True(dto.Tracker.SourceInstanceMatchesOwn);
        var row = Assert.Single(dto.Tracker.Rows);
        Assert.Equal(family, row.FamilyId); Assert.Equal(23040023, row.TitleId); Assert.True(row.Favorite);
        Assert.Null(row.Current); Assert.Null(row.Target); Assert.Null(row.RawValue); // Never repair the tracker from a source count.
        Assert.Equal(new[] { "current", "familyId", "favorite", "historical", "rawValue", "stageId", "state", "target", "titleId", "unit" },
            document.RootElement.GetProperty("tracker").GetProperty("rows")[0].EnumerateObject().Select(item => item.Name).Order());
    }

    [Fact]
    public void EmptyOrDifferentBindingsAndUnknownEnumValuesCannotAcquirePositiveDiagnosticAuthority()
    {
        var view = new AchievementViewState { Source = (AchievementViewSource)999 };
        var own = new AchievementOwnSnapshot(null, view, null, 0);
        var source = new NetworkAchievementObservation(1, 1, DateTimeOffset.UnixEpoch, null,
            new(null, (NetworkCombatSourceStatus)999, false, null, 0, "PRIVATE_SOURCE"), (NetworkAchievementObservationKind)999);
        var tracker = new AchievementTrackerSnapshot(null, [new() { State = (AchievementTrackerRowState)999,
            Unit = (CombatAchievementProgressUnit)999 }]);
        var dto = AchievementTrackerObservationPresentation.Create(source, own, view, tracker, true);
        Assert.False(dto.OwnState!.SelectedBindingMatchesOwn); Assert.False(dto.OwnState.SourceInstanceMatchesSource);
        Assert.False(dto.Tracker!.BindingMatchesOwn); Assert.False(dto.Tracker.SourceInstanceMatchesOwn);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, dto.Source!.Status); Assert.Null(dto.Source.Kind);
        Assert.Equal(AchievementViewSource.Unknown, dto.OwnState.Source);
        Assert.Equal(AchievementTrackerRowState.Unknown, Assert.Single(dto.Tracker.Rows).State);
        Assert.Equal(CombatAchievementProgressUnit.Unknown, Assert.Single(dto.Tracker.Rows).Unit);
    }

    [Theory]
    [InlineData(null, true)] [InlineData("invalid", true)] [InlineData("1024", true)]
    [InlineData("65536", true)] [InlineData("9222", false)]
    public async Task DisabledOrInvalidPortNeverResolvesAnyDiagnosticService(string? port, bool enabled)
    {
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", port);
        var resolutions = 0;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<INetworkAchievementSource>(_ => { resolutions++; throw new InvalidOperationException("Disabled diagnostics resolved a source."); });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AchievementTrackerObservationData>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(AchievementTrackerObservationData.Enabled)] = enabled }))).ToHtmlString());
        Assert.Empty(html); Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task ValidPortWithOptionalServicesMissingPublishesExplicitNullSections()
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AchievementTrackerObservationData>()).ToHtmlString());
        using var json = Read(html);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("ownState").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("tracker").ValueKind);
    }

    [Fact]
    public async Task RealStateAndTrackerEventsRefreshTheDefaultMountedBlockAndDisposalDetachesTheSource()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Binding, 420101100);
        fixture.Source.Emit(1); await fixture.State.FlushAsync(); fixture.Tracker.Tick();
        var subscriptions = fixture.Source.Subscribers;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(fixture.State); services.AddSingleton(fixture.Tracker); services.AddSingleton<INetworkAchievementSource>(fixture.Source);
        await using var provider = services.BuildServiceProvider();
        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        try
        {
            var root = await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<AchievementTrackerObservationData>());
            Assert.Equal(subscriptions + 1, fixture.Source.Subscribers);
            using var first = Read(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.True(first.RootElement.GetProperty("ownState").GetProperty("sourceInstanceMatchesSource").GetBoolean());
            Assert.True(first.RootElement.GetProperty("tracker").GetProperty("sourceInstanceMatchesOwn").GetBoolean());
            Assert.Equal(1, first.RootElement.GetProperty("tracker").GetProperty("rows")[0].GetProperty("current").GetInt64());
            fixture.Source.Emit(2); await fixture.State.FlushAsync(); fixture.Tracker.Tick();
            using var next = Read(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Equal(2, next.RootElement.GetProperty("source").GetProperty("seq").GetInt64());
            Assert.Equal(2, next.RootElement.GetProperty("ownState").GetProperty("seq").GetInt64());
            Assert.Equal(2, next.RootElement.GetProperty("tracker").GetProperty("rows")[0].GetProperty("current").GetInt64());
        }
        finally { await renderer.DisposeAsync(); }
        Assert.Equal(subscriptions, fixture.Source.Subscribers);
        fixture.Source.Emit(3); await fixture.State.FlushAsync(); fixture.Tracker.Tick();
    }

    private static JsonDocument Read(string html)
    {
        var match = Regex.Match(html, "<script[^>]*data-achievement-tracker-observation[^>]*>([\\s\\S]*?)</script>");
        Assert.True(match.Success); Assert.DoesNotContain("&quot;", match.Groups[1].Value);
        return JsonDocument.Parse(match.Groups[1].Value);
    }
    private static CharacterAchievementState Raw(int family, int count) => new(
        ImmutableDictionary<int, CharacterAchievement>.Empty.Add(family, new(count, 0, 0)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-observation-" + Guid.NewGuid().ToString("N"));
        internal Source Source { get; } = new();
        internal AchievementStateService State { get; }
        internal AchievementFavoriteService Favorites { get; }
        internal AchievementTrackerService Tracker { get; }
        internal Fixture()
        {
            State = new(Source, new AchievementStateStore(Path.Combine(_root, "history")), _ => Binding, () => Binding);
            Favorites = new(Path.Combine(_root, "favorites")); Tracker = new(State, Favorites);
        }
        public void Dispose() { Tracker.Dispose(); Favorites.Dispose(); State.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Source : INetworkAchievementSource
    {
        private Action<NetworkAchievementObservation>? _observed;
        private long _sequence;
        public int Subscribers { get; private set; }
        public NetworkAchievementObservation AchievementSnapshot { get; private set; } = new(0, 7, DateTimeOffset.UnixEpoch,
            new(new(101, 501, 300, "Synthetic owner"), 7), NetworkCombatDomain<CharacterAchievementState>.Unknown, NetworkAchievementObservationKind.Invalidated)
            { SourceInstanceId = SourceId };
        public AchievementIngressDiagnostics AchievementDiagnostics => new();
        public event Action<NetworkAchievementObservation>? AchievementObserved
        { add { _observed += value; Subscribers++; } remove { _observed -= value; Subscribers--; } }
        internal void Emit(int count)
        {
            var sequence = ++_sequence;
            AchievementSnapshot = AchievementSnapshot with { Sequence = sequence, Generation = 7,
                Kind = sequence == 1 ? NetworkAchievementObservationKind.Baseline : NetworkAchievementObservationKind.Update,
                Domain = new(Raw(420101100, count), NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch.AddSeconds(sequence), sequence, "own-achievement-list") };
            _observed?.Invoke(AchievementSnapshot);
        }
    }
    public void Dispose() => Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", _port);
}
