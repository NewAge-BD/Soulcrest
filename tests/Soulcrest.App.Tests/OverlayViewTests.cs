using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayViewTests
{
    private static readonly (string Id, string File)[] StaticSamples =
    [
        ("loot", "pet-tracker.png"),
        ("boss", "boss-spawns.png"), ("leveling", "leveling.png"),
        ("alerts", "timer-strip.png"), ("notifications", "notification.png"), ("event-timeline", "timeline.png"),
        ("map", "map-overlay.png"), ("merchant", "merchant-soul-counters.png"), ("daevanion", "daevanion-board.png"),
    ];
    private const string HistoricalBossSample = "boss-spawns-2026-10-09.png";

    [Theory]
    [InlineData("de", "Ingame-Beispiel noch nicht vorhanden.", "Ingame-Beispiel: ")]
    [InlineData("en", "In-game example not available yet.", "In-game example: ")]
    public async Task StaticExamplesOnlyUseExistingScreenshotsAndExplainEveryMissingImage(string language, string missing, string altPrefix)
    {
        await using var fixture = new ScreenshotFixture(language);
        foreach (var sample in StaticSamples)
        {
            fixture.SetAvailable(sample.Id, true);
            var html = await fixture.Show(sample.Id);
            Assert.DoesNotContain("data:image/", html);
            Assert.DoesNotContain("Overlay preview with sample data", html);
            Assert.DoesNotContain("Overlay-Vorschau mit Beispieldaten", html);
            Assert.Contains($"src=\"overlay-samples/{sample.File}\"", html);
            Assert.Contains($"alt=\"{altPrefix}", html);
            Assert.DoesNotContain(missing, html);
            fixture.SetAvailable(sample.Id, false);
            html = await fixture.Show(sample.Id);
            Assert.Contains(missing, html);
            Assert.Contains("role=\"status\"", html);
            Assert.DoesNotContain("<img", html);
            Assert.DoesNotContain("data:image/", html);
        }
    }

    [Fact]
    public async Task MountedScreenshotAndMissingNoticeFollowLanguageChangesWithoutChangingTheirFamily()
    {
        await using var fixture = new ScreenshotFixture("en", localeEvents: true);
        fixture.SetAvailable("map", true);
        Assert.Contains("alt=\"In-game example: ", await fixture.Show("map"));
        await fixture.PublishLanguage("de");
        var html = await fixture.Html();
        Assert.Contains("<figcaption>Ingame-Beispiel</figcaption>", html);
        Assert.Contains("alt=\"Ingame-Beispiel: ", html);
        Assert.Contains("src=\"overlay-samples/map-overlay.png\"", html);
        Assert.Contains("Ingame-Beispiel noch nicht vorhanden.", await fixture.Show("loot"));
        await fixture.PublishLanguage("en");
        html = await fixture.Html();
        Assert.Contains("<figcaption>In-game example</figcaption>", html);
        Assert.Contains("In-game example not available yet.", html);
        Assert.DoesNotContain("Ingame-Beispiel noch nicht vorhanden.", html);
    }

    [Theory]
    [InlineData("de", "Ingame-Aufnahme · älterer Darstellungsstand · 09.10.2026")]
    [InlineData("en", "In-game capture · earlier appearance · 9 October 2026")]
    public async Task HistoricalBossScreenshotCarriesItsDateAndANewerImageTakesPriority(string language, string historicalCaption)
    {
        await using var fixture = new ScreenshotFixture(language);
        fixture.SetHistoricalBossAvailable();
        var html = await fixture.Show("boss");
        Assert.Contains($"src=\"overlay-samples/{HistoricalBossSample}\"", html);
        Assert.Contains(historicalCaption, html);
        fixture.SetAvailable("boss", true);
        html = await fixture.Show("boss");
        Assert.Contains("src=\"overlay-samples/boss-spawns.png\"", html);
        Assert.DoesNotContain(historicalCaption, html);
        await fixture.FailImage();
        html = await fixture.Html();
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain(historicalCaption, html);
        Assert.Contains(language == "de" ? "Ingame-Beispiel noch nicht vorhanden." : "In-game example not available yet.", html);
    }

    [Fact]
    public async Task AnUnloadedScreenshotShowsTheMissingNoticeUntilTheFamilyChangesWithoutAProductionRenderer()
    {
        await using var fixture = new ScreenshotFixture("en");
        fixture.SetAvailable("map", true);
        Assert.Contains("src=\"overlay-samples/map-overlay.png\"", await fixture.Show("map"));
        await fixture.FailImage();
        var html = await fixture.Show("map");
        Assert.Contains("In-game example not available yet.", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("data:image/", html);
        fixture.SetAvailable("merchant", true);
        html = await fixture.Show("merchant");
        Assert.Contains("src=\"overlay-samples/merchant-soul-counters.png\"", html);
        Assert.DoesNotContain("In-game example not available yet.", html);
        html = await fixture.Show("../map-overlay.png");
        Assert.Contains("In-game example not available yet.", html);
        Assert.DoesNotContain("<img", html);
    }

    [Theory]
    [InlineData("de", "Gemeinsame Einstellungen", "Place Mode starten")]
    [InlineData("en", "Shared settings", "Start Place Mode")]
    public async Task CentralTabListsEveryOverlayAndOpeningItLeavesSettingsUntouched(string language, string shared, string place)
    {
        await using var fixture = new Fixture(language);
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var fileBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var html = await fixture.Render();
        foreach (var overlay in OverlayRegistry.All)
            Assert.Contains($"data-overlay-select=\"{overlay.Id}\"", html);
        Assert.Equal(OverlayRegistry.All.Count, System.Text.RegularExpressions.Regex.Matches(html, "data-overlay-select=").Count);
        Assert.Contains(shared, html);
        Assert.Contains(place, html);
        Assert.Contains(language == "de" ? "Pet-Fortschritt" : "Pet progress", html);
        Assert.DoesNotContain("Loot &amp; Pet", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("id=\"overlay-language\"", html);
        Assert.Contains("id=\"overlays-in-recordings\"", html);
        Assert.Contains("hotkey-label-loot.toggle", html);
        Assert.Contains("hotkey-label-loot.unlock", html);
        Assert.DoesNotContain("hotkey-label-boss.toggle", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(fileBefore, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
        Assert.False(fixture.Placement.Active);
        Assert.Empty(fixture.Alerts.Items);
        await fixture.Export(html, "loot");
    }

    [Theory]
    [InlineData("de", "farm-loot")]
    [InlineData("en", "farm-loot")]
    [InlineData("de", "abyss-rank")]
    [InlineData("en", "abyss-rank")]
    public async Task RetiredHudLinksCannotSelectOrReactivateAnOldNativeOverlay(string language, string retired)
    {
        await using var fixture = new Fixture(language);
        fixture.Ui.ShowOverlay(retired);
        fixture.Settings.Current.LootOverlayEnabled = true;
        fixture.Settings.Current.AbyssRankOverlayEnabled = true;
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Contains("data-overlay-detail=\"loot\"", html);
        foreach (var id in new[] { "farm-loot", "abyss-rank" })
        {
            Assert.DoesNotContain($"data-overlay-select=\"{id}\"", html);
            Assert.DoesNotContain($"hotkey-label-{id}.", html);
        }
        Assert.DoesNotContain("loot-overlay-session-only", html);
        Assert.DoesNotContain("abyss-killfeed-enabled", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.False(fixture.Placement.Active);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("merchant")]
    [InlineData("daevanion")]
    public async Task BoundOverlaysHavePurePreviewsAndNoFreeLayoutControls(string id)
    {
        await using var fixture = new Fixture("en");
        fixture.Ui.ShowOverlay(id);
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Contains($"data-overlay-detail=\"{id}\"", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Anchored to game content", html);
        Assert.DoesNotContain("id=\"overlay-scale\"", html);
        Assert.DoesNotContain("id=\"overlay-x\"", html);
        Assert.DoesNotContain("id=\"overlay-context-menu\"", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        if (id == "merchant")
            Assert.Contains("The shop overlay always uses the Soulcrest Legacy style and is excluded from themes.", html);
        if (id == "map")
        {
            Assert.Contains("id=\"map-overlay-routes\"", html);
            Assert.Contains("id=\"guard-overlay-radius\"", html);
            Assert.Contains("map-layers-legend-hint", html);
            Assert.Contains("Open map legend", html);
        }
        await fixture.Export(html, id);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EachFreeOverlayOffersItsOwnContextRulesAndAppropriateControls(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render();
        foreach (var overlay in OverlayRegistry.All.Where(item => item.CanPlace))
        {
            var html = await fixture.Invoke("SelectOverlay", overlay.Id);
            foreach (var context in new[] { "worldmap", "menu", "instances", "missing-minimap" })
                Assert.Contains($"id=\"overlay-context-{context}\"", html);
            Assert.Contains("id=\"overlay-scale\"", html);
            Assert.Contains("id=\"overlay-x\"", html);
            Assert.Contains("id=\"overlay-y\"", html);
            Assert.Equal(overlay.Id != "notifications", html.Contains("id=\"overlay-enabled\"", StringComparison.Ordinal));
            Assert.Equal(overlay.Id is "notifications" or "event-timeline", html.Contains("id=\"overlay-width\"", StringComparison.Ordinal));

        }
        var boss = await fixture.Invoke("SelectOverlay", "boss");
        Assert.Contains("id=\"boss-overlay-enabled\"", boss);
        Assert.Contains("id=\"boss-overlay-count\"", boss);
        var timeline = await fixture.Invoke("SelectOverlay", "event-timeline");
        Assert.Contains("id=\"event-timeline-hours\"", timeline);
        Assert.Contains("value=\"48\"", timeline);
        var timers = await fixture.Invoke("SelectOverlay", "alerts");
        Assert.Contains("id=\"timer-overlay-settings\"", timers);
        Assert.Contains("id=\"timer-overlay-gap\"", timers);
        Assert.Contains("id=\"timer-overlay-seconds\"", timers);
        Assert.Contains("data-timer-choice=", timers);
        Assert.DoesNotContain("id=\"timer-overlay-scale\"", timers);
        Assert.DoesNotContain("id=\"timer-overlay-menu\"", timers);
        await fixture.Export(timers, "timers");
        await fixture.Export(timeline, "timeline");
    }

    [Theory]
    [InlineData("de", "Wann anzeigen", "Wenn Aion2 im Vordergrund ist", "Immer")]
    [InlineData("en", "When to Show", "When Aion2 is in the foreground", "Always")]
    public async Task SharedShowModeOffersExactLabelsAndPersistsWithoutChangingOtherPreferences(string language, string label, string foreground, string always)
    {
        await using var fixture = new Fixture(language);
        Assert.Equal("foreground", new AppSettings().OverlayShowMode);
        fixture.Settings.Current.OverlayShowMode = "foreground";
        static string OtherSettings(AppSettings settings)
        {
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayShowMode));
            return node.ToJsonString();
        }
        var before = OtherSettings(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Contains(label, html);
        Assert.Contains(foreground, html);
        Assert.Contains(always, html);
        Assert.Matches("id=\"overlay-show-mode\"[^>]*value=\"foreground\"", html);
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "always" });
        Assert.Equal("always", fixture.Settings.Current.OverlayShowMode);
        Assert.Equal("always", new SettingsService().Current.OverlayShowMode);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "invalid" });
        Assert.Equal("always", fixture.Settings.Current.OverlayShowMode);
        await fixture.Invoke("SetShowMode", new ChangeEventArgs { Value = "foreground" });
        Assert.Equal("foreground", new SettingsService().Current.OverlayShowMode);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Placement.Active);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AllSixThemesAreLocalizedPersistImmediatelyAndRefreshThePreviewWithoutChangingOtherSettings(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.OverlayTheme = OverlayThemeDefinition.DefaultId;
        static string OtherSettings(AppSettings settings)
        {
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayTheme));
            return node.ToJsonString();
        }
        static string PreviewImage(string markup) => System.Text.RegularExpressions.Regex.Match(markup, "data:image/png;base64,[^\"]+").Value;
        var before = OtherSettings(fixture.Settings.Current);
        var html = await fixture.Render();
        Assert.Matches("id=\"overlay-theme\"[^>]*value=\"aion2-stylized\"", html);
        var themeSelect = System.Text.RegularExpressions.Regex.Match(html, "<select id=\"overlay-theme\"[^>]*>(.*?)</select>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        var expectedIds = new[] { "aion2-stylized", "aion2-neutral", "obsidian", "aurora", "runic", "soulcrest-legacy" };
        var expectedLabels = language == "de"
            ? new[] { "Aion2 - stilisierte", "Aion2 - neutral", "Obsidian", "Nordlicht", "Runen", "Soulcrest legacy" }
            : new[] { "Aion2 - stylized", "Aion2 - neutral", "Obsidian", "Aurora", "Runic", "Soulcrest legacy" };
        var options = System.Text.RegularExpressions.Regex.Matches(themeSelect,
            "<option\\b[^>]*\\bvalue=\"([^\"]*)\"[^>]*>(.*?)</option>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(6, options.Count);
        for (var index = 0; index < expectedIds.Length; index++)
        {
            Assert.Equal(expectedIds[index], options[index].Groups[1].Value);
            var label = System.Text.RegularExpressions.Regex.Replace(options[index].Groups[2].Value,
                "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.Equal(expectedLabels[index], WebUtility.HtmlDecode(label).Trim());
        }
        var previews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in expectedIds)
        {
            html = await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = id });
            Assert.Equal(id, fixture.Settings.Current.OverlayTheme);
            Assert.Equal(id, new SettingsService().Current.OverlayTheme);
            Assert.Matches("id=\"overlay-theme\"[^>]*value=\"" + id + "\"", html);
            Assert.Equal(before, OtherSettings(fixture.Settings.Current));
            Assert.NotEmpty(PreviewImage(html));
            Assert.True(previews.Add(PreviewImage(html)), id + " must refresh the production preview");
        }
        await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = "unknown" });
        Assert.Equal("soulcrest-legacy", fixture.Settings.Current.OverlayTheme);
        await fixture.Invoke("SetTheme", new ChangeEventArgs { Value = "aion2" });
        Assert.Equal(OverlayThemeDefinition.DefaultId, new SettingsService().Current.OverlayTheme);
        Assert.Equal(before, OtherSettings(fixture.Settings.Current));
        Assert.Empty(fixture.Alerts.Items);
        Assert.False(fixture.Placement.Active);
    }

    [Fact]
    public async Task LayoutAndPoliciesPersistSeparatelyWithoutEnablingOverlaysOrChangingReminders()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.OverlayEnabled = false;
        fixture.Settings.Current.BossOverlayEnabled = false;
        var alertsBefore = JsonSerializer.Serialize(fixture.Alerts.Items);
        await fixture.Render();
        await fixture.Invoke("SetScale", new ChangeEventArgs { Value = "135" });
        await fixture.Invoke("SetRule", "menu", true);
        Assert.False(fixture.Settings.Current.OverlayEnabled);
        await fixture.Invoke("SelectOverlay", "boss");
        await fixture.Invoke("SetEnabled", new ChangeEventArgs { Value = false });
        Assert.False(fixture.Settings.Current.BossOverlayEnabled);
        await fixture.Invoke("SetRule", "instances", false);
        var saved = new SettingsService().Current;
        Assert.Equal(1.35, saved.OverlayScale);
        Assert.True(OverlayVisibilityPolicy.Get(saved, "loot").Menu);
        Assert.False(OverlayVisibilityPolicy.Get(saved, "boss").Instances);
        Assert.False(saved.OverlayEnabled);
        Assert.False(saved.BossOverlayVisible);
        Assert.Equal(alertsBefore, JsonSerializer.Serialize(fixture.Alerts.Items));
    }

    [Fact]
    public async Task AutomaticMonitorBringUsesGameMonitorAndStoppingKeepsLayout()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render();
        await fixture.Invoke("BringToMonitor");
        Assert.Equal((-1240, 80), (fixture.Settings.Current.OverlayX, fixture.Settings.Current.OverlayY));
        await fixture.Invoke("StartPlaceMode");
        Assert.True(fixture.Placement.Active);
        Assert.Equal("game", fixture.Placement.Monitor!.Id);
        await fixture.Invoke("SetScale", new ChangeEventArgs { Value = "125" });
        var html = await fixture.Invoke("StopPlaceMode");
        Assert.False(fixture.Placement.Active);
        Assert.Equal(1.25, new SettingsService().Current.OverlayScale);
        Assert.Contains("Start Place Mode", html);
    }

    [Fact]
    public async Task EmbeddedStaticManagementListsTheTwelveNativeFamiliesWithoutChangingSettings()
    {
        await using var fixture = new Fixture("en");
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render(staticOnly: true, embedded: true);
        string[] families = ["loot", "achievements",
            "genus-insight", "boss", "leveling", "alerts", "notifications", "event-timeline", "map", "merchant", "daevanion"];
        foreach (var id in families) Assert.Contains($"data-overlay-select=\"{id}\"", html);
        Assert.Equal(families.Length, System.Text.RegularExpressions.Regex.Matches(html, "data-overlay-select=").Count);
        Assert.Contains("Static Overlays", html);
        Assert.DoesNotContain("data-overlay-select=\"farm-loot\"", html);
        Assert.DoesNotContain("data-overlay-select=\"abyss-rank\"", html);
        Assert.DoesNotContain("class=\"page overlay-page\"", html);
        html = await fixture.Invoke("SelectOverlay", "farm-loot");
        Assert.Contains("data-overlay-detail=\"loot\"", html);
        html = await fixture.Invoke("SelectOverlay", "map");
        Assert.Contains("data-overlay-detail=\"map\"", html);
        Assert.Contains("id=\"map-overlay-routes\"", html);
        Assert.DoesNotContain("id=\"overlay-scale\"", html);
        await fixture.Invoke("StartPlaceMode");
        Assert.True(fixture.Placement.Active);
        Assert.Equal(families, fixture.Placement.Overlays.Select(overlay => overlay.Id));
        Assert.DoesNotContain(fixture.Placement.PlaceableOverlays, overlay => overlay.Id is "farm-loot" or "abyss-rank");
        await fixture.Invoke("StopPlaceMode");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.False(fixture.Placement.Active);
        Assert.Empty(fixture.Alerts.Items);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task GenusStaticSidebarHasItsOwnPreviewAndPositionControlsWithoutCreatingAnEditorWindow(string language)
    {
        await using var fixture = new Fixture(language);
        var settingsBefore = JsonSerializer.Serialize(fixture.Settings.Current);
        var editorBefore = JsonSerializer.Serialize(fixture.EditorService.Overlays);
        await fixture.Render(staticOnly: true, embedded: true);
        var html = await fixture.Invoke("SelectOverlay", "genus-insight");
        Assert.Contains("data-overlay-detail=\"genus-insight\"", html);
        Assert.Contains("Genus Insight", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("id=\"overlay-enabled\"", html);
        Assert.Contains("id=\"overlay-x\"", html);
        Assert.Contains("id=\"overlay-y\"", html);
        Assert.Contains("id=\"overlay-scale\"", html);
        Assert.DoesNotContain("id=\"overlay-width\"", html);
        Assert.DoesNotContain("static-overlay-sample-missing", html);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(editorBefore, JsonSerializer.Serialize(fixture.EditorService.Overlays));
        Assert.False(fixture.Placement.Active);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("en", true)]
    [InlineData("de", false)]
    [InlineData("en", false)]
    public async Task AchievementDurationIsAvailableOnlyInItsOwnOverlaySettings(string language, bool staticOnly)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.AchievementOverlayDisplaySeconds = 20;
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render(staticOnly: staticOnly, embedded: staticOnly);
        Assert.DoesNotContain("data-achievement-overlay-options", html);
        html = await fixture.Invoke("SelectOverlay", "achievements");
        Assert.Contains("data-achievement-overlay-options", html);
        Assert.Contains("id=\"achievement-overlay-display-seconds\"", html);
        Assert.Contains(UiText.T("Favoriten bleiben dauerhaft sichtbar."), html);
        html = await fixture.Invoke("SelectOverlay", "boss");
        Assert.DoesNotContain("data-achievement-overlay-options", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
    }

    [Fact]
    public async Task ExistingOverlayLinksSwitchTheAlreadyOpenEditorAndItsSectionSelectionSurvivesAParentRender()
    {
        await using var fixture = new Fixture("en");
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        await fixture.RenderEditor();
        var editor = fixture.Editor;
        var templateCount = fixture.EditorService.Templates.Count;
        var html = await fixture.NavigateOverlay("boss");
        Assert.Same(editor, fixture.Editor);
        Assert.Equal("static", fixture.Ui.OverlayEditorSection);
        Assert.Contains("section=static\" aria-current=\"page\"", html);
        Assert.Contains("data-overlay-detail=\"boss\"", html);
        html = await fixture.NavigateOverlay("map");
        Assert.Same(editor, fixture.Editor);
        Assert.Contains("data-overlay-detail=\"map\"", html);
        html = await fixture.InvokeEditor("SelectSection", "templates");
        Assert.Equal("presets", fixture.Ui.OverlayEditorSection);
        Assert.Contains("section=presets\" aria-current=\"page\"", html);
        Assert.DoesNotContain("data-static-overlays", html);
        foreach (var id in new[] { "farm-loot", "abyss-rank" })
        {
            html = await fixture.NavigateOverlay(id);
            Assert.Same(editor, fixture.Editor);
            Assert.Equal("overlay-editor", fixture.Ui.Tab);
            Assert.Equal("templates", fixture.Ui.OverlayEditorSection);
            Assert.Contains("section=presets\" aria-current=\"page\"", html);
            Assert.DoesNotContain("data-static-overlays", html);
        }
        Assert.Equal(templateCount, fixture.EditorService.Templates.Count);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.False(fixture.Placement.Active);
        Assert.Empty(fixture.Alerts.Items);
    }

    [Theory]
    [InlineData("de", "Bosse im Alerts-Tab auswählen")]
    [InlineData("en", "Select bosses in the Alerts tab")]
    public async Task StaticTimerSettingsOfferTheBossSelectionLinkEvenWithoutCapturedBosses(string language, string link)
    {
        await using var fixture = new Fixture(language);
        fixture.Ui.ShowOverlay("alerts");
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render(staticOnly: true, embedded: true);
        Assert.Contains("Static Overlays", html);
        Assert.Contains("data-overlay-detail=\"alerts\"", html);
        Assert.Contains("id=\"timer-overlay-bosses\"", html);
        Assert.Contains(link, html);
        Assert.DoesNotContain("data-timer-choice=\"boss:", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        await fixture.Export(html, "static-boss-link");
    }

    [Fact]
    public void SectionNavigationDoesNotTouchVisibilityOrOtherNavigationTargets()
    {
        var ui = new UiState();
        ui.ShowOverlay("alerts");
        Assert.Equal("overlay-editor", ui.Tab);
        Assert.Equal("static", ui.OverlayEditorSection);
        Assert.Equal("alerts", ui.OverlaySection);
        Assert.Null(ui.TakePendingPetFocus());
        ui.ShowOverlay("map");
        Assert.Equal("map", ui.OverlaySection);
        ui.Navigate("pets");
        Assert.Equal("pets", ui.Tab);
        ui.Navigate("overlay");
        Assert.Equal("overlay-editor", ui.Tab);
        Assert.Equal("static", ui.OverlayEditorSection);
    }

    private sealed class ScreenshotFixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly string _root;
        private readonly bool _existed;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly byte[] _fixtureImage;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly SettingsService? _settings;
        private HtmlRootComponent _html;
        private CapturedStaticOverlaySample? _sample;

        public ScreenshotFixture(string language, bool localeEvents = false)
        {
            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.True(File.Exists(Path.Combine(baseDirectory, "Soulcrest.App.Tests.dll")));
            _root = Path.GetFullPath(Path.Combine(baseDirectory, "wwwroot", "overlay-samples"));
            Assert.StartsWith(baseDirectory, _root, StringComparison.OrdinalIgnoreCase);
            _existed = Directory.Exists(_root);
            Directory.CreateDirectory(_root);
            // Only the test output is changed. Product screenshots are never read or overwritten.
            _fixtureImage = File.ReadAllBytes(Path.Combine(baseDirectory, "fixtures", "daevanion", "nezekan-board.png"));
            foreach (var file in StaticSamples.Select(sample => sample.File).Append(HistoricalBossSample))
            {
                var path = Path.Combine(_root, file);
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                File.Delete(path);
            }
            var services = new ServiceCollection();
            services.AddLogging(); // Rendering screenshots needs neither settings nor a production painter.
            if (localeEvents) services.AddSingleton(_settings = new SettingsService());
            _provider = services.BuildServiceProvider();
            UiText.Language = language;
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public void SetAvailable(string overlayId, bool available)
        {
            var path = Path.Combine(_root, StaticSamples.Single(sample => sample.Id == overlayId).File);
            if (available) File.WriteAllBytes(path, _fixtureImage); else File.Delete(path);
        }
        public void SetHistoricalBossAvailable() => File.WriteAllBytes(Path.Combine(_root, HistoricalBossSample), _fixtureImage);
        public Task PublishLanguage(string language) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            UiText.Language = language;
            (typeof(SettingsService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_settings) as Action)?.Invoke();
        });
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_html.ToHtmlString()));

        public async Task<string> Show(string overlayId)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(StaticOverlaySample.OverlayId)] = overlayId,
                    [nameof(CapturedStaticOverlaySample.Captured)] = (Action<CapturedStaticOverlaySample>)(sample => _sample = sample),
                });
                if (_sample is null) _html = await _renderer.RenderComponentAsync<CapturedStaticOverlaySample>(parameters);
                else await _sample.SetParametersAsync(parameters);
            });
            return WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_html.ToHtmlString));
        }

