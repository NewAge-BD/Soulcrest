using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure production-renderer regression checks; no forms, progress services or captures.</summary>
[Collection("Settings file")]
public sealed class PetLevelUpThemeAnimationTests
{
    private static readonly string[] ThemeIds =
        ["aion2-stylized", "aion2-neutral", "obsidian", "aurora", "runic"];

    public static IEnumerable<object[]> InlineCases()
    {
        foreach (var theme in ThemeIds)
        foreach (var level in new[] { 1, 2 })
        foreach (var focus in new[] { true, false })
            yield return [theme, level, focus];
    }

    public static IEnumerable<object[]> Themes() => ThemeIds.Select(theme => new object[] { theme });

    public static IEnumerable<object[]> ScaledLanguageCases()
    {
        foreach (var theme in ThemeIds)
        foreach (var language in new[] { "de", "en" })
        foreach (var scale in new[] { .6, 2.5 })
            yield return [theme, language, scale];
    }

    [Theory]
    [MemberData(nameof(InlineCases))]
    public void OrdinaryUpgradesAnimateTheirRowSurfaceAndPortraitWithoutMovingOtherContent(
        string themeId, int level, bool focused)
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = "en";
            var theme = OverlayThemeDefinition.Resolve(themeId);
            var pet = Pet("Celebrating pet", "celebrating", level);
            var neighbor = Pet("Other pet", "other", 1);
            PetOverlayForm.LootRow[] focus = focused ? [pet] : [neighbor];
            PetOverlayForm.LootRow[] drops = focused ? [neighbor] : [pet];
            var layout = PetOverlayForm.Measure(1, 1, theme: theme);
            var top = layout.ContentTop + layout.SectionHeight;
            if (!focused) top += layout.RowHeight + 6 + layout.SectionHeight;
            var frame = new PetOverlayForm.LevelUpRow(pet.Name, level, false, null, .015, pet.PetId);

            using var entering = Render(focus, drops, frame, theme);
            using var sweeping = Render(focus, drops, frame with { Progress = .045 }, theme);
            using var held = Render(focus, drops, frame with { Progress = .5 }, theme);
            using var leaving = Render(focus, drops, frame with { Progress = .99 }, theme);
            using var first = Render(focus, drops, frame with { Progress = 0 }, theme);
            using var last = Render(focus, drops, frame with { Progress = 1 }, theme);

            Assert.Equal(layout.Size, entering.Size);
            Assert.Equal(entering.Size, sweeping.Size);
            Assert.Equal(entering.Size, held.Size);
            Assert.Equal(entering.Size, leaving.Size);
            // Text and the small level-arrow diamond are outside these independently checked regions.
            Assert.True(DifferentPixels(entering, sweeping, new(190, top + 24, 90, 10)) > 10,
                "The introductory light sweep must visibly cross the row surface.");
            // Left of the portrait, sweep, rail and text: only the expanding halo can change here.
            Assert.True(DifferentPixels(entering, sweeping, new(9, top + 14, 3, 18)) > 2,
                "The portrait halo must pulse, not just the level-up label.");
            Assert.True(DifferentPixels(held, leaving, new(190, top + 24, 90, 10)) > 10,
                "The persistent row highlight must fade out at the original deadline.");
            AssertOnlyChangesInside(entering, sweeping, new(4, top, 352, layout.RowHeight));
            AssertOnlyChangesInside(held, leaving, new(4, top, 352, layout.RowHeight));
            Assert.Equal(0, DifferentPixels(first, last, new(Point.Empty, first.Size)));
            Assert.Null(pet.LevelUp);
            Assert.Equal(4, pet.Current);

