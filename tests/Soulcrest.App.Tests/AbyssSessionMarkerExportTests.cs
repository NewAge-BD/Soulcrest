using System.Collections.Immutable;
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

/// <summary>Opt-in export of the actual Razor view with synthetic data and the real offline Abyss map.</summary>
[Collection("Settings file")]
public sealed class AbyssSessionMarkerExportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
    private const string MapId = "abyss-reshanta-a";
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportActualAbyssSessionViewOnlyWhenExplicitlyRequested(string language)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_ABYSS_MARKER_PREVIEW") is not { Length: > 0 } output) return;
        var temporary = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-marker-export-" + Guid.NewGuid().ToString("N"));
        var previousLanguage = UiText.Language;
        var previousData = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        var previousMaps = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        Directory.CreateDirectory(temporary);
        try
        {
            var mapDirectory = Path.Combine(TestPrerequisites.RepositoryRoot, "imports", "generated", "mapdata");
            Environment.SetEnvironmentVariable("SOULCREST_DATA", temporary);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", mapDirectory);
            var maps = LootTrackerRouteMapPreview.Load(mapDirectory);
            var map = Assert.IsType<LootTrackerRouteMapPreview.MapDefinition>(maps.GetValueOrDefault(MapId));
            Assert.Equal(4096, map.Size);
            var settings = new SettingsService();
            settings.Current.UiLanguage = settings.Current.NameLanguage = language;
            settings.Current.AbyssTrackingEnabled = true;
            settings.Current.LootDropAnimationsEnabled = false;
            UiText.Language = language;
            var progress = new ProgressService(Path.Combine(temporary, "progress.json"), new PetCatalog([]));
            await using var provider = new ServiceCollection().AddLogging().AddSingleton(settings).AddSingleton(progress)
                .AddSingleton(new AlertsService(Path.Combine(temporary, "alerts.json"), runTimer: false))
                .AddSingleton<AlertSoundPlayer>()
                .AddSingleton(new RareDropAlertService(() => new(), () => throw new InvalidOperationException("A preview cannot play audio."), TimeProvider.System))
                .BuildServiceProvider();
            var session = Example(language);
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LootTrackerView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session], session.EndedAt),
                    [nameof(LootTrackerView.IsExample)] = true,
                    [nameof(LootTrackerView.AbyssProgress)] = new AbyssProgressSnapshot(Profile, 35_600, 1_250_000, 999_000, 2_000_000, session.EndedAt, true)
                        { FarmedAbyssPointsConfirmed = true, AbyssPointBalanceConfirmed = true, RankPointsConfirmed = true },
                }))).ToHtmlString());
            Assert.Contains("data-map-background=\"offline\"", html);
            Assert.Contains("https://mapdata.soulcrest/abyss-reshanta-a/", html);
            Assert.Equal(2, Regex.Matches(html, "data-marker-kind=\"kill\"").Count);
            Assert.Single(Regex.Matches(html, "data-marker-kind=\"death\""));
            Assert.Equal(2, Regex.Matches(html, "data-marker-kind=\"odyle\"").Count);
            Assert.DoesNotContain("data-farm-stop-id", html);
            html = Regex.Replace(html, "src=\"(loot-icons/[a-z0-9_-]+\\.png)\"", match =>
            {
                var asset = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", match.Groups[1].Value);
                return "src=\"data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(asset)) + "\"";
            });
            var cssDirectory = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css");
            var css = await File.ReadAllTextAsync(Path.Combine(cssDirectory, "app.css")) + "\n" +
                await File.ReadAllTextAsync(Path.Combine(cssDirectory, "loot-tracker.css"));
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"abyss-sessionmarkers-{language}.html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css +
                "</style></head><body>" + html + "</body></html>");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_DATA", previousData);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", previousMaps);
            UiText.Language = previousLanguage;
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static LootTrackerSession Example(string language)
    {
        var label = language == "de" ? "Chaotische Untere Ebene von Reshanta" : "Chaotic Lower Reshanta";
        return new()
        {
            Id = "synthetic-abyss-marker-preview", Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Completed,
            Location = "Abyss", Character = new(Profile, language == "de" ? "Beispielcharakter" : "Example character", 44_850, 2_430),
            StartedAt = At, EndedAt = At.AddMinutes(20), LastLootAt = At.AddMinutes(19), LastPresenceAt = At.AddMinutes(20), Elapsed = TimeSpan.FromMinutes(20),
            AbyssPoints = 6_000, RankPointsGained = 6_000, RankPointsLost = 650, KinaUnbound = 12_345, ItemCount = 55, SoulCount = 0,
            RouteSegments = [new(MapId, label, [new(1920, 1650), new(2030, 1640), new(2150, 1680), new(2270, 1740),
                new(2370, 1860), new(2440, 1950), new(2510, 2070), new(2600, 2160)]),
                new(MapId, label, [new(2480, 2420), new(2370, 2380), new(2270, 2320), new(2170, 2240), new(2100, 2130), new(2020, 2080)])],
            CombatEvents = [Combat("preview-kill-1", LootTrackerCombatKind.Kill, 3, 2150, 1680, label),
                Combat("preview-death-1", LootTrackerCombatKind.Death, 9, 2600, 2160, label),
                Combat("preview-kill-2", LootTrackerCombatKind.Kill, 16, 2170, 2240, label),
                new("preview-unplaced-death", LootTrackerCombatKind.Death, At.AddMinutes(18), TimeSpan.FromMinutes(18))],
            GatherVisits = [Visit("preview-harvest-1", 5, 2370, 1860, "preview-odyle-1", label),
                Visit("preview-harvest-2", 6, 2370, 1860, "preview-odyle-1", label),
                Visit("preview-harvest-3", 14, 2370, 2380, "preview-odyle-2", label)],
            Loot = [new("Item:610610013", MonsterLootItemCatalog.Default.Name(610610013, language), LootTrackerLootKind.Item, 42),
                new("Item:610610014", MonsterLootItemCatalog.Default.Name(610610014, language), LootTrackerLootKind.Item, 13)],
            MinuteIncome = Enumerable.Range(0, 20).Select(minute => new LootTrackerIncomeBin(minute, null, minute == 19 ? 945 : 600)
                { AbyssPoints = minute is 3 or 16 ? 600 : minute is 9 or 18 ? 0 : 300 }).ToImmutableArray(),
        };
    }
    private static LootTrackerCombatEvent Combat(string id, LootTrackerCombatKind kind, int minute, double x, double y, string label) =>
        new(id, kind, At.AddMinutes(minute), TimeSpan.FromMinutes(minute), new(MapId, label, x, y, At.AddMinutes(minute)));
    private static LootTrackerGatherVisit Visit(string id, int minute, double x, double y, string node, string label) =>
        new(id, 0, MapId, label, x, y, x, y, At.AddMinutes(minute), true, "Odyle", 4096, 123, node);
}