#pragma warning disable BL0006 // Invoke the actual image error callback emitted by the production component.
        public Task FailImage() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(StaticOverlaySample).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_sample, [tree]);
            var frames = tree.GetFrames();
            var callback = Enumerable.Range(0, frames.Count).Select(index => frames.Array[index])
                .Single(frame => frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && frame.AttributeName == "onerror").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.ErrorEventArgs> error) await error.InvokeAsync(new());
            else if (callback is EventCallback<EventArgs> generic) await generic.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else throw new InvalidOperationException("Unsupported image error callback.");
            await _sample!.RefreshNow();
        });
#pragma warning restore BL0006

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, original) in _backups)
                if (original is null) File.Delete(path); else File.WriteAllBytes(path, original);
            if (!_existed && !Directory.EnumerateFileSystemEntries(_root).Any()) Directory.Delete(_root);
            UiText.Language = _language;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-overlay-ui-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private OverlayView? _view;
        private StaticEditorNavigationHost? _editorHost;
        public OriginalKitOverlayEditor Editor => _editorHost!.Editor;
        public IOverlayService EditorService => _provider.GetRequiredService<IOverlayService>();
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public UiState Ui => _provider.GetRequiredService<UiState>();
        public OverlayPlacementService Placement => _provider.GetRequiredService<OverlayPlacementService>();
        public AlertsService Alerts => _provider.GetRequiredService<AlertsService>();

        public Fixture(string language)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<AlertSoundPlayer>();
            services.AddSingleton(new AbyssRankSoundPlayer(() => new(),
                () => throw new InvalidOperationException("Rendering cannot play rank audio."), (_, _, _) => [1]));
            services.AddSingleton(new RareDropAlertService(() => new(), () => throw new InvalidOperationException("Rendering cannot play audio."), TimeProvider.System));
            services.AddSingleton<UiState>();
            services.AddSingleton<IOverlayService>(new OverlayService(Path.Combine(_folder, "editor"), new OverlayCollectionSettings()));
            services.AddSingleton<IOverlayEditorPresentationSource>(new NavigationPresentation());
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NavigationJs());
            services.AddSingleton<OverlayHotkeyService>();
            services.AddSingleton(new OverlayPreviewRenderer(new PetCatalog([])));
            services.AddSingleton(sp => new OverlayPlacementService(sp.GetRequiredService<SettingsService>(),
                () => [new("primary", "Primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040)),
                       new("game", "Game", new(-1280, 0, 1280, 720), new(-1280, 0, 1280, 680))],
                () => new Rectangle(-1280, 0, 1280, 720)));
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false, settings: sp.GetRequiredService<SettingsService>()));
            _provider = services.BuildServiceProvider();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.OverlayVisibility = new(StringComparer.Ordinal);
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render(bool staticOnly = false, bool embedded = false)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedOverlayView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedOverlayView.Captured)] = (Action<OverlayView>)(view => _view = view),
                [nameof(OverlayView.StaticOnly)] = staticOnly,
                [nameof(OverlayView.Embedded)] = embedded,
            })));
            return await Markup();
        }
        public async Task<string> RenderEditor()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<StaticEditorNavigationHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(StaticEditorNavigationHost.Captured)] = (Action<StaticEditorNavigationHost>)(host => _editorHost = host)
            })));
            return await Markup();
        }
        public async Task<string> NavigateOverlay(string id)
        {
            await _renderer.Dispatcher.InvokeAsync(() => Ui.ShowOverlay(id));
            return await Markup();
        }
        public async Task<string> InvokeEditor(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(OriginalKitOverlayEditor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Editor, args);
                if (result is Task task) await task;
                await _editorHost!.RefreshNow();
            });
            return await Markup();
        }
        public async Task<string> Invoke(string method, params object[] args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(OverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, args);
                if (result is Task task) await task;
                await ((CapturedOverlayView)_view!).RefreshNow();
            });
            return await Markup();
        }
        private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public Task Export(string markup, string id)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_UI_PREVIEW") is not { Length: > 0 } output) return Task.CompletedTask;
            Directory.CreateDirectory(output);
            return File.WriteAllTextAsync(Path.Combine(output, $"overlay-{id}-{_language}.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/alerts.css\"><link rel=\"stylesheet\" href=\"/css/overlay.css\"><link rel=\"stylesheet\" href=\"/css/loot-tracker.css\"></head><body>" + markup + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }

    private sealed class NavigationPresentation : IOverlayEditorPresentationSource
    {
        public event Action? Changed { add { } remove { } }
        public OverlaySnapshot Capture(bool examples = false, string? language = null) => OverlaySnapshot.DemoFor(language);
        public IDisposable Acquire(bool examples = false, bool market = false) => new EmptyLease();
        public void Dispose() { }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }

    private sealed class NavigationJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}

