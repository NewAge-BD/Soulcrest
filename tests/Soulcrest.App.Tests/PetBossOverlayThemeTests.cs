using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetBossOverlayThemeTests
{
    private static PetOverlayForm.LootRow Pet(string name = "WWWW preview pet") =>
        new(name, "cogni", 1, false, 17, 25, Quantity: 2, SoulName: "Preview soul");

    private static readonly BossPlace Boss = new(111001, 2400017, "WWWW preview boss", "WWWW Vorschauboss", 100, 200);
    private static BossRushEntry Row(int index = 0) => new(new(1110, "altgard", Boss), false,
        TimeSpan.FromSeconds(61 + index * 90), TimeSpan.Zero, Cached: index == 1);

    [Theory]
    [InlineData("de", .6)]
    [InlineData("en", 1)]
    [InlineData("en", 2.5)]
    public void PetQuestThemeHasItsOwnGeometryWithTransparentSurfaceAndOpaqueText(string language, double scale)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            PetOverlayForm.LootRow[] focus = [Pet()];
            PetOverlayForm.LootRow[] loot = [Pet("Second preview pet"), Pet("Third preview pet")];
            using var image = PetOverlayForm.RenderLootBitmap(focus, loot, true, true, null, OverlayThemeDefinition.Aion2, scale);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.Equal((int)Math.Round(360 * scale), image.Width);
            Assert.Equal((int)Math.Round(PetOverlayForm.LootLogicalHeight(1, 2, theme: OverlayThemeDefinition.Aion2) * scale), image.Height);
            Assert.NotEqual(PetOverlayForm.LootLogicalHeight(1, 2), PetOverlayForm.LootLogicalHeight(1, 2, theme: OverlayThemeDefinition.Aion2));
            Assert.Equal(204, image.GetPixel((int)(8 * scale), (int)(80 * scale)).A);
            if (scale == 1) Assert.True(OpaqueText(image, new(54, 61, 225, 17), OverlayThemeDefinition.Aion2.Primary));
            Save(image, $"pet-aion2-{language}-{scale}.png");
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PetUpgradesRemainInTheirExistingRowsAndMaximumCardForTheQuestTheme(int level)
    {
        var frame = new PetOverlayForm.LevelUpRow("Preview pet", level, level == 3, null, .5, "preview");
        PetOverlayForm.LootRow[] focus = level == 3 ? [] : [Pet() with { LevelUp = frame }];
        using var image = PetOverlayForm.RenderLootBitmap(focus, [], true, false, frame, OverlayThemeDefinition.Aion2);
        Assert.Equal(PetOverlayForm.LootLogicalHeight(focus.Length, 0, level == 3, OverlayThemeDefinition.Aion2), image.Height);
        Assert.Equal(204, image.GetPixel(3, Math.Min(80, image.Height - 4)).A);
        Assert.True(image.Width > 0);
        Save(image, $"pet-aion2-upgrade-{level}.png");
    }

    [Theory]
    [InlineData("de", 1, 0, true, .6)]
    [InlineData("en", 12, 3, true, 1)]
    [InlineData("en", 0, 2, false, 2)]
    public void BossQuestThemeUsesOneCompactRowPerSpawnAndKeepsEveryPortrait(string language,
        int rowCount, int alertCount, bool list, double scale)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var rows = Enumerable.Range(0, rowCount).Select(Row).ToArray();
            var now = DateTimeOffset.UtcNow;
            var alerts = Enumerable.Range(0, alertCount).Select(index => new BossNotice(index, Boss, index == 0,
                TimeSpan.FromSeconds(50), now.AddMinutes(1), now)).ToArray();
            using var portrait = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(portrait)) graphics.Clear(Color.Lime);
            var painted = 0;
            using var image = BossOverlayForm.RenderBitmap(rows, alerts, list, true, language,
                _ => { painted++; return portrait; }, OverlayThemeDefinition.Aion2, scale);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.Equal(alertCount + (list ? rowCount : 0), painted);
            Assert.Equal((int)(BossOverlayForm.LogicalHeight(rowCount, alertCount, list, OverlayThemeDefinition.Aion2) * scale), image.Height);
            Assert.NotEqual(BossOverlayForm.LogicalHeight(rowCount, alertCount, list), BossOverlayForm.LogicalHeight(rowCount, alertCount, list, OverlayThemeDefinition.Aion2));
            Assert.Equal(204, image.GetPixel((int)(8 * scale), (int)(45 * scale)).A);
            if (rowCount > 0 && list)
            {
                var top = 32 + alertCount * 56 + (rowCount - 1) * 48;
                Assert.Equal(Color.Lime.ToArgb(), image.GetPixel((int)(30 * scale), (int)((top + 20) * scale)).ToArgb());
            }
            Save(image, $"boss-aion2-{language}-{rowCount}-{alertCount}-{scale}.png");
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task PresenterModeChangesBeforeFirstShowNeverCreateANativeHandle()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new System.Windows.Forms.Form();
                var handles = 0;
                form.HandleCreated += (_, _) => handles++;
                LayeredOverlayPresenter.Configure(form, false, .94);
                Assert.Equal(.94, form.Opacity);
                Assert.False(form.IsHandleCreated);
                LayeredOverlayPresenter.Configure(form, true, .94);
                Assert.Equal(1, form.Opacity);
                Assert.False(form.IsHandleCreated);
                LayeredOverlayPresenter.Configure(form, false, .94);
                LayeredOverlayPresenter.Configure(form, true, .94);
                Assert.False(form.IsHandleCreated);
                Assert.Equal(0, handles);
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PetPortraitKeepsItsGenusColorAndBossCountdownKeepsItsWarningColor()
    {
        using var portrait = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(portrait)) graphics.Clear(Color.Gray);
        using var cogni = PetOverlayForm.RenderLootBitmap([Pet() with { Icon = portrait, Genus = "cogni" }], [],
            true, true, null, OverlayThemeDefinition.Aion2);
        using var fera = PetOverlayForm.RenderLootBitmap([Pet() with { Icon = portrait, Genus = "fera" }], [],
            true, true, null, OverlayThemeDefinition.Aion2);
        Assert.NotEqual(cogni.GetPixel(12, 80).ToArgb(), fera.GetPixel(12, 80).ToArgb());
        using var countdown = BossOverlayForm.RenderBitmap([Row()], [], true, true, "en", _ => null, OverlayThemeDefinition.Aion2);
        Assert.True(InkNear(countdown, new(267, 44, 117, 26), Color.FromArgb(248, 113, 113)));
        using var pending = BossOverlayForm.RenderBitmap([Row() with { Remaining = TimeSpan.Zero }], [], true, true,
            "en", _ => null, OverlayThemeDefinition.Aion2);
        Assert.True(InkNear(pending, new(267, 50, 117, 20), Color.FromArgb(250, 204, 111)));
    }

    [Fact]
    public void SharedMeasurementsKeepLockAndHeaderInsideTheActualPanelGeometry()
    {
        foreach (var theme in new[] { OverlayThemeDefinition.Legacy, OverlayThemeDefinition.Aion2 })
        {
            var pet = PetOverlayForm.Measure(1, 5, true, theme);
            var boss = BossOverlayForm.Measure(12, 3, true, theme);
            Assert.Equal(PetOverlayForm.LootLogicalHeight(1, 5, true, theme), pet.Size.Height);
            Assert.True(pet.HeaderBounds.Contains(pet.LockBounds));
            Assert.True(pet.DragBounds.Contains(pet.LockBounds));
            Assert.Equal(BossOverlayForm.LogicalHeight(12, 3, true, theme), boss.Size.Height);
            Assert.True(boss.HeaderBounds.Contains(boss.LockBounds));
            Assert.True(boss.DragBounds.Contains(boss.LockBounds));
        }
    }

    [Fact]
    public void ExplicitLegacyPainterIsPixelIdenticalToTheExistingDefaultPainter()
    {
        PetOverlayForm.LootRow[] pets = [Pet()];
        using var defaultPet = new Bitmap(360, PetOverlayForm.LootLogicalHeight(1, 0), PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(defaultPet)) PetOverlayForm.PaintLootPanel(graphics, pets, [], true, false);
        using var legacyPet = PetOverlayForm.RenderLootBitmap(pets, [], true, false, null, OverlayThemeDefinition.Legacy);
        Assert.Equal(Pixels(defaultPet), Pixels(legacyPet));
        BossRushEntry[] rows = [Row(), Row(1)];
        using var defaultBoss = new Bitmap(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(2, 0, true), PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(defaultBoss)) BossOverlayForm.PaintPanel(graphics, rows, [], true, false, "en", _ => null);
        using var legacyBoss = BossOverlayForm.RenderBitmap(rows, [], true, false, "en", _ => null, OverlayThemeDefinition.Legacy);
        Assert.Equal(Pixels(defaultBoss), Pixels(legacyBoss));
        Save(legacyPet, "pet-legacy.png");
        Save(legacyBoss, "boss-legacy.png");
    }

    private static bool OpaqueText(Bitmap image, Rectangle area, Color color)
    {
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
                if (image.GetPixel(x, y).ToArgb() == color.ToArgb()) return true;
        return false;
    }

    private static bool InkNear(Bitmap image, Rectangle area, Color color)
    {
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.A >= 220 && Math.Abs(pixel.R - color.R) <= 35 && Math.Abs(pixel.G - color.G) <= 35
                    && Math.Abs(pixel.B - color.B) <= 35) return true;
            }
        return false;
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++) result[y * image.Width + x] = image.GetPixel(x, y).ToArgb();
        return result;
    }

    private static void Save(Bitmap image, string name)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_THEME_OUTPUT") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, name), ImageFormat.Png);
    }
}
