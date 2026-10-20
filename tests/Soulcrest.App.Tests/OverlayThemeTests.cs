using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayThemeTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Theory]
    [InlineData("{}", "aion2-stylized")]
    [InlineData("{\"SettingsRevision\":5}", "aion2-stylized")]
    [InlineData("{\"OverlayTheme\":null}", "aion2-stylized")]
    [InlineData("{\"OverlayTheme\":\"unknown\"}", "aion2-stylized")]
    [InlineData("{\"OverlayTheme\":\"aion2\"}", "aion2-stylized")]
    [InlineData("{\"OverlayTheme\":\"aion2-stylized\"}", "aion2-stylized")]
    [InlineData("{\"OverlayTheme\":\"aion2-neutral\"}", "aion2-neutral")]
    [InlineData("{\"OverlayTheme\":\"obsidian\"}", "obsidian")]
    [InlineData("{\"OverlayTheme\":\"aurora\"}", "aurora")]
    [InlineData("{\"OverlayTheme\":\"runic\"}", "runic")]
    [InlineData("{\"OverlayTheme\":\"soulcrest-legacy\"}", "soulcrest-legacy")]
    public void ThemeDefaultsNormalizeAndEveryExplicitChoiceSurvivesRepeatedLoading(string json, string expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        var bytes = File.ReadAllBytes(AppPaths.SettingsFile);
        var service = new SettingsService();
        Assert.Equal(expected, service.Current.OverlayTheme);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SettingsFile));
        service.Update(_ => { });
        Assert.Equal(expected, new SettingsService().Current.OverlayTheme);
        Assert.Equal(expected, OverlayThemeDefinition.Resolve(service.Current.OverlayTheme).Id);
    }

    [Fact]
    public void ThemeSwitchWritesOnlyTheThemeAndDoesNotMigrateAnyLayoutOrPreference()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { SettingsRevision = 5 });
        var service = new SettingsService();
        service.Update(settings =>
        {
            settings.OverlayX = -1888; settings.OverlayY = -32; settings.OverlayScale = 1.37;
            settings.AlertNotificationsWidth = 777; settings.EventTimelineOverlayWidth = 1234;
            settings.TimerOverlayTimerIds = []; settings.BossOverlayVisible = false;
        });
        static string Rest(AppSettings settings)
        {
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.OverlayTheme));
            return node.ToJsonString();
        }
        var before = Rest(service.Current);
        var changes = 0; service.Changed += () => changes++;
        foreach (var theme in OverlayThemeDefinition.All)
        {
            service.Update(settings => settings.OverlayTheme = theme.Id);
            var loaded = new SettingsService().Current;
            Assert.Equal(theme.Id, loaded.OverlayTheme);
            Assert.Equal(before, Rest(loaded));
        }
        Assert.Equal(OverlayThemeDefinition.All.Count, changes);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void SevenOverlaysUseThemedProductionPreviewsWhileMerchantKeepsItsFixedStyle(string language)
    {
        UiText.Language = language;
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([
            new PetDefinition { Id = "sample", En = "A very long monster name for the preview", De = "Ein sehr langer Monstername für die Vorschau", Genus = "cogni" }
        ]));
        var settings = new AppSettings { UiLanguage = language, OverlayLanguage = language, TimerOverlayTimerIds = [] };
        foreach (var overlay in OverlayRegistry.All)
        {
            settings.OverlayTheme = "soulcrest-legacy";
            using var legacy = renderer.Render(overlay.Id, settings);
            var reference = Hash(legacy);
            foreach (var theme in OverlayThemeDefinition.All)
            {
                settings.OverlayTheme = theme.Id;
                var before = JsonSerializer.Serialize(settings);
                using var image = renderer.Render(overlay.Id, settings);
                Assert.Equal(before, JsonSerializer.Serialize(settings));
                Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
                if (overlay.Id == "merchant" || theme.IsLegacy) Assert.Equal(reference, Hash(image));
                else Assert.NotEqual(reference, Hash(image));
                if (!theme.IsLegacy && overlay.CanPlace && overlay.Id != "alerts")
                    Assert.True(Pixels(image).Any(pixel => pixel.A is >= 190 and < 250), overlay.Id + ": " + theme.Id);
            }
        }
    }

    public static IEnumerable<object[]> ThemeIds => OverlayThemeDefinition.All.Select(theme => new object[] { theme.Id });

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void OfflinePreviewSheetIncludesAllEightOverlaysOnLightAndDarkBackgrounds(string theme)
    {
        var mapdata = AppPaths.FindMapData();
        var catalogPath = mapdata is null ? null : Path.Combine(mapdata, "pets.json");
        var catalog = catalogPath is not null && File.Exists(catalogPath) ? PetCatalog.Load(catalogPath) : new PetCatalog([]);
        using var renderer = new OverlayPreviewRenderer(catalog, mapdata);
        var settings = new AppSettings { OverlayTheme = theme, BossOverlayCount = 5 };
        var output = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_PREVIEW_OUTPUT");
        using var sheet = new Bitmap(1600, 1800, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(45, 54, 62));
        using var light = new SolidBrush(Color.FromArgb(207, 207, 187));
        graphics.FillRectangle(light, 800, 0, 800, 1800);
        var y = 24;
        foreach (var descriptor in OverlayRegistry.All)
        {
            using var image = renderer.Render(descriptor.Id, settings, 760);
            var scale = Math.Min(1, 180d / image.Height);
            var rect = new Rectangle(24, y, (int)(image.Width * scale), (int)(image.Height * scale));
            graphics.DrawImage(image, rect);
            graphics.DrawImage(image, rect with { X = 824 });
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, theme + "-" + descriptor.Id + ".png"), ImageFormat.Png);
            }
            y += 220;
        }
        if (!string.IsNullOrWhiteSpace(output)) sheet.Save(Path.Combine(output, theme + "-sheet.png"), ImageFormat.Png);
    }

    [Theory]
    [InlineData(240, 0)]
    [InlineData(240, 20)]
    [InlineData(1000, 6)]
    public void NotificationGeometryContainsEveryPaintedRowAndKeepsLockOutsideText(int width, int count)
    {
        var layout = AlertsOverlayForm.NotificationLayout.Create(width, count, OverlayThemeDefinition.Aion2);
        Assert.True(new Rectangle(Point.Empty, layout.Size).Contains(layout.LockBounds));
        Assert.Equal(Math.Min(count, AlertsOverlayForm.MaximumRows), layout.Rows.Count);
        foreach (var row in layout.Rows)
        {
            Assert.True(new Rectangle(Point.Empty, layout.Size).Contains(row));
            Assert.False(row.IntersectsWith(layout.LockBounds));
        }
    }

    private static string Hash(Bitmap image)
    {
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static IEnumerable<Color> Pixels(Bitmap image)
    {
        for (var y = 4; y < image.Height; y += 4)
            for (var x = 4; x < image.Width; x += 4) yield return image.GetPixel(x, y);
    }
    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
