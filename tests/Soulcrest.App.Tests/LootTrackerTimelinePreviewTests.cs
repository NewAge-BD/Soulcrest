using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in production-Razor exports. Only offline map images are read from the real cache.</summary>
[Collection("Settings file")]
public sealed class LootTrackerTimelinePreviewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionTimelineTypesAtMiddleAndEndOnlyWhenRequested(string language)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_LOOT_UI_PREVIEW") is not { Length: > 0 } output) return;
        var previousLanguage = UiText.Language;
        var settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var isolated = Path.Combine(Path.GetTempPath(), "soulcrest-timeline-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            var realUserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
            Assert.NotEqual(Path.GetFullPath(realUserData).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
            var mapDirectory = AppPaths.FindMapData();
            Assert.NotNull(mapDirectory);
            Assert.Contains("altgard", LootTrackerRouteMapPreview.Load(mapDirectory).Keys);
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = language, NameLanguage = language });
            var progress = new ProgressService(Path.Combine(isolated, "progress.json"), new PetCatalog([]));
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton(progress)
                .AddSingleton(new AlertsService(Path.Combine(isolated, "alerts.json"), runTimer: false))
                .AddSingleton<AlertSoundPlayer>()
                .AddSingleton(new RareDropAlertService(() => new(),
                    () => throw new InvalidOperationException("Rendering cannot play audio."), TimeProvider.System))
                .BuildServiceProvider();
            UiText.Language = language;
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var cssDirectory = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css");
            var css = await File.ReadAllTextAsync(Path.Combine(cssDirectory, "app.css")) + "\n" +
                await File.ReadAllTextAsync(Path.Combine(cssDirectory, "loot-tracker.css")) + "\n" +
                await File.ReadAllTextAsync(Path.Combine(cssDirectory, "loot-session-timeline.css"));
            Directory.CreateDirectory(output);
            foreach (var (name, session) in Examples(language))
            {
                CaptureLootTrackerTimelinePreview? captured = null;
                var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CaptureLootTrackerTimelinePreview>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session], session.EndedAt),
                        [nameof(LootTrackerView.IsExample)] = true,
                        [nameof(LootTrackerView.ViewMode)] = name switch { "abyss" => LootTrackerViewMode.Abyss, "lifeskill" => LootTrackerViewMode.Lifeskill, "pve" => LootTrackerViewMode.Pve, _ => LootTrackerViewMode.Mixed },
                        [nameof(CaptureLootTrackerTimelinePreview.Captured)] = (Action<CaptureLootTrackerTimelinePreview>)(view => captured = view)
                    })));
                Assert.NotNull(captured);
                await renderer.Dispatcher.InvokeAsync(() => captured.SeekAndRender(TimeSpan.FromMinutes(10)));
                var middle = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
                VerifyLayoutAndPrefix(middle, session.Kind, atEnd: false);
                await Export(middle, "middle");
                await renderer.Dispatcher.InvokeAsync(captured.FollowAndRender);
                var end = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
                VerifyLayoutAndPrefix(end, session.Kind, atEnd: true);
                Assert.Equal(StatisticsSection(middle), StatisticsSection(end));
                await Export(end, "end");

                async Task Export(string markup, string position)
                {
                    var embedded = EmbedVisibleImages(markup, mapDirectory);
                    Assert.DoesNotContain("https://mapdata.soulcrest/", embedded);
                    Assert.Matches("href=\"data:image/(?:webp|png);base64,", embedded);
                    Assert.DoesNotContain("src=\"class-icons/", embedded);
                    Assert.DoesNotContain("src=\"abyss-rank-icons/", embedded);
                    var document = "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">" +
                        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css +
                        "</style></head><body>" + embedded + "</body></html>";
                    await File.WriteAllTextAsync(Path.Combine(output, $"loot-timeline-{name}-{position}-{language}.html"), document);
                }
            }
        }
        finally
        {
            if (settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, settingsBackup);
            UiText.Language = previousLanguage;
            Directory.Delete(isolated, recursive: true);
        }
    }

    private static void VerifyLayoutAndPrefix(string html, LootTrackerSessionKind kind, bool atEnd)
    {
        var map = html.IndexOf("data-testid=\"session-replay-map\"", StringComparison.Ordinal);
        var timeline = html.IndexOf("data-testid=\"session-timeline\"", StringComparison.Ordinal);
        var statistics = html.IndexOf("class=\"lt-panel lt-session-statistics\"", StringComparison.Ordinal);
        Assert.True(map >= 0 && timeline > map && statistics > timeline);
        var mapMarkup = html[map..timeline];
        Assert.Contains("data-map-background=\"offline\"", mapMarkup);
        Assert.Contains("data-testid=\"replay-player\"", mapMarkup);
        var paths = string.Join(" ", Regex.Matches(mapMarkup, "<polyline class=\"lt-route-path\" points=\"([^\"]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)));
        var abyss = kind == LootTrackerSessionKind.Abyss;
        Assert.Contains(abyss ? "2175,1892.5" : "4350,3785", paths);
        if (atEnd) Assert.Contains(abyss ? "2275,1885" : "4550,3770", paths);
        else { Assert.DoesNotContain(abyss ? "2220,1925" : "4440,3850", paths); Assert.DoesNotContain(abyss ? "2275,1885" : "4550,3770", paths); }
        Assert.Contains("data-testid=\"playhead-time\">" + (atEnd ? "20:00" : "10:00"), html);
        Assert.DoesNotContain("data-testid=\"timeline-legacy\"", html);
        if (kind == LootTrackerSessionKind.Abyss)
        {
            Assert.Contains("data-timeline-lane=\"rank\"", html);
            Assert.Contains("data-combat-category=\"Sorcerer\"", html); // a future opponent still belongs to whole-session statistics
            if (atEnd) Assert.Contains("data-marker-kind=\"death\"", mapMarkup);
            else Assert.DoesNotContain("data-marker-kind=\"death\"", mapMarkup);
        }
    }

    private static string StatisticsSection(string html)
    {
        var start = html.IndexOf("class=\"lt-panel lt-session-statistics\"", StringComparison.Ordinal);
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string EmbedVisibleImages(string markup, string mapDirectory)
    {
        var wwwroot = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot");
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        return Regex.Replace(markup, "\\b(src|href)=\"([^\"]+)\"", match =>
        {
            var reference = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (!cache.TryGetValue(reference, out var data))
            {
                string path;
                const string host = "https://mapdata.soulcrest/";
                if (reference.StartsWith(host, StringComparison.Ordinal))
                {
                    var relative = reference[host.Length..];
                    Assert.True(LootTrackerRouteMapPreview.TileExists(mapDirectory, relative), "A visible tile must resolve within the active offline package.");
                    path = Path.Combine(mapDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                }
                else if (Regex.IsMatch(reference, "\\A(?:loot-icons|class-icons|abyss-rank-icons)/[A-Za-z0-9_-]+\\.png\\z"))
                    path = Path.Combine(wwwroot, reference.Replace('/', Path.DirectorySeparatorChar));
                else return match.Value;
                var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".webp" => "image/webp", ".png" => "image/png", _ => throw new InvalidDataException("Unexpected preview image format.") };
                data = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                cache.Add(reference, data);
            }
            return match.Groups[1].Value + "=\"" + data + "\"";
        });
    }

    private static IEnumerable<(string Name, LootTrackerSession Session)> Examples(string language)
    {
        var material = new LootTrackerLoot("Item:610530001", MonsterLootItemCatalog.Default.Name(610530001, language), LootTrackerLootKind.Item, 8);
        var unique = new LootTrackerLoot("Item:210230006", MonsterLootItemCatalog.Default.Name(210230006, language), LootTrackerLootKind.Item, 1);
        var soul = new LootTrackerLoot("SoulCrystal:2", language == "de" ? "Beispiel-Soul-Crystal" : "Sample Soul Crystal", LootTrackerLootKind.Soul, 3);
        var route = new LootTrackerRouteSegment("altgard", "Altgard",
        [
            Point(0, 4210, 3695), Point(2, 4240, 3710), Point(4, 4270, 3730), Point(6, 4290, 3750),
            Point(8, 4310, 3770), Point(10, 4350, 3785), Point(14, 4440, 3850), Point(18, 4510, 3825), Point(20, 4550, 3770)
        ]);
        var common = new LootTrackerSession
        {
            Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddMinutes(20),
            LastLootAt = At.AddMinutes(18), Elapsed = TimeSpan.FromMinutes(20), Location = "Altgard",
            Character = new("11111111111111111111111111111111", language == "de" ? "Beispielcharakter" : "Example character", 1840, 235),
            TimelineVersion = 1, RouteSegments = [route], KinaBound = null, KinaUnbound = 125_000,
            ItemCount = 9, SoulCount = 3, Loot = [material, unique, soul]
        };
        yield return ("abyss", common with
        {
            Id = "preview-timeline-abyss", Kind = LootTrackerSessionKind.Abyss, LastPresenceAt = At.AddMinutes(20),
            AbyssPoints = 5900, RankPointsGained = 3800, RankPointsLost = 1400,
            Location = "Abyss", RouteSegments = [new("abyss-reshanta-a", "Abyss", route.Points.Select(p => p with { X = p.X / 2, Y = p.Y / 2 }).ToImmutableArray())],
            CombatEvents =
            [
                Combat("kill1", LootTrackerCombatKind.Kill, 4, 4270, 3730, "Cleric", 19),
                Combat("kill2", LootTrackerCombatKind.Kill, 8, 4310, 3770, "Cleric", 18),
                Combat("death1", LootTrackerCombatKind.Death, 14, 4440, 3850, "Sorcerer", 13),
                Combat("kill3", LootTrackerCombatKind.Kill, 16, 4480, 3840, "Templar", 17),
                Combat("death2", LootTrackerCombatKind.Death, 19, 4530, 3790, null, null)
            ],
            TimelineEvents =
            [
                Ready("ap", LootTrackerTimelineMetric.AbyssPoints), Ready("rp", LootTrackerTimelineMetric.RankPoints),
                Ready("kina-abyss", LootTrackerTimelineMetric.KinaUnbound),
                Fact("kina-abyss1", LootTrackerTimelineEventKind.KinaUnbound, 3, 20_000),
                Fact("kina-abyss2", LootTrackerTimelineEventKind.KinaUnbound, 7, 40_000),
                Fact("kina-abyss3", LootTrackerTimelineEventKind.KinaUnbound, 15, 65_000),
                Fact("ap1", LootTrackerTimelineEventKind.AbyssPoints, 1, 1200), Fact("rp-loss1", LootTrackerTimelineEventKind.RankPoints, 2, -600),
                Fact("drop1", LootTrackerTimelineEventKind.Drop, 3) with { Loot = [material], Position = DropPosition("abyss-reshanta-a", "Abyss", 3, 2128, 1858) },
                Fact("ap2", LootTrackerTimelineEventKind.AbyssPoints, 4, 800), Fact("rp-gain", LootTrackerTimelineEventKind.RankPoints, 6, 3800),
                Fact("rank-up", LootTrackerTimelineEventKind.PointsStageReached, 6) with { FromStageId = "rank-19", ToStageId = "rank-18" },
                Fact("ap3", LootTrackerTimelineEventKind.AbyssPoints, 8, 1600), Fact("ap4", LootTrackerTimelineEventKind.AbyssPoints, 12, 900),
                Fact("drop2", LootTrackerTimelineEventKind.Drop, 13) with { Loot = [unique], Position = DropPosition("abyss-reshanta-a", "Abyss", 13, 2208, 1918) },
                Fact("ap5", LootTrackerTimelineEventKind.AbyssPoints, 16, 1400), Fact("rp-loss2", LootTrackerTimelineEventKind.RankPoints, 17, -800),
                Fact("drop3", LootTrackerTimelineEventKind.Drop, 18) with { Loot = [soul], Position = DropPosition("abyss-reshanta-a", "Abyss", 18, 2255, 1912) }
            ]
        });
        var visits = ImmutableArray.Create(
            Visit("gather1", 2, 4240, 3710, true, "Sapphire", 11), Visit("gather2", 6, 4290, 3750, true, "Orichalcum", 12),
            Visit("gather3", 8, 4310, 3770, false, "Sapphire", 11), Visit("gather4", 14, 4440, 3850, true, "Sapphire", 11),
            Visit("gather5", 18, 4510, 3825, false, "Orichalcum", 12));
        var reward2 = material with { Quantity = 2 }; var reward3 = material with { Quantity = 3 }; var reward4 = material with { Quantity = 4 };
        var reward1 = material with { Quantity = 1 };
        yield return ("lifeskill", common with
        {
            Id = "preview-timeline-lifeskill", Kind = LootTrackerSessionKind.Lifeskill, KinaUnbound = null, HarvestCount = 4,
            FailedHarvestCount = 2, ItemCount = 10, SoulCount = 0, LastLootAt = At.AddMinutes(19), Loot = [material with { Quantity = 10 }],
            FarmRouteVersion = 1, GatherVisits = visits, FarmRoutePoints = LifeskillFarmRouteBuilder.Build(visits),
            TimelineEvents =
            [
                Fact("g1", LootTrackerTimelineEventKind.HarvestSuccess, 2) with { GatherVisit = visits[0], GatherableId = 11, Loot = [reward2] },
                Fact("g2", LootTrackerTimelineEventKind.HarvestSuccess, 6) with { GatherVisit = visits[1], GatherableId = 12, Loot = [reward3] },
                Fact("g3", LootTrackerTimelineEventKind.HarvestFailure, 8) with { GatherVisit = visits[2], GatherableId = 11 },
                Fact("g4", LootTrackerTimelineEventKind.HarvestSuccess, 14) with { GatherVisit = visits[3], GatherableId = 11, Loot = [reward4] },
                Fact("g5", LootTrackerTimelineEventKind.HarvestFailure, 18) with { GatherVisit = visits[4], GatherableId = 12 },
                Fact("g6-unplaced", LootTrackerTimelineEventKind.HarvestSuccess, 19) with { GatherableId = 13, Loot = [reward1] }
            ]
        });
        yield return ("mixed", common with
        {
            Id = "preview-timeline-mixed", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1,
            HarvestCount = 4, FailedHarvestCount = 2, GatherVisits = visits, FarmRouteVersion = 1,
            FarmRoutePoints = LifeskillFarmRouteBuilder.Build(visits),
            TimelineEvents =
            [
                Ready("mixed-kina", LootTrackerTimelineMetric.KinaUnbound) with { Origin = LootTrackerActivityOrigin.Pve },
                Fact("mixed-money", LootTrackerTimelineEventKind.KinaUnbound, 1, 125_000) with { Origin = LootTrackerActivityOrigin.Pve },
                Fact("mixed-drop", LootTrackerTimelineEventKind.Drop, 3) with { Origin = LootTrackerActivityOrigin.Pve, Loot = [unique, soul] },
                Fact("mixed-success1", LootTrackerTimelineEventKind.HarvestSuccess, 6) with { Origin = LootTrackerActivityOrigin.Lifeskill, GatherVisit = visits[1], GatherableId = 12, Loot = [reward3] },
                Fact("mixed-failure", LootTrackerTimelineEventKind.HarvestFailure, 8) with { Origin = LootTrackerActivityOrigin.Lifeskill, GatherVisit = visits[2], GatherableId = 11 },
                Fact("mixed-success2", LootTrackerTimelineEventKind.HarvestSuccess, 14) with { Origin = LootTrackerActivityOrigin.Lifeskill, GatherVisit = visits[3], GatherableId = 11, Loot = [reward4] }
            ]
        });
        yield return ("pve", common with
        {
            Id = "preview-timeline-pve", Kind = LootTrackerSessionKind.Grind,
            MonsterCount = 2, Mobs = [new("sample-mob-a", "Sample monster A", 1), new("sample-mob-b", "Sample monster B", 1)],
            TimelineEvents =
            [
                Ready("kina", LootTrackerTimelineMetric.KinaUnbound), Fact("kina1", LootTrackerTimelineEventKind.KinaUnbound, 1, 20_000),
                Fact("pve-drop1", LootTrackerTimelineEventKind.Drop, 2) with { Loot = [material], Position = DropPosition("altgard", "Altgard", 2, 4240, 3710) },
                Fact("kina2", LootTrackerTimelineEventKind.KinaUnbound, 6, 45_000), Fact("kina3", LootTrackerTimelineEventKind.KinaUnbound, 12, 32_000),
                Fact("pve-drop2", LootTrackerTimelineEventKind.Drop, 15) with { Loot = [soul], Position = DropPosition("altgard", "Altgard", 15, 4458, 3843) },
                Fact("kina4", LootTrackerTimelineEventKind.KinaUnbound, 18, 28_000),
                Fact("pve-drop3", LootTrackerTimelineEventKind.Drop, 18) with { Loot = [unique], Position = DropPosition("altgard", "Altgard", 18, 4510, 3825) }
            ]
        });
    }

    private static LootTrackerRoutePoint Point(int minute, double x, double y) => new(x, y, At.AddMinutes(minute), TimeSpan.FromMinutes(minute));
    private static LootTrackerCombatPosition DropPosition(string mapId, string mapName, int minute, double x, double y) =>
        new(mapId, mapName, x, y, At.AddMinutes(minute));
    private static LootTrackerTimelineEvent Fact(string id, LootTrackerTimelineEventKind kind, int minute, long? amount = null) =>
        new(id, kind, At.AddMinutes(minute), TimeSpan.FromMinutes(minute)) { Amount = amount };
    private static LootTrackerTimelineEvent Ready(string id, LootTrackerTimelineMetric metric) =>
        Fact(id, LootTrackerTimelineEventKind.Availability, 0) with { Metric = metric, Available = true };
    private static LootTrackerGatherVisit Visit(string id, int minute, double x, double y, bool success, string name, int resource) =>
        new(id, 0, "altgard", "Altgard", x, y, x, y, At.AddMinutes(minute), success, name, GatherableId: resource);
    private static LootTrackerCombatEvent Combat(string id, LootTrackerCombatKind kind, int minute, double x, double y, string? classId, int? grade) =>
        new(id, kind, At.AddMinutes(minute), TimeSpan.FromMinutes(minute), new("abyss-reshanta-a", "Abyss", x / 2, y / 2, At.AddMinutes(minute)))
        { OpponentClassId = classId, OpponentAbyssRankGrade = grade };
}

public sealed class CaptureLootTrackerTimelinePreview : LootTrackerView
{
    [Parameter] public Action<CaptureLootTrackerTimelinePreview>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task SeekAndRender(TimeSpan elapsed) { SeekTimeline(elapsed); return InvokeAsync(StateHasChanged); }
    public Task FollowAndRender() { FollowTimelineEnd(); return InvokeAsync(StateHasChanged); }
}
