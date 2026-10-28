using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Production SVG and rendered callbacks over synthetic replay prefixes; no app or capture is started.</summary>
[Collection("Settings file")]
public sealed class LootTrackerRouteMarkerIconViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string MapId = "synthetic-marker-map";

    private static LootTrackerSession Session(string language)
    {
        LootTrackerGatherVisit Visit(string id, int second, string node) => new(id, 0, MapId, "Example map",
            1200, 1250, 1200, 1250, At.AddSeconds(second), true, "Synthetic resource", CatalogNodeId: node);
        LootTrackerLoot Item(int id, long quantity) => new("Item:" + id, MonsterLootItemCatalog.Default.Name(id, language), LootTrackerLootKind.Item, quantity);
        var first = Visit("first-harvest", 12, "same-resource-node");
        var second = Visit("second-harvest", 22, "same-resource-node");
        var odyle = Visit("odyle-harvest", 18, "other-resource-node");
        return new()
        {
            Id = "synthetic-route-marker-icons", Kind = LootTrackerSessionKind.Abyss, UnifiedVersion = 1, TimelineVersion = 1,
            Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddSeconds(30), Elapsed = TimeSpan.FromSeconds(30),
            HarvestCount = 3, ItemCount = 24,
            RouteSegments = [new(MapId, "Example map", [new(1000, 1000, At, TimeSpan.Zero),
                new(1100, 1100, At.AddSeconds(5), TimeSpan.FromSeconds(5)), new(1500, 1500, At.AddSeconds(30), TimeSpan.FromSeconds(30))])],
            CombatEvents = [new("known-kill", LootTrackerCombatKind.Kill, At.AddSeconds(10), TimeSpan.FromSeconds(10),
                    new(MapId, "Example map", 1150, 1150, At.AddSeconds(10)))
                    { OpponentClassId = "Ranger", OpponentAbyssRankGrade = 7, Origin = LootTrackerActivityOrigin.Abyss },
                new("own-death", LootTrackerCombatKind.Death, At.AddSeconds(15), TimeSpan.FromSeconds(15),
                    new(MapId, "Example map", 1220, 1220, At.AddSeconds(15))) { Origin = LootTrackerActivityOrigin.Abyss },
                new("unknown-kill", LootTrackerCombatKind.Kill, At.AddSeconds(25), TimeSpan.FromSeconds(25),
                    new(MapId, "Example map", 1400, 1400, At.AddSeconds(25)))
                    { OpponentClassId = "UnknownSyntheticClass", OpponentAbyssRankGrade = 99, Origin = LootTrackerActivityOrigin.Abyss }],
            GatherVisits = [first, second, odyle],
            TimelineEvents = [new("first-reward", LootTrackerTimelineEventKind.HarvestSuccess, first.At, TimeSpan.FromSeconds(12))
                    { GatherVisit = first, Origin = LootTrackerActivityOrigin.Lifeskill,
                        Loot = [Item(610530001, 3), Item(210230006, 1), Item(1999999999, 2)] },
                new("second-reward", LootTrackerTimelineEventKind.HarvestSuccess, second.At, TimeSpan.FromSeconds(22))
                    { GatherVisit = second, Origin = LootTrackerActivityOrigin.Lifeskill, Loot = [Item(610530001, 4)] },
                new("odyle-reward", LootTrackerTimelineEventKind.HarvestSuccess, odyle.At, TimeSpan.FromSeconds(18))
                    { GatherVisit = odyle, Origin = LootTrackerActivityOrigin.Lifeskill, Loot = [Item(610610013, 5)] },
                new("unplaced-drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(26), TimeSpan.FromSeconds(26))
                    { Origin = LootTrackerActivityOrigin.Abyss, Loot = [Item(610530005, 9)] }]
        };
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task KillModeUsesExactBundledRankOrClassAssetsThroughTheRenderedSelector(string language)
    {
        await using var fixture = new Fixture(language); var session = Session(language);
        var html = await fixture.Render(session, 18);
        var kill = Assert.Single(Markers(html, "kill"));
        Assert.Contains("data-kill-icon-mode=\"rank\"", kill);
        Assert.Contains("href=\"abyss-rank-icons/rank-07.png\"", kill);
        Assert.Contains("lt-route-kill-icon", kill); Assert.Contains("lt-route-kill-skull", kill);
        Assert.DoesNotContain("href=\"class-icons/Ranger.png\"", kill);
        var route = RoutePoints(html); var replay = fixture.Component.Replay;
        html = await fixture.ChangeMode("Class"); kill = Assert.Single(Markers(html, "kill"));
        Assert.Contains("data-kill-icon-mode=\"class\"", kill);
        Assert.Contains("href=\"class-icons/Ranger.png\"", kill);
        Assert.DoesNotContain("href=\"abyss-rank-icons/rank-07.png\"", kill);
        Assert.Equal(LootTrackerReplayKillIconMode.Class, new SettingsService().Current.LootReplayKillIconMode);
        Assert.Same(session, fixture.Component.Session); Assert.Same(replay, fixture.Component.Replay);
        Assert.Equal(TimeSpan.FromSeconds(18), fixture.Component.Replay!.Elapsed);
        Assert.Equal(route, RoutePoints(html)); Assert.Equal(3, session.CombatEvents.Length);
        Assert.Single(Markers(html, "death"));
        await fixture.ChangeMode("not-an-icon-mode");
        Assert.Equal(LootTrackerReplayKillIconMode.Class, fixture.Settings.Current.LootReplayKillIconMode);
    }

    [Theory]
    [InlineData("de", "Rank")]
    [InlineData("en", "Rank")]
    [InlineData("de", "Class")]
    [InlineData("en", "Class")]
    public async Task UnknownOpponentKeepsTheKillFallbackWithoutInventingAnAsset(string language, string mode)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.SetLootReplayKillIconMode(Enum.Parse<LootTrackerReplayKillIconMode>(mode));
        var source = Session(language);
        source = source with { CombatEvents = [source.CombatEvents.Single(row => row.Id == "unknown-kill")] };
        var kill = Assert.Single(Markers(await fixture.Render(source, 30), "kill"));
        Assert.Contains("lt-route-kill-skull", kill);
        Assert.DoesNotContain("class=\"lt-route-kill-icon", kill);
        Assert.DoesNotContain("abyss-rank-icons/", kill); Assert.DoesNotContain("class-icons/", kill);
        Assert.Contains("data-kill-icon-mode=\"" + mode.ToLowerInvariant() + "\"", kill);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task HarvestMarkersRenderEveryConfirmedItemWithExactAssetQuantityAndRarity(string language)
    {
        await using var fixture = new Fixture(language);
        var source = Session(language); var html = await fixture.Render(source, 30);
        Assert.Single(Markers(html, "gathering")); Assert.Single(Markers(html, "odyle"));
        foreach (var (key, quantity, rarity) in new[] { ("Item:610530001", "7", "common"), ("Item:210230006", "1", "unique"),
            ("Item:610610013", "5", MonsterLootItemCatalog.Default.DropRarity("Item:610610013").ToString().ToLowerInvariant()) })
        {
            var item = ItemMarker(html, key);
            Assert.Contains("data-quantity=\"" + quantity + "\"", item);
            Assert.Contains("data-rarity=\"" + rarity + "\"", item);
            Assert.Contains("href=\"" + MonsterLootIconCatalog.Default.DropAsset(key) + "\"", item);
            Assert.Matches("<rect\\b", item);
        }
        var unknown = ItemMarker(html, "Item:1999999999");
        Assert.Contains("data-rarity=\"unknown\"", unknown); Assert.Contains("data-quantity=\"2\"", unknown);
        Assert.DoesNotContain("<image", unknown);
        Assert.DoesNotContain("data-loot-id=\"Item:610530005\"", html); // A reward without a position stays in the timeline.
        Assert.Contains(MonsterLootItemCatalog.Default.Name(210230006, language), WebUtility.HtmlDecode(html));
        Assert.Equal(4, Regex.Matches(html, "class=\"lt-route-item-marker").Count);
        Assert.Equal(4, source.TimelineEvents.Length); Assert.Equal(3, source.GatherVisits.Length);
    }

    [Fact]
    public async Task ScrubbingBackwardRemovesFutureIconsAndRestoresOnlyTheEarlierHarvestQuantities()
    {
        await using var fixture = new Fixture("en"); var source = Session("en");
        var end = await fixture.Render(source, 30);
        Assert.Equal(2, Markers(end, "kill").Length);
        Assert.Contains("data-quantity=\"7\"", ItemMarker(end, "Item:610530001"));
        var earlier = await fixture.Prefix(18);
        Assert.Single(Markers(earlier, "kill"));
        Assert.Contains("data-quantity=\"3\"", ItemMarker(earlier, "Item:610530001"));
        Assert.DoesNotContain("data-quantity=\"7\"", ItemMarker(earlier, "Item:610530001"));
        var before = await fixture.Prefix(9);
        Assert.Empty(Markers(before, "kill")); Assert.Empty(Markers(before, "death"));
        Assert.DoesNotContain("lt-route-item-marker", before);
        Assert.DoesNotContain("lt-route-kill-icon", before);
        Assert.Equal(3, source.CombatEvents.Length); Assert.Equal(4, source.TimelineEvents.Length);
    }

    [Fact]
    public async Task PositionedDropHasItsOwnItemMarkerWhileAnUnpositionedRewardNeverBorrowsItsLocation()
    {
        await using var fixture = new Fixture("en");
        var reward = new LootTrackerLoot("Item:610530005", "Synthetic drop reward", LootTrackerLootKind.Item, 4);
        var source = Session("en") with { Kind = LootTrackerSessionKind.Mixed, CombatEvents = [], GatherVisits = [], ItemCount = 13,
            TimelineEvents = [new("placed-drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(20), TimeSpan.FromSeconds(20))
                { Origin = LootTrackerActivityOrigin.Pve, Loot = [reward], Position = new(MapId, "Example map", 1300, 1350, At.AddSeconds(20)) },
                new("unplaced-later-drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(22), TimeSpan.FromSeconds(22))
                { Origin = LootTrackerActivityOrigin.Pve, Loot = [reward with { Quantity = 9 }] }] };
        var html = await fixture.Render(source, 30);
        Assert.Single(Markers(html, "drop"));
        var marker = ItemMarker(html, reward.Id);
        Assert.Contains("data-quantity=\"4\"", marker);
        Assert.Contains("href=\"" + MonsterLootIconCatalog.Default.DropAsset(reward.Id) + "\"", marker);
        Assert.DoesNotContain("data-quantity=\"13\"", marker);
        Assert.DoesNotContain("data-quantity=\"9\"", marker);
        html = await fixture.Prefix(19);
        Assert.Empty(Markers(html, "drop")); Assert.DoesNotContain("lt-route-item-marker", html);
        Assert.Equal(2, source.TimelineEvents.Length); Assert.Null(source.TimelineEvents[1].Position);
    }

    [Fact]
    public async Task FailedPersistenceKeepsThePreviousModeAssetsAndReplayAndSuccessfulRetryPersists()
    {
        await using var fixture = new Fixture("en"); await fixture.Render(Session("en"), 18);
        var replay = fixture.Component.Replay; var bytes = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0; fixture.Settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var html = await fixture.ChangeMode("Class");
            Assert.Equal(LootTrackerReplayKillIconMode.Rank, fixture.Settings.Current.LootReplayKillIconMode);
            Assert.Equal(0, changes); Assert.Same(replay, fixture.Component.Replay);
            Assert.Contains("href=\"abyss-rank-icons/rank-07.png\"", Assert.Single(Markers(html, "kill")));
            Assert.DoesNotContain("href=\"class-icons/Ranger.png\"", html);
            Assert.Contains("data-testid=\"replay-kill-icon-error\"", html);
            Assert.Contains(UiText.T("Die Auswahl konnte nicht gespeichert werden."), WebUtility.HtmlDecode(html));
        }
        finally { Directory.Delete(AppPaths.SettingsFile); File.WriteAllBytes(AppPaths.SettingsFile, bytes); }
        var retried = await fixture.ChangeMode("Class");
        Assert.Equal(LootTrackerReplayKillIconMode.Class, new SettingsService().Current.LootReplayKillIconMode);
        Assert.Equal(1, changes); Assert.Same(replay, fixture.Component.Replay);
        Assert.DoesNotContain("data-testid=\"replay-kill-icon-error\"", retried);
    }

    [Fact]
    public async Task ExampleSelectorChangesOnlyTheSyntheticViewWithoutPersistingTheMode()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render(Session("en"), 18, example: true);
        var html = await fixture.ChangeMode("Class");
        Assert.Contains("href=\"class-icons/Ranger.png\"", Assert.Single(Markers(html, "kill")));
        Assert.Equal(LootTrackerReplayKillIconMode.Rank, fixture.Settings.Current.LootReplayKillIconMode);
        Assert.Equal(LootTrackerReplayKillIconMode.Rank, new SettingsService().Current.LootReplayKillIconMode);
    }

    [Fact]
    public async Task OptionalSettingsServiceKeepsTheExistingDefaultRankRendererUsable()
    {
        var source = Session("en");
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LootTrackerRouteMap>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerRouteMap.Session)] = source, [nameof(LootTrackerRouteMap.MapId)] = MapId,
                [nameof(LootTrackerRouteMap.Replay)] = LootTrackerSessionReplay.Build(source, TimeSpan.FromSeconds(18)) }))).ToHtmlString());
        Assert.Contains("href=\"abyss-rank-icons/rank-07.png\"", Assert.Single(Markers(html, "kill")));
    }

    private static string[] Markers(string html, string kind) => Regex.Matches(html,
        "<g\\b[^>]*data-marker-kind=\"" + Regex.Escape(kind) + "\"[^>]*>")
        .Select(match => BalancedGroup(html, match.Index)).ToArray();
    private static string ItemMarker(string html, string key)
    {
        var match = Regex.Match(html, "<g\\b[^>]*data-loot-id=\"" + Regex.Escape(key) + "\"[^>]*>");
        Assert.True(match.Success, "A confirmed positioned item must have its own SVG marker.");
        return BalancedGroup(html, match.Index);
    }
    private static string BalancedGroup(string html, int start)
    {
        var depth = 0;
        foreach (Match tag in Regex.Matches(html[start..], "</?g\\b[^>]*>"))
        {
            if (tag.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (!tag.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return html[start..(start + tag.Index + tag.Length)];
        }
        throw new InvalidOperationException("The production SVG marker must have balanced group elements.");
    }
    private static string[] RoutePoints(string html) => Regex.Matches(html, "<polyline\\b[^>]*class=\"lt-route-path\"[^>]*>")
        .Select(match => match.Value).ToArray();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private LootTrackerSession _source = new();
        public CaptureRouteMarkerIconView Component { get; private set; } = null!;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public Fixture(string language)
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = language, NameLanguage = language });
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>()); _ = Settings; UiText.Language = language;
        }
        public async Task<string> Render(LootTrackerSession source, int seconds, bool example = false)
        {
            _source = source;
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureRouteMarkerIconView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerRouteMap.Session)] = source,
                    [nameof(LootTrackerRouteMap.MapId)] = MapId,
                    [nameof(LootTrackerRouteMap.Replay)] = LootTrackerSessionReplay.Build(source, TimeSpan.FromSeconds(seconds)),
                    [nameof(LootTrackerRouteMap.IsExample)] = example,
                    [nameof(CaptureRouteMarkerIconView.Captured)] = (Action<CaptureRouteMarkerIconView>)(component => Component = component)
                })));
            return await Markup();
        }
        public async Task<string> Prefix(int seconds)
        {
            await _renderer.Dispatcher.InvokeAsync(() => Component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerRouteMap.Replay)] = LootTrackerSessionReplay.Build(_source, TimeSpan.FromSeconds(seconds)) })));
            return await Markup();
        }
#pragma warning disable BL0006
        public async Task<string> ChangeMode(string mode)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using var builder = new RenderTreeBuilder();
                typeof(LootTrackerRouteMap).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, [builder]);
                var frames = builder.GetFrames();
                var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                    && frames.Array[i].AttributeName == "data-testid" && Equals(frames.Array[i].AttributeValue, "replay-kill-icon-mode"));
                while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
                object? callback = null;
                for (var i = index + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                    if (frames.Array[i].AttributeName == "onchange") callback = frames.Array[i].AttributeValue;
                var args = new ChangeEventArgs { Value = mode };
                switch (callback)
                {
                    case EventCallback<ChangeEventArgs> typed: await typed.InvokeAsync(args); break;
                    case EventCallback untyped: await untyped.InvokeAsync(args); break;
                    case MulticastDelegate handler:
                        if (handler.DynamicInvoke([args]) is Task task) await task;
                        break;
                    default: throw new InvalidOperationException("The actual marker selector must expose its change callback.");
                }
                await Component.RefreshNow();
            });
            return await Markup();
        }
#pragma warning restore BL0006
        private Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }
}

public sealed class CaptureRouteMarkerIconView : LootTrackerRouteMap
{
    [Parameter] public Action<CaptureRouteMarkerIconView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
