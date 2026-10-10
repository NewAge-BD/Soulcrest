using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossOverlayTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void ScheduleAndAlertRenderTheirIconsWithoutClipping(string language)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            var boss = new BossPlace(111001, 2400017, "Melted Danar", "Geschmolzener Danar", 100, 200);
            BossRushEntry[] rows = [new(new(1110, "altgard", boss), false, TimeSpan.FromSeconds(61), TimeSpan.Zero)];
            BossNotice[] alerts = [new(1, boss, true, TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow)];
            using var icon = new Bitmap(36, 36);
            using (var g = Graphics.FromImage(icon)) g.Clear(Color.Lime);
            using var bitmap = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(rows.Length, alerts.Length, true));
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(16, 20, 28));
                BossOverlayForm.PaintPanel(g, rows, alerts, true, true, language, _ => icon);
            }
            Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(40, BossOverlayForm.HeaderHeight + 32).ToArgb());
            Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(44, BossOverlayForm.HeaderHeight + BossOverlayForm.AlertStride + 46).ToArgb());
            Assert.True(bitmap.Height > BossOverlayForm.HeaderHeight + BossOverlayForm.AlertStride + BossOverlayForm.HeroStride);
            if (Environment.GetEnvironmentVariable("SOULCREST_BOSS_UI_PREVIEW") is { Length: > 0 } output
                && AppPaths.FindMapData() is { } data)
            {
                var catalogue = JsonSerializer.Deserialize<BossCatalogue>(File.ReadAllText(Path.Combine(data, "altgard", "bosses.json")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var places = catalogue.Bosses.Take(5).ToArray();
                var demo = places.Select((b, i) => new BossRushEntry(new(1110, "altgard", b), false,
                    TimeSpan.FromSeconds(61 + i * 160), TimeSpan.FromSeconds(2))).ToArray();
                var notice = new BossNotice(1, catalogue.Bosses[5], true, TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow);
                using var preview = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(5, 1, true));
                var images = new List<Image>();
                try
                {
                    using var g = Graphics.FromImage(preview);
                    g.Clear(Color.FromArgb(16, 20, 28));
                    BossOverlayForm.PaintPanel(g, demo, [notice], true, false, language, b =>
                    {
                        if (b.Icon is not { } path) return null;
                        var image = Image.FromFile(Path.Combine(data, path)); images.Add(image); return image;
                    });
                    Directory.CreateDirectory(output);
                    preview.Save(Path.Combine(output, "boss-overlay-" + language + ".png"), ImageFormat.Png);
                    using var cachedPreview = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(5, 0, true));
                    using var cachedGraphics = Graphics.FromImage(cachedPreview);
                    var cached = demo.Select((row, i) => row with { Cached = true, Remaining = i == 0 ? TimeSpan.Zero : row.Remaining }).ToArray();
                    BossOverlayForm.PaintPanel(cachedGraphics, cached, [], true, true, language, b =>
                    {
                        if (b.Icon is not { } path) return null;
                        var image = Image.FromFile(Path.Combine(data, path)); images.Add(image); return image;
                    });
                    cachedPreview.Save(Path.Combine(output, "boss-overlay-cached-" + language + ".png"), ImageFormat.Png);
                    using var alertPreview = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(0, 1, false));
                    using var alertGraphics = Graphics.FromImage(alertPreview);
                    BossOverlayForm.PaintPanel(alertGraphics, [], [notice], false, true, language, b =>
                    {
                        if (b.Icon is not { } path) return null;
                        var image = Image.FromFile(Path.Combine(data, path)); images.Add(image); return image;
                    });
                    alertPreview.Save(Path.Combine(output, "boss-overlay-alert-" + language + ".png"), ImageFormat.Png);
                }
                finally { foreach (var image in images) image.Dispose(); }
            }
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(12, 3, true, .6)]
    [InlineData(12, 3, true, 2.0)]
    [InlineData(0, 0, true, 1.0)]
    [InlineData(0, 3, false, 1.0)]
    public void OverlayReservesSpaceForEveryIconAtConfiguredDensity(int rowCount, int alertCount, bool list, double scale)
    {
        var boss = new BossPlace(111001, 2400017, "Long field boss name that must fit beside its countdown", null, 100, 200);
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, rowCount).Select(i => new BossRushEntry(new(1110, "altgard", boss), false,
            i == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(60 * i), TimeSpan.FromHours(1), Cached: true)).ToArray();
        var alerts = Enumerable.Range(0, alertCount).Select(i => new BossNotice(i + 1, boss, i == 0,
            TimeSpan.FromMinutes(1), now.AddMinutes(1), now)).ToArray();
        using var icon = new Bitmap(40, 40);
        using (var g = Graphics.FromImage(icon)) g.Clear(Color.Lime);
        using var bitmap = new Bitmap((int)Math.Ceiling(BossOverlayForm.PanelWidth * scale),
            (int)Math.Ceiling(BossOverlayForm.LogicalHeight(rowCount, alertCount, list) * scale));
        var rendered = 0;
        using (var g = Graphics.FromImage(bitmap))
        {
            g.ScaleTransform((float)scale, (float)scale);
            BossOverlayForm.PaintPanel(g, rows, alerts, list, false, "en", _ => { rendered++; return icon; });
        }
        Assert.Equal(alertCount + (list ? rowCount : 0), rendered);
        if (rowCount > 1 && list)
        {
            var lastIconY = BossOverlayForm.HeaderHeight + alertCount * BossOverlayForm.AlertStride
                + BossOverlayForm.HeroStride + (rowCount - 2) * BossOverlayForm.RowStride + 20;
            Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel((int)(57 * scale), (int)(lastIconY * scale)).ToArgb());
        }
    }

    [Fact]
    public void SavedScheduleShowsItsStatusWithoutTreatingAnExpiredCountdownAsConfirmed()
    {
        var boss = new BossPlace(111001, 2400017, "Melted Danar", null, 100, 200);
        var row = new BossRushEntry(new(1110, "altgard", boss), false, TimeSpan.Zero, TimeSpan.FromMinutes(10), Cached: true);
        using var bitmap = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(1, 0, true));
        using (var g = Graphics.FromImage(bitmap)) BossOverlayForm.PaintPanel(g, [row], [], true, true, "en", _ => null);
        var statusY = BossOverlayForm.HeaderHeight + BossOverlayForm.HeroStride + 13;
        Assert.Equal(Color.FromArgb(250, 204, 111).ToArgb(), bitmap.GetPixel(19, statusY).ToArgb());
        Assert.False(row.Spawned);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("de", false)]
    [InlineData("en", true)]
    [InlineData("en", false)]
    public void HotkeyHintsExplainHeldAltInteractionWithoutClipping(string language, bool locked)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            var hint = BossOverlayForm.HotkeyHint(locked);
            Assert.Equal(language == "de"
                ? locked ? "Schloss klicken zum Entsperren" : "Schloss: sperren · Kopf ziehen"
                : locked ? "Click the lock to unlock" : "Lock to pass clicks through · Drag header", hint);
            using var bitmap = new Bitmap(BossOverlayForm.PanelWidth, 50);
            using var g = Graphics.FromImage(bitmap);
            using var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
            Assert.True(g.MeasureString(hint, font).Width <= BossOverlayForm.PanelWidth - 34,
                "The held-Alt hint must fit without truncation.");
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(false, true, true, 1, false, true)]
    [InlineData(false, true, true, 0, false, false)]
    [InlineData(false, true, false, 1, false, false)]
    [InlineData(true, true, false, 0, true, true)]
    [InlineData(true, false, true, 1, false, true)]
    [InlineData(true, false, false, 0, false, false)]
    public void AlertsCanShowTheirPanelWithoutEnablingTheSpawnList(bool rush, bool overlay, bool alerts,
        int noticeCount, bool expectedList, bool expectedVisible)
    {
        var settings = new AppSettings { BossRushEnabled = rush, BossOverlayEnabled = overlay, BossAlertsEnabled = alerts };
        Assert.Equal(expectedList, BossOverlayForm.ScheduleEnabled(settings));
        Assert.Equal(expectedVisible, BossOverlayForm.PanelVisible(settings, noticeCount));
    }

    private sealed record BossCatalogue(int MapId, List<BossPlace> Bosses);

    [Fact]
    public void BossPreferencesSurviveRestartAndInvalidBoundsAreClamped()
    {
        var path = AppPaths.SettingsFile;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            var settings = new SettingsService();
            settings.Update(s =>
            {
                s.BossOverlayCount = 8; s.BossOverlayScale = 1.25; s.BossOverlayLocked = false;
                s.BossAlertIds = ["1110:111001", "1010:101001"]; s.BossAlertsEnabled = true;
                s.BossAlertLeadSeconds = 120; s.BossAlertDurationSeconds = 30; s.BossAlertSound = true;
            });
            var saved = new SettingsService().Current;
            Assert.Equal(8, saved.BossOverlayCount);
            Assert.Equal(1.25, saved.BossOverlayScale);
            Assert.False(saved.BossOverlayLocked);
            Assert.Equal(new[] { "1110:111001", "1010:101001" }, saved.BossAlertIds);
            Assert.Equal(120, saved.BossAlertLeadSeconds);
            Assert.Equal(30, saved.BossAlertDurationSeconds);
            Assert.True(saved.BossAlertsEnabled && saved.BossAlertSound);
            settings.Update(s => { s.BossOverlayCount = 99; s.BossOverlayScale = .1; s.BossAlertLeadSeconds = -1; s.BossAlertDurationSeconds = 99; });
            saved = new SettingsService().Current;
            Assert.Equal(12, saved.BossOverlayCount); Assert.Equal(.6, saved.BossOverlayScale);
            Assert.Equal(0, saved.BossAlertLeadSeconds); Assert.Equal(60, saved.BossAlertDurationSeconds);
        }
        finally { if (backup is null) File.Delete(path); else File.WriteAllText(path, backup); _ = new SettingsService(); }
    }
}
