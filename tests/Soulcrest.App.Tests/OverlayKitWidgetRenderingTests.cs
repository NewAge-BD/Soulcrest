using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayKitWidgetRenderingTests : IDisposable
{
    private readonly string _language = UiText.Language;
    public static IEnumerable<object[]> Kinds => OverlayCatalog.Widgets.Select(definition => new object[] { definition.Kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryModuleUsesTheOriginalContentViewportAndUniformFit(string kind)
    {
        UiText.Language = "en";
        var widget = OverlayCatalog.CreateWidget(kind);
        var html = await Render(widget, OverlaySnapshot.DemoFor("en"));
        Assert.Contains("class=\"overlay-widget-viewport\"", html);
        Assert.Contains("overlay-widget-" + kind, html);
        Assert.Contains("data-content-width=\"" + widget.Width, html);
        Assert.Contains("data-min-content-width=\"" + (kind == "genus-insight" ? "320" : kind is "abyss-killfeed" or "abyss-rank-progress" ? "240" : "80") + "\"", html);
        Assert.DoesNotContain("Black Desert", html);
        Assert.DoesNotContain("Grindcrest", html);
        var tiny = widget with { Width = 12, Height = 8, FontScale = 2 };
        var content = OverlayContentLayout.Create(tiny, OverlaySnapshot.DemoFor("en"));
        Assert.True(double.IsFinite(content.Scale) && content.Scale > 0);
        Assert.Equal(tiny.Width, content.LayoutWidget.Width * content.Scale, 8);
        Assert.Equal(tiny.Height, content.LayoutWidget.Height * content.Scale, 8);
    }

    [Theory]
    [InlineData("grid", 344, 168)]
    [InlineData("grid", 120, 64)]
    [InlineData("strip", 344, 112)]
    [InlineData("list", 344, 224)]
    [InlineData("card", 168, 104)]
    public void OriginalInventoryGeometryFitsAllChosenCells(string view, double width, double height)
    {
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { Width = width, Height = height, ItemView = view };
        var snapshot = Items();
        var layout = OverlayContentLayout.Create(widget, snapshot);
        var loot = OverlayLootPresentation.Create(layout.LayoutWidget, snapshot);
        var rows = (int)Math.Ceiling((double)loot.VisibleItems.Count / loot.Columns);
        var usedWidth = loot.Columns * loot.CellWidth + (loot.Columns - 1) * loot.Gap;
        var usedHeight = loot.HeaderHeight + loot.FooterHeight + rows * loot.CellHeight + Math.Max(0, rows - 1) * loot.Gap;
        Assert.True(usedWidth <= layout.LayoutWidget.Width - 20 + .00001);
        Assert.True(usedHeight <= layout.LayoutWidget.Height - 16 + .00001);
        Assert.Equal(view == "card" ? 1 : 3, loot.VisibleItems.Count);
        Assert.True(loot.ItemSize > 0 && double.IsFinite(loot.ItemSize));
    }

    [Fact]
    public void SelectionKeepsExactKeysAndZeroSlotsWhileAllItemsIgnoreTheOptionalLimit()
    {
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemLimit = 1 };
        var snapshot = Items();
        Assert.Equal(3, OverlayLootPresentation.Create(widget, snapshot).VisibleItems.Count);
        var selected = widget with { ItemFilter = "selected", ItemLimit = 2, ItemKeys = ["sample:c", "sample:a", "sample:b"] };
        var result = OverlayLootPresentation.Create(selected, snapshot with { Drops = snapshot.Drops.Take(2).ToArray() });
        Assert.Equal(new[] { "sample:c", "sample:a" }, result.VisibleItems.Select(item => item.Key));
        Assert.Equal(0, result.VisibleItems[0].Quantity);
        Assert.Equal("0", result.VisibleItems[0].QuantityText);
        Assert.Equal(1, result.HiddenCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SelectedCatalogSlotsDoNotInventZeroForAnUnconfirmedSession(bool active, bool tracking)
    {
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemFilter = "selected", ItemKeys = ["sample:c"] };
        var snapshot = Items() with { SessionActive = active, TrackingAvailable = tracking, Drops = [] };
        var result = OverlayLootPresentation.Create(widget, snapshot);
        Assert.Equal("—", Assert.Single(result.VisibleItems).QuantityText);
    }

    [Fact]
    public async Task UnknownDailyProgressDoesNotClaimZeroOrAConfirmedRatio()
    {
        var html = await Render(OverlayCatalog.CreateWidget("daily-kina"), new OverlaySnapshot
        {
            DailyProgress = new("—", "Not confirmed", "—", null),
        });
        Assert.Contains("role=\"progressbar\"", html);
        Assert.DoesNotContain("aria-valuenow", html);
        Assert.Contains("Not confirmed", html);
        Assert.Contains("is-unknown", html);
        Assert.DoesNotContain("overlay-daily-fill", html);
        Assert.DoesNotContain("overlay-daily-session", html);
        Assert.DoesNotContain(">0 %<", html);
    }

    [Fact]
    public async Task DailyProgressExposesANumericAriaValueUnderGermanCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var html = await Render(OverlayCatalog.CreateWidget("daily-kina"), new OverlaySnapshot
            {
                DailyProgress = new("684.720 / 1.000.000", "Bestätigt", "68 %", .68472),
            });
            var attribute = System.Text.RegularExpressions.Regex.Match(html, "aria-valuenow=\"([^\"]+)\"");
            Assert.True(attribute.Success);
            Assert.True(double.TryParse(attribute.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value));
            Assert.Equal(68.472, value, 6);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ClockUsesThePresentationSourcesFormattedLocalTime()
    {
        var now = new DateTimeOffset(2026, 11, 2, 16, 37, 0, TimeSpan.Zero);
        var html = await Render(OverlayCatalog.CreateWidget("clock"), new OverlaySnapshot
        {
            ClockUtcNow = now,
            Metrics = new Dictionary<string, OverlayMetric> { ["clock"] = new("Local time", "23:47") },
        });
        Assert.Contains("23:47", html);
        Assert.DoesNotContain(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), html);
        Assert.DoesNotContain("countdown", html);
        Assert.DoesNotContain("BDO", html);
        Assert.DoesNotContain("is-game", html);
    }

    [Fact]
    public async Task ClockLabelToggleHidesLocalCaptionAndKeepsTheTime()
    {
        UiText.Language = "de";
        var widget = OverlayCatalog.CreateWidget("clock") with { ShowLabel = false };
        var html = await Render(widget, new OverlaySnapshot
        {
            Metrics = new Dictionary<string, OverlayMetric> { ["clock"] = new("Lokale Uhrzeit", "23:47") },
        });
        Assert.Contains("23:47", html);
        Assert.DoesNotContain("Lokal", html);
        Assert.DoesNotContain("overlay-clock-row-label", html);
        Assert.Contains("<svg", html);
    }

    [Fact]
    public async Task NoticeRendersOnlyTheCurrentReceiptAndCanBeEmpty()
    {
        var widget = OverlayCatalog.CreateWidget("rare-notice");
        var snapshot = Items() with { RareNotice = new("sample", "Current sample receipt", 2, "2", null, LootItemRarity.Unique, 1) };
        var html = await Render(widget, snapshot);
        Assert.Contains("Current sample receipt ×2", html);
        Assert.DoesNotContain("Sample item A", html);
        var empty = await Render(widget, snapshot with { RareNotice = null });
        Assert.DoesNotContain("Current sample receipt", empty);
        Assert.Contains("overlay-widget-empty", empty);
    }

    [Theory]
    [InlineData(LootItemRarity.Unique, true, "#FBCA47")]
    [InlineData(LootItemRarity.Epic, false, "#C38EFB")]
    [InlineData(LootItemRarity.Mythic, false, "#FF88AA")]
    [InlineData(LootItemRarity.Special, true, "#75E2EC")]
    public async Task NoticeShowsItsGradeAndExistingPaletteEvenWithoutTheWidgetTitle(LootItemRarity rarity, bool showLabel, string color)
    {
        var widget = OverlayCatalog.CreateWidget("rare-notice") with { ShowLabel = showLabel };
        var snapshot = Items() with { RareNotice = new("sample", "Current sample receipt", 2, "2", null, rarity, 1) };
        var html = await Render(widget, snapshot);
        Assert.Contains("rarity-" + rarity.ToString().ToLowerInvariant(), html);
        Assert.Contains("data-rarity=\"" + rarity + "\"", html);
        Assert.Contains("--notice-rarity-color:" + color, html);
        Assert.Matches("class=\"overlay-rare-grade\"[^>]*>" + rarity + "</span>", html);
        if (!showLabel) Assert.DoesNotContain("class=\"overlay-widget-label\"", html);
    }

    [Theory]
    [InlineData("grid", LootItemRarity.Rare, true)]
    [InlineData("strip", LootItemRarity.Legend, true)]
    [InlineData("list", LootItemRarity.Unique, true)]
    [InlineData("card", LootItemRarity.Epic, true)]
    [InlineData("grid", LootItemRarity.Mythic, false)]
    [InlineData("list", LootItemRarity.Special, false)]
    public async Task DropViewsPaintTheExactGradeBehindContentAndHonorTheMotionSwitch(string view, LootItemRarity rarity, bool motion)
    {
        var item = new OverlayLootItem("sample:graded", "Confirmed grade", "17", Quantity: 17, Rarity: rarity);
        var snapshot = Items() with { Drops = [item], ItemAnimationsEnabled = motion };
        var widget = OverlayCatalog.CreateWidget("drop-grid") with { ItemView = view, ShowIcon = false };
        var html = await Render(widget, snapshot);
        Assert.Contains("overlay-widget-items is-" + view, html);
        Assert.Contains("overlay-rarity-background rarity-" + rarity.ToString().ToLowerInvariant(), html);
        var color = Soulcrest.App.Overlay.LootRarityVisuals.Accent(rarity);
        Assert.Contains($"--rarity-glow:{color.R},{color.G},{color.B}", html);
        Assert.Contains("aria-hidden=\"true\"", html);
        Assert.Equal(!motion, html.Contains("is-motion-disabled", StringComparison.Ordinal));
        Assert.Contains("Confirmed grade", html);
        Assert.Matches("class=\"overlay-item-quantity\"[^>]*>17</strong>", html);
    }

    [Theory]
    [InlineData(LootItemRarity.Unknown)]
    [InlineData(LootItemRarity.Common)]
    public async Task CommonAndUnclassifiedDropsDoNotReceiveInventedRarityEffects(LootItemRarity rarity)
    {
        var snapshot = Items() with
        {
            Drops = [new("sample:unlit", "Plain item", "1", IsRare: true, Quantity: 1, Rarity: rarity)],
        };
        var html = await Render(OverlayCatalog.CreateWidget("drop-grid"), snapshot);
        Assert.DoesNotContain("overlay-rarity-background", html);
        Assert.Contains("Plain item", html);
    }

    [Fact]
    public async Task NoticeAndDropCopiesShareTheRarityClockWithoutChangingTheirReceipt()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, 321, TimeSpan.Zero);
        var snapshot = Items() with
        {
            ClockUtcNow = now,
            Drops = [new("sample:graded", "Same drop", "2", Quantity: 2, Rarity: LootItemRarity.Unique)],
            RareNotice = new("receipt", "Same drop", 2, "2", null, LootItemRarity.Unique, 3.75),
            ItemAnimationsEnabled = false,
        };
        var drop = await Render(OverlayCatalog.CreateWidget("drop-grid"), snapshot);
        var notice = await Render(OverlayCatalog.CreateWidget("rare-notice"), snapshot);
        var phase = FormattableString.Invariant($"--rarity-phase:-{now.ToUnixTimeMilliseconds() / 1000d:0.###}s");
        Assert.Contains(phase, drop);
        Assert.Contains(phase, notice);
        Assert.Contains("is-motion-disabled", notice);
        Assert.Equal(3.75, snapshot.RareNotice.ElapsedSeconds);
        var expired = await Render(OverlayCatalog.CreateWidget("rare-notice"), snapshot with { RareNotice = null });
        Assert.DoesNotContain("overlay-rarity-background", expired);
    }

    [Theory]
    [InlineData("de", "684.720 / 1.000.000")]
    [InlineData("en", "684,720 / 1,000,000")]
    public async Task DailyKinaUsesTheLootBarWithOneIntegratedValueAndItsSessionSegment(string language, string value)
    {
        UiText.Language = language;
        var snapshot = OverlaySnapshot.DemoFor(language);
        var html = await Render(OverlayCatalog.CreateWidget("daily-kina"), snapshot);
        Assert.Contains("overlay-daily-number", html);
        Assert.Contains("overlay-daily-session", html);
        Assert.Contains("overlay-daily-icon", html);
        Assert.Contains(MonsterLootIconCatalog.Default.ItemAsset(930100031)!, html);
        Assert.Contains(value, html);
        Assert.DoesNotContain("class=\"overlay-widget-value\"", html);
        Assert.DoesNotContain(">" + snapshot.DailyProgress.Percentage + "<", html);
        Assert.DoesNotContain("is-complete", html);
        Assert.DoesNotContain("is-unknown", html);
    }

    [Fact]
    public async Task DailyKinaLabelAndIconOptionsKeepTheValueButHideCaptionsAndGraphic()
    {
        var snapshot = OverlaySnapshot.DemoFor("en");
        var widget = OverlayCatalog.CreateWidget("daily-kina") with { ShowLabel = false, ShowIcon = false };
        var html = await Render(widget, snapshot);
        Assert.Contains(snapshot.DailyProgress.Value, html);
        Assert.DoesNotContain("overlay-daily-icon", html);
        Assert.DoesNotContain("overlay-widget-label", html);
        Assert.DoesNotContain("overlay-widget-detail", html);
        Assert.Contains("overlay-daily-session", html);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task DailyKinaKnownZeroAndReachedLimitUseTheirOwnBarStates(double fraction, bool complete)
    {
        var html = await Render(OverlayCatalog.CreateWidget("daily-kina"), new OverlaySnapshot
        {
            DailyProgress = new(complete ? "1,000,000 / 1,000,000" : "0 / 1,000,000", "Confirmed", complete ? "100%" : "0%", fraction),
            ItemAnimationsEnabled = false,
        });
        Assert.Equal(complete, html.Contains("is-complete", StringComparison.Ordinal));
        Assert.DoesNotContain("is-unknown", html);
        Assert.Contains("is-motion-disabled", html);
        Assert.Contains("overlay-daily-fill", html);
        Assert.DoesNotContain("overlay-daily-session", html);
        Assert.Contains("aria-valuenow=\"" + (complete ? "100" : "0") + "\"", html);
    }

    [Theory]
    [InlineData("list", 2)]
    [InlineData("grid", 3)]
    [InlineData("strip", 3)]
    [InlineData("card", 3)]
    public void OnlyTheListCombinesEquipmentAndReordersByTheCombinedQuantity(string view, int count)
    {
        var drops = new[]
        {
            new OverlayLootItem("Item:610530001", "Material", "4", Quantity: 4, Rarity: LootItemRarity.Common),
            new OverlayLootItem("Item:110160001", "First gear", "2", Quantity: 2, Rarity: LootItemRarity.Common),
            new OverlayLootItem("Item:110160008", "Second gear", "3", Quantity: 3, Rarity: LootItemRarity.Common),
        };
        var snapshot = new OverlaySnapshot { Drops = drops, UiLanguage = "en", SessionActive = true, TrackingAvailable = true };
        var widget = OverlayCatalog.CreateWidget("drop-list") with { ItemView = view, Width = 344, Height = 300 };
        var result = OverlayLootPresentation.Create(widget, snapshot);
        Assert.Equal(count, result.Items.Count);
        Assert.Equal(9, result.Items.Sum(item => item.Quantity));
        Assert.Equal(3, snapshot.Drops.Count);
        if (view != "list") { Assert.DoesNotContain(result.Items, item => item.IsEquipmentGroup); return; }
        Assert.True(result.Items[0].IsEquipmentGroup);
        Assert.Equal(5, result.Items[0].Quantity);
        var selected = OverlayLootPresentation.Create(widget with
        {
            ItemFilter = "selected", ItemKeys = ["Item:110160001", "Item:610530001"],
        }, snapshot);
        Assert.Equal(2, selected.Items.Count);
        Assert.Equal(2, selected.Items[0].Quantity);
        Assert.Equal("Item:610530001", selected.Items[1].Key);
    }

    [Theory]
    [InlineData("de", "Epische Ausrüstung", "Seltene Ausrüstung", "Gewöhnliche Ausrüstung")]
    [InlineData("en", "Epic Gear", "Rare Gear", "Common Gear")]
    public async Task EquipmentListRendersTheThreeInGameGroupsWithTheirOwnColors(string language, string epic, string rare, string common)
    {
        UiText.Language = language;
        var snapshot = new OverlaySnapshot { UiLanguage = language, Drops =
        [
            new("Item:110140002", "Exact blue gear A", "1", Quantity: 1, Rarity: LootItemRarity.Legend),
            new("Item:110140003", "Exact blue gear B", "2", Quantity: 2, Rarity: LootItemRarity.Legend),
            new("Item:110150003", "Exact green gear A", "4", Quantity: 4, Rarity: LootItemRarity.Rare),
            new("Item:110150004", "Exact green gear B", "6", Quantity: 6, Rarity: LootItemRarity.Rare),
            new("Item:110160001", "Exact white gear A", "2", Quantity: 2, Rarity: LootItemRarity.Common),
            new("Item:110160008", "Exact white gear B", "3", Quantity: 3, Rarity: LootItemRarity.Common),
        ] };
        var widget = OverlayCatalog.CreateWidget("drop-list") with { Height = 300 };
        var html = await Render(widget, snapshot);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, " is-equipment-group").Count);
        Assert.Contains(epic, html); Assert.Contains(rare, html); Assert.Contains(common, html);
        Assert.Contains("--loot-group-color:#64B7FA", html);
        Assert.Contains("--loot-group-color:#66DC68", html);
        Assert.Contains("--loot-group-color:#FFFFFF", html);
        Assert.DoesNotContain("Exact blue gear", html);
        Assert.DoesNotContain("Legend Gear", html);
        var withoutIcons = await Render(widget with { ShowIcon = false, ShowLabel = false }, snapshot);
        Assert.DoesNotContain("overlay-item-icon", withoutIcons);
        Assert.DoesNotContain("overlay-widget-label", withoutIcons);
        Assert.Contains(epic, withoutIcons);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(withoutIcons, " is-equipment-group").Count);
    }

    private static OverlaySnapshot Items()
    {
        var items = new[]
        {
            new OverlayLootItem("sample:a", "Sample item A", "5", Quantity: 5),
            new OverlayLootItem("sample:b", "Sample item B", "3", IsRare: true, Quantity: 3),
            new OverlayLootItem("sample:c", "Sample item C", "1", Quantity: 1),
        };
        return new() { Drops = items, ItemCatalog = items, SessionActive = true, TrackingAvailable = true };
    }
    private static async Task<string> Render(OverlayWidget widget, OverlaySnapshot snapshot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<OverlayWidgetPreview>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OverlayWidgetPreview.Widget)] = widget,
                [nameof(OverlayWidgetPreview.Snapshot)] = snapshot,
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }
    public void Dispose() => UiText.Language = _language;
}
