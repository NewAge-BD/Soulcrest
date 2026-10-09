using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Optional offline visual QA of the live native renderer (SOULCREST_OVERLAY_PNG).
/// Does not create a capture, tracker, game window or write user settings/progress.
/// </summary>
[Collection("Settings file")]
public sealed class OverlayRenderExploration
{
    [Fact]
    public void RenderOverlay()
    {
        var target = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_PNG");
        if (string.IsNullOrEmpty(target)) return;
        var previousLanguage = UiText.Language;
        var images = new List<Image>();
        var portraits = new Dictionary<string, Image?>();
        try
        {
            var mapdata = AppPaths.FindMapData();
            var petsPath = mapdata is null ? null : Path.Combine(mapdata, "pets.json");
            var catalog = petsPath is not null && File.Exists(petsPath) ? PetCatalog.Load(petsPath) : null;
            Image? Icon(string id)
            {
                if (portraits.TryGetValue(id, out var cached)) return cached;
                if (mapdata is null || catalog?.Find(id)?.Icon is not { } icon) return null;
                var path = Path.Combine(mapdata, icon);
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                using var loaded = Image.FromStream(stream);
                var image = new Bitmap(loaded);
                images.Add(image);
                portraits[id] = image;
                return image;
            }
            string Name(string id, string language) => catalog?.Find(id)?.DisplayName(language) ?? id;
            PetOverlayForm.LootRow[] Focus(string language) =>
            [
                new(Name("kailin", language), "cogni", 1, false, 19, 25, Icon("kailin")),
            ];
            PetOverlayForm.LootRow[] Drops(string language) =>
            [
                new(Name("young-kailin", language), "cogni", 1, false, 12, 25, Icon("young-kailin"), Quantity: 1),
                new(Name("tayga", language), "fera", 2, false, 72, 75, Icon("tayga"), Quantity: 2, Alpha: 140),
            ];
            Render(target, Focus("en"), Drops("en"), "en", 1, true);
            var focus = Focus("de");
            var drops = Drops("de");
            Render(Suffix("de"), focus, drops, "de", 1, true);
            Render(Suffix("de-compact"), focus, drops, "de", .6, true);
            Render(Suffix("de-unlocked"), focus, drops, "de", 1, false);
            Render(Suffix("empty"), [], [], "en", 1, true);
            Render(Suffix("loot-only"), [], drops, "de", 1, true);
            Render(Suffix("focus-only"), focus, [], "de", 1, true);
            Render(Suffix("long-name"), [focus[0] with { Name = focus[0].Name + " · langer Prüftext für die Namenskürzung" }], drops, "de", 1, true);
            Render(Suffix("dense"), focus, drops.Concat(drops).Concat(drops.Take(1)).ToArray(), "de", 1, true);

            string Suffix(string suffix) => Path.Combine(Path.GetDirectoryName(target) ?? ".",
                Path.GetFileNameWithoutExtension(target) + "-" + suffix + ".png");
        }
        finally
        {
            UiText.Language = previousLanguage;
            foreach (var image in images) image.Dispose();
        }
    }

    private static void Render(string path, IReadOnlyList<PetOverlayForm.LootRow> focus,
        IReadOnlyList<PetOverlayForm.LootRow> drops, string language, double scale, bool locked)
    {
        UiText.Language = language;
        var height = PetOverlayForm.LootLogicalHeight(focus.Count, drops.Count);
        using var bitmap = new Bitmap((int)(360 * scale), (int)(height * scale), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.ScaleTransform((float)scale, (float)scale);
        PetOverlayForm.PaintLootPanel(graphics, focus, drops, running: focus.Count != 0, locked);
        bitmap.Save(path, ImageFormat.Png);
    }
}