public sealed class CapturedOverlayView : OverlayView
{
    [Parameter] public Action<OverlayView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}

public sealed class CapturedStaticOverlaySample : StaticOverlaySample
{
    [Parameter] public Action<CapturedStaticOverlaySample>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}

public sealed class StaticEditorNavigationHost : ComponentBase, IDisposable
{
    [Inject] public UiState Ui { get; set; } = null!;
    [Parameter] public Action<StaticEditorNavigationHost>? Captured { get; set; }
    public OriginalKitOverlayEditor Editor { get; private set; } = null!;
    protected override void OnInitialized() { Captured?.Invoke(this); Ui.Changed += Refresh; }
    private void Refresh() => _ = InvokeAsync(StateHasChanged);
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<OriginalKitOverlayEditor>(0);
        builder.AddAttribute(1, nameof(OriginalKitOverlayEditor.Section), Ui.OverlayEditorSection);
        builder.AddAttribute(2, nameof(OriginalKitOverlayEditor.SectionChanged), EventCallback.Factory.Create<string>(this, Ui.ShowOverlayEditorSection));
        builder.AddComponentReferenceCapture(3, instance => Editor = (OriginalKitOverlayEditor)instance);
        builder.CloseComponent();
    }
    public void Dispose() { Ui.Changed -= Refresh; GC.SuppressFinalize(this); }
}
