using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure production-renderer checks; no shown windows, trackers or generated progress events.</summary>
[Collection("Settings file")]
public sealed class MultiThemeTests : IDisposable
{
    private readonly string _language = UiText.Language;

    public MultiThemeTests() => UiText.Language = "en";

    [Fact]
    public void AllSixCanonicalIdsAreStableAndTheFormerAion2IdRemainsAnAlias()
    {
        Assert.Equal(new[] { "aion2-stylized", "aion2-neutral", "obsidian", "aurora", "runic", "soulcrest-legacy" },
            OverlayThemeDefinition.All.Select(theme => theme.Id));
        Assert.Equal("aion2-stylized", OverlayThemeDefinition.DefaultId);
        Assert.Equal(OverlayThemeDefinition.DefaultId, new AppSettings().OverlayTheme);
        Assert.Same(OverlayThemeDefinition.Aion2, OverlayThemeDefinition.Resolve("aion2"));
        Assert.Equal(OverlayThemeDefinition.DefaultId, OverlayThemeDefinition.NormalizeId("aion2"));
        Assert.True(OverlayThemeDefinition.IsKnown("aion2"));
        Assert.False(OverlayThemeDefinition.IsKnown("missing-theme"));
        foreach (var id in new string?[] { null, "", "missing-theme" })
        {
            Assert.Equal(OverlayThemeDefinition.DefaultId, OverlayThemeDefinition.NormalizeId(id));
            Assert.Same(OverlayThemeDefinition.Aion2, OverlayThemeDefinition.Resolve(id));
        }
        Assert.Single(OverlayThemeDefinition.All, theme => theme.IsLegacy);
        foreach (var theme in OverlayThemeDefinition.All)
        {
            Assert.True(OverlayThemeDefinition.IsKnown(theme.Id));
            Assert.Equal(theme.Id, OverlayThemeDefinition.NormalizeId(theme.Id));
            Assert.Same(theme, OverlayThemeDefinition.Resolve(theme.Id));
            Assert.False(string.IsNullOrWhiteSpace(theme.Label));
        }
    }

    [Theory]
    [InlineData("loot")]
    [InlineData("boss")]
    [InlineData("leveling")]
    [InlineData("notifications")]
    [InlineData("event-timeline")]
    public void Aion2VariantsChangeOnlyTheOuterEdgesOfTheSameProductionContent(string overlay)
    {
        using var renderer = Renderer();
        var settings = new AppSettings { OverlayTheme = "aion2-neutral", BossOverlayCount = 5 };
        using var neutral = renderer.Render(overlay, settings);
        settings.OverlayTheme = OverlayThemeDefinition.DefaultId;
        var before = JsonSerializer.Serialize(settings);
        using var stylized = renderer.Render(overlay, settings);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(neutral.Size, stylized.Size);
        Assert.NotEqual(Hash(neutral), Hash(stylized));
        var interior = Rectangle.Inflate(new Rectangle(Point.Empty, neutral.Size), -16, -16);
        Assert.True(interior.Width > 0 && interior.Height > 0);
        Assert.Equal(Pixels(neutral, interior), Pixels(stylized, interior));
    }

    [Fact]
    public void Aion2VariantsShareAllContentTokensAndHaveDistinctPanelFrames()
    {
        var neutral = OverlayThemeDefinition.Aion2Neutral;
        var stylized = OverlayThemeDefinition.Aion2;
        Assert.Equal(neutral.Surface, stylized.Surface);
        Assert.Equal(neutral.Border, stylized.Border);
        Assert.Equal(neutral.Primary, stylized.Primary);
        Assert.Equal(neutral.Secondary, stylized.Secondary);
        Assert.Equal(neutral.Gold, stylized.Gold);
        Assert.Equal(neutral.Mint, stylized.Mint);
        using var first = Panel(neutral);
        using var second = Panel(stylized);
        Assert.NotEqual(Hash(first), Hash(second));
        Assert.Equal(Pixels(first, new(16, 16, 128, 68)), Pixels(second, new(16, 16, 128, 68)));
    }

    [Theory]
    [InlineData("loot")]
    [InlineData("boss")]
    [InlineData("leveling")]
    [InlineData("notifications")]
    [InlineData("event-timeline")]
    public void ThreeAdditionalThemesHaveDistinctMaterialsAndProductionPixels(string overlay)
    {
        using var renderer = Renderer();
        var settings = new AppSettings { BossOverlayCount = 5 };
        var ids = new[] { OverlayThemeDefinition.DefaultId, "obsidian", "aurora", "runic" };
        var hashes = new List<string>();
        var surfaces = new HashSet<int>();
        foreach (var id in ids)
        {
            settings.OverlayTheme = id;
            var before = JsonSerializer.Serialize(settings);
            using var image = renderer.Render(overlay, settings);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.Equal(before, JsonSerializer.Serialize(settings));
            hashes.Add(Hash(image));
            surfaces.Add(OverlayThemeDefinition.Resolve(id).Surface.ToArgb());
        }
        Assert.Equal(ids.Length, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.Length, surfaces.Count);
    }

    private static OverlayPreviewRenderer Renderer() => new(new PetCatalog([
        new PetDefinition { Id = "theme-preview", En = "Preview monster", De = "Vorschaumonster", Genus = "cogni" }
    ]));

    private static Bitmap Panel(OverlayThemeDefinition theme)
    {
        var image = new Bitmap(160, 100, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        theme.Panel(graphics, new Rectangle(Point.Empty, image.Size));
        return image;
    }

    private static string Hash(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static int[] Pixels(Bitmap image, Rectangle area)
    {
        var result = new int[area.Width * area.Height];
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++) result[y * area.Width + x] = image.GetPixel(area.Left + x, area.Top + y).ToArgb();
        return result;
    }

    public void Dispose() => UiText.Language = _language;
}
