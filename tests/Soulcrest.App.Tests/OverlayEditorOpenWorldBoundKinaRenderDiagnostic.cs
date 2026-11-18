using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in local production rendering of anonymous Open World examples; no product host or game sources.</summary>
[Collection("Settings file")]
public sealed class OverlayEditorOpenWorldBoundKinaRenderDiagnostic
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task RenderUpdatedOpenWorldPresetInBothLanguages() => OverlayEditorNativeTestHarness.RunSta(async () =>
    {
        OverlayEditorNativeTestHarness.ValidateIsolation();
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_OVERLAY_OPEN_WORLD_BOUND_KINA_RENDER_DIRECTORY"));
        var permitted = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(permitted + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(target);
        OverlayEditorNativeTestHarness.EnsureFrameworkAssets();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        var window = new OverlayInstance
        {
            Id = "synthetic-open-world-bound-kina", Name = "Open World",
            Settings = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.OpenWorld),
        };
        Assert.DoesNotContain(window.Settings.Widgets, widget => widget.Kind is "dropped-kina" or "dropped-kina-hour");
        Assert.Equal(8, window.Settings.Widgets.Count);
        AssertWidgetGeometry(window, "daily-kina", 0, 0, 336, 104);
        AssertWidgetGeometry(window, "bound-kina", 0, 72, 168, 64);
        AssertWidgetGeometry(window, "bound-kina-hour", 168, 72, 168, 64);
        var originalLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                var snapshot = OverlaySnapshot.DemoFor(language);
                AssertExampleValues(snapshot, language);
                CoreWebView2? browser = null;
                using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: true, drawLockWidget: false,
                    browserInitialized: core => browser = core);
                using var image = await renderer.RenderAsync(window, snapshot);
                Assert.Equal(336, image.Width);
                Assert.Equal(616, image.Height);
                await AssertBrowserValues(Assert.IsType<CoreWebView2>(browser), window, snapshot, language);
                var file = Path.Combine(target, "open-world-bound-kina-" + language + ".png");
                image.Save(file, ImageFormat.Png);
                Assert.True(new FileInfo(file).Length > 4096, "The production renderer must produce a nonempty PNG.");
            }
        }
        finally { UiText.Language = originalLanguage; }
    });

    private static void AssertWidgetGeometry(OverlayInstance window, string kind, double x, double y, double width, double height)
    {
        var widget = Assert.Single(window.Settings.Widgets, candidate => candidate.Kind == kind);
        Assert.Equal(x, widget.X);
        Assert.Equal(y, widget.Y);
        Assert.Equal(width, widget.Width);
        Assert.Equal(height, widget.Height);
    }

    private static void AssertExampleValues(OverlaySnapshot snapshot, string language)
    {
        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        Assert.Equal(184_720L.ToString("N0", culture), snapshot.Metrics["dropped-kina"].Value);
        Assert.Equal(267_540L.ToString("N0", culture), snapshot.Metrics["bound-kina"].Value);
        Assert.Equal((267_540d / snapshot.SessionElapsed.TotalHours).ToString("N0", culture), snapshot.Metrics["bound-kina-hour"].Value);
        Assert.Equal(767_540L, snapshot.DailyKina);
        Assert.True(snapshot.DailyConfirmed);
        Assert.Equal(1_000_000L, snapshot.DailyLimit);
        Assert.Equal(.76754, snapshot.DailyProgress.Fraction);
        Assert.Equal(.5, snapshot.DailyProgress.SessionStartFraction);
        Assert.Equal(.26754, snapshot.DailyProgress.SessionFraction);
        Assert.Equal(767_540L.ToString("N0", culture) + " / " + 1_000_000L.ToString("N0", culture), snapshot.DailyProgress.Value);
    }

    private static async Task AssertBrowserValues(CoreWebView2 browser, OverlayInstance window, OverlaySnapshot snapshot, string language)
    {
        const string script = """
            (() => {
                const origin = document.querySelector('.oe-stage-content').getBoundingClientRect();
                const widgets = ['bound-kina', 'bound-kina-hour', 'daily-kina'].map(kind => {
                    const node = document.querySelector('.overlay-widget-' + kind);
                    const placement = node.closest('.oe-widget');
                    const rect = placement.getBoundingClientRect();
                    const value = node.querySelector(kind === 'daily-kina' ? '.overlay-daily-number' : '.overlay-value-text');
                    const bounds = value.getBoundingClientRect();
                    const icon = node.querySelector('img');
                    return { kind, id: placement.dataset.widgetId, x: rect.x - origin.x, y: rect.y - origin.y,
                        width: rect.width, height: rect.height, value: value.textContent.trim(),
                        valueWidth: bounds.width, valueHeight: bounds.height, icon: icon?.getAttribute('src'),
                        iconLoaded: !!icon?.complete && icon.naturalWidth > 0 && icon.naturalHeight > 0,
                        suffix: node.querySelector('.overlay-hourly-suffix')?.textContent.trim() };
                });
                const daily = document.querySelector('.overlay-daily-bar');
                const session = daily.querySelector('.overlay-daily-session');
                return { widgets, daily: { value: daily.getAttribute('aria-valuetext'),
                    label: daily.getAttribute('aria-label'), percent: Number(daily.getAttribute('aria-valuenow')),
                    start: parseFloat(session?.style.left), contribution: parseFloat(session?.style.width) } };
            })()
            """;
        using var values = JsonDocument.Parse(await browser.ExecuteScriptAsync(script));
        var entries = values.RootElement.GetProperty("widgets");
        Assert.Equal(3, entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString()!;
            var widget = Assert.Single(window.Settings.Widgets, candidate => candidate.Kind == kind);
            Assert.Equal(widget.Id, entry.GetProperty("id").GetString());
            Assert.Equal(widget.X, entry.GetProperty("x").GetDouble(), precision: 3);
            Assert.Equal(widget.Y, entry.GetProperty("y").GetDouble(), precision: 3);
            Assert.Equal(widget.Width, entry.GetProperty("width").GetDouble(), precision: 3);
            Assert.Equal(widget.Height, entry.GetProperty("height").GetDouble(), precision: 3);
            Assert.Equal(snapshot.Metrics[kind].Value, entry.GetProperty("value").GetString());
            Assert.True(entry.GetProperty("valueWidth").GetDouble() > 0);
            Assert.True(entry.GetProperty("valueHeight").GetDouble() > 0);
            Assert.EndsWith(OverlayCatalog.GameIconAsset("bound-kina")!, entry.GetProperty("icon").GetString());
            Assert.True(entry.GetProperty("iconLoaded").GetBoolean(), "The Bound Kina icon must decode in the production WebView.");
            if (kind == "bound-kina-hour") Assert.Equal("/h", entry.GetProperty("suffix").GetString());
        }
        var daily = values.RootElement.GetProperty("daily");
        Assert.Equal(snapshot.DailyProgress.Value, daily.GetProperty("value").GetString());
        Assert.Equal(language == "de" ? "Fortschritt Tages-Kina" : "Daily Kina progress", daily.GetProperty("label").GetString());
        Assert.Equal(76.754, daily.GetProperty("percent").GetDouble(), precision: 3);
        Assert.Equal(50, daily.GetProperty("start").GetDouble(), precision: 3);
        Assert.Equal(26.754, daily.GetProperty("contribution").GetDouble(), precision: 3);
    }
}
