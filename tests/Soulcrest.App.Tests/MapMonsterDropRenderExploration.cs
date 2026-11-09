using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in offline verification and native production-sprite preview using a complete local map package.</summary>
public sealed class MapMonsterDropRenderExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderNativeMaterials()
    {
        var mapdata = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_MONSTER_DROP_MAPDATA"));
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_MONSTER_DROP_NATIVE_PNG"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapdata, "manifest.json")));
        var mapIds = manifest.RootElement.GetProperty("maps").EnumerateArray().Select(map => map.GetProperty("id").GetString()).ToArray();
        string[] expected = ["poeta", "verteron", "elthen", "ishalgen", "altgard", "morheim", "abyss-reshanta-a", "chaotic-middle-reshanta", "chaotic-upper-reshanta"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), mapIds.Order(StringComparer.Ordinal));
        var materials = new List<MapMonsterDropItem>();
        foreach (var map in expected)
        {
            var catalog = MapPetMarkers.MonsterDropCatalogFor(mapdata, map);
            Assert.True(catalog.Available, "Missing or invalid monster-drop contract on " + map);
            materials.AddRange(catalog.Items);
        }
        var items = materials.Where(item => item.Icon is not null).DistinctBy(item => item.Id)
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        Assert.True(items.Length >= 5, "The explicit local package must contain at least five material icons.");
        var icons = new List<Bitmap>();
        try
        {
            foreach (var item in items.Take(5))
            {
                var path = Path.GetFullPath(Path.Combine(mapdata, item.Icon!));
                Assert.StartsWith(mapdata.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                using var stream = File.OpenRead(path);
                using var source = Image.FromStream(stream);
                icons.Add(new Bitmap(source));
            }
            using var output = new Bitmap(360, 432, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(output);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var counts = new[] { 1, 2, 3, 5 };
            for (var row = 0; row < counts.Length; row++)
            {
                var count = counts[row];
                var group = new RouteOverlayForm.MonsterDropIcons(icons.Take(Math.Min(3, count)).ToArray(), Math.Max(0, count - 3),
                    mapdata + "/native-preview/" + string.Join("/", items.Take(count).Select(item => item.Id)));
                var sprite = RouteOverlayForm.MonsterDropSprite(group, OverlayThemeDefinition.Legacy);
                graphics.DrawImage(sprite, new Rectangle((output.Width - sprite.Width * 3) / 2, 24 + row * 102, sprite.Width * 3, sprite.Height * 3),
                    0, 0, sprite.Width, sprite.Height, GraphicsUnit.Pixel);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            output.Save(target, ImageFormat.Png);
        }
        finally
        {
            foreach (var icon in icons) icon.Dispose();
        }
    }
}
