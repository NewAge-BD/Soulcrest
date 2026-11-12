using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootItemNameColorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ImmutableArray<LootTrackerLoot> Items =
    [
        new("Item:610530001", "Recorded common name", LootTrackerLootKind.Item, 2),
        new("Item:610530005", "Recorded rare name", LootTrackerLootKind.Item, 3),
        new("Item:210230006", "Recorded unique name", LootTrackerLootKind.Item, 1),
        new("Item:1999999999", "Recorded unknown name", LootTrackerLootKind.Item, 4)
    ];

    [Theory]
    [InlineData("de", false)]
    [InlineData("en", false)]
    [InlineData("de", true)]
    [InlineData("en", true)]
    public async Task LiveAndHistoricalLootNamesUseExactItemColoursAcrossTheTableStatisticsAndHarvestBars(string language, bool live)
    {
        await using var f = new Fixture(language);
        var session = Session() with { Status = live ? LootTrackerSessionStatus.Live : LootTrackerSessionStatus.Completed, EndedAt = live ? null : At };
        var original = JsonSerializer.Serialize(session);
        var html = await f.Render<LootTrackerView>(new()
        {
            [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session], At),
            [nameof(LootTrackerView.IsExample)] = true, [nameof(LootTrackerView.ViewMode)] = LootTrackerViewMode.Mixed
        });
        AssertName(html, Items[0]); AssertName(html, Items[1]); AssertName(html, Items[2]); AssertNeutralName(html, Items[3].Name);
        Assert.Contains("data-loot-id=\"Item:610530005\"", html);
        Assert.Contains("data-loot-statistic=\"Item:610530005\"", html);
        var harvest = Regex.Match(html, "<div class=\"lt-resource-bars\">.*?</section>", RegexOptions.Singleline).Value;
        AssertName(harvest, Items[1]);
        Assert.Equal(original, JsonSerializer.Serialize(session));
    }

    [Fact]
    public async Task TimelineKeepsEachRecordedIdentityForMixedEventTextAndExactValueRows()
    {
        await using var f = new Fixture("en");
        var session = Session(); var original = JsonSerializer.Serialize(session);
        var events = LootTrackerTimeline.Events(session);
        var drop = Assert.Single(events, row => row.Id == "drop");
        Assert.Equal(Items.Select(row => row.Id), drop.Items.Select(row => row.Key));
        Assert.Equal(Items.Select(row => row.Name), drop.Items.Select(row => row.Name));
        Assert.Equal(Items.Select(row => row.Quantity), drop.Items.Select(row => row.Quantity));
        Assert.Contains("Recorded rare name ×3", drop.Label);
        Assert.Empty(Assert.Single(events, row => row.Id == "failure").Items);

        var text = await f.Render<LootTrackerEventText>(new()
        { [nameof(LootTrackerEventText.Items)] = drop.Items, [nameof(LootTrackerEventText.Text)] = drop.Label });
        foreach (var item in Items.Take(3)) AssertName(text, item);
        AssertNeutralName(text, Items[3].Name);
        Assert.Contains("×3", WebUtility.HtmlDecode(text));

        CapturedColourTimeline? timeline = null;
        var root = await f.RenderRoot<CapturedColourTimeline>(new()
        {
            [nameof(LootTrackerSessionTimeline.Session)] = session,
            [nameof(LootTrackerSessionTimeline.IsExample)] = true,
            [nameof(CapturedColourTimeline.Captured)] = (Action<CapturedColourTimeline>)(value => timeline = value)
        });
        await f.InvokeTimeline(timeline!, "_lootOpen", true);
        await f.InvokeTimeline(timeline!, "_exactOpen", true);
        var markup = await f.Html(root);
        var choices = Regex.Match(markup, "<ul class=\"lst-loot-list\">.*?</ul>", RegexOptions.Singleline).Value;
        foreach (var item in Items.Take(3)) AssertName(choices, item);
        var exact = Regex.Match(markup, "class=\"lst-exact\".*?</section>", RegexOptions.Singleline).Value;
        foreach (var item in Items.Take(3)) AssertName(exact, item);
        AssertNeutralName(exact, Items[3].Name);
        Assert.Equal(original, JsonSerializer.Serialize(session));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("card")]
    [InlineData("grid")]
    [InlineData("strip")]
    public async Task ActiveOverlayItemViewsUseNativeNameColoursAndPreserveCountsAndLighting(string view)
    {
        await using var f = new Fixture("en");
        var item = Items[1]; var rarity = MonsterLootItemCatalog.Default.DropRarity(item.Id);
        var snapshot = new OverlaySnapshot { Drops = [new(item.Id, item.Name, "345", Rarity: rarity, Quantity: 345)], ItemAnimationsEnabled = true };
        var before = JsonSerializer.Serialize(snapshot);
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemView = view, ShowIcon = false, Width = 400, Height = 150 };
        var html = await f.Render<OverlayWidgetPreview>(new()
        { [nameof(OverlayWidgetPreview.Widget)] = widget, [nameof(OverlayWidgetPreview.Snapshot)] = snapshot });
        AssertName(html, item);
        Assert.Contains("overlay-item-quantity", html); Assert.Contains(">345</strong>", html);
        var effect = LootRarityVisuals.Accent(rarity);
        Assert.Contains($"--rarity-glow:{effect.R},{effect.G},{effect.B}", html);
        Assert.Equal(before, JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("strip")]
    [InlineData("list")]
    [InlineData("card")]
    public async Task OverlayCellTooltipsExposeTheExactNameCountAndExistingDetailsWithoutAddingFitTargets(string view)
    {
        await using var f = new Fixture("en");
        var item = Items[1];
        var snapshot = new OverlaySnapshot
        { Drops = [new(item.Id, item.Name, "345", Quantity: 345, Tooltip: item.Name + " ×345\nRecorded detail")] };
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemView = view, ShowIcon = true, Width = 400, Height = 150 };
        var html = await f.Render<OverlayWidgetPreview>(new()
        { [nameof(OverlayWidgetPreview.Widget)] = widget, [nameof(OverlayWidgetPreview.Snapshot)] = snapshot });
        Assert.DoesNotContain("title=\"" + item.Name, html);
        Assert.Contains("overlay-item-tooltip-target", html); Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("role=\"tooltip\"", html);
        var tooltip = Regex.Match(html, "role=\"tooltip\"[^>]*>(.*?)</span>\\s*</span>\\s*</span>", RegexOptions.Singleline).Value;
        AssertName(tooltip, item);
        Assert.Contains("overlay-item-tooltip-quantity", tooltip); Assert.Contains("×345", WebUtility.HtmlDecode(tooltip));
        Assert.Contains("Recorded detail", tooltip);
        Assert.Equal(3, Regex.Matches(html, "data-overlay-fit").Count);
    }

    [Fact]
    public async Task ActiveOverlayUnknownIdentityDoesNotUseItsNameOrSuppliedRarityAsAFallback()
    {
        await using var f = new Fixture("en");
        var snapshot = new OverlaySnapshot
        { Drops = [new("Item:1999999999", "Recorded unique name", "1", Rarity: LootItemRarity.Unique, Quantity: 1)] };
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemView = "list", ShowIcon = false };
        var html = await f.Render<OverlayWidgetPreview>(new()
        { [nameof(OverlayWidgetPreview.Widget)] = widget, [nameof(OverlayWidgetPreview.Snapshot)] = snapshot });
        AssertNeutralName(html, "Recorded unique name");
        Assert.Contains("rarity-unique", html); // Existing effects remain independent of name qualification.
    }

    [Fact]
    public async Task RareNoticeChangesOnlyItsNameForegroundAndRetainsItsExistingGradeAndQuantityAccent()
    {
        await using var f = new Fixture("en");
        var snapshot = new OverlaySnapshot
        { RareNotice = new("synthetic-notice", "Recorded unique name", 2, "2", null, LootItemRarity.Unique, 1) { LootKey = "Item:210230006" } };
        var html = await f.Render<OverlayWidgetPreview>(new()
        {
            [nameof(OverlayWidgetPreview.Widget)] = OverlayCatalog.CreateWidget("rare-notice"),
            [nameof(OverlayWidgetPreview.Snapshot)] = snapshot
        });
        var name = ItemSpan(html, "Recorded unique name");
        Assert.Contains("color:" + ItemNamePresentation.ForGrade(LootItemRarity.Unique).CssColor, name);
        Assert.Contains("--notice-rarity-color:#FBCA47", html);
        Assert.Contains("×2", WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Item:1999999999")]
    public async Task AnUnqualifiedRareNoticeIdentityStaysNeutralWithoutAnOlderGradeFallback(string? key)
    {
        await using var f = new Fixture("en");
        var snapshot = new OverlaySnapshot
        { RareNotice = new("synthetic-unknown-notice", "Recorded notice", 1, "1", null, LootItemRarity.Unique, 1) { LootKey = key } };
        var html = await f.Render<OverlayWidgetPreview>(new()
        {
            [nameof(OverlayWidgetPreview.Widget)] = OverlayCatalog.CreateWidget("rare-notice"),
            [nameof(OverlayWidgetPreview.Snapshot)] = snapshot
        });
        AssertNeutralName(html, "Recorded notice");
        Assert.Contains("--notice-rarity-color:#FBCA47", html);
    }

    [Fact]
    public void AcceptedRareReceiptPreservesItsExactKeyThroughTheActiveOverlayProjectionWithoutStartingAudio()
    {
        const string profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var session = Session() with { Status = LootTrackerSessionStatus.Live, EndedAt = null, Character = new(profile, null) };
        using var alerts = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("A presentation projection must never start audio."), new Clock());
        alerts.Accept(new(session.Id, profile, At, "item:210230006", "Recorded receipt name", 2));
        var receipt = Assert.IsType<RareDropNotice>(alerts.LatestVisualNotice);
        Assert.Equal("item:210230006", receipt.LootKey);
        var projected = OverlayEditorPresentation.Project(new(session, true, new(null, null, null, 0), null, false, At),
            null, receipt, At, TimeZoneInfo.Utc, "en");
        Assert.Equal(receipt.LootKey, projected.RareNotice!.LootKey);
        Assert.Equal("Recorded receipt name", projected.RareNotice.Name);
        Assert.Equal(1, alerts.Diagnostics.ReceivedDrops); Assert.Equal(0, alerts.Diagnostics.AudioStarts);
        Assert.Empty(alerts.CurrentNotices);
    }

    private static void AssertName(string html, LootTrackerLoot item)
    {
        var expected = ItemNamePresentation.ForDropKey(item.Id); Assert.True(expected.IsKnown);
        Assert.Contains("color:" + expected.CssColor, ItemSpan(html, item.Name));
    }
    private static void AssertNeutralName(string html, string name)
    {
        var span = ItemSpan(html, name); Assert.DoesNotContain("style=", span); Assert.DoesNotContain("data-item-grade=", span);
    }
    private static string ItemSpan(string html, string name)
    {
        var result = Regex.Match(html, "<span[^>]*data-item-name=\"true\"[^>]*>" + Regex.Escape(WebUtility.HtmlEncode(name)) + "</span>");
        Assert.True(result.Success, "The recorded name must render through ItemName: " + name); return result.Value;
    }
    private static LootTrackerSession Session() => new()
    {
        Id = "synthetic-item-name-colours", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At.AddMinutes(-1), EndedAt = At, Elapsed = TimeSpan.FromMinutes(1),
        ItemCount = 13, HarvestCount = 1, FailedHarvestCount = 1, KinaUnbound = 0, SoulCount = 0,
        Loot = Items.SetItem(1, Items[1] with { Quantity = 6 }),
        TimelineEvents =
        [
            new("drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(-50), TimeSpan.FromSeconds(10)) { Origin = LootTrackerActivityOrigin.Pve, Loot = Items },
            new("harvest", LootTrackerTimelineEventKind.HarvestSuccess, At.AddSeconds(-40), TimeSpan.FromSeconds(20))
                { Origin = LootTrackerActivityOrigin.Lifeskill, GatherableId = 7001, Loot = [Items[1]] },
            new("failure", LootTrackerTimelineEventKind.HarvestFailure, At.AddSeconds(-30), TimeSpan.FromSeconds(30))
                { Origin = LootTrackerActivityOrigin.Lifeskill, GatherableId = 7001 }
        ]
    };
    public sealed class CapturedColourTimeline : LootTrackerSessionTimeline
    {
        [Parameter] public Action<CapturedColourTimeline>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        public Task RefreshNow() => InvokeAsync(StateHasChanged);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        public Fixture(string language)
        {
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton<UiState>().BuildServiceProvider();
            _ = _provider.GetRequiredService<SettingsService>(); UiText.Language = language;
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => await Html(await RenderRoot<T>(parameters));
        public Task<HtmlRootComponent> RenderRoot<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        public Task<string> Html(HtmlRootComponent root) => _renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public Task InvokeTimeline(CapturedColourTimeline timeline, string field, object value) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(LootTrackerSessionTimeline).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, value);
            if (field == "_exactOpen")
            {
                var rows = typeof(LootTrackerSessionTimeline).GetMethod("ExactRows", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timeline, null);
                typeof(LootTrackerSessionTimeline).GetField("_exactRows", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, rows);
            }
            await timeline.RefreshNow();
        });
        public async ValueTask DisposeAsync()
        { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previousLanguage; }
    }
}
