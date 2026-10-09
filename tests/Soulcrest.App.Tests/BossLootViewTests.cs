using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossLootViewTests
{
    [Theory]
    [InlineData("de", "Großschwert der Fantasie")]
    [InlineData("en", "Fantasy Greatsword")]
    public async Task ExpandedPoolShowsLocalizedItemNamesIconsAndExactSourceLinks(string language, string name)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<SettingsService>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = UiText.Language;
        settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = language;
        try
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var boss = new BossPlace(111001, 2400017, "Melted Danar", "Geschmolzener Danar", 100, 200,
                Loot: [new("110130006", "Fantasy Greatsword", "Großschwert der Fantasie", "icons/first.png", "unique"),
                       new("110130007", "Spectral Greatsword", null, null, "unsupported-token"),
                       new("common", "Common item", null, null, "common"),
                       new("rare", "Rare item", null, null, "rare"),
                       new("legend", "Legendary item", null, null, "legend"),
                       new("epic", "Epic item", null, null, "epic"),
                       new("special", "Special item", null, null, "special"),
                       new("missing", "Missing rarity item", null, null, null)]);
            async Task<string> Render(BossPlace value, bool open) => await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<BossLootCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(BossLootCard.Boss)] = value, [nameof(BossLootCard.InitiallyExpanded)] = open }))).ToHtmlString());
            var collapsed = await Render(boss, false);
            Assert.DoesNotContain("boss-loot-grid", collapsed);
            var html = System.Net.WebUtility.HtmlDecode(await Render(boss, true));
            Assert.Contains(name, html);
            Assert.Contains("Spectral Greatsword", html); // untranslated data stays English
            Assert.Contains("https://mapdata.soulcrest/icons/first.png", html);
            Assert.Contains("https://aion2.gaming.tools/items/110130006", html);
            Assert.Contains("https://aion2.gaming.tools/npcs/2400017", html);
            foreach (var rarity in new[] { "common", "rare", "legend", "unique", "epic", "special", "unknown" })
                Assert.Contains($"class=\"boss-loot-item rarity-{rarity}\"", html);
            foreach (var label in language == "de"
                ? new[] { "Gewöhnlich", "Selten", "Legendär", "Einzigartig", "Episch", "Spezial", "Seltenheit unbekannt" }
                : new[] { "Common", "Rare", "Legendary", "Unique", "Epic", "Special", "Rarity unknown" })
                Assert.Contains($"<small>{label}</small>", html);
            Assert.DoesNotContain("unsupported-token", html);
            Assert.Contains(language == "de" ? "Lootpool unbekannt" : "Loot pool unknown", await Render(boss with { Loot = null }, false));
            Assert.Contains(language == "de" ? "keine Items" : "no items", await Render(boss with { Loot = [] }, true));
            if (Environment.GetEnvironmentVariable("SOULCREST_BOSS_UI_PREVIEW") is { Length: > 0 } output
                && AppPaths.FindMapData() is { } data)
            {
                var catalogue = JsonSerializer.Deserialize<PoolCatalogue>(File.ReadAllText(Path.Combine(data, "altgard", "bosses.json")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var actual = catalogue.Bosses.Single(b => b.SpawnId == 111001);
                var preview = await Render(actual, true);
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "boss-loot-" + language + ".html"),
                    "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body><div class=\"page boss-page\"><section class=\"card sv-card boss-loot-section\">" + preview + "</section></div></body></html>");
            }
        }
        finally { UiText.Language = previous; }
    }
    private sealed record PoolCatalogue(int MapId, List<BossPlace> Bosses);
}
