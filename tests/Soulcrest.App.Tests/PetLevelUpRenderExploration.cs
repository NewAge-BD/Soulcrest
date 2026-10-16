using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Pure bitmap checks plus optional offline animation frames (SOULCREST_LEVELUP_PREVIEW).
/// Never constructs a Form, services, a capture or a user-data store.
/// </summary>
[Collection("Settings file")]
public sealed class PetLevelUpRenderExploration
{
    private const int FrameCount = 300;
    private const double DurationSeconds = PetLevelUpAnimation.DurationSeconds;

    [Fact]
    public void OrdinaryLevelUpsAnimateExistingRowsWhileMaximumUsesItsOwnCard()
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = "en";
            var row = new PetOverlayForm.LevelUpRow("Offline preview pet", 2, false, null, .5, PetId: "preview-pet");
            var focus = new PetOverlayForm.LootRow("Offline preview pet", "fera", 2, false, 0, 75, PetId: "preview-pet");
            using var regular = Render([focus], [], row, "en", 1);
            using var entering = Render([focus], [], row with { Progress = .02 }, "en", 1);
            using var maximum = Render([focus with { Name = "Offline next preview pet", PetId = "preview-next-pet", Current = 3 }], [],
                row with { Level = SoulThresholds.Default.MaxLevel, IsMax = true }, "en", 1);
            using var fallbackOnly = Render([], [], row, "en", 1, focus);
            using var recentLevelOne = Render([], [focus with { Level = 1, Needed = 25 }], row with { Level = 1 }, "en", 1);
            Assert.Equal(PetOverlayForm.LootLogicalHeight(1, 0), regular.Height);
            Assert.Equal(PetOverlayForm.LootLogicalHeight(1, 0, celebrating: true), maximum.Height);
            Assert.True(maximum.Height > regular.Height, "Only MAX may add a separate celebration card.");
            Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 1), fallbackOnly.Height);
            Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 1), recentLevelOne.Height);
            Assert.True(HasInk(fallbackOnly), "An ordinary level-up without existing rows needs a visible recent-loot fallback.");
            Assert.True(HasInk(maximum), "The maximum-level banner must remain visible.");
            Assert.Equal(regular.Size, entering.Size);
            Assert.True(DifferentPixels(regular, entering), "Inline animation must change pixels without adding another card.");
        }
        finally { UiText.Language = previousLanguage; }
    }

    [Fact]
    public void RenderOptionalLevelUpAnimationFramesAndLayoutVariants()
    {
        var output = Environment.GetEnvironmentVariable("SOULCREST_LEVELUP_PREVIEW");
        if (string.IsNullOrWhiteSpace(output)) return;
        var previousLanguage = UiText.Language;
        var images = new List<Image>();
        var portraits = new Dictionary<string, Image>(StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(output);
            var directory = AppPaths.FindMapData();
            Assert.NotNull(directory);
            var catalog = PetCatalog.Load(Path.Combine(directory, "pets.json"));
            PetDefinition Pet(params string[] ids) => ids.Select(catalog.Find).OfType<PetDefinition>()
                .Concat(catalog.Pets).First(pet => pet.Icon is { Length: > 0 } icon && File.Exists(Path.Combine(directory, icon)));
            var levelPet = Pet("kailin", "swarm");
            var maxPet = Pet("tayga", "fungy-01");
            var dropPet = Pet("young-kailin", "contaminated-slime");
            Image Icon(PetDefinition pet)
            {
                if (portraits.TryGetValue(pet.Id, out var cached)) return cached;
                using var stream = File.OpenRead(Path.Combine(directory, pet.Icon!));
                using var loaded = Image.FromStream(stream);
                var image = new Bitmap(loaded);
                images.Add(image);
                portraits.Add(pet.Id, image);
                return image;
            }
            PetOverlayForm.LootRow[] Focus(string language, bool max)
            {
                // After MAX, the focus has already advanced to the next unfinished pet.
                return [new(levelPet.DisplayName(language), levelPet.Genus, 2, false,
                    max ? 3 : 0, 75, Icon(levelPet), Quantity: 3, SoulName: levelPet.DisplayName(language), PetId: levelPet.Id)];
            }
            PetOverlayForm.LootRow[] Drops(string language, bool max)
            {
                var recent = new PetOverlayForm.LootRow(dropPet.DisplayName(language), dropPet.Genus, 1, false, 12, 25, Icon(dropPet),
                    Quantity: 1, SoulName: dropPet.DisplayName(language), PetId: dropPet.Id);
                if (max) return [recent]; // The completed pet appears only in the separate MAX banner.
                return
                [
                    recent,
                    new(maxPet.DisplayName(language), maxPet.Genus, 2, false, 72, 75, Icon(maxPet),
                        Quantity: 2, Alpha: 140, SoulName: maxPet.DisplayName(language), PetId: maxPet.Id),
                ];
            }
            PetOverlayForm.LevelUpRow Celebration(string language, bool max, double progress)
            {
                var pet = max ? maxPet : levelPet;
                return new(pet.DisplayName(language), max ? SoulThresholds.Default.MaxLevel : 2, max, Icon(pet), progress, PetId: pet.Id);
            }

            var files = new List<object>();
            for (var index = 0; index < FrameCount; index++)
            {
                var progress = index / (double)(FrameCount - 1);
                foreach (var max in new[] { false, true })
                {
                    var file = $"{(max ? "max" : "level-up")}-frame-{index:D3}.png";
                    Save(file, Focus("en", max), Drops("en", max), Celebration("en", max, progress), "en", 1);
                    files.Add(new { File = file, Kind = max ? "max" : "level-up", Frame = index,
                        Progress = progress, TimeSeconds = progress * DurationSeconds });
                }
            }
            foreach (var language in new[] { "en", "de" })
            foreach (var scale in new[] { .6, 1, 2.5 })
            foreach (var kind in new[] { "level-up", "level-one-recent", "max" })
            {
                var max = kind == "max";
                var suffix = $"{language}-{kind}-scale-{scale.ToString("0.0", CultureInfo.InvariantCulture)}";
                var row = kind == "level-one-recent"
                    ? new PetOverlayForm.LevelUpRow(dropPet.DisplayName(language), 1, false, Icon(dropPet), .5, PetId: dropPet.Id)
                    : Celebration(language, max, .5);
                var focus = Focus(language, max);
                var drops = Drops(language, max).Select(drop => kind == "level-one-recent" && drop.PetId == dropPet.Id
                    ? drop with { Level = 1, Current = 0, Needed = 25, Quantity = 5 } : drop).ToArray();
                Save(suffix + ".png", focus, drops, row, language, scale);
                Save(suffix + "-no-focus-fallback.png", [], [], row, language, scale);
                var text = language == "de" ? " · sehr langer synthetischer Prüftext für die Namenskürzung"
                    : " · deliberately long synthetic text to inspect name truncation";
                var longRow = row with { Name = row.Name + text };
                PetOverlayForm.LootRow LongName(PetOverlayForm.LootRow loot) => loot.PetId == row.PetId
                    ? loot with { Name = longRow.Name, SoulName = longRow.Name } : loot;
                Save(suffix + "-long-name.png", focus.Select(LongName).ToArray(), drops.Select(LongName).ToArray(), longRow, language, scale);
                files.Add(new { File = suffix + ".png", Language = language, Scale = scale, Kind = kind });
                files.Add(new { File = suffix + "-no-focus-fallback.png", Language = language, Scale = scale, Kind = kind });
                files.Add(new { File = suffix + "-long-name.png", Language = language, Scale = scale, Kind = kind });
            }
            File.WriteAllText(Path.Combine(output, "level-up-preview.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                DurationSeconds,
                FramesPerAnimation = FrameCount,
                SuggestedPlaybackFps = FrameCount / DurationSeconds,
                SyntheticProgress = true,
                PetIconsFromOfflineCatalog = true,
                LevelPetId = levelPet.Id,
                RecentLevelOnePetId = dropPet.Id,
                MaximumPetId = maxPet.Id,
                Note = "Production placement and renderer. Level 2 animates its focus row, level 1 its recent-loot row, MAX its own banner without a MAX recent-loot row. Example values and long-name suffixes are synthetic. No application or game capture is started.",
                Files = files,
            }, new JsonSerializerOptions { WriteIndented = true }));

            void Save(string file, IReadOnlyList<PetOverlayForm.LootRow> focus,
                IReadOnlyList<PetOverlayForm.LootRow> drops, PetOverlayForm.LevelUpRow row, string language, double scale)
            {
                var pet = row.PetId is { } petId ? catalog.Find(petId) : null;
                var needed = row.IsMax ? 0 : SoulThresholds.Default.Steps[row.Level];
                var fallback = new PetOverlayForm.LootRow(row.Name, pet?.Genus ?? "unknown", row.Level, row.IsMax,
                    0, needed, row.Icon, SoulName: row.Name, PetId: row.PetId);
                using var bitmap = Render(focus, drops, row, language, scale, fallback);
                bitmap.Save(Path.Combine(output, file), ImageFormat.Png);
            }
        }
        finally
        {
            UiText.Language = previousLanguage;
            foreach (var image in images) image.Dispose();
        }
    }

    private static Bitmap Render(IReadOnlyList<PetOverlayForm.LootRow> focus,
        IReadOnlyList<PetOverlayForm.LootRow> drops, PetOverlayForm.LevelUpRow row, string language, double scale,
        PetOverlayForm.LootRow? fallback = null)
    {
        UiText.Language = language;
        fallback ??= new(row.Name, "unknown", row.Level, row.IsMax, 0,
            row.IsMax ? 0 : SoulThresholds.Default.Steps[row.Level], row.Icon, SoulName: row.Name, PetId: row.PetId);
        var placed = PetOverlayForm.PlaceLevelUpRows(focus, drops, row, fallback);
        var height = PetOverlayForm.LootLogicalHeight(placed.Focus.Count, placed.Drops.Count, celebrating: row.IsMax);
        var bitmap = new Bitmap((int)Math.Ceiling(360 * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)scale, (float)scale);
            PetOverlayForm.PaintLootPanel(graphics, placed.Focus, placed.Drops, running: true, locked: true, levelUp: row);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static bool HasInk(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A != 0) return true;
        return false;
    }

    private static bool DifferentPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return true;
        for (var y = 0; y < first.Height; y++)
            for (var x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return true;
        return false;
    }
}