            if (focused && level == 2)
            {
                Save(entering, $"{themeId}-level-up-entering.png");
                Save(sweeping, $"{themeId}-level-up-sweep.png");
                Save(held, $"{themeId}-level-up-held.png");
            }
        }
        finally { UiText.Language = previousLanguage; }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void MaximumUsesAnAnimatedSeparateCardAndLeavesFocusAndLootUntouched(string themeId)
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = "en";
            var theme = OverlayThemeDefinition.Resolve(themeId);
            PetOverlayForm.LootRow[] focus = [Pet("Next focus pet", "next", 2)];
            PetOverlayForm.LootRow[] drops = [Pet("Other pet", "other", 1)];
            var frame = new PetOverlayForm.LevelUpRow("Completed pet", 3, true, null, .015, "completed");
            var layout = PetOverlayForm.Measure(1, 1, true, theme);
            var top = layout.ContentTop;

            using var entering = Render(focus, drops, frame, theme);
            using var sweeping = Render(focus, drops, frame with { Progress = .045 }, theme);
            using var held = Render(focus, drops, frame with { Progress = .5 }, theme);
            using var leaving = Render(focus, drops, frame with { Progress = .99 }, theme);
            using var first = Render(focus, drops, frame with { Progress = 0 }, theme);
            using var last = Render(focus, drops, frame with { Progress = 1 }, theme);

            Assert.Equal(layout.Size, entering.Size);
            Assert.Equal(entering.Size, sweeping.Size);
            Assert.Equal(entering.Size, leaving.Size);
            Assert.Equal(PetOverlayForm.Measure(1, 1, theme: theme).Size.Height + layout.CelebrationHeight,
                entering.Height);
            Assert.True(DifferentPixels(entering, sweeping, new(230, top + 29, 90, 13)) > 10,
                "The MAX card must animate its surface as well as fade its text.");
            Assert.True(DifferentPixels(entering, sweeping, new(9, top + 14, 3, 18)) > 2,
                "The MAX portrait must receive the introductory pulse.");
            Assert.True(DifferentPixels(held, leaving, new(230, top + 29, 90, 13)) > 10);
            AssertOnlyChangesInside(entering, sweeping, new(4, top, 352, layout.CelebrationHeight));
            AssertOnlyChangesInside(held, leaving, new(4, top, 352, layout.CelebrationHeight));
            Assert.Equal(0, DifferentPixels(first, last, new(Point.Empty, first.Size)));
            Assert.All(focus.Concat(drops), pet => Assert.Null(pet.LevelUp));

            Save(sweeping, $"{themeId}-maximum-sweep.png");
            Save(held, $"{themeId}-maximum-held.png");
        }
        finally { UiText.Language = previousLanguage; }
    }

    [Theory]
    [MemberData(nameof(ScaledLanguageCases))]
    public void AnimatedRowsAndMaximumStayInsideMeasuredGeometryWithLongNamesAtScaleLimits(
        string themeId, string language, double scale)
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = language;
            var theme = OverlayThemeDefinition.Resolve(themeId);
            var name = language == "de" ? "Sehr langer synthetischer Petname für die Namenskürzung"
                : "Deliberately long synthetic pet name for name truncation";
            PetOverlayForm.LootRow[] focus = [Pet(name, "active", 2)];
            PetOverlayForm.LootRow[] drops = [Pet("Other pet", "other", 1)];
            foreach (var maximum in new[] { false, true })
            {
                var frame = new PetOverlayForm.LevelUpRow(name, maximum ? 3 : 2, maximum, null, .015,
                    maximum ? "completed" : "active");
                using var first = Render(focus, drops, frame, theme, scale);
                using var second = Render(focus, drops, frame with { Progress = .045 }, theme, scale);
                var layout = PetOverlayForm.Measure(1, 1, maximum, theme);
                Assert.Equal((int)Math.Round(layout.Size.Width * scale), first.Width);
                Assert.Equal((int)Math.Round(layout.Size.Height * scale), first.Height);
                Assert.Equal(first.Size, second.Size);
                var top = layout.ContentTop + (maximum ? 0 : layout.SectionHeight);
                var height = maximum ? layout.CelebrationHeight : layout.RowHeight;
                Assert.True(DifferentPixels(first, second, Scale(new(5, top + 3, 46, 39), scale)) > 2,
                    "The animation must remain visible at both supported scale limits.");
                AssertOnlyChangesInside(first, second, Scale(new(3, top, 354, height), scale));
                Save(second, $"{themeId}-{language}-{(maximum ? "maximum" : "level-up")}-{scale:0.0}.png");
            }
        }
        finally { UiText.Language = previousLanguage; }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryCelebrationKeepsSweepingAndPulsingUntilItsFinalFade(string themeId)
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = "en";
            var theme = OverlayThemeDefinition.Resolve(themeId);
            foreach (var level in new[] { 1, 2, 3 })
            {
                var maximum = level == 3;
                PetOverlayForm.LootRow[] focus = [Pet("Focus pet", maximum ? "next" : "active", maximum ? 2 : level)];
                PetOverlayForm.LootRow[] drops = [Pet("Other pet", "other", 1)];
                var frame = new PetOverlayForm.LevelUpRow("Completed pet", level, maximum, null, 0,
                    maximum ? "completed" : "active");
                var layout = PetOverlayForm.Measure(1, 1, maximum, theme);
                var top = layout.ContentTop + (maximum ? 0 : layout.SectionHeight);
                var height = maximum ? layout.CelebrationHeight : layout.RowHeight;

                // Both pairs occur long after the introduction, while opacity is still constant.
                // Surface and isolated left-side halo regions distinguish looping from a static tint.
                foreach (var (firstSeconds, secondSeconds) in new[] { (14.7, 15.6), (27.0, 27.9) })
                {
                    using var first = Render(focus, drops,
                        frame with { Progress = firstSeconds / PetLevelUpAnimation.DurationSeconds }, theme);
                    using var second = Render(focus, drops,
                        frame with { Progress = secondSeconds / PetLevelUpAnimation.DurationSeconds }, theme);
                    Assert.Equal(layout.Size, first.Size);
                    Assert.Equal(first.Size, second.Size);
                    Assert.True(DifferentPixels(first, second, new(230, top + 24, 100, 10)) > 10,
                        $"Level {level} must still sweep across its surface at {firstSeconds} seconds.");
                    Assert.True(DifferentPixels(first, second, new(9, top + 14, 3, 18)) > 2,
                        $"Level {level} must still pulse its portrait at {firstSeconds} seconds.");
                    AssertOnlyChangesInside(first, second, new(4, top, 352, height));
                    if (firstSeconds == 14.7) Save(first, $"{themeId}-level-{level}-late-sweep.png");
                }
            }
        }
        finally { UiText.Language = previousLanguage; }
    }

    private static PetOverlayForm.LootRow Pet(string name, string id, int level) =>
        new(name, "fera", level, false, 4, level == 1 ? 25 : 75,
            SoulName: "Preview soul", PetId: id);

    private static Bitmap Render(IReadOnlyList<PetOverlayForm.LootRow> focus,
        IReadOnlyList<PetOverlayForm.LootRow> drops, PetOverlayForm.LevelUpRow frame,
        OverlayThemeDefinition theme, double scale = 1)
    {
        var placed = PetOverlayForm.PlaceLevelUpRows(focus, drops, frame);
        return PetOverlayForm.RenderLootBitmap(placed.Focus, placed.Drops, true, true, frame, theme, scale);
    }

    private static Rectangle Scale(Rectangle rectangle, double scale) => Rectangle.FromLTRB(
        (int)Math.Floor(rectangle.Left * scale), (int)Math.Floor(rectangle.Top * scale),
        (int)Math.Ceiling(rectangle.Right * scale), (int)Math.Ceiling(rectangle.Bottom * scale));

    private static int DifferentPixels(Bitmap first, Bitmap second, Rectangle area)
    {
        Assert.Equal(first.Size, second.Size);
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
    }

    private static void AssertOnlyChangesInside(Bitmap first, Bitmap second, Rectangle area)
    {
        Assert.Equal(first.Size, second.Size);
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
            if (!area.Contains(x, y)) Assert.Equal(first.GetPixel(x, y).ToArgb(), second.GetPixel(x, y).ToArgb());
    }

    private static void Save(Bitmap image, string name)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_LEVELUP_THEME_OUTPUT") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, name), ImageFormat.Png);
    }
}
