using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayAbyssWidgetRenderingTests : IDisposable
{
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task KillFeedShowsResultsAndOpponentRanksWithoutPointRewards(string language)
    {
        UiText.Language = language;
        var sample = Frame(language);
        var widget = OverlayCatalog.CreateWidget("abyss-killfeed") with { KillFeedRows = 3 };
        var html = await Render(widget, sample);
        Assert.Contains("Opponent Alpha", html);
        Assert.Contains("Opponent Beta", html);
        Assert.Contains("Opponent Gamma", html);
        Assert.Contains("data-killfeed-kind=\"Assist\"", html);
        Assert.Contains(language == "de" ? "geschätzt" : "estimated", html);
        Assert.DoesNotContain("923456789", html);
        Assert.DoesNotContain("734567890", html);
        Assert.DoesNotContain("923,456,789", html);
        Assert.DoesNotContain("734,567,890", html);
        Assert.DoesNotContain("title=", html);
        Assert.DoesNotContain("AP", html);
        Assert.DoesNotContain("RP", html);
    }

    [Fact]
    public async Task KillFeedRetainsSourceFiltersRowLimitAndIconSwitch()
    {
        var sample = Frame("en");
        var widget = OverlayCatalog.CreateWidget("abyss-killfeed") with { KillFeedRows = 1, ShowIcon = false };
        var html = await Render(widget, sample);
        Assert.Contains("Opponent Alpha", html);
        Assert.DoesNotContain("Opponent Beta", html);
        Assert.DoesNotContain("<img", html);
        html = await Render(widget with { KillFeedRows = 10 }, sample with { Abyss = sample.Abyss with { ShowKills = false, ShowDeaths = false } });
        Assert.Contains("Opponent Gamma", html);
        Assert.DoesNotContain("Opponent Alpha", html);
        Assert.DoesNotContain("Opponent Beta", html);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(19)]
    public async Task OpponentLightingUsesObservedGradeInsteadOfOwnRankPointTier(int grade)
    {
        var sample = Frame("en");
        var entry = sample.Abyss.KillFeed[0] with { OpponentAbyssRankGrade = grade };
        sample = sample with { Abyss = sample.Abyss with { RankPoints = 0, KillFeed = [entry] } };
        var html = await Render(OverlayCatalog.CreateWidget("abyss-killfeed"), sample);
        var treatment = AbyssKillFeedRankVisuals.ForGrade(grade)!.Value;
        Assert.Contains($"--rank-glow:{treatment.Accent.R},{treatment.Accent.G},{treatment.Accent.B}", html);
        var strongerOwn = await Render(OverlayCatalog.CreateWidget("abyss-killfeed"), sample with { Abyss = sample.Abyss with { RankPoints = 9_000_000 } });
        Assert.Equal(html, strongerOwn);
        var stopped = await Render(OverlayCatalog.CreateWidget("abyss-killfeed"), sample with { ItemAnimationsEnabled = false });
        Assert.Contains("is-motion-disabled", stopped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(20)]
    public async Task UnknownOpponentGradesHaveNoInventedLighting(int? grade)
    {
        var sample = Frame("en");
        sample = sample with { Abyss = sample.Abyss with { KillFeed = [sample.Abyss.KillFeed[0] with { OpponentAbyssRankGrade = grade }] } };
        var html = await Render(OverlayCatalog.CreateWidget("abyss-killfeed"), sample);
        Assert.Contains("Opponent Alpha", html);
        Assert.DoesNotContain("overlay-abyss-rank-background", html);
    }

    [Theory]
    [InlineData("abyss-killfeed")]
    [InlineData("abyss-rank-progress")]
    public async Task CachedAbyssDataCannotBypassTheWorldContextGate(string kind)
    {
        var sample = Frame("en");
        var html = await Render(OverlayCatalog.CreateWidget(kind), sample with { Abyss = sample.Abyss with { WorldContextVisible = false } });
        Assert.DoesNotContain("Opponent Alpha", html);
        Assert.DoesNotContain("overlay-abyss-rank-fill", html);
        Assert.DoesNotContain("overlay-abyss-stage-names", html);
    }

    [Theory]
    [InlineData("de", "en")]
    [InlineData("en", "de")]
    public async Task RankProgressKeepsGameNameLanguageSeparateFromUiAndOptionalCaptions(string ui, string names)
    {
        UiText.Language = ui;
        var sample = Frame(ui) with { NameLanguage = names };
        var progress = AbyssRankPresentation.Project(sample.Abyss.RankPoints);
        var html = await Render(OverlayCatalog.CreateWidget("abyss-rank-progress"), sample);
        Assert.Contains(progress.Current!.Name(names), html);
        Assert.Contains(progress.Current.IconAsset, html);
        Assert.Contains("role=\"progressbar\"", html);
        Assert.Contains("aria-valuenow=", html);
        var compact = await Render(OverlayCatalog.CreateWidget("abyss-rank-progress") with { ShowLabel = false, ShowIcon = false }, sample);
        Assert.DoesNotContain("overlay-abyss-stage-names", compact);
        Assert.DoesNotContain("overlay-abyss-remaining", compact);
        Assert.DoesNotContain("<img", compact);
        Assert.Contains("overlay-abyss-rank-number", compact);
    }

    [Fact]
    public async Task RankProgressDistinguishesUnknownCachedAndMaximumTotals()
    {
        var sample = Frame("en");
        var widget = OverlayCatalog.CreateWidget("abyss-rank-progress");
        var unknown = await Render(widget, sample with { Abyss = sample.Abyss with { RankPoints = null } });
        Assert.Contains("is-unknown", unknown);
        Assert.DoesNotContain("aria-valuenow=", unknown);
        Assert.DoesNotContain("overlay-abyss-rank-fill", unknown);
        var cached = await Render(widget, sample with { Abyss = sample.Abyss with { Confirmed = false } });
        Assert.Contains("is-cached", cached);
        Assert.Contains("overlay-abyss-progress-status", cached);
        var max = AbyssRankCatalog.Default.Stages.Max(stage => stage.Threshold);
        var maximum = await Render(widget, sample with { Abyss = sample.Abyss with { RankPoints = max } });
        Assert.Contains("is-maximum", maximum);
        Assert.Contains("aria-valuenow=\"100\"", maximum);
    }

    private static OverlaySnapshot Frame(string language)
    {
        var sample = OverlaySnapshot.DemoFor(language);
        var now = sample.ClockUtcNow;
        return sample with { Abyss = sample.Abyss with
        {
            ShowKills = true, ShowDeaths = true, ShowAssists = true,
            KillFeed = [
                new(now, AbyssKillFeedKind.Kill, "Opponent Alpha", 734567890) { Sequence = 3, OpponentAbyssRankGrade = 1, AbyssPointGain = 923456789 },
                new(now.AddSeconds(-1), AbyssKillFeedKind.Death, "Opponent Beta") { Sequence = 2, OpponentAbyssRankGrade = 19 },
                new(now.AddSeconds(-2), AbyssKillFeedKind.Assist, "Opponent Gamma", 734567890) { Sequence = 1, IsEstimated = true, AbyssPointGain = 923456789 },
            ],
        }};
    }

    private static async Task<string> Render(OverlayWidget widget, OverlaySnapshot snapshot)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<OverlayWidgetPreview>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OverlayWidgetPreview.Widget)] = widget, [nameof(OverlayWidgetPreview.Snapshot)] = snapshot,
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    public void Dispose() => UiText.Language = _language;
}
