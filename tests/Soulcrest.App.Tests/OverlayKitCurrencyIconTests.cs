using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
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
public sealed class OverlayKitCurrencyIconTests : IDisposable
{
    private readonly string _language = UiText.Language;
    private static readonly string[] Kina = ["market-kina", "market-kina-hour", "dropped-kina", "dropped-kina-hour", "daily-kina"];
    private static readonly string[] Ap = ["abyss-points", "abyss-points-hour"];

    public static IEnumerable<object[]> IconModes() => Kina.Concat(Ap).SelectMany(kind =>
        new[] { false, true }.SelectMany(label => new[] { false, true }.Select(icon => new object[] { kind, label, icon })));

    [Fact]
    public void CurrencyBindingsReuseTheSameBundledItemAssetsAsTheNativeLootSource()
    {
        var kina = Assert.IsType<string>(MonsterLootIconCatalog.Default.ItemAsset(930100031));
        var ap = Assert.IsType<string>(MonsterLootIconCatalog.Default.ItemAsset(930100003));
        Assert.NotEqual(kina, ap);
        Assert.All(Kina, kind => Assert.Equal(kina, OverlayCatalog.GameIconAsset(kind)));
        Assert.All(Ap, kind => Assert.Equal(ap, OverlayCatalog.GameIconAsset(kind)));
        Assert.Null(OverlayCatalog.GameIconAsset("rank-net"));
        Assert.Null(OverlayCatalog.GameIconAsset("clock"));
        Assert.Null(OverlayCatalog.GameIconAsset(null));
    }

    [Theory]
    [MemberData(nameof(IconModes))]
    public async Task CurrencyModulesShowExactlyOneOriginalIconInEitherLabelModeAndNoneWhenDisabled(string kind, bool label, bool icon)
    {
        var widget = OverlayCatalog.CreateWidget(kind) with { ShowLabel = label, ShowIcon = icon };
        var html = await WidgetHtml(widget);
        var asset = Assert.IsType<string>(OverlayCatalog.GameIconAsset(kind));
        Assert.Equal(icon ? 1 : 0, Regex.Matches(html, "<img[^>]+src=\"" + Regex.Escape(asset) + "\"").Count);
        var market = kind is "market-kina" or "market-kina-hour";
        Assert.Equal(icon && market ? 1 : 0, Regex.Matches(html, "<svg").Count);
        Assert.Equal(icon && market, html.Contains("overlay-market-glyph", StringComparison.Ordinal));
        if (icon && market)
        {
            Assert.True(html.IndexOf("overlay-market-glyph", StringComparison.Ordinal) < html.IndexOf(asset, StringComparison.Ordinal));
            if (kind == "market-kina-hour")
                Assert.True(html.IndexOf(asset, StringComparison.Ordinal) < html.IndexOf("overlay-hourly-suffix", StringComparison.Ordinal));
        }
        if (!icon) Assert.DoesNotContain("overlay-currency-icon", html);
        if (kind == "daily-kina")
        {
            Assert.Equal(icon, html.Contains("overlay-daily-icon", StringComparison.Ordinal));
            Assert.Contains("overlay-daily-number", html);
        }
        else if (icon)
        {
            var row = Regex.Match(html, label
                ? "<div class=\"overlay-widget-label\"[^>]*>.*?</div>"
                : "<strong class=\"overlay-widget-value\"[^>]*>.*?</strong>", RegexOptions.Singleline).Value;
            Assert.Contains(asset, row);
            Assert.Contains("class=\"icon overlay-currency-icon\"", row);
        }
    }

    public static IEnumerable<object[]> HourlyIconModes() => OverlayCatalog.Widgets
        .Where(module => module.Kind.EndsWith("-hour", StringComparison.Ordinal)).SelectMany(module =>
            new[] { false, true }.SelectMany(label => new[] { false, true }
                .Select(icon => new object[] { module.Kind, label, icon })));

    [Theory]
    [MemberData(nameof(HourlyIconModes))]
    public async Task EveryHourlyModuleAttachesOneHourSuffixToItsOwnIconAndHidesBothTogether(string kind, bool label, bool icon)
    {
        var html = await WidgetHtml(OverlayCatalog.CreateWidget(kind) with { ShowLabel = label, ShowIcon = icon });
        Assert.Equal(icon ? 1 : 0, Regex.Matches(html, "class=\"overlay-hourly-suffix\">/h</span>").Count);
        Assert.Equal(icon, html.Contains("class=\"overlay-hourly-icon\"", StringComparison.Ordinal));
        if (icon && OverlayCatalog.GameIconAsset(kind) is { } asset) Assert.Contains(asset, html);
        if (icon && kind.StartsWith("rank-", StringComparison.Ordinal)) Assert.Contains("<svg", html);
        if (!icon) Assert.DoesNotContain("overlay-currency-icon", html);
        Assert.Equal(6, OverlayCatalog.Widgets.Count(module => module.Kind.EndsWith("-hour", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("market-kina")]
    [InlineData("dropped-kina")]
    [InlineData("abyss-points")]
    [InlineData("rank-net")]
    [InlineData("rank-gained")]
    [InlineData("rank-lost")]
    [InlineData("daily-kina")]
    public async Task SessionTotalsAndDailyProgressDoNotReceiveAnHourlySuffix(string kind)
    {
        Assert.DoesNotContain("overlay-hourly-suffix", await WidgetHtml(OverlayCatalog.CreateWidget(kind)));
    }

    [Theory]
    [InlineData("dropped-kina", .5)]
    [InlineData("dropped-kina", 1)]
    [InlineData("dropped-kina", 2)]
    [InlineData("abyss-points-hour", .5)]
    [InlineData("abyss-points-hour", 1)]
    [InlineData("abyss-points-hour", 2)]
    public async Task ResizedCurrencyModulesKeepTheirReferenceGeometryAndOneUniformContentScale(string kind, double scale)
    {
        var original = OverlayCatalog.CreateWidget(kind) with { ShowLabel = false, FontScale = 2 };
        var resized = Soulcrest.App.OverlayEditorKit.OverlayLayout.ResizeWidget(original,
            original.Width * scale, original.Height * scale);
        var html = await WidgetHtml(resized);
        Assert.Contains("data-content-width=\"168\"", html);
        Assert.Contains("data-content-height=\"72\"", html);
        Assert.Contains("transform:scale(" + scale.ToString("0.######", CultureInfo.InvariantCulture) + ")", html);
        Assert.Contains("--widget-font-scale:2", html);
        Assert.DoesNotContain("scaleX", html);
        Assert.DoesNotContain("scaleY", html);
        Assert.Contains(OverlayCatalog.GameIconAsset(kind)!, html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RankSourceOptionsRemainAvailableInTheEditorAndAllActionsAreDisabledForExamples(bool examples)
    {
        var settings = new SettingsService();
        using var sounds = new AbyssRankSoundPlayer(() => new());
        UiText.Language = "de";
        var html = await Html<OverlayAbyssRankSourceSettings>(new()
        {
            [nameof(OverlayAbyssRankSourceSettings.Disabled)] = examples,
        }, services => { services.AddSingleton(settings); services.AddSingleton(sounds); });
        Assert.Contains("data-abyss-rank-source-settings=\"true\"", html);
        foreach (var id in new[] { "abyss-rank-reduced-motion", "abyss-rank-sound-enabled", "abyss-rank-sound-volume", "abyss-rank-sound-preview" })
        {
            var element = Regex.Match(html, "<(?:input|button)[^>]*id=\"" + id + "\"[^>]*>").Value;
            Assert.NotEmpty(element);
            if (examples) Assert.Contains("disabled", element);
        }
    }

    [Fact]
    public async Task PureWidgetRenderersDoNotRequireOrCreateRankSettingsServices()
    {
        var html = await Html<OverlayAbyssRankSourceSettings>(new());
        Assert.DoesNotContain("data-abyss-rank-source-settings", html);
    }

    private static Task<string> WidgetHtml(OverlayWidget widget) => Html<OverlayWidgetPreview>(new()
    {
        [nameof(OverlayWidgetPreview.Widget)] = widget,
        [nameof(OverlayWidgetPreview.Snapshot)] = OverlaySnapshot.DemoFor("en"),
    });

    private static async Task<string> Html<T>(Dictionary<string, object?> parameters, Action<ServiceCollection>? configure = null) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    public void Dispose() => UiText.Language = _language;
}
